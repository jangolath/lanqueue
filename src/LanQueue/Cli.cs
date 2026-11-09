using System.Globalization;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Ssh;

namespace LanQueue;

public sealed record ParsedArgs(string Command, string? ConfigPath, IReadOnlyList<string> Needs, string Directory, int WaitSeconds, IReadOnlyList<string> Argv)
{
    public const int DefaultTimeoutMinutes = 240;

    /// <summary>How long the job itself may run (lanq run --timeout, in minutes).</summary>
    public int TimeoutMinutes { get; init; } = DefaultTimeoutMinutes;

    /// <summary>lanq run --mem: minimum available memory in GB, or null.</summary>
    public double? MemGb { get; init; }

    /// <summary>--json: machine-readable output on stdout.</summary>
    public bool Json { get; init; }

    /// <summary>The one positional argument some commands take (a machine name).</summary>
    public string? Target { get; init; }

    /// <summary>lanq unlock --all: every slot file, not only the expired ones.</summary>
    public bool All { get; init; }

    /// <summary>--yes: confirms a destructive option such as unlock --all.</summary>
    public bool Yes { get; init; }

    /// <summary>--force: makeready scans a machine even when it is paused or outside its allowed hours.</summary>
    public bool Force { get; init; }

    /// <summary>makeready --ssh user@host: a PC that is not in the config yet.</summary>
    public string? SshTarget { get; init; }

    /// <summary>makeready --os windows|linux, with --ssh.</summary>
    public MachineOs? Os { get; init; }

    /// <summary>lanq doctor --remote: also check each worker over ssh.</summary>
    public bool Remote { get; init; }

    /// <summary>The options each command accepts (--config is accepted everywhere); anything else is an error, never silently ignored.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["health"] = ["--json"],
        ["run"] = ["--needs", "--dir", "--wait", "--timeout", "--mem", "--json"],
        ["stats"] = [],
        ["unlock"] = ["--all", "--yes"],
        ["doctor"] = ["--remote", "--force"],
        ["makeready"] = ["--ssh", "--os", "--json", "--force"],
    };

    /// <summary>Commands that take one positional machine name.</summary>
    private static readonly HashSet<string> TakesTarget = new(StringComparer.Ordinal) { "unlock", "makeready" };

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
        var timeout = DefaultTimeoutMinutes;
        double? mem = null;
        var json = false;
        var all = false;
        var yes = false;
        var remote = false;
        var force = false;
        string? sshTarget = null;
        MachineOs? os = null;
        string? target = null;
        var seen = new List<string>();
        var command = own.Length > 0 ? own[0] : "help";
        for (var i = 1; i < own.Length; i++)
        {
            if (own[i].StartsWith("--", StringComparison.Ordinal))
            {
                seen.Add(own[i]);
            }

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
                case "--timeout" when i + 1 < own.Length:
                    timeout = int.Parse(own[++i], CultureInfo.InvariantCulture);
                    if (timeout is < 1 or > 10080)
                    {
                        throw new ArgumentException("--timeout must be a whole number of minutes from 1 to 10080 (one week).");
                    }

                    break;
                case "--mem" when i + 1 < own.Length:
                    if (!double.TryParse(own[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || !double.IsFinite(gb) || gb is <= 0 or > 4096)
                    {
                        throw new ArgumentException("--mem must be the available memory a machine needs, in GB, above 0 and at most 4096 (for example --mem 4 or --mem 2.5).");
                    }

                    mem = gb;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--remote":
                    remote = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--ssh" when i + 1 < own.Length:
                    sshTarget = own[++i];
                    break;
                case "--os" when i + 1 < own.Length:
                    os = own[++i] switch
                    {
                        "windows" => MachineOs.Windows,
                        "linux" => MachineOs.Linux,
                        _ => throw new ArgumentException("--os must be windows or linux."),
                    };
                    break;
                default:
                    if (TakesTarget.Contains(command) && target is null && !own[i].StartsWith('-'))
                    {
                        target = own[i];
                        break;
                    }

                    throw new ArgumentException($"Unknown or incomplete option '{own[i]}'.");
            }
        }

        if (Allowed.TryGetValue(command, out var allowed) && seen.FirstOrDefault(o => o != "--config" && !allowed.Contains(o)) is { } bad)
        {
            throw new ArgumentException($"lanq {command} does not take {bad}.");
        }

        if (split >= 0 && command != "run")
        {
            throw new ArgumentException($"lanq {command} does not take a command after --; only lanq run does.");
        }

        return new ParsedArgs(command, config, needs, dir, wait, argv) { TimeoutMinutes = timeout, MemGb = mem, Json = json, Target = target, All = all, Yes = yes, Remote = remote, Force = force, SshTarget = sshTarget, Os = os };
    }
}

public static partial class Cli
{
    private const string Usage = """
        lanq - headless job runner for your own LAN machines

          lanq health [--json] [--config FILE]
          lanq run --needs dotnet,linux [--dir src] [--wait 120] [--timeout 240] [--mem 4] [--json] [--config FILE] -- dotnet test
          lanq stats
          lanq unlock <machine> [--all --yes] [--config FILE]
          lanq doctor [--remote [--force]] [--config FILE]
          lanq makeready <machine> [--json] [--force] [--config FILE]
          lanq makeready --ssh user@host --os windows|linux [--json]

        Config lookup: --config, then $LANQ_CONFIG, then the per-user config folder (never the current directory).
        State (job log, logs) lives in $LANQ_HOME or a per-user folder, never inside your repository.
        --wait is in seconds (how long to wait for a free machine); --timeout is in minutes (how long the job may run, default 240).
        --mem is in GB: only machines reporting at least that much available memory take the job.
        unlock removes slot files past their recorded deadline (left by a crashed lanq); --all --yes removes every slot file on that machine.
        doctor checks this machine's setup without any network (ssh, git, config, known_hosts, keys, state folder); --remote also checks
        each worker over ssh (work_root, git or tar, and each capability's tool in the job environment); a paused machine or one outside its
        allowed hours is left alone (HOLD) unless --force. Exit 0 when no line is WARN or FAIL, 1 otherwise.
        makeready runs the read-only lanq-makeready check on a worker (sent on ssh stdin, nothing is written to its disk) and prints verdicts,
        problems with advice, the capability diff and a suggested machines.yml entry. Exit 0 ready, 1 not ready, 75 the machine is paused or
        outside its allowed hours (--force scans anyway). lanq never changes a worker.
        --json: health prints one JSON array; run streams the job's output to stderr and prints one JSON object on stdout at the end;
        makeready prints the validated report.
        Exit codes (run): job's own code; 2 rejected by policy or bad arguments; 70 infrastructure error; 75 no eligible machine; 124 job timed out or its machine's allowed hours ended.
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
            // The message can quote what was typed: sanitised. With --json (run or health) stdout still gets its one object.
            var message = JobRunner.SafeLine(ex.Message);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            var own = Array.IndexOf(args, "--") is var dash and >= 0 ? args[..dash] : args;
            if (own.Length > 0 && own[0] is "run" or "health" && own.Contains("--json"))
            {
                await WriteJsonError(own[0], "Rejected", 2, message, stdout).ConfigureAwait(false);
            }

            return 2;
        }

        var home = ResolveHome(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
        var log = new JobLog(home);
        if (p.Command == "stats")
        {
            return Stats(log, stdout);
        }

        if (p.Command is not ("health" or "run" or "unlock" or "doctor" or "makeready"))
        {
            // Help that was asked for goes to stdout; usage after a mistake goes to stderr, so stdout stays clean for scripts.
            var asked = p.Command is "help" or "--help" or "-h";
            await (asked ? stdout : stderr).WriteLineAsync(Usage).ConfigureAwait(false);
            return asked ? 0 : 2;
        }

        FleetConfig LoadConfig() => ConfigLoader.Load(ResolveConfig(p.ConfigPath, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists));
        if (p.Command == "doctor")
        {
            var doctorSsh = new ProcessSshClient(Environment.GetEnvironmentVariable);
            return await Doctor(p, LoadConfig, DoctorHost.Real(home), doctorSsh, new HealthProbe(doctorSsh, TimeProvider.System), stdout, ct).ConfigureAwait(false);
        }

        if (p.Command == "makeready")
        {
            var client = new ProcessSshClient(Environment.GetEnvironmentVariable);
            return await Makeready(p, LoadConfig, client, new HealthProbe(client, TimeProvider.System), stdout, stderr, ct).ConfigureAwait(false);
        }

        FleetConfig config;
        try
        {
            config = LoadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            var message = JobRunner.SafeLine(ex.Message);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            if (p.Json)
            {
                await WriteJsonError(p.Command, "Rejected", 2, message, stdout).ConfigureAwait(false);
            }

            return 2;
        }

        var ssh = new ProcessSshClient(Environment.GetEnvironmentVariable);
        if (p.Command == "unlock")
        {
            return await Unlock(p, config, ssh, stdout, stderr, ct).ConfigureAwait(false);
        }

        var probe = new HealthProbe(ssh, TimeProvider.System);
        if (p.Command == "health")
        {
            return await Health(config, probe, stdout, stderr, ct, p.Json).ConfigureAwait(false);
        }

        return await Run(p, config, ssh, new GitPayloadBuilder(), probe, log, home, Directory.GetCurrentDirectory(), stdout, stderr, ct).ConfigureAwait(false);
    }

    /// <summary>lanq run. With --json the job's output streams to stderr and stdout carries only the final JSON object.</summary>
    internal static async Task<int> Run(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, HealthProbe probe, JobLog log, string home, string repoDir,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var jobOutput = p.Json ? stderr : stdout;
        var runner = new JobRunner(config, ssh, payloads, probe, log, Path.Combine(home, "tmp"), line => jobOutput.WriteLine(line), bases: new BaseStore(Path.Combine(home, "bases.json")));
        var started = System.Diagnostics.Stopwatch.StartNew();
        JobResult result;
        try
        {
            result = await runner.RunAsync(
                new JobRequest(p.Needs, p.Argv, p.Directory, repoDir, TimeSpan.FromSeconds(p.WaitSeconds), TimeSpan.FromMinutes(p.TimeoutMinutes), p.MemGb),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (p.Json && ct.IsCancellationRequested)
        {
            // Ctrl+C: an agent reading --json still gets its one object.
            await stderr.WriteLineAsync("[lanq] Cancelled").ConfigureAwait(false);
            await WriteJsonError("run", "Cancelled", 130, "cancelled (Ctrl+C); the job was cleaned up where it had started", stdout, started.Elapsed).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (p.Json && ex is not OperationCanceledException)
        {
            var message = JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            await stderr.WriteLineAsync("[lanq] InfraError: " + message).ConfigureAwait(false);
            await WriteJsonError("run", "InfraError", 70, message, stdout, started.Elapsed).ConfigureAwait(false);
            return 70;
        }

        await stderr.WriteLineAsync(StatusLine(result)).ConfigureAwait(false);
        if (p.Json)
        {
            var json = new RunJson(
                result.Status.ToString(), result.CliExitCode, result.Machine, result.JobId, Math.Round(started.Elapsed.TotalSeconds, 1), JobRunner.SafeLine(result.Message));
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.RunJson)).ConfigureAwait(false);
        }

        return result.CliExitCode;
    }

    /// <summary>
    /// The one JSON object --json promises on stdout when no normal result exists: for run the usual RunJson shape (no machine or job id),
    /// for health an object with status, exitCode and message instead of the array.
    /// </summary>
    private static Task WriteJsonError(string command, string status, int exitCode, string message, TextWriter stdout, TimeSpan? elapsed = null) =>
        stdout.WriteLineAsync(command == "run"
            ? JsonSerializer.Serialize(new RunJson(status, exitCode, null, null, Math.Round((elapsed ?? TimeSpan.Zero).TotalSeconds, 1), message), LanqJson.Default.RunJson)
            : JsonSerializer.Serialize(new ErrorJson(status, exitCode, message), LanqJson.Default.ErrorJson));

    /// <summary>The final line on stderr; TimedOut and WindowClosed (both exit 124) are told apart here.</summary>
    internal static string StatusLine(JobResult result) => $"[lanq] {result.Status}: {result.Message}";

    /// <summary>State folder: $LANQ_HOME, else a per-user folder. Deliberately never inside the current repository.</summary>
    internal static string ResolveHome(Func<string, string?> env, bool windows)
    {
        var explicitHome = env("LANQ_HOME");
        if (!string.IsNullOrEmpty(explicitHome))
        {
            return explicitHome;
        }

        if (windows)
        {
            var local = env("LOCALAPPDATA");
            return Path.Combine(string.IsNullOrEmpty(local) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : local, "lanq");
        }

        var state = env("XDG_STATE_HOME");
        return !string.IsNullOrEmpty(state)
            ? Path.Combine(state, "lanq")
            : Path.Combine(HomeDir(env), ".local", "state", "lanq");
    }

    /// <summary>Config lookup: --config, then $LANQ_CONFIG, then the per-user config folder. Never the current directory.</summary>
    internal static string ResolveConfig(string? explicitPath, Func<string, string?> env, bool windows, Func<string, bool> exists)
    {
        var path = explicitPath ?? env("LANQ_CONFIG");
        if (!string.IsNullOrEmpty(path))
        {
            return exists(path) ? path : throw new ConfigException($"Config file not found: {path}");
        }

        string userDir;
        if (windows)
        {
            var appData = env("APPDATA");
            userDir = Path.Combine(string.IsNullOrEmpty(appData) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : appData, "lanq");
        }
        else
        {
            var xdg = env("XDG_CONFIG_HOME");
            userDir = Path.Combine(!string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(HomeDir(env), ".config"), "lanq");
        }

        var userFile = Path.Combine(userDir, "machines.yml");
        return exists(userFile)
            ? userFile
            : throw new ConfigException($"No config found. Pass --config FILE, set LANQ_CONFIG, or create {userFile}. (A machines file in the current directory is never picked up implicitly.)");
    }

    private static string HomeDir(Func<string, string?> env) =>
        env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static async Task<int> Health(FleetConfig config, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct, bool json = false)
    {
        foreach (var w in await KeyCheck.WarningsAsync(config, Environment.GetEnvironmentVariable, KeyCheck.SshKeygenAcceptsEmptyPassphrase).ConfigureAwait(false))
        {
            await stderr.WriteLineAsync(w).ConfigureAwait(false);
        }

        // Probe all machines concurrently, then print in config order.
        var results = await Task.WhenAll(config.Machines.Select(m => probe.ProbeAsync(m, config.Policy, ct))).ConfigureAwait(false);
        if (json)
        {
            var rows = config.Machines.Zip(results).Select(x => HealthRow(x.First, x.Second)).ToList();
            await stdout.WriteLineAsync(JsonSerializer.Serialize(rows, LanqJson.Default.ListHealthJson)).ConfigureAwait(false);
            return 0;
        }

        foreach (var (m, h) in config.Machines.Zip(results))
        {
            var state = !h.Reachable ? "DOWN" : h.Hold is not null ? "HOLD" : "OK";
            var detail = h.Reachable
                ? string.Create(CultureInfo.InvariantCulture, $"load {h.LoadRatio:0.00}  disk free {h.DiskFreeGb:0.#} GB  {Memory(h)}  slots {h.SlotsUsed}/{m.MaxJobs}")
                    + (m.AllowedHours is { Count: > 0 } && !h.WorkerClock ? "  time: local" : "")
                : h.Error ?? "";
            await stdout.WriteLineAsync($"{m.Name,-14} {state,-5} {detail} {(h.Hold is not null && h.Reachable ? "(" + h.Hold + ")" : "")}".TrimEnd()).ConfigureAwait(false);
        }

        return 0;
    }

    internal static HealthJson HealthRow(Machine m, MachineHealth h)
    {
        var state = !h.Reachable ? "down" : h.Hold is not null ? "hold" : "ok";
        var reason = !h.Reachable ? h.Error ?? h.Hold : h.Hold;
        return new HealthJson(
            m.Name, state, reason is null ? [] : [JobRunner.SafeLine(reason)], h.LoadRatio, h.DiskFreeGb, h.MemFreeGb, h.SlotsUsed, m.MaxJobs, h.LocalTime);
    }

    private static string Memory(MachineHealth h) =>
        (h.MemFreeGb is { } mem ? string.Create(CultureInfo.InvariantCulture, $"mem free {mem:0.0} GB") : "mem free ?")
        + (h.SwapFreeGb is { } swap ? string.Create(CultureInfo.InvariantCulture, $" (swap {swap:0.0} GB)") : "");

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
