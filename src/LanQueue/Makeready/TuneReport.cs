using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Jobs;

namespace LanQueue.Makeready;

/// <summary>
/// A lanq-makeready-tune report (schema lanq-makeready-tune/v1, lanq-makeready 0.5.0+), as read back from a worker by
/// <c>lanq makeready &lt;machine&gt; --tune</c>. It is UNTRUSTED input, like the scanner's report: size-capped (256 KB) before parsing,
/// parsed with a source-generated context (no reflection, depth 8), validated, then rebuilt by <see cref="ForJson"/> with every string
/// sanitised and cut to length, fixId taken from lanq's own table and security from lanq's own set. Nothing in it is ever executed.
/// </summary>
internal sealed partial record TuneReport(
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("tool")] MakereadyTool? Tool,
    [property: JsonPropertyName("generatedAt")] string? GeneratedAt,
    [property: JsonPropertyName("host")] MakereadyHost? Host,
    [property: JsonPropertyName("context")] TuneContext? Context,
    [property: JsonPropertyName("items")] List<TuneItem>? Items,
    [property: JsonPropertyName("summary")] TuneSummary? Summary)
{
    internal const string SchemaV1 = "lanq-makeready-tune/v1";

    internal const string ToolName = "lanq-makeready-tune";

    /// <summary>The largest tune report lanq reads; a worker that sends more is cut off and the report is refused.</summary>
    internal const int MaxChars = 256 * 1024;

    /// <summary>The most items a report may have.</summary>
    internal const int MaxItems = 64;

    internal const int TitleMax = 120;
    internal const int TextMax = 1024;
    internal const int HostMax = 128;
    internal const int ArchMax = 32;

    private static readonly string[] Statuses = ["OK", "INFO", "WARN", "SKIP"];

    // \z, not $: an id with a trailing newline is not a clean one.
    [GeneratedRegex(@"^[a-z0-9]{1,16}(\.[a-z0-9]{1,16}){0,2}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdRegex();

    /// <summary>Parses and validates; throws <see cref="FormatException"/> with a short reason (never echoing the input).</summary>
    public static TuneReport Parse(string json)
    {
        if (json.Length > MaxChars)
        {
            throw new FormatException("report larger than 256 KB");
        }

        TuneReport? report;
        try
        {
            report = JsonSerializer.Deserialize(json, TuneJson.Default.TuneReport);
        }
        catch (JsonException)
        {
            throw new FormatException("not valid JSON");
        }

        if (report is null || report.Schema != SchemaV1 || report.Tool?.Name != ToolName)
        {
            throw new FormatException($"schema is not {SchemaV1} from {ToolName}");
        }

        if (report.Items is not { } items || items.Count > MaxItems)
        {
            throw new FormatException($"items missing, or more than {MaxItems}");
        }

        if (items.Any(i => i is null || i.Id is null || !IdRegex().IsMatch(i.Id)))
        {
            throw new FormatException("an item id is missing or not a lanq-makeready-tune id");
        }

        if (items.Any(i => !Statuses.Contains(i.Status, StringComparer.Ordinal)))
        {
            throw new FormatException("an item status is not OK/INFO/WARN/SKIP");
        }

        if (report.Summary is not { } s
            || s.Ok != Count("OK") || s.Info != Count("INFO") || s.Warn != Count("WARN") || s.Skip != Count("SKIP"))
        {
            throw new FormatException("summary missing, or its counts do not match the items");
        }

        return report;

        int Count(string status) => items.Count(i => i.Status == status);
    }

    /// <summary>
    /// The fix id lanq-makeready-fix 0.5.0 (-Tune / --tune) uses for a tune item on <paramref name="os"/>, from lanq's own table, or null. Only
    /// INFO and WARN items have one (power.usbsuspend only WARN). A test keeps the table equal to the fix ids in the embedded scripts.
    /// </summary>
    internal static string? TuneFix(MachineOs os, string id, string status) => (os, id, status) switch
    {
        (_, _, not ("INFO" or "WARN")) => null,
        (MachineOs.Windows, "power.plan", _) => "power.cpu",
        (MachineOs.Windows, "power.usbsuspend", "WARN") => "power.usbsuspend",
        (MachineOs.Windows, "startup.vendor", _) => "startup.vendor",
        (MachineOs.Windows, "vendor.services", _) => "vendor.services",
        (MachineOs.Windows, "vendor.tasks", _) => "vendor.tasks",
        (MachineOs.Windows, "sysmain", _) => "sysmain",
        (MachineOs.Windows, "do.mode", _) => "do.mode",
        (MachineOs.Windows, "dotnet.env", _) => "dotnet.env",
        (MachineOs.Windows, "defender.schedule", _) => "defender.scantime",
        (MachineOs.Linux, "sysctl.inotify", _) => "sysctl.tune",
        (MachineOs.Linux, "journald.size", _) => "journald.cap",
        (MachineOs.Linux, "services.unused", _) => "services.unused",
        (MachineOs.Linux, "fstrim", _) => "fstrim.timer",
        _ => null,
    };

    /// <summary>Every fix id in <see cref="TuneFix"/>, per OS (for the table test).</summary>
    internal static readonly IReadOnlyDictionary<MachineOs, string[]> FixIds = new Dictionary<MachineOs, string[]>
    {
        [MachineOs.Windows] = ["power.cpu", "power.usbsuspend", "startup.vendor", "vendor.services", "vendor.tasks", "sysmain", "do.mode", "dotnet.env", "defender.scantime"],
        [MachineOs.Linux] = ["sysctl.tune", "journald.cap", "services.unused", "fstrim.timer"],
    };

    /// <summary>The items lanq may mark as security relevant on <paramref name="os"/>; the worker's own flag counts only for these.</summary>
    internal static bool SecurityItem(MachineOs os, string id) => os switch
    {
        MachineOs.Windows => id is "defender.paths" or "av.other" or "autologon.plain",
        _ => id is "services.unused",
    };

    /// <summary>
    /// The report as lanq prints it (lanqTune.report): only the documented keys, every string through <see cref="JobRunner.SafeLine"/> and cut
    /// to its length (title 120, detail and advice 1024, host fields 128, arch 32), fixId only when the worker's value equals lanq's table
    /// (<see cref="TuneFix"/>), security only when the worker says so AND the item is in lanq's set (<see cref="SecurityItem"/>).
    /// </summary>
    internal TuneReport ForJson(MachineOs os)
    {
        static string? S(string? v, int max) => v is null ? null : Cli.Truncate(JobRunner.SafeLine(v), max);
        return new TuneReport(
            Schema,
            new MakereadyTool(Tool!.Name, S(Tool.Version, 32)),
            S(GeneratedAt, 32),
            Host is { } h ? new MakereadyHost(S(h.Hostname, HostMax), S(h.User, HostMax), S(h.Os, HostMax), S(h.Arch, ArchMax)) : null,
            Context is { } c ? new TuneContext(c.Container, c.Elevated, S(c.WorkRoot, 256), S(c.Hours, 16)) : null,
            [.. Items!.Select(i =>
            {
                var fix = TuneFix(os, i.Id!, i.Status!) is { } f && f == i.FixId ? f : null;
                return new TuneItem(i.Id, S(i.Title, TitleMax), i.Status, S(i.Detail, TextMax), S(i.Advice, TextMax), i.NeedsAdmin, fix, i.Security && SecurityItem(os, i.Id!));
            })],
            Summary);
    }
}

/// <summary>Where the tune scan ran: in a container, elevated, and the work_root and hours it was given (each optional).</summary>
internal sealed record TuneContext(
    [property: JsonPropertyName("container")] bool Container,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("workRoot")] string? WorkRoot,
    [property: JsonPropertyName("hours")] string? Hours);

/// <summary>One tune item. <see cref="FixId"/> is always written (null when lanq-makeready-fix has no fix for it).</summary>
internal sealed record TuneItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("advice")] string? Advice,
    [property: JsonPropertyName("needsAdmin")] bool NeedsAdmin,
    [property: JsonPropertyName("fixId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FixId,
    [property: JsonPropertyName("security")] bool Security);

/// <summary>The counts per status; seconds only from the Windows script.</summary>
internal sealed record TuneSummary(
    [property: JsonPropertyName("ok")] int Ok,
    [property: JsonPropertyName("info")] int Info,
    [property: JsonPropertyName("warn")] int Warn,
    [property: JsonPropertyName("skip")] int Skip,
    [property: JsonPropertyName("seconds")] int? Seconds = null);

/// <summary>
/// lanqTune: lanq's own object (never read from a worker) in <c>lanq makeready --tune --json</c>, inside the report (one machine) or each
/// --all row's report. <see cref="Status"/> is Ok, TimedOut, Unreachable or Unusable (values may be added); <see cref="Report"/> only when
/// Ok; <see cref="Error"/> only when not. The tune scan never changes the exit code.
/// </summary>
internal sealed record MakereadyTuneJson(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("report")] TuneReport? Report,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<TuneNextStepJson> NextSteps);

/// <summary>
/// One next step inside lanqTune, with the same keys as every nextSteps entry (see Output.NextStepJson), named explicitly: the single-machine
/// report is written by a context without a naming policy.
/// </summary>
internal sealed record TuneNextStepJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("command"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Command,
    [property: JsonPropertyName("argv"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Argv,
    [property: JsonPropertyName("safeToRunUnattended")] bool SafeToRunUnattended);

/// <summary>Source-generated parsing of the tune report: no reflection, depth capped at 8 (the report needs 4).</summary>
[JsonSourceGenerationOptions(MaxDepth = 8)]
[JsonSerializable(typeof(TuneReport))]
internal sealed partial class TuneJson : JsonSerializerContext;
