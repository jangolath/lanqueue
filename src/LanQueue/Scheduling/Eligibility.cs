using LanQueue.Config;
using LanQueue.Health;

namespace LanQueue.Scheduling;

/// <summary>What one job asks of a machine: every capability, --mem (GB of available memory), and whether its arguments are unsafe for Windows cmd shims.</summary>
public sealed record JobNeeds(IReadOnlyCollection<string> Capabilities, double? MemGb, bool WindowsUnsafe)
{
    /// <summary>--size / --min-size: the size classes the job may run on, or null (any size).</summary>
    public SizeRule? Size { get; init; }
}

/// <summary>
/// --size C[,C] (<paramref name="AnyOf"/>: one of these classes) or --min-size C (<paramref name="AtLeast"/>: that class or larger); exactly
/// one of the two is set. A machine's class is its size: key when it has one, else the probed class (<see cref="MachineSize.Effective"/>).
/// </summary>
public sealed record SizeRule(IReadOnlySet<SizeClass>? AnyOf, SizeClass? AtLeast)
{
    /// <summary>Whether a machine of class <paramref name="size"/> may take the job; never for <see cref="SizeClass.Unknown"/>.</summary>
    public bool Allows(SizeClass size) =>
        size != SizeClass.Unknown && (AnyOf is { } any ? any.Contains(size) : AtLeast is not { } least || size >= least);

    /// <summary>The option as typed back: "--size light,medium" (classes in size order) or "--min-size heavy".</summary>
    public string Text => Option + " " + Value;

    /// <summary>--size or --min-size.</summary>
    public string Option => AnyOf is not null ? "--size" : "--min-size";

    /// <summary>The option's value: "light,medium" or "heavy".</summary>
    public string Value => AnyOf is { } any
        ? string.Join(',', any.Order().Select(MachineSize.Word))
        : MachineSize.Word(AtLeast ?? SizeClass.Unknown);

    /// <summary>--size: a comma list of light, medium, heavy (any case; repeats are fine). Throws <see cref="ArgumentException"/> for anything else.</summary>
    public static SizeRule AnyOfText(string text)
    {
        var words = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<SizeClass>();
        foreach (var w in words)
        {
            set.Add(MachineSize.Parse(w) ?? throw new ArgumentException($"--size takes light, medium or heavy, or a comma list of them such as --size light,medium (got '{w}')."));
        }

        return set.Count > 0 ? new SizeRule(set, null) : throw new ArgumentException("--size takes light, medium or heavy, or a comma list of them such as --size light,medium.");
    }

    /// <summary>--min-size: one of light, medium, heavy (any case). Throws <see cref="ArgumentException"/> for anything else.</summary>
    public static SizeRule AtLeastText(string text) =>
        MachineSize.Parse(text.Trim()) is { } least
            ? new SizeRule(null, least)
            : throw new ArgumentException($"--min-size takes one of light, medium or heavy (got '{text}').");
}

/// <summary>--on m[,m]: the machines a job (or lanq health) is narrowed to, by name.</summary>
public static class Pinning
{
    /// <summary>
    /// The --on names in the config's own spelling (matched ignoring case, in the order given, repeats dropped), or the refusal: "--on names
    /// a machine that is not in the config: big-02. Configured: build-01, big-01, micro-01." Null names (no --on) give null.
    /// </summary>
    public static (IReadOnlyList<string>? Names, string? Error) Resolve(FleetConfig config, IReadOnlyList<string>? on)
    {
        if (on is null)
        {
            return (null, null);
        }

        var names = new List<string>();
        var unknown = new List<string>();
        foreach (var n in on)
        {
            if (config.Machines.FirstOrDefault(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase)) is { } m)
            {
                if (!names.Contains(m.Name, StringComparer.Ordinal))
                {
                    names.Add(m.Name);
                }
            }
            else
            {
                unknown.Add(n);
            }
        }

        return unknown.Count > 0
            ? (null, $"--on names a machine that is not in the config: {string.Join(", ", unknown)}. Configured: {string.Join(", ", config.Machines.Select(m => m.Name))}.")
            : (names, null);
    }

    /// <summary>The machines of <paramref name="config"/> the job may use, in config order: every one without --on, else only those named (ignoring case).</summary>
    public static List<Machine> Candidates(FleetConfig config, IReadOnlyList<string>? on) =>
        on is null ? config.Machines : [.. config.Machines.Where(m => on.Contains(m.Name, StringComparer.OrdinalIgnoreCase))];
}

/// <summary>
/// Why a machine cannot take a job, in the order the reasons are looked at: the permanent ones (the config or the machine itself rules it
/// out) first, then unreachable and holds, then --mem, then a busy machine. <see cref="None"/> means it can take the job now.
/// </summary>
public enum Exclusion
{
    None,
    MissingCapability,

    /// <summary>--size / --min-size: the machine's class (declared size:, or probed) is not one the job allows.</summary>
    WrongSize,
    WindowsUnsafeArgs,
    MemExceedsTotal,
    MemUnknown,

    /// <summary>--size / --min-size: the machine has no size: key and did not report its total memory, so its class is unknown.</summary>
    SizeUnknown,
    ToolMissing,
    Unreachable,
    Asleep,
    Unreadable,
    Paused,
    OutsideHours,
    WindowClosing,
    LowDisk,
    LowMemory,
    MemoryPressure,
    Load,
    MemBelowRequest,
    Busy,

    /// <summary>Shards only: every waiting shard must avoid the machine (an earlier attempt lost the connection there and may still run).</summary>
    Avoided,

    /// <summary>A hold of no known kind (never produced by the probe itself); temporary.</summary>
    Held,
}

/// <summary>One machine judged for one job.</summary>
/// <param name="Health">The probe result with this job's own holds added (arguments unsafe for Windows, --mem), as the runner uses it.</param>
/// <param name="Reason">The first reason (in <see cref="Exclusion"/> order) the machine cannot take the job, or <see cref="Exclusion.None"/>.</param>
/// <param name="Why">The reason for people (the hold, ssh error, missing capability), or null when eligible.</param>
/// <param name="Permanent">True when waiting cannot help: the config or the machine itself rules it out for this job.</param>
/// <param name="FreeSlots">max_jobs minus the slots in use (never below 0).</param>
/// <param name="JobSlots">How many copies of this job the machine could take now (see <see cref="Scheduler.FreeFor"/>); 0 when not eligible.</param>
public sealed record Assessment(Machine Machine, MachineHealth Health, Exclusion Reason, string? Why, bool Permanent, int FreeSlots, int JobSlots)
{
    public bool Eligible => Reason == Exclusion.None;

    /// <summary>The reason as the snake_case code lanq prints in JSON, or null when eligible.</summary>
    public string? Code => Eligibility.Code(Reason);
}

public static class Eligibility
{
    /// <summary>The snake_case JSON code of a reason (missing_capability, busy, ...), or null for <see cref="Exclusion.None"/>.</summary>
    public static string? Code(Exclusion reason) => reason switch
    {
        Exclusion.None => null,
        Exclusion.MissingCapability => "missing_capability",
        Exclusion.WrongSize => "wrong_size",
        Exclusion.SizeUnknown => "size_unknown",
        Exclusion.WindowsUnsafeArgs => "windows_unsafe_args",
        Exclusion.MemExceedsTotal => "mem_exceeds_total",
        Exclusion.MemUnknown => "mem_unknown",
        Exclusion.ToolMissing => "tool_missing",
        Exclusion.Unreachable => "unreachable",
        Exclusion.Asleep => "asleep",
        Exclusion.Unreadable => "unreadable",
        Exclusion.Paused => "paused",
        Exclusion.OutsideHours => "outside_hours",
        Exclusion.WindowClosing => "window_closing",
        Exclusion.LowDisk => "low_disk",
        Exclusion.LowMemory => "low_memory",
        Exclusion.MemoryPressure => "memory_pressure",
        Exclusion.Load => "load",
        Exclusion.MemBelowRequest => "mem_below_request",
        Exclusion.Busy => "busy",
        Exclusion.Avoided => "avoided",
        _ => "held",
    };

    /// <summary>True for the reasons waiting can never cure: the config (capabilities, size:, Windows) or the machine itself (its RAM, its size, its tools).</summary>
    public static bool IsPermanent(Exclusion reason) =>
        reason is Exclusion.MissingCapability or Exclusion.WrongSize or Exclusion.WindowsUnsafeArgs or Exclusion.MemExceedsTotal or Exclusion.MemUnknown
            or Exclusion.SizeUnknown or Exclusion.ToolMissing;

    /// <summary>
    /// A machine's declared size: that the job's --size / --min-size rules out, judged from the config alone ("size light (config); --size
    /// medium,heavy"), or null (no rule, no size: key, or it fits).
    /// </summary>
    public static string? DeclaredSizeMismatch(Machine m, SizeRule? rule) =>
        rule is not null && MachineSize.Declared(m) is { } declared && !rule.Allows(declared)
            ? $"size {MachineSize.Word(declared)} (config); {rule.Text}"
            : null;

    /// <summary>The capabilities of <paramref name="needs"/> that the machine does not have (case-insensitive), in the order asked for.</summary>
    public static List<string> Missing(Machine m, IEnumerable<string> needs) =>
        [.. needs.Where(n => !m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase))];

    /// <summary>"lacks gpu (capabilities: dotnet, linux)".</summary>
    public static string Lacks(Machine m, IEnumerable<string> missing) =>
        $"lacks {string.Join(", ", missing)} (capabilities: {string.Join(", ", m.Capabilities)})";
}
