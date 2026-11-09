using System.Text.Json.Serialization;

namespace LanQueue.Output;

/// <summary>One machine in <c>lanq health --json</c>.</summary>
/// <param name="State">ok, hold or down.</param>
/// <param name="Reasons">Why the machine is held or down; empty when ok.</param>
/// <param name="MemFreeGb">Null when the machine did not report memory.</param>
/// <param name="LocalTime">The worker's own clock (yyyy-MM-ddTHH:mm:ss), or null when it did not report one.</param>
internal sealed record HealthJson(
    string Name, string State, IReadOnlyList<string> Reasons, double Load, double DiskFreeGb, double? MemFreeGb, int SlotsUsed, int MaxJobs, string? LocalTime);

/// <summary>The one object <c>lanq run --json</c> prints on stdout when the job is over.</summary>
/// <param name="Status">Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut or WindowClosed.</param>
/// <param name="ExitCode">lanq's own exit code (the job's code, or 2, 70, 75, 124).</param>
internal sealed record RunJson(string Status, int ExitCode, string? Machine, string JobId, double DurationSeconds, string Message);

/// <summary>Source-generated serialization (no reflection) for everything lanq prints as JSON.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HealthJson>))]
[JsonSerializable(typeof(RunJson))]
internal sealed partial class LanqJson : JsonSerializerContext;
