using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Makeready;
using LanQueue.Output;
using LanQueue.Ssh;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>The lanq-makeready release embedded in this build (see scripts/sync-makeready.sh).</summary>
    internal const string MakereadyVersion = "0.1.0";

    internal static readonly TimeSpan MakereadyTimeout = TimeSpan.FromSeconds(120);

    internal const string WorkerUntouched = "lanq never changes a worker. Fixes are for the PC's owner to run on that PC.";

    /// <summary>Capabilities lanq-makeready can detect; any other configured name is reported as not checked.</summary>
    private static readonly string[] MakereadyKnownCapabilities = ["dotnet", "node", "python", "java", "go", "rust", "docker"];

    private static readonly string[] OsTags = ["windows", "linux"];

    // \z, not $: a report value with a trailing newline must not pass as a clean one.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.+-]{0,39}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CapabilityNameRegex();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]-([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HoursRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MachineNameRegex();

    /// <summary>The embedded script, byte for byte as released (pinned by a test).</summary>
    internal static byte[] MakereadyScript(MachineOs os)
    {
        using var stream = typeof(Cli).Assembly.GetManifestResourceStream(os == MachineOs.Windows ? "lanq-makeready.ps1" : "lanq-makeready.sh")
            ?? throw new InvalidOperationException("The embedded lanq-makeready script is missing from this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The script arrives on stdin and runs from memory; it prints only the JSON report (redacted) on stdout.</summary>
    internal static string MakereadyCommand(MachineOs os) => os == MachineOs.Windows
        ? "powershell -NoProfile -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact\""
        : "sh -s -- --json - --redact";

    /// <summary>
    /// lanq makeready &lt;machine&gt; | --ssh user@host --os windows|linux | --all: runs the read-only lanq-makeready check on a worker (or on
    /// every configured machine) and reads its report. One machine: exit 0 worker-ready, 1 not ready, 2 bad arguments, 70 ssh or an unusable
    /// report, 75 machine paused or outside its allowed hours (unless --force). --all: see <see cref="MakereadyAll"/>.
    /// </summary>
    internal static async Task<int> Makeready(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!p.Json)
        {
            return p.All
                ? await MakereadyAll(p, loadConfig, ssh, probe, stdout, stderr, ct).ConfigureAwait(false)
                : await MakereadyCore(p, loadConfig, ssh, probe, stdout, stderr, ct).ConfigureAwait(false);
        }

        // --json: stdout is exactly one object on every path: the validated report (exit 0 or 1), the --all summary, or
        // status/exitCode/message. The people-readable messages still go to stderr.
        var errors = new StringWriter();
        var body = new StringWriter();
        int code;
        try
        {
            code = p.All
                ? await MakereadyAll(p, loadConfig, ssh, probe, body, errors, ct).ConfigureAwait(false)
                : await MakereadyCore(p, loadConfig, ssh, probe, body, errors, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errors.WriteLine("cancelled (Ctrl+C)");
            body.GetStringBuilder().Clear();
            code = 130;
        }
        catch (Exception ex)
        {
            errors.WriteLine(JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"));
            body.GetStringBuilder().Clear();
            code = 70;
        }

        var text = errors.ToString();
        await stderr.WriteAsync(text).ConfigureAwait(false);
        if (body.GetStringBuilder().Length > 0)
        {
            await stdout.WriteAsync(body.ToString()).ConfigureAwait(false);
        }
        else
        {
            var message = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "failed";
            var status = code switch { 2 => "Rejected", 75 => "Held", 130 => "Cancelled", _ => "InfraError" };
            await WriteJsonError("makeready", status, code, message, stdout).ConfigureAwait(false);
        }

        return code;
    }

    /// <summary>
    /// The outcome of scanning one machine: <paramref name="Code"/> is what lanq makeready &lt;machine&gt; exits with (0 ready, 1 not ready,
    /// 70 unreachable or unusable report, 75 held), <paramref name="Message"/> the line it prints on stderr when there is no report, and
    /// <paramref name="Reason"/> the same without the machine's name (for the --all table).
    /// </summary>
    private sealed record MakereadyScan(int Code, MakereadyReport? Report, string? Message, string? Reason, bool Unusable = false);

    /// <summary>
    /// Scans one machine. With <paramref name="policy"/> (a configured machine) and without <paramref name="force"/>, a health probe first:
    /// an unreachable machine is not scanned, and a paused machine or one outside its allowed hours is held. The script goes on ssh stdin.
    /// </summary>
    private static async Task<MakereadyScan> ScanAsync(
        Machine machine, string name, PolicyConfig? policy, bool force, ISshClient ssh, HealthProbe probe, CancellationToken ct)
    {
        if (policy is not null && !force)
        {
            // A scan is light, but a family PC's PAUSE file and hours are respected by default.
            var h = await probe.ProbeAsync(machine, policy, ct).ConfigureAwait(false);
            if (!h.Reachable)
            {
                return new(70, null, $"{name}: unreachable: {h.Error}", h.Error ?? "unreachable");
            }

            var held = h.HoldKind switch
            {
                HoldKind.Paused => "paused (a PAUSE file is in its work_root)",
                HoldKind.OutsideHours => "outside allowed hours",
                _ => null,
            };
            if (held is not null)
            {
                return new(75, null, $"{name} is {held}; not scanning it now. Add --force to scan anyway.", $"{held}; --force scans anyway");
            }
        }

        SshResult r;
        try
        {
            r = await ssh.RunWithInputAsync(machine, MakereadyCommand(machine.Os), MakereadyScript(machine.Os), MakereadyReport.MaxChars, MakereadyTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new(70, null, ex.Message, ex.Message);
        }

        if (r.TimedOut || r.ExitCode == 255)
        {
            var why = JobRunner.SafeLine(r.TimedOut
                ? "timed out after " + ProcessSshClient.Describe(MakereadyTimeout)
                : "ssh failed: " + SshErrors.Explain(r.ErrorOutput, machine.Ssh));
            return new(70, null, JobRunner.SafeLine($"{name}: {why}"), why);
        }

        try
        {
            var report = r.OutputTruncated ? throw new FormatException("report larger than 1 MB") : MakereadyReport.Parse(r.Output);
            return new(report.WorkerReady ? 0 : 1, report, null, null);
        }
        catch (FormatException ex)
        {
            var detail = FirstLine(r.ErrorOutput);
            var why = JobRunner.SafeLine(
                $"unusable lanq-makeready report ({ex.Message}; script exit {r.ExitCode.ToString(CultureInfo.InvariantCulture)})"
                + (detail.Length > 0 ? ": " + detail : ""));
            return new(70, null, JobRunner.SafeLine($"{name}: {why}"), why, Unusable: true);
        }
    }

    private static async Task<int> MakereadyCore(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        Machine machine;
        Machine? configured = null;
        PolicyConfig? policy = null;
        if ((p.Target is null) == (p.SshTarget is null) || (p.SshTarget is null) != (p.Os is null))
        {
            await stderr.WriteLineAsync(MakereadyUsage).ConfigureAwait(false);
            return 2;
        }

        if (p.SshTarget is { } target)
        {
            if (!ConfigLoader.IsValidSshTarget(target))
            {
                await stderr.WriteLineAsync("--ssh must be user@host (no leading '-', no spaces).").ConfigureAwait(false);
                return 2;
            }

            machine = new Machine { Name = target, Os = p.Os!.Value, Ssh = target, WorkRoot = p.Os == MachineOs.Windows ? "C:/lq" : "/srv/lq" };
        }
        else
        {
            FleetConfig config;
            try
            {
                config = loadConfig();
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 2;
            }

            if (FindMachine(config, p.Target, "makeready", stderr) is not { } m)
            {
                return 2;
            }

            machine = configured = m;
            policy = config.Policy;
        }

        var name = configured?.Name ?? machine.Ssh;
        var scan = await ScanAsync(machine, name, policy, p.Force, ssh, probe, ct).ConfigureAwait(false);
        if (scan.Report is not { } report)
        {
            await stderr.WriteLineAsync(scan.Message).ConfigureAwait(false);
            return scan.Code;
        }

        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, MakereadyJson.Default.MakereadyReport)).ConfigureAwait(false);
        }
        else
        {
            foreach (var line in MakereadyText(report, name, configured, p.SshTarget))
            {
                await stdout.WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
            }
        }

        return scan.Code;
    }

    private const string MakereadyUsage =
        "Usage: lanq makeready <machine> | lanq makeready --ssh user@host --os windows|linux | lanq makeready --all [--json] [--force]";

    /// <summary>How many machines lanq makeready --all scans at once.</summary>
    internal const int MakereadyParallelism = 4;

    /// <summary>The longest top problem the --all table shows; --json carries it whole.</summary>
    private const int MakereadyProblemWidth = 84;

    /// <summary>
    /// lanq makeready --all: every machine in the config, <see cref="MakereadyParallelism"/> at a time, with the same rules as one machine
    /// (PAUSE and allowed_hours respected unless --force, the script on ssh stdin), then one summary table (or, with --json, one object).
    /// Exit codes: see <see cref="MakereadyAllExitCode"/>; 2 bad arguments or config; 130 Ctrl+C.
    /// </summary>
    private static async Task<int> MakereadyAll(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (p.Target is not null || p.SshTarget is not null || p.Os is not null)
        {
            await stderr.WriteLineAsync("lanq makeready --all checks every machine in the config; it takes no machine name and no --ssh or --os.")
                .ConfigureAwait(false);
            return 2;
        }

        FleetConfig config;
        try
        {
            config = loadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        using var gate = new SemaphoreSlim(MakereadyParallelism);
        async Task<MakereadyScan> Scanned(Machine m)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ScanAsync(m, m.Name, config.Policy, p.Force, ssh, probe, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        var scans = await Task.WhenAll(config.Machines.Select(Scanned)).ConfigureAwait(false);
        var rows = config.Machines.Zip(scans).Select(x => MakereadyRow(x.First, x.Second)).ToList();
        var code = MakereadyAllExitCode(rows);
        if (p.Json)
        {
            var status = code switch { 0 => "Ok", 1 => "Failing", 75 => "Held", _ => "InfraError" };
            await stdout.WriteLineAsync(JsonSerializer.Serialize(new MakereadyAllJson(status, code, rows), MakereadyAllJsonContext.Default.MakereadyAllJson))
                .ConfigureAwait(false);
            return code;
        }

        var nameWidth = Math.Max("MACHINE".Length, config.Machines.Max(m => m.Name.Length)) + 2;
        await stdout.WriteLineAsync(
            $"lanq makeready --all: {rows.Count} machine(s), read-only (lanq-makeready {MakereadyVersion}{(p.Force ? ", --force" : "")})").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"{"MACHINE".PadRight(nameWidth)}{"OS",-9}{"VERDICT",-13}TOP PROBLEM").ConfigureAwait(false);
        foreach (var r in rows)
        {
            var verdict = r.Verdict switch
            {
                "WARN" => $"WARN {r.Warnings}",
                "FAIL" => r.Failures > 0 ? $"FAIL {r.Failures}" : "FAIL",
                _ => r.Verdict,
            };
            var problem = Truncate(r.Problem ?? "", MakereadyProblemWidth);
            await stdout.WriteLineAsync(JobRunner.SafeLine($"{r.Machine.PadRight(nameWidth)}{r.Os,-9}{verdict,-13}{problem}").TrimEnd()).ConfigureAwait(false);
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync("Details, advice and a suggested machines.yml entry for one machine: lanq makeready <machine>").ConfigureAwait(false);
        await stdout.WriteLineAsync(WorkerUntouched).ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// One --all row. The verdict comes from the report's checks (any FAIL: FAIL with their count, else any WARN: WARN, else OK); a report
    /// whose transfer verdicts say not ready is FAIL even without a failing check. The top problem is the first FAIL check, else the first WARN.
    /// </summary>
    /// <summary>
    /// The --all exit code: 70 when any machine was unreachable or sent an unusable report (the check is incomplete); else 1 when any has a
    /// FAIL; else 75 when any was held; else 0. Warnings alone exit 0, as lanq makeready &lt;machine&gt; does; they stay in the table and JSON.
    /// </summary>
    private static int MakereadyAllExitCode(IReadOnlyCollection<MakereadyRowJson> rows) =>
        rows.Any(r => r.Verdict is "unreachable" or "error") ? 70
        : rows.Any(r => r.Verdict == "FAIL") ? 1
        : rows.Any(r => r.Verdict == "held") ? 75
        : 0;

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="width"/> UTF-16 units with "...", never inside a surrogate pair.</summary>
    internal static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        var kept = new System.Text.StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (kept.Length + rune.Utf16SequenceLength > width - 3)
            {
                break;
            }

            kept.Append(rune.ToString());
        }

        return kept.Append("...").ToString();
    }

    private static MakereadyRowJson MakereadyRow(Machine m, MakereadyScan scan)
    {
        var os = m.Os == MachineOs.Windows ? "windows" : "linux";
        if (scan.Report is not { } report)
        {
            var verdict = scan.Code == 75 ? "held" : scan.Unusable ? "error" : "unreachable";
            return new MakereadyRowJson(m.Name, os, verdict, 0, 0, scan.Code, scan.Reason is { } reason ? JobRunner.SafeLine(reason) : null, null);
        }

        var checks = report.Checks!;
        var failures = checks.Count(c => c.Status == "FAIL");
        var warnings = checks.Count(c => c.Status == "WARN");
        var top = checks.FirstOrDefault(c => c.Status == "FAIL") ?? checks.FirstOrDefault(c => c.Status == "WARN");
        var problem = top is not null
            ? JobRunner.SafeLine(string.IsNullOrWhiteSpace(top.Detail) ? top.Title ?? top.Id ?? "?" : $"{top.Title}: {top.Detail}")
            : report.WorkerReady ? null : "no transfer mode works (worker_git and worker_archive are FAIL)";
        var verdictText = failures > 0 || !report.WorkerReady ? "FAIL" : warnings > 0 ? "WARN" : "OK";
        return new MakereadyRowJson(m.Name, os, verdictText, warnings, failures, scan.Code, problem, report);
    }

    /// <summary>The human report. Every string from the worker is sanitised by the caller, line by line; nothing from it is executed.</summary>
    private static IEnumerable<string> MakereadyText(MakereadyReport report, string name, Machine? configured, string? sshTarget)
    {
        var v = report.Verdicts!;
        yield return $"lanq makeready {name} (lanq-makeready {report.Tool?.Version ?? "?"}, read-only; host {report.Host?.Os} {report.Host?.Arch})";
        yield return "";
        yield return "Verdicts";
        foreach (var (key, value) in new[] { ("worker_git", v.WorkerGit), ("worker_archive", v.WorkerArchive), ("dotnet10", v.Dotnet10), ("docker", v.Docker), ("overnight", v.Overnight) })
        {
            yield return $"  {key,-15} {value}";
        }

        var problems = report.Checks!.Where(c => c.Status is "WARN" or "FAIL").ToList();
        if (problems.Count > 0)
        {
            yield return "";
            yield return "Checks to look at";
            foreach (var c in problems)
            {
                yield return $"  [{c.Status,-4}] {c.Title}: {c.Detail}";
                if (!string.IsNullOrWhiteSpace(c.Advice))
                {
                    yield return $"         advice: {c.Advice}";
                }
            }
        }

        var detected = report.Capabilities!.Where(c => CapabilityNameRegex().IsMatch(c) && !OsTags.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (configured is not null)
        {
            var mine = configured.Capabilities.Select(c => c.ToLowerInvariant()).Where(c => !OsTags.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
            yield return "";
            yield return "Capabilities (machines.yml vs detected and working)";
            yield return "  detected but not configured: " + List(detected.Except(mine));
            yield return "  configured but not detected: " + List(mine.Where(c => MakereadyKnownCapabilities.Contains(c)).Except(detected));
            yield return "  configured, not checked by lanq-makeready: " + List(mine.Where(c => !MakereadyKnownCapabilities.Contains(c)));
        }

        yield return "";
        yield return "Suggested machines.yml entry (confirm every value):";
        foreach (var line in SuggestedEntry(report.SuggestedMachine!, configured, sshTarget))
        {
            yield return line;
        }

        yield return "";
        yield return report.WorkerReady ? "Ready: at least one transfer mode works." : "Not ready yet: fix the FAIL items above on that PC, then run lanq makeready again.";
        yield return WorkerUntouched;
    }

    private static string List(IEnumerable<string> items) => string.Join(", ", items) is { Length: > 0 } s ? s : "none";

    /// <summary>A machines.yml entry built from the report's suggestion; values that do not look right are left as placeholders.</summary>
    private static IEnumerable<string> SuggestedEntry(MakereadySuggestion s, Machine? configured, string? sshTarget)
    {
        if (s.Os is not ("windows" or "linux"))
        {
            yield return "# This OS cannot be a lanq worker (workers are Windows or Linux).";
            yield break;
        }

        var caps = (s.Capabilities ?? []).Where(c => c is not null && CapabilityNameRegex().IsMatch(c)).Distinct(StringComparer.Ordinal);
        var name = configured?.Name ?? (s.Name is { } n && MachineNameRegex().IsMatch(n) ? n : "old-pc");
        var ssh = configured?.Ssh ?? sshTarget;
        yield return "machines:";
        yield return $"  - name: {Yaml(name)}";
        yield return $"    os: {s.Os}";
        yield return ssh is not null ? $"    ssh: {Yaml(ssh)}" : $"    ssh: {Yaml(s.Ssh ?? "user@host.lan")}   # placeholder: confirm user and host name";
        yield return $"    work_root: {Yaml(configured?.WorkRoot ?? s.WorkRoot ?? "")}";
        yield return $"    capabilities: [{string.Join(", ", caps)}]";
        yield return $"    max_jobs: {Math.Clamp(s.MaxJobs, 1, 64).ToString(CultureInfo.InvariantCulture)}";
        yield return s.Transfer is "git" or "archive" ? $"    transfer: {s.Transfer}" : "    # transfer: no transfer mode works yet (needs git, or tar for archive)";
        if (s.AllowedHours is { } hours && HoursRegex().IsMatch(hours))
        {
            yield return $"    allowed_hours: {Yaml(hours)}";
        }
    }

    private static string Yaml(string value) =>
        "\"" + value.Replace(Backslash, Backslash + Backslash, StringComparison.Ordinal).Replace("\"", Backslash + "\"", StringComparison.Ordinal) + "\"";

    private static readonly string Backslash = ((char)92).ToString();
}
