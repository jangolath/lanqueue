using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;

namespace LanQueue;

public sealed record ParsedArgs(string Command, string? ConfigPath, IReadOnlyList<string> Needs, string Directory, int WaitSeconds, bool Json, IReadOnlyList<string> Argv)
{
    /// <summary>Everything after a bare "--" is the job command and is never interpreted as lanq options.</summary>
    public static ParsedArgs Parse(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        var own = split >= 0 ? args[..split] : args;
        var argv = split >= 0 ? args[(split + 1)..] : [];
        string? config = null;
        var needs = new List<string>();
        var dir = ".";
        var wait = 0;
        var command = own.Length > 0 ? own[0] : "help";
        for (var i = 1; i < own.Length; i++)
        {
            switch (own[i])
            {
                case "--config" when i + 1 < own.Length:
                    config = own[++i];
                    break;
                case "--needs" when i + 1 < own.Length:
                    needs.AddRange(own[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--dir" when i + 1 < own.Length:
                    dir = own[++i];
                    break;
                case "--wait" when i + 1 < own.Length:
                    wait = int.Parse(own[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option '{own[i]}'.");
            }
        }

        return new ParsedArgs(command, config, needs, dir, wait, false, argv);
    }
}

public static class Cli
{
    private const string Usage = """
        lanq - headless job runner for your own LAN machines

          lanq health [--config machines.local.yml]
          lanq run --needs dotnet,linux [--dir src] [--wait 120] [--config FILE] -- dotnet test
          lanq stats

        Config lookup: --config, then $LANQ_CONFIG, then ./machines.local.yml.
        Exit codes: job's own code; 2 rejected by policy; 70 infrastructure error; 75 no eligible machine.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ParsedArgs p;
        try
        {
            p = ParsedArgs.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        var home = Environment.GetEnvironmentVariable("LANQ_HOME") ?? Path.Combine(Directory.GetCurrentDirectory(), ".lanq");
        var log = new JobLog(home);
        if (p.Command == "stats")
        {
            return Stats(log, stdout);
        }

        if (p.Command is not ("health" or "run"))
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return p.Command is "help" or "--help" or "-h" ? 0 : 2;
        }

        FleetConfig config;
        try
        {
            config = ConfigLoader.Load(ResolveConfig(p.ConfigPath));
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        var ssh = new ProcessSshClient(Environment.GetEnvironmentVariable);
        var probe = new HealthProbe(ssh, TimeProvider.System);
        if (p.Command == "health")
        {
            return await Health(config, probe, stdout, ct).ConfigureAwait(false);
        }

        var scratch = Path.Combine(home, "tmp");
        var runner = new JobRunner(config, ssh, new GitPayloadBuilder(), probe, log, scratch, line => stdout.WriteLine(line));
        var result = await runner.RunAsync(
            new JobRequest(p.Needs, p.Argv, p.Directory, Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(p.WaitSeconds)),
            ct).ConfigureAwait(false);
        await stderr.WriteLineAsync($"[lanq] {result.Status}: {result.Message}").ConfigureAwait(false);
        return result.CliExitCode;
    }

    private static string ResolveConfig(string? explicitPath)
    {
        var path = explicitPath ?? Environment.GetEnvironmentVariable("LANQ_CONFIG") ?? "machines.local.yml";
        return File.Exists(path) ? path : throw new ConfigException($"Config file not found: {path}");
    }

    private static async Task<int> Health(FleetConfig config, HealthProbe probe, TextWriter stdout, CancellationToken ct)
    {
        foreach (var m in config.Machines)
        {
            var h = await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false);
            var state = !h.Reachable ? "DOWN" : h.Hold is not null ? "HOLD" : "OK";
            var detail = h.Reachable
                ? string.Create(CultureInfo.InvariantCulture, $"load {h.LoadRatio:0.00}  disk free {h.DiskFreeGb:0.#} GB  slots {h.SlotsUsed}/{m.MaxJobs}")
                : h.Error ?? "";
            await stdout.WriteLineAsync($"{m.Name,-14} {state,-5} {detail} {(h.Hold is not null && h.Reachable ? "(" + h.Hold + ")" : "")}".TrimEnd()).ConfigureAwait(false);
        }

        return 0;
    }

    private static int Stats(JobLog log, TextWriter stdout)
    {
        var all = log.ReadAll();
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{all.Count} jobs, {all.Sum(r => r.WallSeconds) / 60:0.#} min total wall-clock"));
        foreach (var g in all.GroupBy(r => r.Machine ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {g.Key}: {g.Count()} jobs, {g.Sum(r => r.WallSeconds) / 60:0.#} min"));
        }

        return 0;
    }
}
