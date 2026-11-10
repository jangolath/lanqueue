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
    [property: JsonPropertyName("summary")] MakereadySummary? Summary,
    [property: JsonPropertyName("suitedFor")] MakereadySuitedFor? SuitedFor,
    [property: JsonPropertyName("checks")] List<MakereadyCheck>? Checks,
    [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
    [property: JsonPropertyName("suggestedMachine")] MakereadySuggestion? SuggestedMachine)
{
    internal const string SchemaV1 = "lanq-makeready/v1";

    /// <summary>The largest report lanq reads; a worker that sends more is cut off and the report is refused.</summary>
    internal const int MaxChars = 1024 * 1024;

    private static readonly string[] Statuses = ["OK", "WARN", "FAIL", "SKIP"];

    private static readonly string[] SummaryVerdicts = ["READY", "READY_WITH_WARNINGS", "NOT_READY"];

    private static readonly string[] Tiers = ["light", "medium", "heavy"];

    /// <summary>Ready when at least one transfer mode is not FAIL: the same rule the script uses for its own exit code.</summary>
    [JsonIgnore]
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

        // Optional since lanq-makeready 0.2.0; when present they must have the documented shape.
        if (report.Summary is { } s
            && (!SummaryVerdicts.Contains(s.Verdict, StringComparer.Ordinal) || s.Text is null || s.Blockers < 0 || s.Warnings < 0 || s.NextSteps is null
                || s.NextSteps.Any(st => st is null || st.Step < 1 || st.Status is not ("WARN" or "FAIL"))))
        {
            throw new FormatException("summary is not verdict/text/blockers/warnings/nextSteps as documented");
        }

        if (report.SuitedFor is { } f && (!Tiers.Contains(f.Tier, StringComparer.Ordinal) || f.Text is null))
        {
            throw new FormatException("suitedFor is not tier/containers/text as documented");
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

/// <summary>The script's one-line verdict and its numbered next steps (every WARN/FAIL check, blockers first). Optional, 0.2.0+.</summary>
internal sealed record MakereadySummary(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("blockers")] int Blockers,
    [property: JsonPropertyName("warnings")] int Warnings,
    [property: JsonPropertyName("nextSteps")] List<MakereadyStep>? NextSteps);

internal sealed record MakereadyStep(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("blocker")] bool Blocker,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("adminToFix")] bool AdminToFix);

/// <summary>Job sizing hint (light, medium, heavy) and its text. Optional, 0.2.0+.</summary>
internal sealed record MakereadySuitedFor(
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("containers")] bool Containers,
    [property: JsonPropertyName("text")] string? Text);

/// <summary>A suggested Wake-on-LAN block (only when the script's wol check is OK; mac is &lt;mac&gt; when redacted). Optional, 0.2.0+.</summary>
internal sealed record MakereadyWake([property: JsonPropertyName("mac")] string? Mac);

internal sealed record MakereadyCheck(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("advice")] string? Advice,
    [property: JsonPropertyName("needsAdmin")] bool NeedsAdmin);

/// <summary>
/// The suggested machines.yml entry. <see cref="PathPrepend"/> and <see cref="Env"/> (optional, 0.3.0+) come only when the newest .NET SDK
/// is in a folder that is not the dotnet on PATH: that folder, and DOTNET_ROOT set to it.
/// </summary>
internal sealed record MakereadySuggestion(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("os")] string? Os,
    [property: JsonPropertyName("ssh")] string? Ssh,
    [property: JsonPropertyName("work_root")] string? WorkRoot,
    [property: JsonPropertyName("capabilities")] List<string>? Capabilities,
    [property: JsonPropertyName("max_jobs")] int MaxJobs,
    [property: JsonPropertyName("min_free_mem_gb")] double? MinFreeMemGb,
    [property: JsonPropertyName("transfer")] string? Transfer,
    [property: JsonPropertyName("allowed_hours")] string? AllowedHours,
    [property: JsonPropertyName("wake")] MakereadyWake? Wake,
    [property: JsonPropertyName("path_prepend")] List<string>? PathPrepend = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null);

/// <summary>Source-generated (de)serialization for the report: no reflection. Depth is capped well below the default.</summary>
[JsonSourceGenerationOptions(MaxDepth = 16, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MakereadyReport))]
internal sealed partial class MakereadyJson : JsonSerializerContext;
