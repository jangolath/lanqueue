using System.Globalization;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
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

    /// <summary>Counts lanq's job logs in a logs folder and their size (<see cref="LogPrune.Measure"/>); tests stand in for a huge folder.</summary>
    public Func<string, (int Files, long Bytes)> MeasureLogs { get; init; } = LogPrune.Measure;
}

/// <summary>One line of lanq doctor: its text line (<see cref="Text"/>) and, with --json, one entry of checks[]. Names and levels are only ever added.</summary>
/// <param name="Scope">local (this computer), machine (a machine's settings, checked here) or remote (checked on the machine over ssh).</param>
/// <param name="Machine">The machine's name; null for a local check.</param>
/// <param name="Name">What was checked, in snake_case (for example known_hosts, work_root_writable or capability).</param>
/// <param name="Level">ok, warn, fail, skip or hold.</param>
/// <param name="Detail">The text after the level and the machine prefix, exactly as in the text line.</param>
/// <param name="Fix">A short remedy, or null.</param>
/// <param name="Capability">The capability a capability check is about; null for every other check.</param>
internal sealed record DoctorCheck(string Scope, string? Machine, string Name, string Level, string Detail, string? Fix, string? Capability = null)
{
    /// <summary>The text line, exactly as lanq doctor printed it before --json: "LEVEL  [machine: | machine (remote): ]detail", sanitised.</summary>
    public string Text => $"{Level.ToUpperInvariant(),-4}  {JobRunner.SafeLine(Prefix + Detail)}";

    private string Prefix => Scope switch
    {
        "machine" => Machine + ": ",
        "remote" => Machine + " (remote): ",
        _ => "",
    };

    /// <summary>A check of this computer (no machine).</summary>
    public static DoctorCheck Local(string name, string level, string detail, string? fix = null) => new("local", null, name, level, detail, fix);
}

public static partial class Cli
{
    /// <summary>How long one tool check of lanq doctor --remote may take on the worker before it is stopped and reported as timed out.</summary>
    internal const int DoctorCheckSeconds = 20;

    /// <summary>How many machines lanq doctor --remote checks at once.</summary>
    internal const int DoctorParallelism = 4;

    /// <summary>
    /// lanq doctor: local checks only (no network): ssh, scp, git and ssh-keygen on PATH; the state folder; the config; the job logs in
    /// logs/ (size, and WARN over 2 GB when never pruned); per machine a known_hosts entry, the key_env file and its passphrase, work_root. With --remote, one ssh call per machine (a few at a time) checks
    /// work_root, git or tar, and each known capability's tool in the job environment. Exit 0 when no line is WARN or FAIL, else 1 (a config
    /// that does not load is a FAIL line). Text: the lines on stdout as they are found, nothing else. --json: the same lines on stderr, then
    /// stdout gets one <see cref="DoctorJson"/> on every path (2 bad arguments, 70 unexpected error and 130 Ctrl+C with the checks so far) and
    /// the next steps go to stderr as "Next:" lines.
    /// </summary>
    internal static async Task<int> Doctor(
        ParsedArgs p, Func<FleetConfig> loadConfig, DoctorHost host, ISshClient ssh, LanQueue.Health.HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        Func<Machine, string>? doctorScript = null)
    {
        var checks = new List<DoctorCheck>();
        var lines = p.Json ? stderr : stdout;
        async Task Say(DoctorCheck check)
        {
            checks.Add(check);
            await lines.WriteLineAsync(check.Text).ConfigureAwait(false);
        }

        if (!p.Json)
        {
            // Exactly as before --json: an unexpected error or Ctrl+C ends lanq here (Program.cs exits 130 on Ctrl+C, with no output).
            await DoctorChecks(p, loadConfig, host, ssh, probe, Say, ct, doctorScript).ConfigureAwait(false);
            return DoctorExitCode(checks);
        }

        try
        {
            await DoctorChecks(p, loadConfig, host, ssh, probe, Say, ct, doctorScript).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await WriteDoctorJson("Cancelled", 130, "cancelled (Ctrl+C); the checks that had finished are listed", p.Remote, checks, p.ConfigPath, stdout, stderr)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await WriteDoctorJson("InfraError", 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), p.Remote, checks, p.ConfigPath, stdout, stderr)
                .ConfigureAwait(false);
        }

        var code = DoctorExitCode(checks);
        var status = code == 0 ? "Ok" : checks.Any(c => c.Level == "fail") ? "Failing" : "Warnings";
        var message = checks.FirstOrDefault(c => c is { Scope: "local", Name: "config", Level: "fail" }) is { } config
            ? JobRunner.SafeLine(config.Detail)
            : CountsText(checks);
        return await WriteDoctorJson(status, code, message, p.Remote, checks, p.ConfigPath, stdout, stderr).ConfigureAwait(false);
    }

    private static int DoctorExitCode(List<DoctorCheck> checks) => checks.Any(c => c.Level is "warn" or "fail") ? 1 : 0;

    /// <summary>"14 checks: 12 ok, 1 warn, 1 fail, 0 skip, 0 hold".</summary>
    private static string CountsText(IReadOnlyCollection<DoctorCheck> checks)
    {
        var c = DoctorCounts(checks);
        return string.Create(CultureInfo.InvariantCulture, $"{checks.Count} checks: {c.Ok} ok, {c.Warn} warn, {c.Fail} fail, {c.Skip} skip, {c.Hold} hold");
    }

    private static DoctorCountsJson DoctorCounts(IReadOnlyCollection<DoctorCheck> checks)
    {
        int Count(string level) => checks.Count(c => c.Level == level);
        return new DoctorCountsJson(Count("ok"), Count("warn"), Count("fail"), Count("skip"), Count("hold"));
    }

    /// <summary>The one object lanq doctor --json prints after the checks ran (or were cut short), with its next steps also on stderr for people.</summary>
    private static async Task<int> WriteDoctorJson(
        string status, int exitCode, string message, bool remote, IReadOnlyList<DoctorCheck> checks, string? config, TextWriter stdout, TextWriter stderr)
    {
        var steps = NextSteps.ForDoctor(status, checks, remote, config, message);
        await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
        var json = new DoctorJson(
            status,
            exitCode,
            message,
            remote,
            DoctorCounts(checks),
            [.. checks.Select(c => new DoctorCheckJson(
                c.Scope,
                c.Machine is null ? null : JobRunner.SafeLine(c.Machine),
                c.Name,
                c.Level,
                JobRunner.SafeLine(c.Detail),
                c.Fix is null ? null : JobRunner.SafeLine(c.Fix),
                c.Capability is null ? null : JobRunner.SafeLine(c.Capability)))])
        {
            NextSteps = steps,
        };
        await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.DoctorJson)).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>Every check of lanq doctor, in the order they are printed, each handed to <paramref name="say"/> as soon as it is known.</summary>
    private static async Task DoctorChecks(
        ParsedArgs p, Func<FleetConfig> loadConfig, DoctorHost host, ISshClient ssh, LanQueue.Health.HealthProbe probe, Func<DoctorCheck, Task> say, CancellationToken ct,
        Func<Machine, string>? doctorScript)
    {
        foreach (var (exe, args) in new (string, string[])[] { ("ssh", ["-V"]), ("scp", []), ("git", ["--version"]), ("ssh-keygen", []) })
        {
            var r = await host.Exec(exe, args).ConfigureAwait(false);
            var missing = r.ExitCode == 255 && r.Output.StartsWith("Could not start", StringComparison.Ordinal);
            var name = exe.Replace('-', '_');
            await say(missing
                ? DoctorCheck.Local(name, "fail", $"{exe}: not found on PATH", exe == "git" ? "install git and put it on PATH" : "install the OpenSSH client and put it on PATH")
                : DoctorCheck.Local(name, "ok", $"{exe}: {(args.Length > 0 ? FirstLine(r.Output) : "found")}")).ConfigureAwait(false);
        }

        await say(StateFolderWritable(host.Home) is { } error
            ? DoctorCheck.Local("state_folder", "fail", $"state folder {host.Home} is not writable: {error}", "set LANQ_HOME to a folder this user can write")
            : DoctorCheck.Local("state_folder", "ok", $"state folder {host.Home} is writable")).ConfigureAwait(false);

        FleetConfig config;
        try
        {
            config = loadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            await say(DoctorCheck.Local("config", "fail", "config: " + ex.Message, "the developer fixes machines.yml (lanq init writes a starter file)")).ConfigureAwait(false);
            return;
        }

        await say(DoctorCheck.Local("config", "ok", $"config: {config.Machines.Count} machine(s)")).ConfigureAwait(false);
        await say(LogsCheck(host, config.Policy.KeepLogsDays)).ConfigureAwait(false);
        foreach (var m in config.Machines)
        {
            foreach (var check in await LocalMachineChecks(m, host).ConfigureAwait(false))
            {
                await say(check).ConfigureAwait(false);
            }
        }

        if (p.Remote)
        {
            using var gate = new SemaphoreSlim(DoctorParallelism);
            async Task<List<DoctorCheck>> Checked(Machine m)
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
                            return
                            [
                                new(
                                    "remote",
                                    m.Name,
                                    "held",
                                    "hold",
                                    $"{(h.HoldKind == HoldKind.Paused ? "paused (a PAUSE file is in its work_root)" : "outside allowed hours")}; not checked. Add --force to check it anyway",
                                    "the developer can add --force to check it anyway"),
                            ];
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
            foreach (var check in results.SelectMany(r => r))
            {
                await say(check).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Above this, a logs/ folder that is never pruned (policy.keep_logs_days 0) is a WARN line of lanq doctor.</summary>
    internal const long LogsWarnBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The job logs in the state folder's logs/: OK with their count and size; WARN when they are over 2 GB and policy.keep_logs_days is 0 (never
    /// pruned); SKIP when the folder could not be read.
    /// </summary>
    private static DoctorCheck LogsCheck(DoctorHost host, int keepDays)
    {
        var folder = Path.Combine(host.Home, "logs");
        (int Files, long Bytes) size;
        try
        {
            size = host.MeasureLogs(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DoctorCheck.Local("logs", "skip", $"logs: {folder} could not be read: {ex.Message}");
        }

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"logs: {size.Files} files, {size.Bytes / (1024.0 * 1024):0.0} MB ({(keepDays == 0 ? "kept for ever: policy.keep_logs_days is 0" : $"kept {keepDays} days")})");
        return keepDays == 0 && size.Bytes > LogsWarnBytes
            ? DoctorCheck.Local(
                "logs",
                "warn",
                text + $"; over 2 GB in {folder}",
                "set policy.keep_logs_days (for example 30) so lanq deletes old job logs after a run, or delete old logs there yourself")
            : DoctorCheck.Local("logs", "ok", text);
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

    private static async Task<List<DoctorCheck>> LocalMachineChecks(Machine m, DoctorHost host)
    {
        var lines = new List<DoctorCheck>();
        DoctorCheck Check(string name, string level, string detail, string? fix = null) => new("machine", m.Name, name, level, detail, fix);

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
            ? Check("known_hosts", "ok", $"known_hosts has {key}")
            : Check(
                "known_hosts",
                "fail",
                $"no known_hosts entry for {key}: connect once with ssh {m.Ssh}, check the fingerprint with the machine's owner, then accept it",
                $"connect once with ssh {m.Ssh}, compare the fingerprint with the machine's owner, then accept it"));

        if (!string.IsNullOrEmpty(m.KeyEnv))
        {
            var path = host.GetEnv(m.KeyEnv);
            var setIt = $"set {m.KeyEnv} to the private key file's path";
            if (string.IsNullOrEmpty(path))
            {
                lines.Add(Check("key_env", "fail", $"key_env {m.KeyEnv} is not set", setIt));
            }
            else if (!host.FileExists(path))
            {
                lines.Add(Check("key_env", "fail", $"key file named by {m.KeyEnv} not found", setIt));
            }
            else if (await KeyCheck.IsUnencryptedAsync(path, host.AcceptsEmptyPassphrase).ConfigureAwait(false))
            {
                lines.Add(Check(
                    "key_env",
                    "warn",
                    $"the key file named by {m.KeyEnv} has no passphrase (see README, \"Keeping your keys safe\")",
                    "prefer a passphrase-protected key loaded into ssh-agent and drop key_env"));
            }
            else
            {
                lines.Add(Check("key_env", "ok", $"key file named by {m.KeyEnv} found"));
            }
        }

        lines.Add(Check("work_root", "ok", $"work_root {m.WorkRoot} ({(m.Os == MachineOs.Windows ? "windows" : "linux")}, transfer: {m.Transfer.ToString().ToLowerInvariant()})"));

        if (m.Wake is { } wake)
        {
            const string Note = "Wake-on-LAN must also be enabled in the BIOS/UEFI and the network card driver, and usually only works on wired Ethernet (docs/WORKERS.md)";
            lines.Add(Check("wake", "ok", $"wake: magic packet to {wake.Broadcast}:{wake.Port} for {wake.Mac}, waits up to {wake.WaitSeconds} s. {Note}"));
        }
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

    /// <summary>doctor --remote's fix for a Windows ssh user that is denied WMI (only a suggestion: lanq works without it).</summary>
    internal const string WmiFix =
        "nothing is required (lanq uses fallbacks); for faster probes the machine's owner can grant the account WMI read: wmimgmt.msc, "
        + "WMI Control, Properties, Security, Root\\CIMV2, Security: Enable Account and Remote Enable for the user";

    private static async Task<List<DoctorCheck>> RemoteMachineChecks(Machine m, ISshClient ssh, Func<Machine, string> doctorScript, CancellationToken ct)
    {
        DoctorCheck Check(string name, string level, string detail, string? fix = null, string? capability = null) => new("remote", m.Name, name, level, detail, fix, capability);
        const string PathFix = "add the tool's folder to path_prepend for this machine";

        // Loading the config only checks the job script against the Windows command-line cap (lanq run must not be blocked by doctor);
        // the doctor script, which also carries a tool check per capability, is checked here and reported, never thrown.
        var script = doctorScript(m);
        if (m.Os == MachineOs.Windows && RemoteScripts.WindowsCommand(script).Length is var size && size > RemoteScripts.MaxWindowsCommandLength)
        {
            return [Check("script_size", "fail", $"not checked: with its env: ({m.Env.Count} entries), path_prepend: ({m.PathPrepend.Count} entries) and "
                + $"{m.Capabilities.Count(RemoteScripts.ToolChecks.ContainsKey)} tool checks, the doctor script is {size} characters once encoded, "
                + $"{size - RemoteScripts.MaxWindowsCommandLength} over the {RemoteScripts.MaxWindowsCommandLength}-character Windows command-line limit. "
                + "Shorten env: or path_prepend: for this machine", "shorten env: or path_prepend: for this machine")];
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
            return [Check("reachable", "fail", ex.Message)];
        }

        // A call that ran out of time still reports what arrived; only an ssh failure with nothing to show is "unreachable".
        var partial = r.TimedOut && r.Output.Contains("root=", StringComparison.Ordinal);
        if ((r.ExitCode == 255 || r.TimedOut) && !partial)
        {
            return [Check("reachable", "fail", "unreachable: " + (r.IsSshFailure && !r.TimedOut ? SshErrors.Explain(r.Output, m.Ssh) : SshErrors.FirstLine(r.Output)))];
        }

        var values = LanQueue.Health.HealthProbe.Parse(r.Output);
        var lines = new List<DoctorCheck>
        {
            values.GetValueOrDefault("root") switch
            {
                "ok" => Check("work_root_writable", "ok", $"work_root {m.WorkRoot} is writable"),
                "missing" => Check("work_root_writable", "warn", $"work_root {m.WorkRoot} does not exist yet (the first job creates it)"),
                _ => Check("work_root_writable", "fail", $"work_root {m.WorkRoot} is not writable by the ssh user", "give the ssh user write access to work_root, or change work_root"),
            },
            values.GetValueOrDefault("tool") == "ok"
                ? Check("transfer_tool", "ok", m.Transfer == TransferMode.Archive ? "tar (transfer: archive)" : "git (transfer: git)")
                : Check(
                    "transfer_tool",
                    "fail",
                    m.Transfer == TransferMode.Archive
                        ? "no usable tar for transfer: archive (see docs/WORKERS.md)"
                        : "git not found in a non-login ssh session (install it, or set transfer: archive)",
                    m.Transfer == TransferMode.Archive
                        ? "make tar usable in a non-login ssh session on this machine (see docs/WORKERS.md), or set transfer: git if git is installed"
                        : "install git, or set transfer: archive"),
        };
        if (m.Transfer == TransferMode.Git && GitVersion(values.GetValueOrDefault("git_version")) is { } git && git < new Version(2, 32))
        {
            lines.Add(Check(
                "git_version",
                "warn",
                $"git older than 2.32: global config not isolated (git {git} ignores GIT_CONFIG_GLOBAL, so a filter or "
                    + "attributes file in the ssh user's ~/.gitconfig can still run during a job's checkout; update git on that machine)",
                "update git on that machine to 2.32 or later"));
        }

        if (m.Os == MachineOs.Windows && await WmiDenied(m, ssh, ct).ConfigureAwait(false) is { } why)
        {
            lines.Add(Check(
                "wmi",
                "warn",
                $"WMI/CIM is not available to the ssh user ({why}); lanq uses its fallbacks for memory, load and stopping job trees",
                WmiFix));
        }

        foreach (var cap in m.Capabilities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!RemoteScripts.ToolChecks.TryGetValue(cap, out var commands))
            {
                lines.Add(Check("capability", "skip", $"{cap}: not checked (no known tool command)", capability: cap));
                continue;
            }

            var command = m.Os == MachineOs.Windows ? commands.Windows : commands.Linux;
            var raw = values.GetValueOrDefault("cap." + cap.ToLowerInvariant());
            var bar = raw?.IndexOf('|', StringComparison.Ordinal) ?? -1;
            var code = bar < 0 ? "?" : raw![..bar];
            var text = bar < 0 ? (partial ? "did not finish before the ssh call timed out" : "no answer") : raw![(bar + 1)..];
            if (code is not ("0" or "timeout" or "?") && RemoteScripts.FindableTools.Contains(cap)
                && await FindToolchain(m, ssh, cap, ct).ConfigureAwait(false) is { } search)
            {
                // A dotnet with runtimes only fails dotnet --version ("No .NET SDKs were found"): say so in plain words.
                var failed = search.Runtime is { } runtime ? $"dotnet {runtime} runtime only, no SDK" : text.Length > 0 ? text : "exit " + code;
                if (search.Found is { } found)
                {
                    lines.Add(Check(
                        "capability",
                        "fail",
                        $"{cap}: '{command}' failed in the job environment ({failed}), but {found.What} is installed in {found.Dir}, a folder the ssh session's PATH does not reach",
                        found.Fix,
                        cap));
                    continue;
                }

                text = failed;
            }

            lines.Add(code == "0"
                ? Check("capability", "ok", $"{cap}: {text}", capability: cap)
                : code == "timeout"
                ? Check("capability", "fail", $"{cap}: '{command}' timed out after {DoctorCheckSeconds} s in the job environment (stopped; the other checks went on)", PathFix, cap)
                : bar < 0 ? Check("capability", "fail", $"{cap}: {text}", PathFix, cap)
                : Check(
                    "capability",
                    "fail",
                    $"{cap}: '{command}' failed in the job environment ({(text.Length > 0 ? text : "exit " + code)}). "
                        + "If the tool is installed, a non-login ssh session does not see it: add its folder to path_prepend for this machine",
                    PathFix,
                    cap));
        }

        return lines;
    }

    /// <summary>
    /// After a failed tool check: searches the worker's usual and private install folders (<see cref="RemoteScripts.FindToolchain"/>, one more
    /// read-only ssh call). Found is the best folder (highest version) with the path_prepend: line to suggest, or null; Runtime the version of a
    /// runtime-only dotnet on PATH, or null. Null when the search could not run. lanq never edits the config itself.
    /// </summary>
    internal static async Task<((string Dir, string What, string Fix)? Found, string? Runtime)?> FindToolchain(Machine m, ISshClient ssh, string tool, CancellationToken ct)
    {
        SshResult r;
        try
        {
            r = await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.FindToolchain(m, tool)), null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }

        var runtime = HealthProbe.Parse(r.Output).GetValueOrDefault("runtime." + tool.ToLowerInvariant()) is { Length: > 0 and <= 40 } v && System.Version.TryParse(v.Split('-')[0], out _)
            ? JobRunner.SafeLine(v)
            : null;
        if (RemoteScripts.ParseFound(r.Output, tool) is not [var best, ..])
        {
            return (null, runtime);
        }

        var dir = JobRunner.SafeLine(best.Dir);
        var version = JobRunner.SafeLine(best.Version);
        var what = tool.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $".NET SDK {version}" : $"{tool} {version}";
        var fix = $"add `path_prepend: [{dir}]` to {m.Name} in machines.yml (found {what} there)"
            + (tool.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $", and `DOTNET_ROOT: {dir}` under its env: so the SDK's tools find the runtimes next to it" : "");
        return ((dir, what, fix), runtime);
    }

    /// <summary>Why the Windows ssh user may not use WMI/CIM (one more small ssh call), or null when it may (or the check did not answer).</summary>
    private static async Task<string?> WmiDenied(Machine m, ISshClient ssh, CancellationToken ct)
    {
        SshResult r;
        try
        {
            r = await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.WmiCheck), null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }

        return HealthProbe.Parse(r.Output).GetValueOrDefault("wmi") is { } wmi && wmi.StartsWith("fail", StringComparison.Ordinal)
            ? JobRunner.SafeLine(wmi.IndexOf('|', StringComparison.Ordinal) is var bar && bar >= 0 && wmi[(bar + 1)..].Trim() is { Length: > 0 } text ? text : "failed")
            : null;
    }
}
