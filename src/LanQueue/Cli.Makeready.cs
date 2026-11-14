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
    internal const string MakereadyVersion = "0.5.0";

    internal static readonly TimeSpan MakereadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long the tune scan (lanq makeready --tune) may take; the script itself stops probing after about 90 seconds.</summary>
    internal static readonly TimeSpan TuneTimeout = TimeSpan.FromSeconds(150);

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

    // A toolchain folder the script reports (0.3.0+): absolute, forward slashes, printable ASCII only (no Unicode line or bidi characters),
    // and none of the characters ConfigLoader refuses in path_prepend or that would mark a redacted value: quote, backslash, < > | ? * ; :.
    [GeneratedRegex(@"^[A-Za-z]:/[\x20-\x7e-[""<>|?*;:\\]]{0,240}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WindowsToolDirRegex();

    [GeneratedRegex(@"^/[\x20-\x7e-[""<>|?*;:\\]]{0,240}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LinuxToolDirRegex();

    // The values lanq puts on a worker's command line (-Hours, -WorkRoot): validated again right before use, and single-quoted.
    [GeneratedRegex(@"^[A-Za-z0-9_./:-]{1,255}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RemoteArgRegex();

    // lanq-makeready-tune's own work_root rules (0.5.0), stricter than lanq's: Windows refuses "C:/", "C://x" and '.' parts; sh caps at 255.
    [GeneratedRegex(@"^[A-Za-z]:/[A-Za-z0-9_.-][A-Za-z0-9_./-]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TuneWindowsWorkRootRegex();

    [GeneratedRegex(@"^/[A-Za-z0-9_./-]{0,254}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TuneLinuxWorkRootRegex();

    [GeneratedRegex(@"(^|/)\.(/|\z)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DotPartRegex();

    /// <summary>An embedded lanq-makeready file, byte for byte as released (pinned by a test).</summary>
    internal static byte[] EmbeddedScript(string name)
    {
        using var stream = typeof(Cli).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded {name} is missing from this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The embedded scanner (lanq-makeready), byte for byte as released.</summary>
    internal static byte[] MakereadyScript(MachineOs os) => EmbeddedScript(os == MachineOs.Windows ? "lanq-makeready.ps1" : "lanq-makeready.sh");

    /// <summary>The embedded read-only tune scan (lanq-makeready-tune, 0.5.0+), byte for byte as released.</summary>
    internal static byte[] TuneScript(MachineOs os) => EmbeddedScript(os == MachineOs.Windows ? "lanq-makeready-tune.ps1" : "lanq-makeready-tune.sh");

    private const string StdinScriptBlock = "& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact";

    /// <summary>
    /// The script arrives on stdin and runs from memory; it prints only the JSON report (redacted) on stdout. <paramref name="hours"/> (0.12.0:
    /// a configured machine's one allowed_hours window, see <see cref="ScanHours"/>) is passed as -Hours / --hours.
    /// </summary>
    internal static string MakereadyCommand(MachineOs os, string? hours = null) => StdinCommand(os, [("Hours", "hours", hours)]);

    /// <summary>lanq makeready --tune: the tune script, the same way as the scanner, with -Hours and -WorkRoot when lanq passes them.</summary>
    internal static string TuneCommand(MachineOs os, string? hours, string? workRoot) =>
        StdinCommand(os, [("Hours", "hours", hours), ("WorkRoot", "work-root", workRoot)]);

    private static string StdinCommand(MachineOs os, (string Ps, string Sh, string? Value)[] options)
    {
        var extra = new System.Text.StringBuilder();
        foreach (var (ps, sh, value) in options)
        {
            if (value is null)
            {
                continue;
            }

            // Validated where it is chosen; checked once more here, so nothing else can ever reach a worker's command line.
            if (!RemoteArgRegex().IsMatch(value))
            {
                throw new ArgumentException($"refusing to pass -{ps}: unexpected characters");
            }

            extra.Append(os == MachineOs.Windows ? $" -{ps} '{value}'" : $" --{sh} '{value}'");
        }

        return os == MachineOs.Windows
            ? $"powershell -NoProfile -NonInteractive -Command \"{StdinScriptBlock}{extra}\""
            : $"sh -s -- --json - --redact{extra}";
    }

    /// <summary>The longest line of ssh's or the worker's stderr lanq quotes in a scan or tune error.</summary>
    internal const int StderrLineMax = 256;

    /// <summary>The longest whole error text (lanq's own guidance, such as the known_hosts steps, plus at most one quoted line).</summary>
    internal const int ErrorTextMax = 1024;

    /// <summary>An error line for the report, the --all table and lanqTune: sanitised, then cut to <see cref="ErrorTextMax"/>.</summary>
    internal static string ErrorText(string text) => Truncate(JobRunner.SafeLine(text), ErrorTextMax);

    /// <summary>stderr as lanq may quote it: at most 64 lines, each sanitised and cut to <see cref="StderrLineMax"/>.</summary>
    internal static string QuotableStderr(string stderr) => string.Join(
        '\n',
        stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(64).Select(l => Truncate(JobRunner.SafeLine(l), StderrLineMax)));

    /// <summary>
    /// The -Hours lanq passes for a configured machine (0.12.0), normalised HH:mm-HH:mm from the parsed entries; null without allowed_hours or
    /// with different windows. For the scanner (<paramref name="dropDays"/> false) only when no entry has days, so the allowed_hours it
    /// suggests can never be wider than the configured one; for the tune scan (true) the days are dropped, as it only compares schedules
    /// with the window's times.
    /// </summary>
    internal static string? ScanHours(Machine m, bool dropDays)
    {
        string? common = null;
        foreach (var entry in m.AllowedHours ?? [])
        {
            if (!HoursWindow.TryParse(entry, out var w) || w.IsSingleInstant || (!dropDays && w.HasDays) || (common is not null && common != w.TimeText))
            {
                return null;
            }

            common = w.TimeText;
        }

        return common is not null && HoursRegex().IsMatch(common) ? common : null;
    }

    /// <summary>
    /// The -WorkRoot lanq passes to the tune scan for a configured machine: its work_root, only when it passes both lanq's rules (ConfigLoader)
    /// and lanq-makeready-tune's stricter ones; else null (the tune scan then skips its work_root checks).
    /// </summary>
    internal static string? TuneWorkRoot(Machine m)
    {
        var w = m.WorkRoot;
        var ok = m.Os == MachineOs.Windows
            ? TuneWindowsWorkRootRegex().IsMatch(w) && !DotPartRegex().IsMatch(w)
            : TuneLinuxWorkRootRegex().IsMatch(w);
        return ok && !w.Contains("..", StringComparison.Ordinal) && RemoteArgRegex().IsMatch(w) ? w : null;
    }

    /// <summary>
    /// lanq makeready &lt;machine&gt; | --ssh user@host --os windows|linux | --all: runs the read-only lanq-makeready check on a worker (or on
    /// every configured machine) and reads its report. One machine: exit 0 worker-ready, 1 not ready, 2 bad arguments, 70 ssh or an unusable
    /// report, 75 machine paused or outside its allowed hours (unless --force). --all: see <see cref="MakereadyAll"/>.
    /// </summary>
    /// <param name="host">This computer's environment, ssh-agent and terminal for --setup and --add (tests replace it); null: the real one.</param>
    internal static async Task<int> Makeready(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        MakereadyHost? host = null)
    {
        if (p.Setup)
        {
            return await MakereadySetup(p, host ?? MakereadyHost.Real(stderr), stdout, stderr).ConfigureAwait(false);
        }

        if (p.Add)
        {
            return await MakereadyAdd(p, host ?? MakereadyHost.Real(stderr), ssh, stdout, stderr, ct).ConfigureAwait(false);
        }

        var failed = new MakereadyFailed();
        var again = SuggestedCommand.Makeready(p.Target, p.ConfigPath, p.SshTarget, p.Os, p.KeyEnv);
        if (!p.Json)
        {
            var result = p.All
                ? await MakereadyAll(p, loadConfig, ssh, probe, stdout, stderr, ct, failed).ConfigureAwait(false)
                : await MakereadyCore(p, loadConfig, ssh, probe, stdout, stderr, ct, failed).ConfigureAwait(false);
            if (failed.Kind is { } kind)
            {
                await NextSteps.WriteAsync(stderr, NextSteps.ForMakereadyError(kind, failed.Message, again, p.ConfigPath, SetupStep(p, failed), p.Json)).ConfigureAwait(false);
            }

            return result;
        }

        // --json: stdout is exactly one object on every path: the validated report (exit 0 or 1), the --all summary, or
        // status/exitCode/message. The people-readable messages still go to stderr.
        var errors = new StringWriter();
        var body = new StringWriter();
        int code;
        try
        {
            code = p.All
                ? await MakereadyAll(p, loadConfig, ssh, probe, body, errors, ct, failed).ConfigureAwait(false)
                : await MakereadyCore(p, loadConfig, ssh, probe, body, errors, ct, failed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errors.WriteLine("cancelled (Ctrl+C)");
            body.GetStringBuilder().Clear();
            code = 130;
            failed.Set(NextSteps.MakereadyFailure.Cancelled);
        }
        catch (Exception ex)
        {
            errors.WriteLine(JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"));
            body.GetStringBuilder().Clear();
            code = 70;
            failed.Set(NextSteps.MakereadyFailure.Unexpected);
        }

        var text = errors.ToString();
        await stderr.WriteAsync(text).ConfigureAwait(false);
        if (body.GetStringBuilder().Length > 0)
        {
            await stdout.WriteAsync(body.ToString()).ConfigureAwait(false);
        }
        else
        {
            // The single machine's report stays the script's lanq-makeready/v1 report exactly; only this error object carries nextSteps.
            var message = LastLine(text);
            var status = code switch { 2 => "Rejected", 75 => "Held", 130 => "Cancelled", _ => "InfraError" };
            var kind = failed.Kind ?? code switch
            {
                2 => NextSteps.MakereadyFailure.Arguments,
                75 => NextSteps.MakereadyFailure.Held,
                130 => NextSteps.MakereadyFailure.Cancelled,
                _ => NextSteps.MakereadyFailure.Unexpected,
            };
            var steps = NextSteps.ForMakereadyError(kind, message, again, p.ConfigPath, SetupStep(p, failed), p.Json);
            await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
            await WriteJsonError("makeready", status, code, message, stdout, steps).ConfigureAwait(false);
        }

        return code;

        static string LastLine(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "failed";
    }

    /// <summary>lanq makeready --ssh ... --setup as a next step, when the worker refused the key or its host key is unknown (never for a configured machine).</summary>
    private static SuggestedCommand? SetupStep(ParsedArgs p, MakereadyFailed failed) =>
        failed.SetupWouldHelp && p.SshTarget is { } target && p.Os is { } os ? SuggestedCommand.Setup(target, os, p.KeyEnv) : null;

    /// <summary>Why lanq makeready ended without a report (set where that happens; null when there was a report or the --all table).</summary>
    private sealed class MakereadyFailed
    {
        public NextSteps.MakereadyFailure? Kind { get; private set; }

        /// <summary>ssh failed because the key was refused or the host key is unknown (see <see cref="SshErrors.SetupWouldHelp"/>).</summary>
        public bool SetupWouldHelp { get; set; }

        /// <summary>The line lanq printed about it (sanitised).</summary>
        public string Message { get; private set; } = "";

        public void Set(NextSteps.MakereadyFailure kind, string? message = null)
        {
            Kind = kind;
            Message = JobRunner.SafeLine(message ?? "");
        }
    }

    /// <summary>
    /// The outcome of scanning one machine: <paramref name="Code"/> is what lanq makeready &lt;machine&gt; exits with (0 ready, 1 not ready,
    /// 70 unreachable or unusable report, 75 held), <paramref name="Message"/> the line it prints on stderr when there is no report, and
    /// <paramref name="Reason"/> the same without the machine's name (for the --all table).
    /// </summary>
    private sealed record MakereadyScan(int Code, MakereadyReport? Report, string? Message, string? Reason, bool Unusable = false, bool SetupWouldHelp = false)
    {
        /// <summary>lanq makeready --tune: the tune scan's outcome (only after a report); null without --tune or without a report.</summary>
        public MakereadyTuneJson? Tune { get; init; }
    }

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
            // 0.12.0: a configured machine's one every-day window goes to the scanner as -Hours (it then suggests that allowed_hours and
            // compares Windows Update's active hours with it); day-limited windows and an ad-hoc PC (no policy) get none.
            var hours = policy is null ? null : ScanHours(machine, dropDays: false);
            r = await ssh.RunWithInputAsync(machine, MakereadyCommand(machine.Os, hours), MakereadyScript(machine.Os), MakereadyReport.MaxChars, MakereadyTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new(70, null, ex.Message, ex.Message);
        }

        if (r.TimedOut || r.ExitCode == 255)
        {
            var why = ErrorText(r.TimedOut
                ? "timed out after " + ProcessSshClient.Describe(MakereadyTimeout)
                : "ssh failed: " + SshErrors.Explain(QuotableStderr(r.ErrorOutput), machine.Ssh));
            return new(70, null, JobRunner.SafeLine($"{name}: {why}"), why, SetupWouldHelp: !r.TimedOut && SshErrors.SetupWouldHelp(r.ErrorOutput));
        }

        try
        {
            var report = r.OutputTruncated ? throw new FormatException("report larger than 1 MB") : MakereadyReport.Parse(r.Output);
            return new(report.WorkerReady ? 0 : 1, report, null, null);
        }
        catch (FormatException ex)
        {
            var detail = FirstLine(QuotableStderr(r.ErrorOutput));
            var why = ErrorText(
                $"unusable lanq-makeready report ({ex.Message}; script exit {r.ExitCode.ToString(CultureInfo.InvariantCulture)})"
                + (detail.Length > 0 ? ": " + detail : ""));
            return new(70, null, JobRunner.SafeLine($"{name}: {why}"), why, Unusable: true);
        }
    }

    private static async Task<int> MakereadyCore(
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct, MakereadyFailed failed)
    {
        Machine machine;
        Machine? configured = null;
        PolicyConfig? policy = null;
        if ((p.Target is null) == (p.SshTarget is null) || (p.SshTarget is null) != (p.Os is null))
        {
            await stderr.WriteLineAsync(MakereadyUsage).ConfigureAwait(false);
            failed.Set(NextSteps.MakereadyFailure.Arguments, MakereadyUsage);
            return 2;
        }

        if (p.SshTarget is { } target)
        {
            if (!ConfigLoader.IsValidSshTarget(target))
            {
                const string BadTarget = "--ssh must be user@host (no leading '-', no spaces).";
                await stderr.WriteLineAsync(BadTarget).ConfigureAwait(false);
                failed.Set(NextSteps.MakereadyFailure.Arguments, BadTarget);
                return 2;
            }

            machine = AdHocMachine(target, p.Os!.Value, p.KeyEnv);
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
                failed.Set(NextSteps.MakereadyFailure.Config, ex.Message);
                return 2;
            }

            if (FindMachine(config, p.Target, "makeready", stderr) is not { } m)
            {
                failed.Set(NextSteps.MakereadyFailure.Arguments, "name a machine in the config: " + string.Join(", ", config.Machines.Select(x => x.Name)));
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
            failed.Set(
                scan.Code == 75 ? NextSteps.MakereadyFailure.Held : scan.Unusable ? NextSteps.MakereadyFailure.Unusable : NextSteps.MakereadyFailure.Unreachable,
                scan.Message);
            failed.SetupWouldHelp = scan.SetupWouldHelp;
            return scan.Code;
        }

        // --tune: only after a report; never changes the exit code (a failure is lanqTune.status).
        var tune = p.Tune
            ? await TuneAsync(machine, configured is not null, SuggestedCommand.Makeready(p.Target, p.ConfigPath, p.SshTarget, p.Os, p.KeyEnv, tune: true), ssh, ct)
                .ConfigureAwait(false)
            : null;
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(ReportForJson(report, tune), MakereadyJson.Default.MakereadyReportJson)).ConfigureAwait(false);
        }
        else
        {
            foreach (var line in MakereadyText(report, name, configured, p.SshTarget, keyEnv: p.KeyEnv).Concat(tune is null ? [] : TuneText(tune, machine.Os)))
            {
                await stdout.WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
            }
        }

        return scan.Code;
    }

    /// <summary>A PC that is not in the config yet (lanq makeready --ssh): its key from ssh-agent, or from --key-env (as key_env).</summary>
    private static Machine AdHocMachine(string target, MachineOs os, string? keyEnv) =>
        new() { Name = target, Os = os, Ssh = target, KeyEnv = keyEnv, WorkRoot = os == MachineOs.Windows ? "C:/lq" : "/srv/lq" };

    private const string MakereadyUsage =
        "Usage: lanq makeready <machine> [--tune] | lanq makeready --ssh user@host --os windows|linux [--key-env VAR] [--tune | --setup | --add --name NAME [--yes]] "
        + "| lanq makeready --all [--json] [--force] [--tune]";

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
        ParsedArgs p, Func<FleetConfig> loadConfig, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct, MakereadyFailed failed)
    {
        if (p.Target is not null || p.SshTarget is not null || p.Os is not null)
        {
            const string AllTakesNoMachine = "lanq makeready --all checks every machine in the config; it takes no machine name and no --ssh or --os.";
            await stderr.WriteLineAsync(AllTakesNoMachine).ConfigureAwait(false);
            failed.Set(NextSteps.MakereadyFailure.Arguments, AllTakesNoMachine);
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
            failed.Set(NextSteps.MakereadyFailure.Config, ex.Message);
            return 2;
        }

        using var gate = new SemaphoreSlim(MakereadyParallelism);
        async Task<MakereadyScan> Scanned(Machine m)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var scan = await ScanAsync(m, m.Name, config.Policy, p.Force, ssh, probe, ct).ConfigureAwait(false);
                if (!p.Tune || scan.Report is null)
                {
                    return scan;
                }

                // --all --tune: in the same slot, right after the machine's own scan (at most 120 s + 150 s per machine).
                var again = SuggestedCommand.Makeready(m.Name, p.ConfigPath, tune: true);
                return scan with { Tune = await TuneAsync(m, configured: true, again, ssh, ct).ConfigureAwait(false) };
            }
            finally
            {
                gate.Release();
            }
        }

        var scans = await Task.WhenAll(config.Machines.Select(Scanned)).ConfigureAwait(false);
        var rows = config.Machines.Zip(scans).Select(x => MakereadyRow(x.First, x.Second)).ToList();
        rows = [.. rows.Select(r => r with { NextSteps = NextSteps.ForMakereadyRow(r.Machine, r.Verdict, r.Problem, p.ConfigPath, p.Json) })];
        var code = MakereadyAllExitCode(rows);
        var status = code switch { 0 => "Ok", 1 => "Failing", 75 => "Held", _ => "InfraError" };
        var next = NextSteps.ForMakereadyAll(status, rows, p.ConfigPath, p.Json);
        await NextSteps.WriteAsync(stderr, next).ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(new MakereadyAllJson(status, code, rows) { NextSteps = next }, MakereadyAllJsonContext.Default.MakereadyAllJson))
                .ConfigureAwait(false);
            return code;
        }

        var nameWidth = Math.Max("MACHINE".Length, config.Machines.Max(m => m.Name.Length)) + 2;
        await stdout.WriteLineAsync(
            $"lanq makeready --all: {rows.Count} machine(s), read-only (lanq-makeready {MakereadyVersion}{(p.Force ? ", --force" : "")})").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"{"MACHINE".PadRight(nameWidth)}{"OS",-9}{"VERDICT",-13}{(p.Tune ? $"{"TUNE",-16}" : "")}TOP PROBLEM").ConfigureAwait(false);
        foreach (var (r, scan) in rows.Zip(scans))
        {
            var verdict = r.Verdict switch
            {
                "WARN" => $"WARN {r.Warnings}",
                "FAIL" => r.Failures > 0 ? $"FAIL {r.Failures}" : "FAIL",
                _ => r.Verdict,
            };
            var problem = Truncate(r.Problem ?? "", MakereadyProblemWidth);
            var tuneCell = p.Tune ? $"{TuneCell(scan.Tune),-16}" : "";
            await stdout.WriteLineAsync(JobRunner.SafeLine($"{r.Machine.PadRight(nameWidth)}{r.Os,-9}{verdict,-13}{tuneCell}{problem}").TrimEnd()).ConfigureAwait(false);
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync("Details, advice and a suggested machines.yml entry for one machine: lanq makeready <machine>").ConfigureAwait(false);
        if (p.Tune)
        {
            await stdout.WriteLineAsync("Tune items and their advice for one machine: lanq makeready <machine> --tune (TUNE: WARN and INFO counts)").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync(WorkerUntouched).ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// The --all exit code: 70 when any machine was unreachable or sent an unusable report (the check is incomplete); else 1 when any is NOT
    /// READY (FAIL: lanq makeready &lt;machine&gt; exits 1 for it); else 75 when any was held; else 0. Warnings alone, including FAIL checks
    /// that do not stop the PC being a worker (docker, dotnet), exit 0 as lanq makeready &lt;machine&gt; does; they stay in the table and JSON.
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

    /// <summary>
    /// One --all row. FAIL only when the machine is not worker-ready (no transfer mode works: the same rule as the single-machine exit code
    /// and the script's NOT READY); otherwise WARN when anything is left to look at, else OK. With the script's summary (0.2.0+), the counts
    /// and the top problem (its first next step, blockers first) come from it; a summary that contradicts the transfer verdicts is ignored.
    /// Without one (0.1), every FAIL check of a not-ready machine counts as a failure, and on a worker-ready machine every WARN or FAIL check
    /// counts as a warning. <see cref="MakereadyRowJson.Failures"/> is the blocker count, <see cref="MakereadyRowJson.Warnings"/> the rest.
    /// </summary>
    private static MakereadyRowJson MakereadyRow(Machine m, MakereadyScan scan)
    {
        var os = m.Os == MachineOs.Windows ? "windows" : "linux";
        if (scan.Report is not { } report)
        {
            var verdict = scan.Code == 75 ? "held" : scan.Unusable ? "error" : "unreachable";
            return new MakereadyRowJson(m.Name, os, verdict, 0, 0, scan.Code, scan.Reason is { } reason ? JobRunner.SafeLine(reason) : null, null);
        }

        var ready = report.WorkerReady;
        int failures, warnings;
        string? title, detail;
        if (report.Summary is { } s && (s.Verdict == "NOT_READY") != ready)
        {
            failures = ready ? 0 : s.Blockers;
            warnings = s.Warnings;
            var step = s.NextSteps!.FirstOrDefault();
            (title, detail) = (step?.Title ?? step?.Id, step?.Detail);
        }
        else
        {
            var problems = report.Checks!.Where(c => c.Status is "WARN" or "FAIL").ToList();
            failures = ready ? 0 : problems.Count(c => c.Status == "FAIL");
            warnings = problems.Count - failures;
            var top = problems.FirstOrDefault(c => c.Status == "FAIL") ?? problems.FirstOrDefault();
            (title, detail) = (top?.Title ?? top?.Id, top?.Detail);
        }

        var problem = title is not null || !string.IsNullOrWhiteSpace(detail)
            ? JobRunner.SafeLine(string.IsNullOrWhiteSpace(detail) ? title ?? "?" : $"{title}: {detail}")
            : ready ? null : "no transfer mode works (worker_git and worker_archive are FAIL)";
        var verdictText = !ready ? "FAIL" : warnings > 0 ? "WARN" : "OK";
        return new MakereadyRowJson(m.Name, os, verdictText, warnings, failures, scan.Code, problem, ReportForJson(report, scan.Tune));
    }

    /// <summary>The human report. Every string from the worker is sanitised by the caller, line by line; nothing from it is executed.</summary>
    /// <param name="entryName">The entry's name (lanq makeready --add --name); null: the configured name, else the report's suggestion.</param>
    /// <param name="keyEnv">--key-env, written into the entry as key_env.</param>
    /// <param name="withEntry">False for --add, which shows the exact lines it writes instead.</param>
    private static IEnumerable<string> MakereadyText(
        MakereadyReport report, string name, Machine? configured, string? sshTarget, string? entryName = null, string? keyEnv = null, bool withEntry = true)
    {
        var v = report.Verdicts!;
        yield return $"lanq makeready {name} (lanq-makeready {report.Tool?.Version ?? "?"}, read-only; host {report.Host?.Os} {report.Host?.Arch})";
        yield return "";
        yield return "Verdicts";
        foreach (var (key, value) in new[] { ("worker_git", v.WorkerGit), ("worker_archive", v.WorkerArchive), ("dotnet10", v.Dotnet10), ("docker", v.Docker), ("overnight", v.Overnight) })
        {
            yield return $"  {key,-15} {value}";
        }

        if (report.Summary is { } summary)
        {
            // The script's own verdict line and numbered next steps, worded as the script prints them (0.2.0+).
            yield return "";
            yield return $"VERDICT: {summary.Text}";
            if (summary.NextSteps!.Count > 0)
            {
                yield return "Next steps (blockers first; nothing here is run for you):";
                var linux = report.SuggestedMachine!.Os == "linux";
                foreach (var st in summary.NextSteps)
                {
                    var who = (st.AdminToFix, linux) switch
                    {
                        (true, true) => "needs root",
                        (false, true) => "no root needed",
                        (true, false) => "needs admin: elevated PowerShell",
                        (false, false) => "no admin needed",
                    };
                    yield return $"{st.Step.ToString(CultureInfo.InvariantCulture),2}. [{(st.Blocker ? "MUST FIX" : "should fix")}] {st.Title}: {st.Detail}";
                    yield return $"    do ({who}): {(string.IsNullOrEmpty(st.Action) ? "see the detail above" : st.Action)}";
                }
            }
        }

        if (report.SuitedFor is { } suited)
        {
            if (report.Summary is null)
            {
                yield return "";
            }

            yield return $"Suited for: {suited.Text}";
        }

        var problems = report.Checks!.Where(c => c.Status is "WARN" or "FAIL").ToList();
        if (report.Summary is null && problems.Count > 0)
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

        if (withEntry)
        {
            yield return "";
            yield return "Suggested machines.yml entry (confirm every value):";
            foreach (var line in SuggestedEntry(report.SuggestedMachine!, configured, sshTarget, entryName, keyEnv))
            {
                yield return line;
            }
        }

        if (FixScriptLine(report) is { } fix)
        {
            yield return "";
            yield return fix;
        }

        yield return "";
        yield return report.WorkerReady ? "Ready: at least one transfer mode works."
            : report.Summary is not null ? "Not ready yet: fix the MUST FIX steps above on that PC, then run lanq makeready again."
            : "Not ready yet: fix the FAIL items above on that PC, then run lanq makeready again.";
        yield return WorkerUntouched;
    }

    /// <summary>
    /// One line pointing the PC's owner at lanq-makeready-fix (lanq-makeready 0.4.0+) when a WARN/FAIL check may be one its fix list covers
    /// (Windows: ssh.start, ssh.auto, power.sleep, longpaths.on, git.longpaths, ssh.aclkeys; Linux: ssh.start, power.sleep), else null. Only
    /// "may": the fix script has its own preconditions (systemd, where git is installed, the file's owner, ...), so its dry run says what it
    /// can do. lanq never embeds, sends or runs the fix script: it refuses to run over SSH and asks before each change, at that PC.
    /// </summary>
    internal static string? FixScriptLine(MakereadyReport report)
    {
        var os = report.SuggestedMachine?.Os;
        var ids = (report.Checks ?? [])
            .Where(c => c.Status is "WARN" or "FAIL")
            .Select(c => FixScriptFix(os, c.Id, c.Detail ?? ""))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var dryRun = os == "windows" ? "lanq-makeready-fix.ps1 -WhatIf" : "sh lanq-makeready-fix.sh --dry-run";
        return ids.Count == 0 ? null
            : $"May be fixable at that PC (lanq-makeready-fix: {string.Join(", ", ids)}): its owner can run {dryRun} there to see what it can do, "
                + "local and interactive only (see lanq-makeready's README). lanq never runs it.";
    }

    /// <summary>The lanq-makeready-fix 0.4.0 fix id that may fix a WARN/FAIL scanner check (its fix table, mapped to the scanner's check ids), or null.</summary>
    private static string? FixScriptFix(string? os, string? id, string detail) => (os, id) switch
    {
        ("windows", "ssh.server") when detail.Contains("not running", StringComparison.Ordinal) => "ssh.start",
        ("windows", "ssh.server") when detail.StartsWith("running, start type", StringComparison.Ordinal) => "ssh.auto",
        ("windows", "power.sleep") => "power.sleep",
        ("windows", "longpaths") => "longpaths.on",
        ("windows", "git") when detail.Contains("core.longpaths", StringComparison.Ordinal) => "git.longpaths", // not git.install
        ("windows", "ssh.keys") when detail.Contains("too-open permissions", StringComparison.Ordinal) => "ssh.aclkeys", // not ssh.nokeys
        ("linux", "ssh.server") when detail.Contains("not running", StringComparison.Ordinal) => "ssh.start",
        ("linux", "power.sleep") => "power.sleep",
        _ => null,
    };

    private static string List(IEnumerable<string> items) => string.Join(", ", items) is { Length: > 0 } s ? s : "none";

    /// <summary>A machines.yml entry built from the report's suggestion: the <c>machines:</c> header, then <see cref="SuggestedItem"/>.</summary>
    private static IEnumerable<string> SuggestedEntry(MakereadySuggestion s, Machine? configured, string? sshTarget, string? entryName = null, string? keyEnv = null) =>
        SuggestedItem(s, configured, sshTarget, entryName, keyEnv) is { } item
            ? ["machines:", .. item]
            : ["# This OS cannot be a lanq worker (workers are Windows or Linux)."];

    /// <summary>
    /// One machine's lines (the dash at column 2, its keys at column 4), or null when the report's OS cannot be a worker. Values that do not
    /// look right are left as placeholders. lanq makeready --add writes exactly these lines (re-indented to the file's own items).
    /// </summary>
    private static List<string>? SuggestedItem(MakereadySuggestion s, Machine? configured, string? sshTarget, string? entryName, string? keyEnv)
    {
        if (s.Os is not ("windows" or "linux"))
        {
            return null;
        }

        var caps = (s.Capabilities ?? []).Where(c => c is not null && CapabilityNameRegex().IsMatch(c)).Distinct(StringComparer.Ordinal);
        var name = entryName ?? configured?.Name ?? (s.Name is { } n && MachineNameRegex().IsMatch(n) ? n : "old-pc");
        var ssh = configured?.Ssh ?? sshTarget;
        List<string> lines = [];
        lines.Add($"  - name: {Yaml(name)}");
        lines.Add($"    os: {s.Os}");
        lines.Add(ssh is not null ? $"    ssh: {Yaml(ssh)}" : $"    ssh: {Yaml(s.Ssh ?? "user@host.lan")}   # placeholder: confirm user and host name");
        if (keyEnv is not null)
        {
            lines.Add($"    key_env: {keyEnv}   # NAME of the environment variable holding your key file's path");
        }

        lines.Add($"    work_root: {Yaml(configured?.WorkRoot ?? s.WorkRoot ?? "")}");
        lines.Add($"    capabilities: [{string.Join(", ", caps)}]");
        lines.Add($"    max_jobs: {Math.Clamp(s.MaxJobs, 1, 64).ToString(CultureInfo.InvariantCulture)}");
        if (s.MinFreeMemGb is { } minFree && double.IsFinite(minFree) && minFree is >= 0 and <= 1024)
        {
            lines.Add($"    min_free_mem_gb: {minFree.ToString("0.##", CultureInfo.InvariantCulture)}   # LanQueue starts no new job while less memory than this is free");
        }

        lines.Add(s.Transfer is "git" or "archive" ? $"    transfer: {s.Transfer}" : "    # transfer: no transfer mode works yet (needs git, or tar for archive)");
        if (s.AllowedHours is { } hours && HoursRegex().IsMatch(hours))
        {
            lines.Add($"    allowed_hours: {Yaml(hours)}");
        }

        // Only when the script's Wake-on-LAN check passed; commented, so waking stays the owner's choice. A redacted MAC stays "<mac>",
        // which ConfigLoader refuses until the real one is filled in.
        if (s.Wake is { } wake)
        {
            lines.Add($"    # wake:   # Wake-on-LAN looks ready; uncomment to let lanq wake this PC (test: lanq wake {name} --wait)");
            lines.Add(MagicPacket.TryParseMac(wake.Mac, out _)
                ? $"    #   mac: {Yaml(wake.Mac!)}"
                : "    #   mac: \"<mac>\"   # placeholder: this PC's wired MAC address");
        }

        // The newest .NET SDK sits outside the PATH (a private folder such as D:/build/dotnet): only exactly one well-formed folder with
        // DOTNET_ROOT set to the same folder is taken. A redacted folder (one under the user's home) is not usable, so it is left out and
        // lanq makeready --add looks for the SDK itself.
        if (SuggestedDotnetDir(s) is { } dotnetDir)
        {
            lines.Add($"    path_prepend: [{Yaml(dotnetDir)}]   # the newest .NET SDK is here, not on PATH");
            lines.Add("    env:");
            lines.Add($"      DOTNET_ROOT: {Yaml(dotnetDir)}");
        }
        else if (s.PathPrepend is not null)
        {
            lines.Add(RedactedDotnetComment);
        }

        return lines;
    }

    internal const string WithheldPathPrependReason =
        "not one usable folder with DOTNET_ROOT set to it (a redacted home folder, a list, or characters lanq refuses in path_prepend)";

    internal const string WithheldEnvReason = "kept only as DOTNET_ROOT set to the one usable path_prepend folder";

    /// <summary>
    /// The report for every <c>lanq makeready</c> JSON path (one machine, each <c>--all</c> row, <c>--add</c>; 0.9.2): every string passed
    /// through <see cref="JobRunner.SafeLine"/> (env keys included), and suggestedMachine.path_prepend and env kept only when
    /// <see cref="SuggestedDotnetDir"/> accepts them, as the text and <c>--add</c> do. What was dropped is listed in lanqWithheld (absent
    /// when nothing was). The schema stays lanq-makeready/v1 and the report still has no nextSteps of lanq's.
    /// </summary>
    /// <param name="tune">lanq makeready --tune: the tune scan's outcome, written last as lanqTune (absent when null).</param>
    internal static MakereadyReportJson ReportForJson(MakereadyReport r, MakereadyTuneJson? tune = null)
    {
        static string? S(string? v) => v is null ? null : JobRunner.SafeLine(v);
        static List<string>? L(List<string>? v) => v?.Select(x => JobRunner.SafeLine(x ?? "")).ToList();
        List<MakereadyWithheld> withheld = [];
        MakereadySuggestion? suggestion = null;
        if (r.SuggestedMachine is { } s)
        {
            var usable = SuggestedDotnetDir(s) is not null;
            if (!usable && s.PathPrepend is not null)
            {
                withheld.Add(new MakereadyWithheld("suggestedMachine.path_prepend", WithheldPathPrependReason));
            }

            if (!usable && s.Env is not null)
            {
                withheld.Add(new MakereadyWithheld("suggestedMachine.env", WithheldEnvReason));
            }

            suggestion = new MakereadySuggestion(
                S(s.Name), S(s.Os), S(s.Ssh), S(s.WorkRoot), L(s.Capabilities), s.MaxJobs, s.MinFreeMemGb, S(s.Transfer), S(s.AllowedHours),
                s.Wake is { } w ? new MakereadyWake(S(w.Mac)) : null,
                usable ? L(s.PathPrepend) : null,
                usable ? s.Env!.ToDictionary(e => JobRunner.SafeLine(e.Key), e => JobRunner.SafeLine(e.Value ?? ""), StringComparer.Ordinal) : null);
        }

        return new MakereadyReportJson(
            S(r.Schema),
            r.Tool is { } t ? new MakereadyTool(S(t.Name), S(t.Version)) : null,
            S(r.GeneratedAt),
            r.Host is { } h ? new Makeready.MakereadyHost(S(h.Hostname), S(h.User), S(h.Os), S(h.Arch)) : null,
            r.Verdicts is { } v ? new MakereadyVerdicts(S(v.WorkerGit), S(v.WorkerArchive), S(v.Dotnet10), S(v.Docker), S(v.Overnight)) : null,
            r.Summary is { } sum
                ? sum with
                {
                    Verdict = S(sum.Verdict),
                    Text = S(sum.Text),
                    NextSteps = sum.NextSteps?.Select(st => st with { Id = S(st.Id), Title = S(st.Title), Status = S(st.Status), Detail = S(st.Detail), Action = S(st.Action) }).ToList(),
                }
                : null,
            r.SuitedFor is { } f ? f with { Tier = S(f.Tier), Text = S(f.Text) } : null,
            r.Checks?.Select(c => c with { Id = S(c.Id), Title = S(c.Title), Status = S(c.Status), Detail = S(c.Detail), Advice = S(c.Advice) }).ToList(),
            L(r.Capabilities),
            suggestion,
            withheld.Count > 0 ? withheld : null)
        {
            LanqTune = tune,
        };
    }

    internal const string RedactedDotnetComment =
        "    # path_prepend: the newest .NET SDK is outside PATH, in a folder the report hides or lanq cannot use as written; lanq makeready --add looks it up";

    /// <summary>The script's suggested .NET SDK folder when it is usable as written (see <see cref="SuggestedItem"/>), else null.</summary>
    internal static string? SuggestedDotnetDir(MakereadySuggestion s) =>
        s.PathPrepend is [{ } dir] && s.Env is { Count: 1 } env && env.TryGetValue("DOTNET_ROOT", out var root) && root == dir
        && (s.Os == "windows" ? WindowsToolDirRegex() : LinuxToolDirRegex()).IsMatch(dir)
            ? dir
            : null;

    private static string Yaml(string value) =>
        "\"" + value.Replace(Backslash, Backslash + Backslash, StringComparison.Ordinal).Replace("\"", Backslash + "\"", StringComparison.Ordinal) + "\"";

    private static readonly string Backslash = ((char)92).ToString();
}
