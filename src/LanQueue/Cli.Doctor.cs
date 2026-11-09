using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Ssh;

namespace LanQueue;

/// <summary>What lanq doctor needs from the local machine, so tests can stand in for it.</summary>
/// <param name="Exec">Runs a local program (no shell) and returns its exit code and combined output; 255 with "Could not start" when it is not on PATH.</param>
internal sealed record DoctorHost(
    Func<string, IReadOnlyList<string>, Task<SshResult>> Exec,
    Func<string, string?> GetEnv,
    Func<string, bool> FileExists,
    Func<string, Task<bool>> AcceptsEmptyPassphrase,
    string Home)
{
    public static DoctorHost Real(string home) => new(
        (exe, args) => ProcessSshClient.Execute(exe, [.. args], null, TimeSpan.FromSeconds(15), CancellationToken.None),
        Environment.GetEnvironmentVariable,
        File.Exists,
        KeyCheck.SshKeygenAcceptsEmptyPassphrase,
        home);
}

public static partial class Cli
{
    /// <summary>How many machines lanq doctor --remote checks at once.</summary>
    internal const int DoctorParallelism = 4;

    /// <summary>
    /// lanq doctor: local checks only (no network): ssh, scp, git and ssh-keygen on PATH; the config; per machine a known_hosts entry, the
    /// key_env file and its passphrase, work_root; the state folder. With --remote, one ssh call per machine (a few at a time) checks
    /// work_root, git or tar, and each known capability's tool in the job environment. Exit 0 when every line is OK, else 1.
    /// </summary>
    internal static async Task<int> Doctor(ParsedArgs p, Func<FleetConfig> loadConfig, DoctorHost host, ISshClient ssh, TextWriter stdout, CancellationToken ct)
    {
        var problems = 0;
        async Task Say(string level, string text)
        {
            problems += level is "WARN" or "FAIL" ? 1 : 0;
            await stdout.WriteLineAsync($"{level,-4}  {JobRunner.SafeLine(text)}").ConfigureAwait(false);
        }

        foreach (var (exe, args) in new (string, string[])[] { ("ssh", ["-V"]), ("scp", []), ("git", ["--version"]), ("ssh-keygen", []) })
        {
            var r = await host.Exec(exe, args).ConfigureAwait(false);
            var missing = r.ExitCode == 255 && r.Output.StartsWith("Could not start", StringComparison.Ordinal);
            await (missing
                ? Say("FAIL", $"{exe}: not found on PATH")
                : Say("OK", $"{exe}: {(args.Length > 0 ? FirstLine(r.Output) : "found")}")).ConfigureAwait(false);
        }

        await (StateFolderWritable(host.Home) is { } error
            ? Say("FAIL", $"state folder {host.Home} is not writable: {error}")
            : Say("OK", $"state folder {host.Home} is writable")).ConfigureAwait(false);

        FleetConfig config;
        try
        {
            config = loadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            await Say("FAIL", "config: " + ex.Message).ConfigureAwait(false);
            return 1;
        }

        await Say("OK", $"config: {config.Machines.Count} machine(s)").ConfigureAwait(false);
        foreach (var m in config.Machines)
        {
            foreach (var (level, text) in await LocalMachineChecks(m, host).ConfigureAwait(false))
            {
                await Say(level, $"{m.Name}: {text}").ConfigureAwait(false);
            }
        }

        if (p.Remote)
        {
            using var gate = new SemaphoreSlim(DoctorParallelism);
            async Task<List<(string, string)>> Checked(Machine m)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await RemoteMachineChecks(m, ssh, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }

            var results = await Task.WhenAll(config.Machines.Select(Checked)).ConfigureAwait(false);
            foreach (var (m, lines) in config.Machines.Zip(results))
            {
                foreach (var (level, text) in lines)
                {
                    await Say(level, $"{m.Name} (remote): {text}").ConfigureAwait(false);
                }
            }
        }

        return problems == 0 ? 0 : 1;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <returns>Null when a file could be created and deleted in the folder, else the error.</returns>
    private static string? StateFolderWritable(string home)
    {
        try
        {
            Directory.CreateDirectory(home);
            var probe = Path.Combine(home, ".doctor-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static async Task<List<(string Level, string Text)>> LocalMachineChecks(Machine m, DoctorHost host)
    {
        var lines = new List<(string, string)>();

        // ssh -G resolves ~/.ssh/config (HostName, Port, UserKnownHostsFile) without connecting.
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (await host.Exec("ssh", ["-G", m.Ssh]).ConfigureAwait(false)).Output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                resolved.TryAdd(line[..space], line[(space + 1)..].Trim());
            }
        }

        var name = resolved.GetValueOrDefault("hostname") is { Length: > 0 } h ? h : m.Ssh[(m.Ssh.LastIndexOf('@') + 1)..];
        var port = resolved.GetValueOrDefault("port") is { Length: > 0 } pt ? pt : "22";
        var key = port == "22" ? name : $"[{name}]:{port}";
        var files = (resolved.GetValueOrDefault("userknownhostsfile") ?? "~/.ssh/known_hosts")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => ExpandHome(f, host))
            .Where(f => f is not null && host.FileExists(f))
            .ToList();
        // ssh-keygen's own default file first, then the files ssh -G named (a different ssh build may print paths in its own form).
        string[][] lookups = [["-F", key], .. files.Select(f => new[] { "-F", key, "-f", f! })];
        var known = false;
        foreach (var args in lookups)
        {
            var r = await host.Exec("ssh-keygen", args).ConfigureAwait(false);
            if (r.Ok && r.Output.Trim().Length > 0)
            {
                known = true;
                break;
            }
        }

        lines.Add(known
            ? ("OK", $"known_hosts has {key}")
            : ("FAIL", $"no known_hosts entry for {key}: connect once with ssh {m.Ssh}, check the fingerprint with the machine's owner, then accept it"));

        if (!string.IsNullOrEmpty(m.KeyEnv))
        {
            var path = host.GetEnv(m.KeyEnv);
            if (string.IsNullOrEmpty(path))
            {
                lines.Add(("FAIL", $"key_env {m.KeyEnv} is not set"));
            }
            else if (!host.FileExists(path))
            {
                lines.Add(("FAIL", $"key file named by {m.KeyEnv} not found"));
            }
            else if (await KeyCheck.IsUnencryptedAsync(path, host.AcceptsEmptyPassphrase).ConfigureAwait(false))
            {
                lines.Add(("WARN", $"the key file named by {m.KeyEnv} has no passphrase (see README, \"Keeping your keys safe\")"));
            }
            else
            {
                lines.Add(("OK", $"key file named by {m.KeyEnv} found"));
            }
        }

        lines.Add(("OK", $"work_root {m.WorkRoot} ({(m.Os == MachineOs.Windows ? "windows" : "linux")}, transfer: {m.Transfer.ToString().ToLowerInvariant()})"));
        return lines;
    }

    /// <summary>Expands a leading ~ the way ssh does; a path with other ssh tokens (%d, %u) is skipped.</summary>
    private static string? ExpandHome(string file, DoctorHost host)
    {
        if (file.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        if (!file.StartsWith('~'))
        {
            return file;
        }

        var home = host.GetEnv("HOME") is { Length: > 0 } h ? h : host.GetEnv("USERPROFILE");
        return string.IsNullOrEmpty(home) ? null : home.Replace('\\', '/') + file[1..];
    }

    private static async Task<List<(string Level, string Text)>> RemoteMachineChecks(Machine m, ISshClient ssh, CancellationToken ct)
    {
        SshResult r;
        try
        {
            r = await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.Doctor(m)), null, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return [("FAIL", ex.Message)];
        }

        if (r.ExitCode == 255 || r.TimedOut)
        {
            return [("FAIL", "unreachable: " + FirstLine(r.Output))];
        }

        var values = LanQueue.Health.HealthProbe.Parse(r.Output);
        var lines = new List<(string, string)>
        {
            values.GetValueOrDefault("root") switch
            {
                "ok" => ("OK", $"work_root {m.WorkRoot} is writable"),
                "missing" => ("WARN", $"work_root {m.WorkRoot} does not exist yet (the first job creates it)"),
                _ => ("FAIL", $"work_root {m.WorkRoot} is not writable by the ssh user"),
            },
            values.GetValueOrDefault("tool") == "ok"
                ? ("OK", m.Transfer == TransferMode.Archive ? "tar (transfer: archive)" : "git (transfer: git)")
                : ("FAIL", m.Transfer == TransferMode.Archive
                    ? "no usable tar for transfer: archive (see docs/WORKERS.md)"
                    : "git not found in a non-login ssh session (install it, or set transfer: archive)"),
        };
        foreach (var cap in m.Capabilities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!RemoteScripts.ToolChecks.TryGetValue(cap, out var commands))
            {
                lines.Add(("SKIP", $"{cap}: not checked (no known tool command)"));
                continue;
            }

            var command = m.Os == MachineOs.Windows ? commands.Windows : commands.Linux;
            var raw = values.GetValueOrDefault("cap." + cap.ToLowerInvariant());
            var bar = raw?.IndexOf('|', StringComparison.Ordinal) ?? -1;
            var code = bar < 0 ? "?" : raw![..bar];
            var text = bar < 0 ? "no answer" : raw![(bar + 1)..];
            lines.Add(code == "0"
                ? ("OK", $"{cap}: {text}")
                : ("FAIL", $"{cap}: '{command}' failed in the job environment ({(text.Length > 0 ? text : "exit " + code)}). "
                    + "If the tool is installed, a non-login ssh session does not see it: add its folder to path_prepend for this machine"));
        }

        return lines;
    }
}
