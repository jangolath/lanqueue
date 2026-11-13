using System.Text.Json.Serialization;

namespace LanQueue.Output;

/// <summary>One machine in <c>lanq health --json</c>.</summary>
/// <param name="State">ok, hold or down.</param>
/// <param name="Reasons">Why the machine is held or down; empty when ok.</param>
/// <param name="MemFreeGb">Null when the machine did not report memory.</param>
/// <param name="LocalTime">The worker's own clock (yyyy-MM-ddTHH:mm:ss), or null when it did not report one.</param>
/// <remarks>Its null members are always written (also inside the --add object, whose context leaves out the report's null members).</remarks>
internal record HealthJson(
    string Name,
    string State,
    IReadOnlyList<string> Reasons,
    double Load,
    double DiskFreeGb,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? MemFreeGb,
    int SlotsUsed,
    int MaxJobs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LocalTime)
{
    /// <summary>Logical processors, or null when the machine did not report them (down, or an older probe).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Cores { get; init; }

    /// <summary>Total memory in GB, or null when not reported.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? MemTotalGb { get; init; }

    /// <summary>
    /// False when the worker could not measure its load (a Windows account denied WMI where the fallback is refused too): <see cref="HealthJson.Load"/>
    /// is then 0 and means nothing. True otherwise (also for a machine that is down). Added in 0.8.0.
    /// </summary>
    public bool LoadKnown { get; init; } = true;

    /// <summary>max_jobs minus the slots in use, stale ones not counted (0 for a machine that is down).</summary>
    public int FreeSlots { get; init; }

    /// <summary>
    /// Of slotsUsed, the slot files left by a crashed run (more than 30 minutes past their deadline): they do not count against freeSlots,
    /// and the next claim reclaims them. Only when above 0 (0.9.0).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlotsStale { get; init; }

    /// <summary>
    /// light, medium, heavy or unknown: the machine's effective size (its size: in machines.yml when it has one, else the probed class; see
    /// <see cref="Health.MachineSize"/>).
    /// </summary>
    public string Size { get; init; } = "unknown";

    /// <summary>Where <see cref="Size"/> came from: "probe" (memory and cores) or "config" (the machine's size: key). Always present.</summary>
    public string SizeSource { get; init; } = "probe";

    /// <summary>What the machine suits, in lanq-makeready's words, for example "light: lint and unit-test shards + container builds".</summary>
    public string SuitedFor { get; init; } = "";

    /// <summary>What to do about this machine (see <see cref="NextStepJson"/>): none when it can take work (or, with --needs/--mem, the job).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>
/// One next step, in every --json object (nextSteps, at least one entry; <c>none</c> when there is nothing to do). <paramref name="Argv"/>,
/// the exact arguments with no shell involved, is present exactly when a command is suggested and is the authoritative form;
/// <paramref name="Command"/> is the same as one line for bash and PowerShell, present only when every argument can be written safely for both.
/// </summary>
/// <param name="Action">proceed, retry, wait, fix_code, fix_command, ask_developer, run_doctor, run_health or none. Values may be added; treat an unknown one as ask_developer.</param>
/// <param name="Reason">Why, for people and agents (sanitised).</param>
/// <param name="SafeToRunUnattended">True only for a suggested command an agent may run without asking (read-only, or the same run again with no more side effects).</param>
internal sealed record NextStepJson(
    string Action,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Argv,
    bool SafeToRunUnattended);

/// <summary>
/// One machine in <c>lanq health --needs ... --json</c> (or --mem): the plain row plus how the machine was judged for such a job, by the rules
/// lanq run uses. A separate type, so plain <c>lanq health --json</c> rows stay exactly as they were, while these fields are always present here
/// (why and reason are null when eligible).
/// </summary>
internal sealed record HealthNeedsJson : HealthJson
{
    public HealthNeedsJson(HealthJson row)
        : base(row)
    {
    }

    /// <summary>True when the machine could take the job now.</summary>
    public bool Eligible { get; init; }

    /// <summary>Why not, for people (sanitised), or null when eligible.</summary>
    public string? Why { get; init; }

    /// <summary>The snake_case reason code (as in <see cref="ExcludedJson.Reason"/>), or null when eligible.</summary>
    public string? Reason { get; init; }

    /// <summary>True when waiting can never make this machine take the job (its config or the machine itself rules it out).</summary>
    public bool Permanent { get; init; }

    /// <summary>How many copies of the job (shards) the machine could take now, with --mem counted per copy; 0 when not eligible.</summary>
    public int JobSlots { get; init; }
}

/// <summary>
/// The one object <c>lanq run --json</c> prints on stdout when the job is over, and also when it never started (bad arguments or config:
/// Rejected, exit 2), was cancelled (Cancelled, 130) or hit an unexpected error (InfraError, 70); then machine and jobId are null.
/// </summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed or Cancelled.</param>
/// <param name="ExitCode">lanq's own exit code (the job's code, or 2, 70, 75, 124, 130).</param>
internal sealed record RunJson(string Status, int ExitCode, string? Machine, string? JobId, double DurationSeconds, string Message)
{
    /// <summary>--shards only, in each element of shards: the 1-based shard index.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Shard { get; init; }

    /// <summary>--shards only: how many times the shard was started (more than 1 after --shard-retries re-ran it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; init; }

    /// <summary>--fetch only: the files brought back (relative path below fetchDir, size, SHA-256 hex).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FetchedJson>? Fetched { get; init; }

    /// <summary>--fetch only: the files not brought back, each with a reason.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FetchSkipJson>? FetchSkipped { get; init; }

    /// <summary>--fetch only: skipped files beyond the ones listed in fetchSkipped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FetchSkippedMore { get; init; }

    /// <summary>--fetch only: the local folder the files were written to (out/jobid), when a fetch ran.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FetchDir { get; init; }

    /// <summary>--fetch only: a problem with the fetch as a whole (timed out, cut short, the window ended), or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FetchError { get; init; }

    /// <summary>NoCapacity only (top level): false when no machine can ever take the job as configured (waiting will not help), else true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only (top level): every machine that did not take the job, with its reason.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExcludedJson>? Excluded { get; init; }

    /// <summary>Always present: the full path of the job's local log (every line lanq printed for the job), or null when no job started.</summary>
    public string? LogPath { get; init; }

    /// <summary>--log-tail N only: the log's last N lines (sanitised, each at most 4000 characters); empty when no job started.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LogTail { get; init; }

    /// <summary>What to do next (see <see cref="NextStepJson"/>): always present on a single run's object; absent in the shards of --shards (the top level carries them).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NextStepJson>? NextSteps { get; init; }

    /// <summary>--env only, on a single run's object (the top level of --shards carries it for every shard) (0.9.0): the names of the job's own variables, in the order given (this field lists names only; suggested commands in nextSteps repeat the command line, values included). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>--exclusive (lanq.yml exclusive: true) only (0.9.0): true when the job takes every slot of its machine. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; init; }

    /// <summary>--baseline only (0.9.0), on a single run's object and on each shard of --shards: the rerun at the base commit after the job (shard) ended Failed. Absent when not asked for or not run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaselineJson? Baseline { get; init; }

    /// <summary>On a single run's object only (the top level of --shards carries it) (0.9.2): uncommitted files git shows as binary that the secret scan did not read in full (over a cap). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanSkipJson>? ScanSkipped { get; init; }
}

/// <summary>
/// One file in scanSkipped (0.9.2): an uncommitted file git shows as binary whose content the secret scan did not read in full. Reported,
/// never refused.
/// </summary>
/// <param name="Path">The file's path in the repository (sanitised).</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Reason">binary_over_file_cap (a binary over 8 MB), binary_over_total_cap (past 64 MB of binaries in the snapshot), masked_text_over_cap (text over 32 MB) or masked_text_over_total_cap (past 256 MB of such text in the snapshot). Values may be added.</param>
internal sealed record ScanSkipJson(string Path, long Bytes, string Reason);

/// <summary>
/// lanq run --baseline (0.9.0): the rerun of a failed job (shard, plan job) at the base commit. Every member is always written (null where it
/// does not apply). Information only: the run's own status and exit code never change.
/// </summary>
/// <param name="Ref">What the base came from: the default branch found (origin/main) or --baseline-ref as given.</param>
/// <param name="Sha">The base commit's full id, or null when it could not be found (a shallow clone).</param>
/// <param name="Status">The rerun's own status (Completed, Failed, TimedOut, InfraError, NoCapacity, Cancelled, ...), or null when it never started.</param>
/// <param name="ExitCode">The rerun's exit code as lanq run would exit with it, or null when it never started.</param>
/// <param name="DurationSeconds">How long the rerun's command ran, or null when it never started.</param>
/// <param name="Verdict">new (the base completed), preexisting (it failed there too), inconclusive (anything else) or skipped (the snapshot is the base commit; nothing ran). Values may be added. Null only in a dry run (nothing ran yet).</param>
/// <param name="Message">The same line lanq prints after "[lanq] baseline: ".</param>
internal sealed record BaselineJson(
    string Ref, string? Sha, string? Status, int? ExitCode, string? Machine, string? JobId, string? LogPath, double? DurationSeconds, string? Verdict, string Message)
{
    /// <summary>
    /// True when the rerun ran on the machine the job failed on, false when on another (the verdict may then reflect the machine, and the
    /// message says so). Absent when the rerun ran nowhere.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SameMachine { get; init; }
}

/// <summary>
/// The one object <c>lanq run --shards --json</c> prints on stdout: the overall status and exit code, N, and one <see cref="RunJson"/> per
/// shard (with shard and attempts) in index order. shards is empty when nothing ran (refused before anything was sent, or bad arguments).
/// </summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed or Cancelled.</param>
internal sealed record ShardRunJson(string Status, int ExitCode, int ShardCount, double DurationSeconds, string Message, IReadOnlyList<RunJson> Shards)
{
    /// <summary>NoCapacity only: as in <see cref="RunJson.Retryable"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: as in <see cref="RunJson.Excluded"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExcludedJson>? Excluded { get; init; }

    /// <summary>--shards auto --target-minutes only: how N was picked from earlier runs (left out otherwise).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShardTimingJson? ShardTiming { get; init; }

    /// <summary>What to do next for the whole run (see <see cref="NextStepJson"/>).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];

    /// <summary>--env only (0.9.0): the names of the job's own variables, in the order given (this field lists names only; suggested commands in nextSteps repeat the command line, values included). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>--exclusive (lanq.yml exclusive: true) only (0.9.0): true when the job takes every slot of its machine. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; init; }

    /// <summary>As in <see cref="RunJson.ScanSkipped"/> (0.9.2), for the one snapshot every shard runs. Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanSkipJson>? ScanSkipped { get; init; }
}

/// <summary>
/// How --shards auto --target-minutes picked N (<see cref="Scheduling.ShardTiming.Pick"/>): in lanq run --shards --json and in the dry run's
/// plan. Every member is always written (null where it does not apply); members are only ever added.
/// </summary>
/// <param name="Command">The command's key (its template argv, {shard}/{shards} literal).</param>
/// <param name="Dir">The normalised --dir.</param>
/// <param name="Basis">history, no_history or overhead (more may be added).</param>
/// <param name="RunsUsed">How many earlier runs the estimate comes from (at most 10, of the last 90 days).</param>
/// <param name="LastRunUtc">The newest of them, or null.</param>
/// <param name="WorkMinutes">The command's whole work on a machine of speed factor 1, or null.</param>
/// <param name="OverheadSeconds">Per shard: wall-clock minus the command's own run time, or null.</param>
/// <param name="SlowestMachine">The slowest machine the shards would be dealt to now, or null.</param>
/// <param name="SpeedFactor">Its speed factor (above 1 is slower), or null.</param>
/// <param name="Wanted">The N the target needs before any cap, or null.</param>
/// <param name="FreeSlots">The free slots that can take the job now.</param>
/// <param name="Capped">free_slots or max_shards when that capped N, else null.</param>
/// <param name="Picked">N (the same as shardCount).</param>
/// <param name="ExpectedShardMinutes">About how long each shard takes on the slowest machine, or null.</param>
internal sealed record ShardTimingJson(
    int TargetMinutes, string Command, string Dir, string Basis, int RunsUsed, DateTime? LastRunUtc, double? WorkMinutes, double? OverheadSeconds,
    string? SlowestMachine, double? SpeedFactor, int? Wanted, int FreeSlots, string? Capped, int Picked, double? ExpectedShardMinutes, string Why);

/// <summary>One machine in excluded (NoCapacity only).</summary>
/// <param name="Reason">
/// missing_capability, wrong_size, size_unknown, windows_unsafe_args, env_conflict, windows_command_too_long, mem_exceeds_total, mem_unknown,
/// tool_missing (permanent); unreachable, asleep, unreadable, paused, outside_hours, window_closing, low_disk, low_memory, memory_pressure,
/// load, mem_below_request, busy, avoided, held (waiting may help).
/// </param>
/// <param name="Permanent">True when waiting can never make this machine take the job (its config or the machine itself rules it out).</param>
internal sealed record ExcludedJson(string Machine, string Reason, string Why, bool Permanent);

/// <summary>
/// The one object <c>lanq run --dry-run --json</c> prints on stdout: what lanq run would do now. Every field is always present (null where it
/// does not apply), except logTail (only with --log-tail, and then always empty: a dry run starts no job). Fields are only ever added.
/// </summary>
/// <param name="Status">WouldRun (0), Rejected (2), NoCapacity (75), InfraError (70) or Cancelled (130).</param>
/// <param name="ExitCode">The exit code of this dry run.</param>
internal sealed record PlanJson(string Status, int ExitCode)
{
    /// <summary>Always true: nothing was sent, no slot claimed, nothing woken.</summary>
    public bool DryRun { get; init; } = true;

    public string Message { get; init; } = "";

    /// <summary>The machine the job (or shard 1) would go to, or null.</summary>
    public string? Machine { get; init; }

    /// <summary>The machines that could take the job now, in the order lanq run would try them (empty when none could, or nothing was probed).</summary>
    public IReadOnlyList<string> Ranked { get; init; } = [];

    /// <summary>--shards only: N (the choice for auto); else null.</summary>
    public int? ShardCount { get; init; }

    /// <summary>--shards only: true for --shards auto; else null.</summary>
    public bool? ShardCountAuto { get; init; }

    /// <summary>--shards auto only: why that N (the free slots, and with --target-minutes the timing first, as lanq run prints it); else null.</summary>
    public string? ShardCountWhy { get; init; }

    /// <summary>--shards auto --target-minutes only: how N was picked from earlier runs; else null.</summary>
    public ShardTimingJson? ShardTiming { get; init; }

    /// <summary>--shards only: where the shards would start now (empty when none could); else null.</summary>
    public IReadOnlyList<PlacementJson>? Placement { get; init; }

    /// <summary>Every machine judged for the job, in config order (empty when the run was refused before any machine was looked at).</summary>
    public IReadOnlyList<PlanMachineJson> Machines { get; init; } = [];

    /// <summary>The sleeping machine lanq run would wake (with --wait above 0 and without --no-wake, when no awake machine can take the job), or null.</summary>
    public string? WouldWake { get; init; }

    /// <summary>NoCapacity only: as in <see cref="RunJson.Retryable"/>; else null.</summary>
    public bool? Retryable { get; init; }

    /// <summary>--log-tail only: always empty (a dry run starts no job, so there is no log).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LogTail { get; init; }

    /// <summary>What to do next (see <see cref="NextStepJson"/>): WouldRun proceeds with the same command without --dry-run.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];

    /// <summary>--env only (0.9.0): the names of the job's own variables, in the order given (this field lists names only; suggested commands in nextSteps repeat the command line, values included). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>--exclusive (lanq.yml exclusive: true) only (0.9.0): true when the job takes every slot of its machine. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; init; }

    /// <summary>--baseline only (0.9.0): the base a failure would be rerun at (ref, sha, message; verdict skipped when the snapshot is the base, else null). Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaselineJson? Baseline { get; init; }

    /// <summary>As in <see cref="RunJson.ScanSkipped"/> (0.9.2), for the snapshot the dry run took. Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanSkipJson>? ScanSkipped { get; init; }
}

/// <summary>One machine in <c>lanq run --dry-run --json</c>: as the health --needs rows judge it (reason codes as in <see cref="ExcludedJson.Reason"/>).</summary>
/// <param name="FreeSlots">max_jobs minus the slots in use.</param>
/// <param name="JobSlots">How many copies of the job (shards) it could take now; for an --exclusive job the slots it would take (max_jobs); 0 when not eligible.</param>
internal sealed record PlanMachineJson(string Machine, bool Eligible, string? Reason, string? Why, bool Permanent, int FreeSlots, int JobSlots);

/// <summary>--shards --dry-run: the shards one machine would take at the start.</summary>
internal sealed record PlacementJson(string Machine, IReadOnlyList<int> Shards);

/// <summary>
/// The one object <c>lanq run --plan --json</c> prints on stdout, on every path: the overall status and exit code, the plan file, the launch
/// order and every selected job in that order. jobs is empty when the plan was refused or ended before anything ran (bad arguments, config,
/// plan file, a refused command line, the snapshot), and then order may be empty too. Fields are only ever added.
/// </summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed or Cancelled.</param>
/// <param name="Plan">The plan file's full path, or null when it was not found.</param>
/// <param name="Order">The launch order (job names).</param>
internal sealed record PlanFileRunJson(
    string Status, int ExitCode, string? Plan, double DurationSeconds, string Message, IReadOnlyList<string> Order, IReadOnlyList<PlanFileJobJson> Jobs)
{
    /// <summary>NoCapacity only: as in <see cref="RunJson.Retryable"/>, from the job that decided the status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: as in <see cref="RunJson.Excluded"/>, from the job that decided the status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExcludedJson>? Excluded { get; init; }

    /// <summary>The plan file's static warnings (also printed on stderr).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What to do next for the whole plan (see <see cref="NextStepJson"/>); a re-run adds --only with the jobs that did not complete.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];

    /// <summary>As in <see cref="RunJson.ScanSkipped"/> (0.9.2), for the plan's one snapshot. Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanSkipJson>? ScanSkipped { get; init; }

    /// <summary>--plan-timeout or lanq.yml plan_timeout: (0.10.0): the whole plan's limit in minutes. Absent without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanTimeoutMinutes { get; init; }

    /// <summary>true when the plan's limit was reached before it ended (0.10.0). Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PlanTimedOut { get; init; }
}

/// <summary>
/// One job of <c>lanq run --plan --json</c>. Every positional member is always written (null where it does not apply): exitCode is null only
/// for Skipped, startedSeconds only when the job never started; machine, jobId and logPath belong to a single job (a sharded job has them per
/// shard); shardCount and shards to a sharded job. The rest is written only where it applies, as in lanq run --json.
/// </summary>
/// <param name="Status">A lanq run status, Cancelled, or Skipped (never started: a job it starts after did not complete, --fail-fast, Ctrl+C).</param>
/// <param name="After">The jobs it started after (within the selection).</param>
/// <param name="StartedSeconds">Seconds from the plan's start to the job's start.</param>
internal sealed record PlanFileJobJson(
    string Name, string Status, int? ExitCode, IReadOnlyList<string> After, string? Machine, string? JobId, double? StartedSeconds, double DurationSeconds,
    string Message, string? LogPath, int? ShardCount, IReadOnlyList<RunJson>? Shards)
{
    /// <summary>--log-tail N, single job only: its log's last N lines (empty when it never started).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LogTail { get; init; }

    /// <summary>NoCapacity only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExcludedJson>? Excluded { get; init; }

    /// <summary>A single job with fetch: in lanq.yml only, as in <see cref="RunJson.Fetched"/> (a sharded job has them per shard).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FetchedJson>? Fetched { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FetchSkipJson>? FetchSkipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FetchSkippedMore { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FetchDir { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FetchError { get; init; }

    /// <summary>shards: auto with target_minutes only: how N was picked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShardTimingJson? ShardTiming { get; init; }

    /// <summary>env: in lanq.yml only (0.9.0): the names of the job's own variables, in the order given (this field lists names only; suggested commands in nextSteps repeat the command line, values included). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>--exclusive (lanq.yml exclusive: true) only (0.9.0): true when the job takes every slot of its machine. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; init; }

    /// <summary>baseline: true only (0.9.0), single job: its rerun at the base commit after it ended Failed (a sharded job has one per failed shard). Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaselineJson? Baseline { get; init; }
}

/// <summary>
/// The one object <c>lanq run --plan --dry-run --json</c> prints on stdout, on every path. Every member is always written (null where it does
/// not apply), except logTail (only with --log-tail, and then always empty). Fields are only ever added.
/// </summary>
/// <param name="Status">WouldRun (0), NoCapacity (75), Rejected (2), InfraError (70) or Cancelled (130).</param>
internal sealed record PlanFileDryJson(string Status, int ExitCode)
{
    /// <summary>Always true: nothing was sent, no slot claimed, nothing woken.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>The plan file's full path, or null when it was not found.</summary>
    public string? Plan { get; init; }

    public string Message { get; init; } = "";

    /// <summary>The launch order (job names).</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>Every selected job, in launch order (empty when the plan was refused before any machine was looked at).</summary>
    public IReadOnlyList<PlanFileDryJobJson> Jobs { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>NoCapacity only: false when some job can never run as configured; else null.</summary>
    public bool? Retryable { get; init; }

    /// <summary>--log-tail only: always empty (a dry run starts no job).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LogTail { get; init; }

    /// <summary>What to do next: WouldRun proceeds with the same command without --dry-run.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];

    /// <summary>As in <see cref="RunJson.ScanSkipped"/> (0.9.2), for the snapshot the dry run took. Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScanSkipJson>? ScanSkipped { get; init; }

    /// <summary>--plan-timeout or lanq.yml plan_timeout: (0.10.0): the limit the plan would run under, in minutes. Absent without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanTimeoutMinutes { get; init; }
}

/// <summary>One job of <c>lanq run --plan --dry-run --json</c> (every member always written, null where it does not apply).</summary>
/// <param name="RunArgv">The equivalent lanq run command line ("lanq", "run", ...), exactly as the job expands.</param>
/// <param name="RunCommand">The same as one line for bash and PowerShell, or null when it cannot be written safely for both.</param>
/// <param name="Start">now (first wave), waits_for_plan (first wave, but every slot it could use is this plan's) or after (it starts after other jobs).</param>
/// <param name="Status">WouldRun, WouldWait, Pending or NoCapacity.</param>
internal sealed record PlanFileDryJobJson(string Name, IReadOnlyList<string> After, IReadOnlyList<string> RunArgv, string? RunCommand, string Start, string Status)
{
    public string Message { get; init; } = "";

    /// <summary>The machine a single job (or shard 1) would go to, or null.</summary>
    public string? Machine { get; init; }

    public int? ShardCount { get; init; }

    public bool? ShardCountAuto { get; init; }

    public IReadOnlyList<PlacementJson>? Placement { get; init; }

    public string? ShardCountWhy { get; init; }

    public ShardTimingJson? ShardTiming { get; init; }

    /// <summary>The job's expected work from jobs.jsonl (minutes, on a machine of speed factor 1), or null without history.</summary>
    public double? ExpectedMinutes { get; init; }

    /// <summary>Every candidate machine judged for the job (empty when nothing was probed).</summary>
    public IReadOnlyList<PlanMachineJson> Machines { get; init; } = [];

    /// <summary>NoCapacity only: false when the job can never run as configured.</summary>
    public bool? Retryable { get; init; }

    /// <summary>env: in lanq.yml only (0.9.0): the names of the job's own variables, in the order given (this field lists names only; suggested commands in nextSteps repeat the command line, values included). Absent when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>--exclusive (lanq.yml exclusive: true) only (0.9.0): true when the job takes every slot of its machine. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; init; }
}

/// <summary>One fetched file in <c>lanq run --json</c>.</summary>
internal sealed record FetchedJson(string Path, long Bytes, string Sha256);

/// <summary>One skipped file in <c>lanq run --json</c>.</summary>
internal sealed record FetchSkipJson(string Path, string Reason);

/// <summary>
/// What <c>lanq health --json</c> and <c>lanq makeready --json</c> print instead of their normal output when there is none: Rejected (2),
/// InfraError (70), Held (75, makeready only) or Cancelled (130).
/// </summary>
internal sealed record ErrorJson(string Status, int ExitCode, string Message)
{
    /// <summary>What to do next (see <see cref="NextStepJson"/>).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>The one object <c>lanq wake --json</c> prints when a packet was sent: Sent (0), Awake (0, with --wait) or NoAnswer (1, with --wait). Failures use <see cref="ErrorJson"/>.</summary>
/// <param name="Awake">Null without --wait (not checked).</param>
internal sealed record WakeJson(string Status, int ExitCode, string Machine, bool Sent, bool? Awake, double DurationSeconds, string Message)
{
    /// <summary>What to do next (see <see cref="NextStepJson"/>): Sent suggests lanq health, never another wake.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>
/// The one object <c>lanq doctor --json</c> prints on every path: Ok (0: no warn or fail; hold and skip allowed), Warnings (1), Failing (1: any
/// fail, a config that does not load included), Rejected (2: bad arguments; no checks), InfraError (70: unexpected error; the checks so far) or
/// Cancelled (130: Ctrl+C; the checks so far). Every member is always written.
/// </summary>
/// <param name="Remote">Whether --remote was given; null when the command line did not parse.</param>
internal sealed record DoctorJson(string Status, int ExitCode, string Message, bool? Remote, DoctorCountsJson Counts, IReadOnlyList<DoctorCheckJson> Checks)
{
    /// <summary>What to do next (see <see cref="NextStepJson"/>): never --force, and never a command for a problem only the developer can fix.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>How many checks of each level lanq doctor --json found.</summary>
internal sealed record DoctorCountsJson(int Ok, int Warn, int Fail, int Skip, int Hold);

/// <summary>One check of <c>lanq doctor --json</c>: one text line (see <see cref="DoctorCheck"/>); names and levels are only ever added.</summary>
/// <param name="Scope">local, machine (a machine's settings, checked on this computer) or remote (checked on the machine over ssh).</param>
/// <param name="Machine">Null for a local check.</param>
/// <param name="Level">ok, warn, fail, skip or hold.</param>
/// <param name="Detail">The text line after its level and machine prefix (sanitised).</param>
/// <param name="Fix">A short remedy, or null.</param>
/// <param name="Capability">The capability of a capability check, else null.</param>
internal sealed record DoctorCheckJson(string Scope, string? Machine, string Name, string Level, string Detail, string? Fix, string? Capability);

/// <summary>
/// The one object <c>lanq stats --json</c> prints on every path: Ok (0; no job log is 0 jobs), Rejected (2: bad arguments), InfraError (70: the
/// job log could not be read) or Cancelled (130: Ctrl+C). The error objects carry zeros and empty lists. Every member is always written except
/// <see cref="CommandsMore"/>.
/// </summary>
/// <param name="Files">The job log files read, oldest first (jobs.1.jsonl, then jobs.jsonl).</param>
/// <param name="SkippedLines">Lines that were not a job record (a torn line, a hand edit): skipped and counted.</param>
/// <param name="OldestUtc">The start of the oldest job (UTC), or null for none.</param>
/// <param name="Machines">Ordinal by name ("-" for jobs that never got a machine).</param>
/// <param name="Commands">Newest first; at most <see cref="Cli.MaxStatsCommands"/> (see <see cref="CommandsMore"/>).</param>
internal sealed record StatsJson(
    string Status, int ExitCode, string Message, IReadOnlyList<string> Files, int Jobs, double WallMinutes, int SkippedLines, DateTime? OldestUtc,
    DateTime? NewestUtc, StatsLogsJson Logs, IReadOnlyList<StatsMachineJson> Machines, IReadOnlyList<StatsCommandJson> Commands)
{
    /// <summary>How many more commands there were beyond <see cref="Commands"/>; left out when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CommandsMore { get; init; }

    /// <summary>What to do next (see <see cref="NextStepJson"/>): stats never suggests a command.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>lanq's own job logs in the state folder's logs/ (<see cref="Jobs.LogPrune.Measure"/>) and how long they are kept (0 = for ever).</summary>
internal sealed record StatsLogsJson(int Files, double Megabytes, int? KeepDays);

/// <summary>One machine of <c>lanq stats --json</c>; median and p90 are wall-clock seconds of its Completed jobs (nearest rank), null when none.</summary>
/// <param name="SpeedFactor">
/// How much longer the machine takes than is typical for the same command (1.62 = 62% slower), from at least 5 completed jobs of the last 90
/// days that ran 30 s or more; clamped to 0.25..4. Null with fewer samples.
/// </param>
/// <param name="SpeedSamples">How many such jobs it has (0 for none).</param>
internal sealed record StatsMachineJson(
    string Machine, int Jobs, int Completed, int Failed, int Other, double WallMinutes, double? MedianSeconds, double? P90Seconds, DateTime LastRunUtc,
    double? SpeedFactor, int SpeedSamples);

/// <summary>One command of <c>lanq stats --json</c>: every job with the same key and dir (or, before 0.7.0, the same recorded command; dir null).</summary>
/// <param name="Runs">Distinct runs: every shard of one --shards run is one run.</param>
/// <param name="Jobs">Every job: each shard and each retried attempt.</param>
/// <param name="LastShards">The newest job's shard count (1 for a single run); null for lines written before 0.7.0.</param>
/// <param name="Timing">What earlier runs say about the command's work and overhead, as --target-minutes estimates it; null without a key or a usable run.</param>
internal sealed record StatsCommandJson(
    string Command, string? Dir, int Runs, int Jobs, int Completed, int Failed, int Other, double? MedianSeconds, double? P90Seconds, DateTime LastRunUtc,
    int? LastShards, StatsTimingJson? Timing);

/// <summary>
/// The timing block of a <c>lanq stats --json</c> command: the estimate --shards auto --target-minutes uses (machines without their own speed
/// factor count as 1: stats probes nothing).
/// </summary>
/// <param name="RunsUsed">How many earlier runs it comes from (the newest 10 of the last 90 days in which at least half the shards completed).</param>
/// <param name="WorkMinutes">The whole command's own run time on a machine of speed factor 1.</param>
/// <param name="OverheadSeconds">Per shard: wall-clock minus the command's own run time.</param>
/// <param name="SuggestedShards">With --target-minutes: the shards it needs on such a machine, before any free-slot cap; null without it, or when the overhead alone is near or above it.</param>
internal sealed record StatsTimingJson(int RunsUsed, double WorkMinutes, double OverheadSeconds, int? SuggestedShards);

/// <summary>Source-generated serialization (no reflection) for everything lanq prints as JSON.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HealthJson>))]
[JsonSerializable(typeof(List<HealthNeedsJson>))]
[JsonSerializable(typeof(RunJson))]
[JsonSerializable(typeof(ShardRunJson))]
[JsonSerializable(typeof(ExcludedJson))]
[JsonSerializable(typeof(BaselineJson))]
[JsonSerializable(typeof(ErrorJson))]
[JsonSerializable(typeof(WakeJson))]
[JsonSerializable(typeof(InitJson))]
[JsonSerializable(typeof(PlanJson))]
[JsonSerializable(typeof(PlanFileRunJson))]
[JsonSerializable(typeof(PlanFileJobJson))]
[JsonSerializable(typeof(PlanFileDryJson))]
[JsonSerializable(typeof(PlanFileDryJobJson))]
[JsonSerializable(typeof(ShardTimingJson))]
[JsonSerializable(typeof(DoctorJson))]
[JsonSerializable(typeof(DoctorCheckJson))]
[JsonSerializable(typeof(DoctorCountsJson))]
[JsonSerializable(typeof(StatsJson))]
[JsonSerializable(typeof(StatsMachineJson))]
[JsonSerializable(typeof(StatsCommandJson))]
[JsonSerializable(typeof(StatsTimingJson))]
[JsonSerializable(typeof(StatsLogsJson))]
[JsonSerializable(typeof(NextStepJson))]
[JsonSerializable(typeof(List<NextStepJson>))]
internal sealed partial class LanqJson : JsonSerializerContext;

/// <summary>One machine in <c>lanq makeready --all --json</c>.</summary>
/// <param name="Verdict">FAIL (not worker-ready), WARN (something to look at) or OK, or unreachable, held or error (no report).</param>
/// <param name="Warnings">What to look at that does not block the worker: the summary's warnings, else the WARN and non-blocking FAIL checks.</param>
/// <param name="Failures">Blockers of a not-ready machine: the summary's blockers, else its FAIL checks (0 when ready or without a report).</param>
/// <param name="ExitCode">What <c>lanq makeready &lt;machine&gt;</c> would exit with: 0, 1, 70 or 75.</param>
/// <param name="Problem">The first FAIL check, else the first WARN (title: detail), or why there is no report; absent when there is nothing.</param>
/// <param name="Report">The validated lanq-makeready/v1 report, exactly as <c>lanq makeready &lt;machine&gt; --json</c> prints it (sanitised and filtered, 0.9.2); absent without one.</param>
internal sealed record MakereadyRowJson(
    string Machine, string Os, string Verdict, int Warnings, int Failures, int ExitCode, string? Problem, Makeready.MakereadyReportJson? Report)
{
    /// <summary>What to do about this machine (see <see cref="NextStepJson"/>).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>The one object <c>lanq makeready --all --json</c> prints: Ok (0), Failing (1: a machine is NOT READY), InfraError (70: unreachable or unusable) or Held (75), and every machine in config order.</summary>
internal sealed record MakereadyAllJson(string Status, int ExitCode, IReadOnlyList<MakereadyRowJson> Machines)
{
    /// <summary>What to do next for the whole check (see <see cref="NextStepJson"/>).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>
/// The one object <c>lanq init --json</c> prints: Created (0), Rejected (2: the file exists or bad arguments) or InfraError (70: it could not
/// be written). <paramref name="Path"/> is the full path of the config file (null only when the arguments were refused).
/// </summary>
internal sealed record InitJson(string Status, int ExitCode, string? Path, string Message)
{
    /// <summary>What to do next (see <see cref="NextStepJson"/>).</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>
/// The one object <c>lanq makeready --ssh ... --add --json</c> prints on every path: Added (0), NotAdded (1: NOT READY, or declined),
/// Rejected (2: arguments, config, a duplicate, or a file lanq does not edit by itself), InfraError (70: ssh, an unusable report, or the
/// write failed) or Cancelled (130). Every member is always written; the embedded report keeps exactly the fields it has in lanq makeready --json.
/// </summary>
/// <param name="Path">The full path of machines.yml, or null before it was found.</param>
/// <param name="Machine">The new machine's name (--name), or null when the arguments were refused.</param>
/// <param name="Entry">The lines written (Added) or that would have been written, exactly; null before there was an entry.</param>
/// <param name="AfterLine">The 1-based line of machines.yml the entry follows; null without an entry.</param>
/// <param name="Health">One read-only probe of the added machine (as a lanq health --json row); null unless Added.</param>
/// <param name="Report">The validated lanq-makeready/v1 report, as <c>lanq makeready --json</c> prints it (sanitised and filtered, 0.9.2); null when there was none.</param>
internal sealed record MakereadyAddJson(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Machine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Entry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? AfterLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HealthJson? Health,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Makeready.MakereadyReportJson? Report,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Message)
{
    /// <summary>What to do next (see <see cref="NextStepJson"/>): Added suggests lanq doctor --remote; never --add or --yes.</summary>
    public IReadOnlyList<NextStepJson> NextSteps { get; init; } = [];
}

/// <summary>
/// Source-generated serialization for <c>lanq makeready --all --json</c> and <c>--add --json</c>. Null members are left out (except where a
/// member says otherwise), as in the single-machine report, so each embedded report keeps exactly the fields it has there (the report records
/// carry their own JSON names).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 16)]
[JsonSerializable(typeof(MakereadyAllJson))]
[JsonSerializable(typeof(MakereadyAddJson))]
[JsonSerializable(typeof(NextStepJson))]
internal sealed partial class MakereadyAllJsonContext : JsonSerializerContext;
