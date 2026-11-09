using System.Text.Json.Serialization;

namespace LanQueue.Output;

/// <summary>One machine in <c>lanq health --json</c>.</summary>
/// <param name="State">ok, hold or down.</param>
/// <param name="Reasons">Why the machine is held or down; empty when ok.</param>
/// <param name="MemFreeGb">Null when the machine did not report memory.</param>
/// <param name="LocalTime">The worker's own clock (yyyy-MM-ddTHH:mm:ss), or null when it did not report one.</param>
internal sealed record HealthJson(
    string Name, string State, IReadOnlyList<string> Reasons, double Load, double DiskFreeGb, double? MemFreeGb, int SlotsUsed, int MaxJobs, string? LocalTime);

/// <summary>
/// The one object <c>lanq run --json</c> prints on stdout when the job is over, and also when it never started (bad arguments or config:
/// Rejected, exit 2), was cancelled (Cancelled, 130) or hit an unexpected error (InfraError, 70); then machine and jobId are null.
/// </summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed or Cancelled.</param>
/// <param name="ExitCode">lanq's own exit code (the job's code, or 2, 70, 75, 124, 130).</param>
internal sealed record RunJson(string Status, int ExitCode, string? Machine, string? JobId, double DurationSeconds, string Message)
{
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
[JsonSerializable(typeof(ErrorJson))]
[JsonSerializable(typeof(WakeJson))]
[JsonSerializable(typeof(InitJson))]
internal sealed partial class LanqJson : JsonSerializerContext;

/// <summary>One machine in <c>lanq makeready --all --json</c>.</summary>
/// <param name="Verdict">OK, WARN or FAIL (from the report's checks), or unreachable, held or error (no report).</param>
/// <param name="Warnings">The report's WARN checks (0 without a report).</param>
/// <param name="Failures">The report's FAIL checks (0 without a report).</param>
/// <param name="ExitCode">What <c>lanq makeready &lt;machine&gt;</c> would exit with: 0, 1, 70 or 75.</param>
/// <param name="Problem">The first FAIL check, else the first WARN (title: detail), or why there is no report; absent when there is nothing.</param>
/// <param name="Report">The validated lanq-makeready/v1 report, exactly as <c>lanq makeready &lt;machine&gt; --json</c> prints it; absent without one.</param>
internal sealed record MakereadyRowJson(
    string Machine, string Os, string Verdict, int Warnings, int Failures, int ExitCode, string? Problem, Makeready.MakereadyReport? Report);

/// <summary>The one object <c>lanq makeready --all --json</c> prints: Ok (0), Failing (1: a FAIL check), InfraError (70: unreachable or unusable) or Held (75), and every machine in config order.</summary>
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
