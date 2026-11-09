using System.Text.Json.Serialization;

namespace LanQueue.Output;

/// <summary>One machine in <c>lanq health --json</c>.</summary>
/// <param name="State">ok, hold or down.</param>
/// <param name="Reasons">Why the machine is held or down; empty when ok.</param>
/// <param name="MemFreeGb">Null when the machine did not report memory.</param>
/// <param name="LocalTime">The worker's own clock (yyyy-MM-ddTHH:mm:ss), or null when it did not report one.</param>
internal record HealthJson(
    string Name, string State, IReadOnlyList<string> Reasons, double Load, double DiskFreeGb, double? MemFreeGb, int SlotsUsed, int MaxJobs, string? LocalTime)
{
    /// <summary>Logical processors, or null when the machine did not report them (down, or an older probe).</summary>
    public int? Cores { get; init; }

    /// <summary>Total memory in GB, or null when not reported.</summary>
    public double? MemTotalGb { get; init; }

    /// <summary>max_jobs minus the slots in use (0 for a machine that is down).</summary>
    public int FreeSlots { get; init; }

    /// <summary>light, medium, heavy or unknown (see <see cref="Health.MachineSize"/>).</summary>
    public string Size { get; init; } = "unknown";

    /// <summary>What the machine suits, in lanq-makeready's words, for example "light: lint and unit-test shards + container builds".</summary>
    public string SuitedFor { get; init; } = "";
}

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
}

/// <summary>One machine in excluded (NoCapacity only).</summary>
/// <param name="Reason">
/// missing_capability, windows_unsafe_args, mem_exceeds_total, mem_unknown, tool_missing (permanent); unreachable, asleep, unreadable, paused,
/// outside_hours, window_closing, low_disk, low_memory, memory_pressure, load, mem_below_request, busy, avoided, held (waiting may help).
/// </param>
/// <param name="Permanent">True when waiting can never make this machine take the job (its config or the machine itself rules it out).</param>
internal sealed record ExcludedJson(string Machine, string Reason, string Why, bool Permanent);

/// <summary>
/// The one object <c>lanq run --dry-run --json</c> prints on stdout: what lanq run would do now. Every field is always present (null where it
/// does not apply), except logTail (only with --log-tail, and then always empty: a dry run starts no job). Fields are only ever added (a
/// later version adds nextSteps here).
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
}

/// <summary>One machine in <c>lanq run --dry-run --json</c>: as the health --needs rows judge it (reason codes as in <see cref="ExcludedJson.Reason"/>).</summary>
/// <param name="FreeSlots">max_jobs minus the slots in use.</param>
/// <param name="JobSlots">How many copies of the job (shards) it could take now; 0 when not eligible.</param>
internal sealed record PlanMachineJson(string Machine, bool Eligible, string? Reason, string? Why, bool Permanent, int FreeSlots, int JobSlots);

/// <summary>--shards --dry-run: the shards one machine would take at the start.</summary>
internal sealed record PlacementJson(string Machine, IReadOnlyList<int> Shards);

/// <summary>One fetched file in <c>lanq run --json</c>.</summary>
internal sealed record FetchedJson(string Path, long Bytes, string Sha256);

/// <summary>One skipped file in <c>lanq run --json</c>.</summary>
internal sealed record FetchSkipJson(string Path, string Reason);

/// <summary>
/// What <c>lanq health --json</c> and <c>lanq makeready --json</c> print instead of their normal output when there is none: Rejected (2),
/// InfraError (70), Held (75, makeready only) or Cancelled (130).
/// </summary>
internal sealed record ErrorJson(string Status, int ExitCode, string Message);

/// <summary>The one object <c>lanq wake --json</c> prints when a packet was sent: Sent (0), Awake (0, with --wait) or NoAnswer (1, with --wait). Failures use <see cref="ErrorJson"/>.</summary>
/// <param name="Awake">Null without --wait (not checked).</param>
internal sealed record WakeJson(string Status, int ExitCode, string Machine, bool Sent, bool? Awake, double DurationSeconds, string Message);

/// <summary>Source-generated serialization (no reflection) for everything lanq prints as JSON.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HealthJson>))]
[JsonSerializable(typeof(List<HealthNeedsJson>))]
[JsonSerializable(typeof(RunJson))]
[JsonSerializable(typeof(ShardRunJson))]
[JsonSerializable(typeof(ExcludedJson))]
[JsonSerializable(typeof(ErrorJson))]
[JsonSerializable(typeof(WakeJson))]
[JsonSerializable(typeof(InitJson))]
[JsonSerializable(typeof(PlanJson))]
internal sealed partial class LanqJson : JsonSerializerContext;

/// <summary>One machine in <c>lanq makeready --all --json</c>.</summary>
/// <param name="Verdict">FAIL (not worker-ready), WARN (something to look at) or OK, or unreachable, held or error (no report).</param>
/// <param name="Warnings">What to look at that does not block the worker: the summary's warnings, else the WARN and non-blocking FAIL checks.</param>
/// <param name="Failures">Blockers of a not-ready machine: the summary's blockers, else its FAIL checks (0 when ready or without a report).</param>
/// <param name="ExitCode">What <c>lanq makeready &lt;machine&gt;</c> would exit with: 0, 1, 70 or 75.</param>
/// <param name="Problem">The first FAIL check, else the first WARN (title: detail), or why there is no report; absent when there is nothing.</param>
/// <param name="Report">The validated lanq-makeready/v1 report, exactly as <c>lanq makeready &lt;machine&gt; --json</c> prints it; absent without one.</param>
internal sealed record MakereadyRowJson(
    string Machine, string Os, string Verdict, int Warnings, int Failures, int ExitCode, string? Problem, Makeready.MakereadyReport? Report);

/// <summary>The one object <c>lanq makeready --all --json</c> prints: Ok (0), Failing (1: a machine is NOT READY), InfraError (70: unreachable or unusable) or Held (75), and every machine in config order.</summary>
internal sealed record MakereadyAllJson(string Status, int ExitCode, IReadOnlyList<MakereadyRowJson> Machines);

/// <summary>
/// The one object <c>lanq init --json</c> prints: Created (0), Rejected (2: the file exists or bad arguments) or InfraError (70: it could not
/// be written). <paramref name="Path"/> is the full path of the config file (null only when the arguments were refused).
/// </summary>
internal sealed record InitJson(string Status, int ExitCode, string? Path, string Message);

/// <summary>
/// Source-generated serialization for <c>lanq makeready --all --json</c>. Null members are left out, as in the single-machine report, so
/// each embedded report keeps exactly the fields it has there (the report records carry their own JSON names).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 16)]
[JsonSerializable(typeof(MakereadyAllJson))]
internal sealed partial class MakereadyAllJsonContext : JsonSerializerContext;
