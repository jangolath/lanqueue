using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Scheduling;

namespace LanQueue.Output;

/// <summary>
/// What a next step asks for (the <c>action</c> of a <see cref="NextStepJson"/>, written in snake_case). Values may be added in a later
/// version; a reader that does not know one treats it as <see cref="AskDeveloper"/>.
/// </summary>
internal enum NextAction
{
    /// <summary>Go ahead: the command does what the dry run said it would.</summary>
    Proceed,

    /// <summary>Run the same command again (edited only as the step says).</summary>
    Retry,

    /// <summary>Something temporary is in the way; wait (and, with a command, run it then).</summary>
    Wait,

    /// <summary>The job itself failed or hung: fix the code.</summary>
    FixCode,

    /// <summary>The command line is wrong: fix it.</summary>
    FixCommand,

    /// <summary>Only the developer (or a machine's owner) can fix this; tell them and stop.</summary>
    AskDeveloper,

    /// <summary>Run lanq doctor (read-only) to find the setup problem.</summary>
    RunDoctor,

    /// <summary>Run lanq health (read-only) to see the machines now.</summary>
    RunHealth,

    /// <summary>Nothing to do.</summary>
    None,
}

/// <summary>
/// A command lanq suggests. Only the factories below make one, so a suggestion can only ever be one of these commands: never unlock,
/// init, wake, health --wake, --force, --yes or --add, never more than the command that produced it (see <see cref="Rerun"/>), and
/// --setup only through <see cref="Setup"/>, which is never safe unattended.
/// </summary>
internal sealed class SuggestedCommand
{
    /// <summary>lanq's own options a suggestion never carries (whatever the user typed); --setup only through <see cref="Setup"/>.</summary>
    internal static readonly string[] NeverSuggested = ["--force", "--yes", "--wake", "--add", "--setup"];

    /// <summary>The only commands a suggestion may be.</summary>
    internal static readonly string[] SuggestedVerbs = ["run", "health", "doctor", "makeready"];

    /// <summary>The longest --timeout lanq accepts (minutes).</summary>
    private const int MaxTimeout = 10080;

    /// <summary>The --wait a wait step suggests (seconds); a run that already waited this long gets no command.</summary>
    internal const int WaitSeconds = 120;

    private SuggestedCommand(List<string> argv, bool safe)
    {
        Argv = argv;
        SafeToRunUnattended = safe;
    }

    /// <summary>The exact arguments, starting with "lanq".</summary>
    public IReadOnlyList<string> Argv { get; }

    public bool SafeToRunUnattended { get; }

    /// <summary>
    /// lanq health [--on m,n] [--needs a,b] [--mem N] [--size C,C | --min-size C] --json [--config FILE]: read-only, safe. --on, --size and
    /// --min-size are passed on exactly as the command that produced the step had them (never added or dropped).
    /// </summary>
    public static SuggestedCommand? Health(IReadOnlyCollection<string> needs, double? memGb, string? config, IReadOnlyList<string>? on = null, SizeRule? size = null)
    {
        List<string> argv = ["lanq", "health"];
        if (on is { Count: > 0 })
        {
            argv.AddRange(["--on", string.Join(',', on)]);
        }

        if (needs.Count > 0)
        {
            argv.AddRange(["--needs", string.Join(',', needs)]);
        }

        if (memGb is { } mem)
        {
            argv.AddRange(["--mem", mem.ToString(CultureInfo.InvariantCulture)]);
        }

        if (size is not null)
        {
            argv.AddRange([size.Option, size.Value]);
        }

        argv.Add("--json");
        return Make(WithConfig(argv, config), safe: true);
    }

    /// <summary>The user's own lanq run with --dry-run added: says what a run would do and does none of it, safe.</summary>
    public static SuggestedCommand? DryRun(ParsedArgs p) =>
        p.Command == "run" ? Make(["lanq", .. CommandLine.Edit(p.Raw, add: ["--dry-run"])], safe: true) : null;

    /// <summary>
    /// The user's own lanq run again, edited from <see cref="ParsedArgs.Raw"/>: --config, --dir, --mem, --fetch and the job's arguments stay
    /// as typed. It never does more than the original: a run that could not wake a machine (--wait 0, or --no-wake) gets --no-wake, --no-wake
    /// is never dropped, <paramref name="waitSeconds"/> only raises --wait, <paramref name="timeoutMinutes"/> only raises --timeout (at most
    /// 10080) and <paramref name="shardRetries"/> only raises --shard-retries. Safe (the developer already ran it).
    /// </summary>
    public static SuggestedCommand? Rerun(ParsedArgs p, bool dropDryRun = false, int? waitSeconds = null, int? timeoutMinutes = null, int? shardRetries = null)
    {
        if (p.Command != "run")
        {
            return null;
        }

        var set = new List<(string, string)>();
        if (waitSeconds is { } w && w > p.WaitSeconds)
        {
            set.Add(("--wait", w.ToString(CultureInfo.InvariantCulture)));
        }

        if (timeoutMinutes is { } t && t > p.TimeoutMinutes)
        {
            set.Add(("--timeout", Math.Min(t, MaxTimeout).ToString(CultureInfo.InvariantCulture)));
        }

        if (shardRetries is { } r && p.Shards is { } shards && r > shards.Retries)
        {
            set.Add(("--shard-retries", r.ToString(CultureInfo.InvariantCulture)));
        }

        // The original could not wake a machine with --wait 0; a longer wait must not start doing so.
        var add = p.WaitSeconds == 0 && !p.NoWake ? new[] { "--no-wake" } : [];
        return Make(["lanq", .. CommandLine.Edit(p.Raw, set, add, dropDryRun ? ["--dry-run"] : null)], safe: true);
    }

    /// <summary>
    /// The user's own lanq run --plan again for some of its jobs: <see cref="ParsedArgs.Raw"/> with --only set to <paramref name="only"/> (the
    /// jobs that did not complete; --only only ever narrows, and every job named was in the run). <paramref name="waitSeconds"/> only raises
    /// --wait, and a run that could not wake a machine (--wait 0 without --no-wake) gets --no-wake, as <see cref="Rerun"/>. Nothing else
    /// changes. Safe (the developer already ran it, and lanq.yml is the same file).
    /// </summary>
    public static SuggestedCommand? RerunPlan(ParsedArgs p, IReadOnlyCollection<string> only, int? waitSeconds = null)
    {
        if (p.Command != "run" || !p.Plan || only.Count == 0)
        {
            return null;
        }

        var set = new List<(string, string)> { ("--only", string.Join(',', only)) };
        if (waitSeconds is { } w && w > p.WaitSeconds)
        {
            set.Add(("--wait", w.ToString(CultureInfo.InvariantCulture)));
        }

        var add = p.WaitSeconds == 0 && !p.NoWake ? new[] { "--no-wake" } : [];
        return Make(["lanq", .. CommandLine.Edit(p.Raw, set, add)], safe: true);
    }

    /// <summary>
    /// lanq doctor [--remote] [--json] [--config FILE]: read-only (--remote reads each worker over ssh, and leaves held machines alone), safe.
    /// <paramref name="json"/> is the producing command's own --json: an agent reading JSON gets doctor's JSON too, people get the text.
    /// </summary>
    public static SuggestedCommand? Doctor(bool remote, string? config, bool json = false)
    {
        List<string> argv = ["lanq", "doctor"];
        if (remote)
        {
            argv.Add("--remote");
        }

        if (json)
        {
            argv.Add("--json");
        }

        return Make(WithConfig(argv, config), safe: true);
    }

    /// <summary>
    /// lanq makeready MACHINE [--config FILE] (or --ssh user@host --os X [--key-env VAR]): it runs a script on someone else's PC, so never safe
    /// unattended.
    /// </summary>
    public static SuggestedCommand? Makeready(string? machine, string? config, string? sshTarget = null, MachineOs? os = null, string? keyEnv = null)
    {
        if (sshTarget is not null && os is { } o)
        {
            return Make(WithKeyEnv(["lanq", "makeready", "--ssh", sshTarget, "--os", OsWord(o)], keyEnv), safe: false);
        }

        return machine is null ? null : Make(WithConfig(["lanq", "makeready", machine], config), safe: false);
    }

    /// <summary>
    /// lanq makeready --ssh user@host --os X --setup [--key-env VAR]: it connects to nothing, but what it prints is for the developer and the
    /// PC's owner to read and run, so it is never safe unattended. The only suggestion that may carry --setup.
    /// </summary>
    public static SuggestedCommand? Setup(string sshTarget, MachineOs os, string? keyEnv) =>
        Make(WithKeyEnv(["lanq", "makeready", "--ssh", sshTarget, "--os", OsWord(os), "--setup"], keyEnv), safe: false, setup: true);

    private static string OsWord(MachineOs os) => os == MachineOs.Windows ? "windows" : "linux";

    private static List<string> WithKeyEnv(List<string> argv, string? keyEnv)
    {
        if (keyEnv is not null)
        {
            argv.AddRange(["--key-env", keyEnv]);
        }

        return argv;
    }

    private static List<string> WithConfig(List<string> argv, string? config)
    {
        if (config is not null)
        {
            argv.AddRange(["--config", config]);
        }

        return argv;
    }

    /// <summary>
    /// The last guard: a suggestion that is not one of the allowed commands, carries a banned option or would not parse is never made. Only
    /// <see cref="Setup"/> passes <paramref name="setup"/>: then --setup (and nothing else on the banned list) is allowed, only on lanq
    /// makeready, and the step is never safe unattended whatever <paramref name="safe"/> says.
    /// </summary>
    private static SuggestedCommand? Make(List<string> argv, bool safe, bool setup = false)
    {
        var own = argv.Skip(1).Take(CommandLine.IndexOfDashDash(argv) is var dash and >= 0 ? dash - 1 : argv.Count).ToList();
        if (own.Count == 0 || !SuggestedVerbs.Contains(own[0])
            || own.Any(o => NeverSuggested.Contains(o) && !(setup && o == "--setup" && own[0] == "makeready")))
        {
            return null;
        }

        if (setup)
        {
            safe = false;
        }

        try
        {
            ParsedArgs.Parse([.. argv.Skip(1)]);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return null;
        }

        return new SuggestedCommand(argv, safe);
    }
}

/// <summary>
/// The next steps lanq prints after a command: in JSON (<c>nextSteps</c>, always at least one entry, <c>none</c> when there is nothing to do)
/// and for people (<see cref="Lines"/>, on stderr, before the status line). Pure: every step is decided from the result and the command line.
/// </summary>
internal static class NextSteps
{
    /// <summary>The snake_case name of an action, as in JSON.</summary>
    public static string Name(NextAction action) => action switch
    {
        NextAction.Proceed => "proceed",
        NextAction.Retry => "retry",
        NextAction.Wait => "wait",
        NextAction.FixCode => "fix_code",
        NextAction.FixCommand => "fix_command",
        NextAction.AskDeveloper => "ask_developer",
        NextAction.RunDoctor => "run_doctor",
        NextAction.RunHealth => "run_health",
        _ => "none",
    };

    /// <summary>One step: the reason sanitised; argv and command only with a suggested command (command only when it renders safely).</summary>
    public static NextStepJson Step(NextAction action, string reason, SuggestedCommand? command = null) => new(
        Name(action),
        JobRunner.SafeLine(reason),
        command is null ? null : CommandLine.Render(command.Argv),
        command?.Argv,
        command?.SafeToRunUnattended ?? false);

    public static List<NextStepJson> Nothing(string reason) => [Step(NextAction.None, reason)];

    /// <summary>
    /// The lines for people: "Next: reason: command" per step (none left out), "(for the developer)" before a command that is not safe to run
    /// unattended, and only the reason when there is no command line. <paramref name="prefix"/> is "[lanq] " inside lanq run.
    /// </summary>
    public static IEnumerable<string> Lines(IEnumerable<NextStepJson> steps, string prefix = "") =>
        steps.Where(s => s.Action != Name(NextAction.None)).Select(s => JobRunner.SafeLine(
            prefix + "Next: " + (s.Command is null ? s.Reason : (s.SafeToRunUnattended ? "" : "(for the developer) ") + s.Reason + ": " + s.Command)));

    /// <summary>Writes <see cref="Lines"/> to <paramref name="stderr"/>.</summary>
    public static async Task WriteAsync(TextWriter stderr, IEnumerable<NextStepJson> steps, string prefix = "")
    {
        foreach (var line in Lines(steps, prefix))
        {
            await stderr.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>What one lanq run (or dry run) ended with, for <see cref="ForRun(ParsedArgs, RunOutcome)"/>.</summary>
    /// <param name="MayStillRun">The job may still be running on <paramref name="Machine"/> (connection lost while it ran, cleanup not done).</param>
    internal sealed record RunOutcome(
        string Status, int ExitCode, string Message, RejectKind? RejectKind, InfraKind? InfraKind, bool MayStillRun, string? Machine, bool? Retryable,
        IReadOnlyList<ExcludedMachine>? Excluded, string? LogPath)
    {
        /// <summary>lanq run --baseline: the rerun at the base commit after the job failed, or null.</summary>
        public BaselineResult? Baseline { get; init; }
    }

    internal static RunOutcome Outcome(JobResult r) => new(
        r.Status.ToString(), r.CliExitCode, r.Message, r.RejectKind, r.InfraKind, r.MayStillRun && !r.CleanedUp, r.Machine, r.Retryable, r.Excluded, r.LogPath)
    {
        Baseline = r.Baseline,
    };

    /// <summary>
    /// What a baseline rerun says about a failure, for the fix_code reason: "; new: the base commit passes", "; pre-existing: it fails at the
    /// base commit too (log: PATH)", "; baseline inconclusive: ..." or "; baseline skipped: ..."; empty without one.
    /// </summary>
    internal static string BaselineSentence(BaselineResult? b) => b?.Verdict switch
    {
        null => "",
        BaselineVerdict.New => "; new: the base commit passes" + b.MachineCaveat,
        BaselineVerdict.Preexisting => $"; pre-existing: it fails at the base commit too (log: {b.LogPath ?? "none"})",
        BaselineVerdict.Skipped => "; baseline skipped: the snapshot is the base commit",
        _ => "; baseline inconclusive: " + b.Message,
    };

    /// <summary>lanq health judging a job like the one of <paramref name="p"/>: its --on, --needs, --mem, --size / --min-size and --config.</summary>
    private static SuggestedCommand? HealthFor(ParsedArgs p) => SuggestedCommand.Health(p.Needs, p.MemGb, p.ConfigPath, p.On, p.Size);

    /// <summary>lanq run (one job) or an error object of lanq run: the steps for its status (see docs/JSON.md).</summary>
    public static List<NextStepJson> ForRun(ParsedArgs p, RunOutcome o)
    {
        var health = HealthFor(p);
        switch (o.Status)
        {
            case nameof(JobStatus.Completed):
                return Nothing("the job completed");
            case nameof(JobStatus.Failed):
                return [Step(NextAction.FixCode, string.Create(CultureInfo.InvariantCulture, $"the job exited {o.ExitCode}; read logTail or the log ({o.LogPath ?? "no log"}) and fix the code") + BaselineSentence(o.Baseline))];
            case nameof(JobStatus.Rejected):
                return Rejected(p, o.RejectKind, o.Message);
            case nameof(JobStatus.NoCapacity):
                return NoCapacity(p, o.Retryable ?? true, o.Excluded ?? [], health);
            case nameof(JobStatus.InfraError):
                return Infra(p, o, health);
            case nameof(JobStatus.TimedOut):
                List<NextStepJson> steps = [Step(NextAction.FixCode, string.Create(CultureInfo.InvariantCulture, $"the job timed out after {p.TimeoutMinutes} min; see logTail or the log ({o.LogPath ?? "no log"}) for where it hung"))];
                if (p.TimeoutMinutes < 10080 && SuggestedCommand.Rerun(p, timeoutMinutes: p.TimeoutMinutes * 2) is { } longer)
                {
                    steps.Add(Step(NextAction.Retry, "if the job is only slow, run it again with a longer --timeout", longer));
                }

                return steps;
            case nameof(JobStatus.WindowClosed):
                return [WaitStep(p, "the machine's allowed hours ended while the job ran; run it again when a machine is free")];
            case JobRunner.Cancelled:
                return Nothing("cancelled (Ctrl+C)");
            default:
                return [Step(NextAction.AskDeveloper, "unexpected status " + o.Status)];
        }
    }

    /// <summary>lanq run --shards: by the overall status, with the failing shards and their logs, and --shard-retries for lost connections.</summary>
    public static List<NextStepJson> ForShards(ParsedArgs p, ShardRunResult r)
    {
        if (r.Status == nameof(JobStatus.Failed))
        {
            var failed = r.Shards.Where(s => s.Status == nameof(JobStatus.Failed)).ToList();
            var which = string.Join("; ", failed.Select(s => string.Create(
                CultureInfo.InvariantCulture, $"shard {s.Index}/{r.Count} exited {s.ExitCode} (log: {s.LogPath ?? "none"}){BaselineSentence(s.Baseline)}")));
            return [Step(NextAction.FixCode, $"{failed.Count} shard(s) failed: {which}; read their logTail or logs and fix the code")];
        }

        var lost = r.Shards.Where(s => s.Status == nameof(JobStatus.InfraError) && s.InfraKind == InfraKind.Connection).ToList();
        if (r.Status == nameof(JobStatus.InfraError) && lost.Count > 0)
        {
            var stillRunning = lost.Where(s => s.MayStillRun).ToList();
            var retry = SuggestedCommand.Rerun(p, shardRetries: 1);
            List<NextStepJson> steps = [];
            if (stillRunning.Count > 0)
            {
                steps.Add(Step(
                    NextAction.AskDeveloper,
                    "the job may still be running on " + string.Join(", ", stillRunning.Select(s => s.Machine ?? "?").Distinct(StringComparer.Ordinal))
                        + " (the ssh connection dropped and its cleanup did not finish); do not re-run before it has stopped"));
                steps.Add(Step(NextAction.RunHealth, "see which machines answer now", HealthFor(p)));
                retry = null;
            }

            if (retry is not null)
            {
                steps.Add(Step(NextAction.Retry, $"{lost.Count} shard(s) lost their machine's ssh connection; run again, re-running such a shard once", retry));
            }

            return steps;
        }

        var o = new RunOutcome(
            r.Status, r.ExitCode, r.Message, r.RejectKind, r.InfraKind, false, null, r.Retryable, r.Excluded,
            r.Shards.FirstOrDefault(s => s.Status != nameof(JobStatus.Completed) && s.LogPath is not null)?.LogPath);
        return ForRun(p, o);
    }

    /// <summary>lanq run --dry-run: WouldRun proceeds with the same command without --dry-run; everything else as lanq run would.</summary>
    public static List<NextStepJson> ForPlan(ParsedArgs p, JobPlan plan)
    {
        if (plan.Status == JobPlan.WouldRun)
        {
            return [Step(NextAction.Proceed, "lanq run would start now as shown; run it", SuggestedCommand.Rerun(p, dropDryRun: true))];
        }

        if (plan.Status == nameof(JobStatus.NoCapacity) && plan.Retryable != false)
        {
            // A dry run waits for nothing: look again later with the same dry run (never turned into a real run here).
            return
            [
                Step(NextAction.Wait, "no machine could take the job now; machines may free up, so check again later", SuggestedCommand.DryRun(p)),
                Step(NextAction.RunHealth, "see how each machine is judged for this job", HealthFor(p)),
            ];
        }

        var excluded = JobRunner.Excluded(plan.Assessed.Where(a => !a.Eligible));
        return ForRun(p, new RunOutcome(plan.Status, plan.ExitCode, plan.Message, plan.RejectKind, plan.InfraKind, false, null, plan.Retryable, excluded, null));
    }

    /// <summary>
    /// lanq run --plan (see docs/JSON.md): by the overall status, naming the jobs. A suggested re-run is the same command with --only set to the
    /// jobs that did not complete (<see cref="SuggestedCommand.RerunPlan"/>); a health step judges the deciding job's machines (its needs, mem,
    /// size and on from lanq.yml). <paramref name="jobs"/> gives each job's own lanq run arguments by name (may miss jobs).
    /// </summary>
    public static List<NextStepJson> ForPlanFile(ParsedArgs p, PlanRunResult r, IReadOnlyDictionary<string, ParsedArgs> jobs)
    {
        var again = r.Jobs.Where(j => j.Status != nameof(JobStatus.Completed)).Select(j => j.Name).ToList();
        var decided = r.Decided is { } d && jobs.TryGetValue(d, out var a) ? a : null;
        var health = decided is null ? SuggestedCommand.Health([], null, p.ConfigPath) : SuggestedCommand.Health(decided.Needs, decided.MemGb, p.ConfigPath, decided.On, decided.Size);
        switch (r.Status)
        {
            case nameof(JobStatus.Completed):
                return Nothing("every job completed");
            case nameof(JobStatus.Failed):
                var failed = r.Jobs.Where(j => j.Status == nameof(JobStatus.Failed)).Select(FailedText).ToList();
                return [Step(NextAction.FixCode, $"{failed.Count} job(s) failed: {string.Join("; ", failed)}; read their logTail or logs and fix the code")];
            case nameof(JobStatus.Rejected):
                return Rejected(p, r.RejectKind, r.Message);
            case nameof(JobStatus.NoCapacity) when r.Retryable == false:
                return
                [
                    Step(NextAction.AskDeveloper, $"job '{r.Decided}' can never run on these machines as configured, so waiting will not help ({ExcludedText(r.Excluded)}); "
                        + "a machine needs the capability, the size or the memory, or that job's needs, size, min_size, mem or on in lanq.yml must change (the developer decides)"
                        + EnvConflictAdvice(r.Excluded ?? [], plan: true)),
                    Step(NextAction.RunHealth, $"see how each machine is judged for job '{r.Decided}'", health),
                ];
            case nameof(JobStatus.NoCapacity):
                return [PlanWaitStep(p, $"no machine could take job '{r.Decided}' now; machines may free up", again), Step(NextAction.RunHealth, $"see how each machine is judged for job '{r.Decided}'", health)];
            case nameof(JobStatus.InfraError):
                var lost = r.Jobs.Where(j => j.Status == nameof(JobStatus.InfraError) && (j.InfraKind == InfraKind.Connection
                    || j.Sharded?.Shards.Any(s => s.InfraKind == InfraKind.Connection) == true)).ToList();
                if (r.Jobs.Any(j => j.MayStillRun))
                {
                    var where = r.Jobs.Where(j => j.MayStillRun).Select(j => j.Machine ?? string.Join(",", j.Sharded?.Shards.Where(s => s.MayStillRun).Select(s => s.Machine ?? "?") ?? [])).Distinct(StringComparer.Ordinal);
                    return
                    [
                        Step(NextAction.AskDeveloper, $"a job may still be running on {string.Join(", ", where)} (the ssh connection dropped and its cleanup did not finish); do not re-run before it has stopped"),
                        Step(NextAction.RunHealth, "see which machines answer now", health),
                    ];
                }

                if (lost.Count > 0)
                {
                    return [Step(NextAction.Retry, $"{string.Join(", ", lost.Select(j => j.Name))} lost the machine's ssh connection; run the jobs that did not complete again", SuggestedCommand.RerunPlan(p, again))];
                }

                return Infra(p, new RunOutcome(r.Status, r.ExitCode, r.Message, null, r.InfraKind, false, null, null, null, null), health);
            case nameof(JobStatus.TimedOut):
                var slow = r.Jobs.Where(j => j.Status == nameof(JobStatus.TimedOut)).Select(j => j.Name).ToList();
                return [Step(NextAction.FixCode, $"{string.Join(", ", slow)} timed out; see logTail or the logs for where it hung; if a job is only slow, raise timeout: for job {slow.FirstOrDefault()} in lanq.yml")];
            case nameof(JobStatus.WindowClosed):
                return [PlanWaitStep(p, "a machine's allowed hours ended while a job ran; run the jobs that did not complete when a machine is free", again)];
            case JobRunner.Cancelled:
                return Nothing("cancelled (Ctrl+C)");
            default:
                return [Step(NextAction.AskDeveloper, "unexpected status " + r.Status)];
        }

        static string FailedText(PlanJobOutcome j) => j.Sharded is { } s
            ? $"{j.Name} ({string.Join("; ", s.Shards.Where(x => x.Status == nameof(JobStatus.Failed)).Select(x => string.Create(CultureInfo.InvariantCulture, $"shard {x.Index}/{s.Count} exited {x.ExitCode}, log: {x.LogPath ?? "none"}{BaselineSentence(x.Baseline)}")))})"
            : string.Create(CultureInfo.InvariantCulture, $"{j.Name} exited {j.ExitCode} (log: {j.LogPath ?? "none"}){BaselineSentence(j.Baseline)}");
    }

    /// <summary>lanq run --plan --dry-run: WouldRun proceeds with the same command without --dry-run; otherwise as the plan run would, without --only edits.</summary>
    public static List<NextStepJson> ForPlanFileDry(ParsedArgs p, string status, string message, bool? retryable, RejectKind? reject, InfraKind? infra, ParsedArgs? decided)
    {
        var health = decided is null ? SuggestedCommand.Health([], null, p.ConfigPath) : SuggestedCommand.Health(decided.Needs, decided.MemGb, p.ConfigPath, decided.On, decided.Size);
        return status switch
        {
            JobPlan.WouldRun => [Step(NextAction.Proceed, "lanq run --plan would start as shown; run it", SuggestedCommand.Rerun(p, dropDryRun: true))],
            nameof(JobStatus.NoCapacity) when retryable != false =>
            [
                Step(NextAction.Wait, "no machine could take a job of the plan now; machines may free up, so check again later", SuggestedCommand.DryRun(p)),
                Step(NextAction.RunHealth, "see how each machine is judged for that job", health),
            ],
            nameof(JobStatus.NoCapacity) =>
            [
                Step(NextAction.AskDeveloper, message + "; a machine needs the capability, the size or the memory, or that job's settings in lanq.yml must change (the developer decides)"
                    + (message.Contains(EnvConflictWhy, StringComparison.Ordinal) ? "; for an env clash, ask the developer: they can drop the variable from that job's env: in lanq.yml, or remove it from the machine's env in machines.yml" : "")),
                Step(NextAction.RunHealth, "see how each machine is judged for that job", health),
            ],
            nameof(JobStatus.Rejected) => Rejected(p, reject, message),
            nameof(JobStatus.InfraError) => Infra(p, new RunOutcome(status, 70, message, null, infra, false, null, null, null, null), health),
            JobRunner.Cancelled => Nothing("cancelled (Ctrl+C)"),
            _ => [Step(NextAction.AskDeveloper, "unexpected status " + status)],
        };
    }

    /// <summary>The words every env_conflict reason carries ("env NAME is set by this machine's env in machines.yml").</summary>
    private const string EnvConflictWhy = "is set by this machine's env in machines.yml";

    /// <summary>
    /// For machines ruled out because their env: sets one of the job's variables (env_conflict): "; ask the developer: they can drop --env NAME
    /// or remove it from the machine's env (build-01, big-01)", naming each variable and machine (a plan job: its env: in lanq.yml, or
    /// machines.yml). Advice for the developer, never a step for the agent. Empty when none.
    /// </summary>
    private static string EnvConflictAdvice(IReadOnlyList<ExcludedMachine> excluded, bool plan)
    {
        var clashes = excluded.Where(x => x.Reason == Eligibility.Code(Exclusion.EnvConflict)).ToList();
        if (clashes.Count == 0)
        {
            return "";
        }

        var names = string.Join(", ", clashes.Select(x => x.Why.Split(' ') is { Length: > 1 } w ? w[1] : "?").Distinct(StringComparer.OrdinalIgnoreCase));
        var machines = string.Join(", ", clashes.Select(x => x.Machine));
        return plan
            ? $"; ask the developer: they can drop {names} from that job's env: in lanq.yml, or remove it from the env of {machines} in machines.yml"
            : $"; ask the developer: they can drop --env {names} or remove it from the machine's env ({machines})";
    }

    private static string ExcludedText(IReadOnlyList<ExcludedMachine>? excluded) =>
        excluded is { Count: > 0 } x ? string.Join("; ", x.Select(e => $"{e.Machine}: {e.Why}")) : "no machine can take it";

    /// <summary>A plan's wait step: the same plan for <paramref name="only"/> with --wait 120 when it waited less (and --no-wake when it could not wake), else no command.</summary>
    private static NextStepJson PlanWaitStep(ParsedArgs p, string why, IReadOnlyCollection<string> only) =>
        p.WaitSeconds < SuggestedCommand.WaitSeconds && SuggestedCommand.RerunPlan(p, only, SuggestedCommand.WaitSeconds) is { } waiting
            ? Step(NextAction.Wait, why + string.Create(CultureInfo.InvariantCulture, $"; run them again waiting up to {SuggestedCommand.WaitSeconds} s"), waiting)
            : Step(NextAction.Wait, why + string.Create(CultureInfo.InvariantCulture, $"; it already waited {p.WaitSeconds} s, so try again later"));

    private static List<NextStepJson> Rejected(ParsedArgs p, RejectKind? kind, string message) => kind switch
    {
        RejectKind.PlanFile when Policy.JobEnv.IsDeniedMessage(message) =>
            [Step(NextAction.AskDeveloper, "this variable can never be set by a job; the developer drops it from that job's env: in lanq.yml (" + message + ")")],
        RejectKind.PlanFile => [Step(NextAction.FixCode, "fix lanq.yml: " + message)],
        RejectKind.Policy when Policy.JobEnv.IsDeniedMessage(message) => ArgumentsError(message),
        RejectKind.Policy => [Step(NextAction.AskDeveloper, $"refused by policy, not allowlisted ({message}); do not work around it, ask the developer")],
        RejectKind.Secret => [Step(NextAction.FixCode, "possible secrets: remove them from the uncommitted changes, the command line or --env values; nothing was sent")],
        RejectKind.Config => ConfigError(p.ConfigPath, message, p.Json),
        // The variables cannot work on the machines as configured: what changes (the values, lanq.yml or machines.yml) is the developer's call.
        RejectKind.JobEnv when p.Plan =>
            [Step(NextAction.AskDeveloper, message + "; ask the developer: they can shorten or drop that job's env: values in lanq.yml, or change machines.yml")],
        RejectKind.JobEnv => [Step(NextAction.AskDeveloper, message + "; ask the developer: they can shorten or drop the --env values, or change machines.yml")],
        _ => [Step(NextAction.FixCommand, "fix the command line: " + message)],
    };

    /// <summary>The config could not be found or loaded (any command). <paramref name="json"/>: the command's own --json.</summary>
    public static List<NextStepJson> ConfigError(string? config, string message, bool json = false) =>
    [
        Step(NextAction.AskDeveloper, "the config could not be loaded (" + message + "); the developer owns machines.yml"),
        Step(NextAction.RunDoctor, "check this computer's lanq setup", SuggestedCommand.Doctor(remote: false, config, json)),
    ];

    /// <summary>The command line could not be parsed (any command).</summary>
    /// <remarks>A deny-table --env (the agent's own flag) is "drop it or ask the developer", never a way to get it through.</remarks>
    public static List<NextStepJson> ArgumentsError(string message) =>
        [Step(NextAction.FixCommand, Policy.JobEnv.IsDeniedMessage(message)
            ? "this variable can never be set by a job; drop it or ask the developer: " + message
            : "fix the command line: " + message)];

    private static List<NextStepJson> NoCapacity(ParsedArgs p, bool retryable, IReadOnlyList<ExcludedMachine> excluded, SuggestedCommand? health)
    {
        if (!retryable && p.On is { Count: > 0 } on)
        {
            // Pinned: say so plainly; the suggested health keeps --on (a suggestion never drops it, only the developer may).
            var lacks = string.Join("; ", excluded.Select(x => $"{x.Machine}: {x.Why}"));
            return
            [
                Step(NextAction.AskDeveloper, $"--on {string.Join(',', on)} cannot ever take this job" + (lacks.Length > 0 ? ": " + lacks : "") + "; pick another machine or drop --on"
                    + EnvConflictAdvice(excluded, plan: false)),
                Step(NextAction.RunHealth, "see how each machine is judged for this job", health),
            ];
        }

        if (!retryable)
        {
            var why = string.Join("; ", excluded.Select(x => $"{x.Machine}: {x.Why}"));
            return
            [
                Step(NextAction.AskDeveloper, "no machine can ever take this job as configured, so waiting will not help" + (why.Length > 0 ? " (" + why + ")" : "")
                    + "; a machine needs the capability, the size or the memory, or the job needs other --needs, --size / --min-size or --mem"
                    + EnvConflictAdvice(excluded, plan: false)),
                Step(NextAction.RunHealth, "see how each machine is judged for this job", health),
            ];
        }

        List<NextStepJson> steps =
        [
            WaitStep(p, "no machine could take the job now; machines may free up"),
            Step(NextAction.RunHealth, "see how each machine is judged for this job", health),
        ];
        if (excluded.Count > 0 && excluded.All(x => x.Reason == Eligibility.Code(Exclusion.Unreachable)))
        {
            steps.Add(Step(NextAction.RunDoctor, "no machine answered ssh; check this computer's ssh setup", SuggestedCommand.Doctor(remote: false, p.ConfigPath, p.Json)));
        }

        return steps;
    }

    /// <summary>A wait step: the same run with --wait 120 when it waited less (and --no-wake when it could not wake), else no command.</summary>
    private static NextStepJson WaitStep(ParsedArgs p, string why) =>
        p.WaitSeconds < SuggestedCommand.WaitSeconds && SuggestedCommand.Rerun(p, waitSeconds: SuggestedCommand.WaitSeconds) is { } waiting
            ? Step(NextAction.Wait, why + string.Create(CultureInfo.InvariantCulture, $"; run again waiting up to {SuggestedCommand.WaitSeconds} s"), waiting)
            : Step(NextAction.Wait, why + string.Create(CultureInfo.InvariantCulture, $"; it already waited {p.WaitSeconds} s, so try again later"));

    private static List<NextStepJson> Infra(ParsedArgs p, RunOutcome o, SuggestedCommand? health) => o.InfraKind switch
    {
        InfraKind.Connection when o.MayStillRun =>
        [
            Step(NextAction.AskDeveloper, $"the job may still be running on {o.Machine ?? "its machine"} (the ssh connection dropped and its cleanup did not finish); do not re-run before it has stopped"),
            Step(NextAction.RunHealth, "see which machines answer now", health),
        ],
        InfraKind.Connection => [Step(NextAction.Retry, "the machine's ssh connection dropped before the job ran (or after its cleanup); run it again", SuggestedCommand.Rerun(p))],
        InfraKind.Snapshot or InfraKind.Bundle =>
        [
            Step(NextAction.RunDoctor, "check git and lanq on this computer", SuggestedCommand.Doctor(remote: false, p.ConfigPath, p.Json)),
            Step(NextAction.AskDeveloper, o.Message),
        ],
        InfraKind.Prepare =>
        [
            Step(NextAction.RunDoctor, "check each worker's work_root, git or tar", SuggestedCommand.Doctor(remote: true, p.ConfigPath, p.Json)),
            Step(NextAction.AskDeveloper, o.Message),
        ],
        InfraKind.Archive => [Step(NextAction.FixCode, "the archive could not be built (for example a path Windows cannot store): " + o.Message)],
        _ =>
        [
            Step(NextAction.AskDeveloper, o.Message),
            Step(NextAction.RunDoctor, "check this computer's lanq setup", SuggestedCommand.Doctor(remote: false, p.ConfigPath, p.Json)),
        ],
    };

    /// <summary>
    /// One lanq health row: none when it can take work (or, with --needs/--mem, the job); a hold that passes by itself is wait (no command);
    /// low disk is for the owner; a missing tool or unreadable probe points at lanq doctor --remote; a machine that is down at the developer
    /// and lanq doctor; one that looks asleep at lanq wake (text only, never a command).
    /// </summary>
    /// <param name="json">lanq health's own --json (a doctor suggestion then carries --json too).</param>
    public static List<NextStepJson> ForHealthRow(Machine m, MachineHealth h, Assessment? a, string? config, bool json = false)
    {
        var name = m.Name;
        if (a is { Eligible: false } job && job.Reason is Exclusion.MissingCapability or Exclusion.WrongSize or Exclusion.WindowsUnsafeArgs or Exclusion.MemExceedsTotal
            or Exclusion.MemUnknown or Exclusion.SizeUnknown or Exclusion.EnvConflict or Exclusion.WindowsCommandTooLong)
        {
            return [Step(NextAction.AskDeveloper, $"{name} can never take such a job as configured: {job.Why}")];
        }

        if (!h.Reachable)
        {
            return h.LooksAsleep
                ? [Step(NextAction.AskDeveloper, $"{name} looks asleep or switched off; `lanq wake {name} --wait` can wake it, ask the developer first")]
                :
                [
                    Step(NextAction.AskDeveloper, $"{name} is down: {h.Error ?? "unreachable"}"),
                    Step(NextAction.RunDoctor, "check this computer's ssh setup and known_hosts", SuggestedCommand.Doctor(remote: false, config, json)),
                ];
        }

        var hours = m.AllowedHours is { } list && list.Any() ? " (allowed hours: " + string.Join(", ", list) + ")" : "";
        switch (h.HoldKind)
        {
            case HoldKind.None or HoldKind.Job when h.Hold is null:
                break;
            case HoldKind.Paused:
                return [Step(NextAction.Wait, $"{name} is paused; it takes jobs again when its owner removes the PAUSE file")];
            case HoldKind.OutsideHours or HoldKind.WindowClosing:
                return [Step(NextAction.Wait, $"{name} is {h.Hold}; it takes jobs again in its next window{hours}")];
            case HoldKind.LowDisk:
                return [Step(NextAction.AskDeveloper, $"{name}: {h.Hold}; its owner must free disk space")];
            case HoldKind.ToolMissing:
                return
                [
                    Step(NextAction.AskDeveloper, $"{name}: {h.Hold}"),
                    Step(NextAction.RunDoctor, "check each worker's tools", SuggestedCommand.Doctor(remote: true, config, json)),
                ];
            case HoldKind.Unreadable:
                return [Step(NextAction.RunDoctor, $"{name}: {h.Hold}; check each worker", SuggestedCommand.Doctor(remote: true, config, json))];
            default:
                return [Step(NextAction.Wait, $"{name} is held ({h.Hold}); it takes jobs again once that passes")];
        }

        return a is { Eligible: false } busy
            ? [Step(NextAction.Wait, $"{name} cannot take such a job now ({busy.Why ?? busy.Code}); it may later")]
            : Nothing(a is null ? $"{name} can take work" : $"{name} can take such a job now");
    }

    /// <summary>lanq health's error object: Rejected (arguments or config), InfraError (retry the same read-only health), Cancelled.</summary>
    public static List<NextStepJson> ForHealthError(
        string status, string message, IReadOnlyCollection<string> needs, double? memGb, string? config, bool json = false, IReadOnlyList<string>? on = null, SizeRule? size = null) => status switch
        {
            "InfraError" => [Step(NextAction.Retry, "lanq health broke unexpectedly (" + message + "); try once more", SuggestedCommand.Health(needs, memGb, config, on, size))],
            "Cancelled" => Nothing("cancelled (Ctrl+C)"),
            _ => [Step(NextAction.AskDeveloper, message), Step(NextAction.RunDoctor, "check this computer's lanq setup", SuggestedCommand.Doctor(remote: false, config, json))],
        };

    /// <summary>lanq wake: Sent -> look at the machines with lanq health (read-only); never a wake command.</summary>
    public static List<NextStepJson> ForWake(string status, string message, string? config) => status switch
    {
        "Sent" => [Step(NextAction.RunHealth, "the packet was sent; see whether the machine answers (it may take a minute or two)", SuggestedCommand.Health([], null, config))],
        "Awake" => Nothing("the machine answers ssh"),
        "NoAnswer" => [Step(NextAction.AskDeveloper, "no answer: Wake-on-LAN may be off in the BIOS/UEFI or the network card driver, or the machine is not on wired Ethernet")],
        "Cancelled" => Nothing("cancelled (Ctrl+C)"),
        _ => [Step(NextAction.AskDeveloper, message)],
    };

    /// <summary>lanq init: Created -> replace example-01 (the developer's file) and check it with lanq doctor --config.</summary>
    public static List<NextStepJson> ForInit(string status, string? path, string message, bool fileExists, bool json = false) => status switch
    {
        "Created" =>
        [
            Step(NextAction.AskDeveloper, $"replace example-01 in {path} with real workers (lanq makeready --ssh user@host --os windows|linux --setup prints a worker's one-time setup; --add --name NAME then adds it once it is ready)"),
            Step(NextAction.RunDoctor, "check the new config and this computer's setup", SuggestedCommand.Doctor(remote: false, path, json)),
        ],
        "Rejected" when fileExists => [Step(NextAction.RunDoctor, "a config is already there; check it", SuggestedCommand.Doctor(remote: false, path, json))],
        "Rejected" => [Step(NextAction.FixCommand, message)],
        _ => [Step(NextAction.AskDeveloper, message)],
    };

    /// <summary>Why lanq makeready gave no report (its error object, or a --all row).</summary>
    internal enum MakereadyFailure
    {
        Arguments,
        Config,
        Held,
        Unreachable,
        Unusable,
        Cancelled,
        Unexpected,
    }

    /// <summary>
    /// lanq makeready's error object. A rescan is never safe unattended (it runs a script on someone else's PC). <paramref name="setup"/> (lanq
    /// makeready --ssh, when the worker refused the key or its host key is not in known_hosts yet) adds the one-time setup, for the developer.
    /// </summary>
    public static List<NextStepJson> ForMakereadyError(MakereadyFailure failure, string message, SuggestedCommand? again, string? config, SuggestedCommand? setup = null, bool json = false) => failure switch
    {
        MakereadyFailure.Arguments => ArgumentsError(message),
        MakereadyFailure.Config => ConfigError(config, message, json),
        MakereadyFailure.Held => [Step(NextAction.Wait, "the machine is paused or outside its allowed hours; check it later", again)],
        MakereadyFailure.Unreachable when setup is not null =>
        [
            Step(NextAction.RunDoctor, "check this computer's ssh setup and known_hosts", SuggestedCommand.Doctor(remote: false, config, json)),
            Step(NextAction.AskDeveloper, message),
            Step(NextAction.AskDeveloper, "if the PC is not set up for your key yet, print its one-time setup (it connects to nothing; the PC's owner runs the worker part)", setup),
        ],
        MakereadyFailure.Unreachable =>
        [
            Step(NextAction.RunDoctor, "check this computer's ssh setup and known_hosts", SuggestedCommand.Doctor(remote: false, config, json)),
            Step(NextAction.AskDeveloper, message),
        ],
        MakereadyFailure.Cancelled => Nothing("cancelled (Ctrl+C)"),
        MakereadyFailure.Unexpected =>
        [
            Step(NextAction.AskDeveloper, message),
            Step(NextAction.RunDoctor, "check this computer's lanq setup", SuggestedCommand.Doctor(remote: false, config, json)),
        ],
        _ => [Step(NextAction.AskDeveloper, message)],
    };

    /// <summary>How lanq makeready --ssh ... --add ended (its status is Added, NotAdded, Rejected, InfraError or Cancelled).</summary>
    internal enum AddOutcome
    {
        Added,
        NotReady,
        Declined,
        Arguments,
        Config,
        Duplicate,
        Unsafe,
        Unreachable,
        Unusable,
        WriteFailed,
        Cancelled,
        Unexpected,
    }

    /// <summary>
    /// lanq makeready --add. Added: lanq doctor --remote (read-only, safe), and the developer deletes init's example-01 when it is still there.
    /// NOT READY: for the PC's owner, with the rescan (never --add or --yes). The rest as lanq makeready's error object.
    /// </summary>
    /// <param name="rescan">lanq makeready --ssh ... --os ... [--key-env V] (never safe unattended).</param>
    /// <param name="setup">The --setup step when the worker refused the key or its host key is unknown (see <see cref="ForMakereadyError"/>).</param>
    /// <param name="placeholder">init's example-01 is still in the file.</param>
    public static List<NextStepJson> ForMakereadyAdd(
        AddOutcome outcome, string message, string? config, SuggestedCommand? rescan, SuggestedCommand? setup, bool placeholder = false, bool json = false)
    {
        switch (outcome)
        {
            case AddOutcome.Added:
                List<NextStepJson> steps = [Step(NextAction.RunDoctor, "check the new machine and this computer's setup", SuggestedCommand.Doctor(remote: true, config, json))];
                if (placeholder)
                {
                    steps.Add(Step(NextAction.AskDeveloper, "example-01, the example from lanq init, is still in machines.yml; delete it (it never answers, so it shows DOWN everywhere)"));
                }

                return steps;
            case AddOutcome.NotReady:
                return [Step(NextAction.AskDeveloper, "the PC is NOT READY and was not added (" + message + "); its owner must fix the MUST FIX steps on that PC, then add it again; details", rescan)];
            case AddOutcome.Declined:
                return Nothing("not added: nothing was written");
            case AddOutcome.Arguments:
                return ArgumentsError(message);
            case AddOutcome.Config:
                return ConfigError(config, message, json);
            case AddOutcome.Duplicate:
                return [Step(NextAction.FixCommand, message)];
            case AddOutcome.Unsafe or AddOutcome.WriteFailed:
                return [Step(NextAction.AskDeveloper, message)];
            case AddOutcome.Unreachable:
                return ForMakereadyError(MakereadyFailure.Unreachable, message, rescan, config, setup, json);
            case AddOutcome.Unusable:
                return ForMakereadyError(MakereadyFailure.Unusable, message, rescan, config, json: json);
            case AddOutcome.Cancelled:
                return Nothing("cancelled (Ctrl+C); nothing was written");
            default:
                return ForMakereadyError(MakereadyFailure.Unexpected, message, rescan, config, json: json);
        }
    }

    /// <summary>One lanq makeready --all row: OK and WARN none, FAIL for the PC's owner (with lanq makeready MACHINE for the details), else as the error object.</summary>
    public static List<NextStepJson> ForMakereadyRow(string machine, string verdict, string? problem, string? config, bool json = false) => verdict switch
    {
        "OK" or "WARN" => Nothing($"{machine} is worker-ready"),
        "FAIL" => [Step(NextAction.AskDeveloper, $"{machine} is NOT READY ({problem ?? "see the report"}); its owner must fix it, details", SuggestedCommand.Makeready(machine, config))],
        "held" => ForMakereadyError(MakereadyFailure.Held, $"{machine}: {problem}", SuggestedCommand.Makeready(machine, config), config, json: json),
        "unreachable" => ForMakereadyError(MakereadyFailure.Unreachable, $"{machine}: {problem ?? "unreachable"}", null, config, json: json),
        _ => ForMakereadyError(MakereadyFailure.Unusable, $"{machine}: {problem ?? "no usable report"}", null, config, json: json),
    };

    /// <summary>lanq makeready --all as a whole, from its rows.</summary>
    public static List<NextStepJson> ForMakereadyAll(string status, IReadOnlyList<MakereadyRowJson> rows, string? config, bool json = false)
    {
        string Names(params string[] verdicts) => string.Join(", ", rows.Where(r => verdicts.Contains(r.Verdict)).Select(r => r.Machine));
        return status switch
        {
            "Ok" => Nothing("every machine is worker-ready"),
            "Failing" => [.. rows.Where(r => r.Verdict == "FAIL").Select(r => ForMakereadyRow(r.Machine, r.Verdict, r.Problem, config, json)[0])],
            "Held" => [Step(NextAction.Wait, $"held now (paused or outside allowed hours): {Names("held")}; check them later")],
            _ =>
            [
                Step(NextAction.RunDoctor, "check this computer's ssh setup and known_hosts", SuggestedCommand.Doctor(remote: false, config, json)),
                Step(NextAction.AskDeveloper, $"no usable report from: {Names("unreachable", "error")}"),
            ],
        };
    }

    /// <summary>
    /// lanq doctor --json: Ok without a held machine -> none. A warn or fail -> one ask_developer naming up to five "machine check" pairs and
    /// pointing to each check's fix, never a command (only the developer or a machine's owner fixes known_hosts, keys, the config or tools).
    /// Held machines (doctor --remote) -> also wait, without a command (--force is never suggested). A config that does not load ->
    /// ask_developer only. Rejected -> fix the command line; InfraError -> the same doctor once more (read-only, --json, never --force);
    /// Cancelled -> none.
    /// </summary>
    public static List<NextStepJson> ForDoctor(string status, IReadOnlyList<DoctorCheck> checks, bool remote, string? config, string message)
    {
        switch (status)
        {
            case "Rejected":
                return ArgumentsError(message);
            case "InfraError":
                return [Step(NextAction.Retry, "lanq doctor broke unexpectedly (" + message + "); try once more", SuggestedCommand.Doctor(remote, config, json: true))];
            case "Cancelled":
                return Nothing("cancelled (Ctrl+C)");
        }

        if (checks.FirstOrDefault(c => c is { Scope: "local", Name: "config", Level: "fail" }) is { } bad)
        {
            return [Step(NextAction.AskDeveloper, "the config could not be loaded (" + bad.Detail + "); the developer owns machines.yml")];
        }

        List<NextStepJson> steps = [];
        var problems = checks.Where(c => c.Level is "warn" or "fail").ToList();
        if (problems.Count > 0)
        {
            var names = problems.Select(c => (c.Machine ?? "local") + " " + (c.Capability is { } cap ? c.Name + " " + cap : c.Name)).Distinct(StringComparer.Ordinal).ToList();
            var fails = problems.Count(c => c.Level == "fail");
            steps.Add(Step(
                NextAction.AskDeveloper,
                string.Create(CultureInfo.InvariantCulture, $"lanq doctor found {fails} fail and {problems.Count - fails} warn check(s): {Listed(names)}; ")
                    + "each check's fix (checks[].fix) says what to do, and the developer or the machine's owner does it"));
        }

        var held = checks.Where(c => c.Level == "hold").Select(c => c.Machine ?? "?").Distinct(StringComparer.Ordinal).ToList();
        if (held.Count > 0)
        {
            steps.Add(Step(NextAction.Wait, $"not checked now (paused or outside allowed hours): {Listed(held)}; run the same lanq doctor again later"));
        }

        return steps.Count > 0 ? steps : Nothing("every check passed");

        static string Listed(List<string> names) =>
            string.Join(", ", names.Take(5)) + (names.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $" and {names.Count - 5} more") : "");
    }

    /// <summary>
    /// lanq stats only reads the local job log, so it never suggests a command: Ok -> none; Rejected -> fix the command line; InfraError (the
    /// job log could not be read) -> ask_developer; Cancelled -> none.
    /// </summary>
    public static List<NextStepJson> ForStats(string status, string message) => status switch
    {
        "Ok" => Nothing("lanq stats only reads the local job log; nothing to do"),
        "Rejected" => ArgumentsError(message),
        "Cancelled" => Nothing("cancelled (Ctrl+C)"),
        _ => [Step(NextAction.AskDeveloper, message + "; the developer can check that file's permissions or whether another program holds it locked")],
    };
}
