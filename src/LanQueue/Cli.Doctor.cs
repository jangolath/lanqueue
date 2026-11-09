using LanQueue.Config;
using LanQueue.Health;
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
    /// <summary>How long one tool check of lanq doctor --remote may take on the worker before it is stopped and reported as timed out.</summary>
    internal const int DoctorCheckSeconds = 20;

    /// <summary>How many machines lanq doctor --remote checks at once.</summary>
    internal const int DoctorParallelism = 4;

    /// <summary>
    /// lanq doctor: local checks only (no network): ssh, scp, git and ssh-keygen on PATH; the config; per machine a known_hosts entry, the
    /// key_env file and its passphrase, work_root; the state folder. With --remote, one ssh call per machine (a few at a time) checks
    /// work_root, git or tar, and each known capability's tool in the job environment. Exit 0 when every line is OK, else 1.
    /// </summary>
    internal static async Task<int> Doctor(ParsedArgs p, Func<FleetConfig> loadConfig, DoctorHost host, ISshClient ssh, LanQueue.Health.HealthProbe probe, TextWriter stdout, CancellationToken ct,
        Func<Machine, string>? doctorScript = null)
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
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
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
                    if (!p.Force)
                    {
                        // Like makeready: a family PC's PAUSE file and allowed hours are respected; the probe is the only call made then.
                        var h = await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false);
                        if (h.HoldKind is HoldKind.Paused or HoldKind.OutsideHours)
                        {
                            return [("HOLD", $"{(h.HoldKind == HoldKind.Paused ? "paused (a PAUSE file is in its work_root)" : "outside allowed hours")}; not checked. Add --force to check it anyway")];
                        }
                    }

                    return await RemoteMachineChecks(m, ssh, doctorScript ?? (x => RemoteScripts.Doctor(x, DoctorCheckSeconds)), ct).ConfigureAwait(false);
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

    /// <summary>"git version 2.48.1.windows.1" -> 2.48; null when the line is missing or does not look like that.</summary>
    internal static Version? GitVersion(string? line)
    {
        var match = GitVersionRegex().Match(line ?? "");
        return match.Success
            && int.TryParse(match.Groups["major"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups["minor"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor)
                ? new Version(major, minor)
                : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^git version (?<major>[0-9]{1,4})\.(?<minor>[0-9]{1,4})", System.Text.RegularExpressions.RegexOptions.ExplicitCapture, 1000)]
    private static partial System.Text.RegularExpressions.Regex GitVersionRegex();

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
        // ssh looks a host key up under HostKeyAlias when one is set, used as it is (no port); otherwise host, or [host]:port.
        var alias = resolved.GetValueOrDefault("hostkeyalias") is { Length: > 0 } a && !a.Equals("none", StringComparison.OrdinalIgnoreCase) ? a : null;
        var key = alias ?? (port == "22" ? name : $"[{name}]:{port}");
        var files = ((resolved.GetValueOrDefault("userknownhostsfile") ?? "~/.ssh/known_hosts") + " "
                + (resolved.GetValueOrDefault("globalknownhostsfile") ?? "/etc/ssh/ssh_known_hosts /etc/ssh/ssh_known_hosts2"))
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

    private static async Task<List<(string Level, string Text)>> RemoteMachineChecks(Machine m, ISshClient ssh, Func<Machine, string> doctorScript, CancellationToken ct)
    {
        // Loading the config only checks the job script against the Windows command-line cap (lanq run must not be blocked by doctor);
        // the doctor script, which also carries a tool check per capability, is checked here and reported, never thrown.
        var script = doctorScript(m);
        if (m.Os == MachineOs.Windows && RemoteScripts.WindowsCommand(script).Length is var size && size > RemoteScripts.MaxWindowsCommandLength)
        {
            return [("FAIL", $"not checked: with its env: ({m.Env.Count} entries), path_prepend: ({m.PathPrepend.Count} entries) and "
                + $"{m.Capabilities.Count(RemoteScripts.ToolChecks.ContainsKey)} tool checks, the doctor script is {size} characters once encoded, "
                + $"{size - RemoteScripts.MaxWindowsCommandLength} over the {RemoteScripts.MaxWindowsCommandLength}-character Windows command-line limit. "
                + "Shorten env: or path_prepend: for this machine")];
        }

        SshResult r;
        try
        {
            // Every check has its own limit on the worker; the call as a whole gets room for all of them.
            var checks = m.Capabilities.Count(RemoteScripts.ToolChecks.ContainsKey);
            r = await ssh.RunAsync(m, RemoteScripts.Wrap(m, script), null, TimeSpan.FromSeconds(60 + ((DoctorCheckSeconds + 10) * checks)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return [("FAIL", ex.Message)];
        }

        // A call that ran out of time still reports what arrived; only an ssh failure with nothing to show is "unreachable".
        var partial = r.TimedOut && r.Output.Contains("root=", StringComparison.Ordinal);
        if ((r.ExitCode == 255 || r.TimedOut) && !partial)
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
        if (m.Transfer == TransferMode.Git && GitVersion(values.GetValueOrDefault("git_version")) is { } git && git < new Version(2, 32))
        {
            lines.Add(("WARN", $"git older than 2.32: global config not isolated (git {git} ignores GIT_CONFIG_GLOBAL, so a filter or "
                + "attributes file in the ssh user's ~/.gitconfig can still run during a job's checkout; update git on that machine)"));
        }

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
            var text = bar < 0 ? (partial ? "did not finish before the ssh call timed out" : "no answer") : raw![(bar + 1)..];
            lines.Add(code == "0"
                ? ("OK", $"{cap}: {text}")
                : code == "timeout"
                ? ("FAIL", $"{cap}: '{command}' timed out after {DoctorCheckSeconds} s in the job environment (stopped; the other checks went on)")
                : bar < 0 ? ("FAIL", $"{cap}: {text}")
                : ("FAIL", $"{cap}: '{command}' failed in the job environment ({(text.Length > 0 ? text : "exit " + code)}). "
                    + "If the tool is installed, a non-login ssh session does not see it: add its folder to path_prepend for this machine"));
        }

        return lines;
    }
}
