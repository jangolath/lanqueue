using System.Text.Json.Serialization;

namespace LanQueue.Output;

/// <summary>One machine in <c>lanq health --json</c>.</summary>
/// <param name="State">ok, hold or down.</param>
/// <param name="Reasons">Why the machine is held or down; empty when ok.</param>
/// <param name="MemFreeGb">Null when the machine did not report memory.</param>
/// <param name="LocalTime">The worker's own clock (yyyy-MM-ddTHH:mm:ss), or null when it did not report one.</param>
internal sealed record HealthJson(
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

    /// <summary>What the machine suits, for example "light: lint, type-check, unit-test shards + container builds".</summary>
    public string SuitedFor { get; init; } = "";
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
}

/// <summary>
/// The one object <c>lanq run --shards --json</c> prints on stdout: the overall status and exit code, N, and one <see cref="RunJson"/> per
/// shard (with shard and attempts) in index order. shards is empty when nothing ran (refused before anything was sent, or bad arguments).
/// </summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed or Cancelled.</param>
internal sealed record ShardRunJson(string Status, int ExitCode, int ShardCount, double DurationSeconds, string Message, IReadOnlyList<RunJson> Shards);

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
[JsonSerializable(typeof(RunJson))]
[JsonSerializable(typeof(ShardRunJson))]
[JsonSerializable(typeof(ErrorJson))]
[JsonSerializable(typeof(WakeJson))]
internal sealed partial class LanqJson : JsonSerializerContext;
