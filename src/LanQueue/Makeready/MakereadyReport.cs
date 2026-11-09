using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanQueue.Makeready;

/// <summary>
/// A lanq-makeready report (schema lanq-makeready/v1), as read back from a worker. It is UNTRUSTED input: it is size-capped before parsing,
/// parsed with a source-generated context (no reflection, no polymorphism), validated, and every string is sanitised before display.
/// Nothing in it is ever executed: advice is printed for the PC's owner to read.
/// </summary>
internal sealed record MakereadyReport(
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("tool")] MakereadyTool? Tool,
    [property: JsonPropertyName("generatedAt")] string? GeneratedAt,
    [property: JsonPropertyName("host")] MakereadyHost? Host,
    [property: JsonPropertyName("verdicts")] MakereadyVerdicts? Verdicts,
    [property: JsonPropertyName("checks")] List<MakereadyCheck>? Checks,
    [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
    [property: JsonPropertyName("suggestedMachine")] MakereadySuggestion? SuggestedMachine)
{
    internal const string SchemaV1 = "lanq-makeready/v1";

    /// <summary>The largest report lanq reads; a worker that sends more is cut off and the report is refused.</summary>
    internal const int MaxChars = 1024 * 1024;

    private static readonly string[] Statuses = ["OK", "WARN", "FAIL", "SKIP"];

    /// <summary>Ready when at least one transfer mode is not FAIL: the same rule the script uses for its own exit code.</summary>
    public bool WorkerReady => Verdicts!.WorkerGit != "FAIL" || Verdicts.WorkerArchive != "FAIL";

    /// <summary>Parses and validates; throws <see cref="FormatException"/> with a short reason (never echoing the input).</summary>
    public static MakereadyReport Parse(string json)
    {
        if (json.Length > MaxChars)
        {
            throw new FormatException("report larger than 1 MB");
        }

        MakereadyReport? report;
        try
        {
            report = JsonSerializer.Deserialize(json, MakereadyJson.Default.MakereadyReport);
        }
        catch (JsonException)
        {
            throw new FormatException("not valid JSON");
        }

        if (report is null || report.Schema != SchemaV1)
        {
            throw new FormatException($"schema is not {SchemaV1}");
        }

        var v = report.Verdicts ?? throw new FormatException("verdicts missing");
        if (new[] { v.WorkerGit, v.WorkerArchive, v.Dotnet10, v.Docker, v.Overnight }.Any(s => !Statuses.Contains(s, StringComparer.Ordinal)))
        {
            throw new FormatException("a verdict is missing or not OK/WARN/FAIL/SKIP");
        }

        if (report.Checks is null || report.Checks.Any(c => c is null || !Statuses.Contains(c.Status, StringComparer.Ordinal)))
        {
            throw new FormatException("checks missing, or a check status is not OK/WARN/FAIL/SKIP");
        }

        if (report.Capabilities is null || report.Capabilities.Any(c => c is null) || report.SuggestedMachine is null)
        {
            throw new FormatException("capabilities or suggestedMachine missing");
        }

        return report;
    }
}

internal sealed record MakereadyTool([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("version")] string? Version);

internal sealed record MakereadyHost(
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("os")] string? Os,
    [property: JsonPropertyName("arch")] string? Arch);

internal sealed record MakereadyVerdicts(
    [property: JsonPropertyName("worker_git")] string? WorkerGit,
    [property: JsonPropertyName("worker_archive")] string? WorkerArchive,
    [property: JsonPropertyName("dotnet10")] string? Dotnet10,
    [property: JsonPropertyName("docker")] string? Docker,
    [property: JsonPropertyName("overnight")] string? Overnight);

internal sealed record MakereadyCheck(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("advice")] string? Advice,
    [property: JsonPropertyName("needsAdmin")] bool NeedsAdmin);

internal sealed record MakereadySuggestion(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("os")] string? Os,
    [property: JsonPropertyName("ssh")] string? Ssh,
    [property: JsonPropertyName("work_root")] string? WorkRoot,
    [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
    [property: JsonPropertyName("max_jobs")] int MaxJobs,
    [property: JsonPropertyName("transfer")] string? Transfer,
    [property: JsonPropertyName("allowed_hours")] string? AllowedHours);

/// <summary>Source-generated (de)serialization for the report: no reflection. Depth is capped well below the default.</summary>
[JsonSourceGenerationOptions(MaxDepth = 16, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MakereadyReport))]
internal sealed partial class MakereadyJson : JsonSerializerContext;
