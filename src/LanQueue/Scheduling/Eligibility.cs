using LanQueue.Config;
using LanQueue.Health;

namespace LanQueue.Scheduling;

/// <summary>What one job asks of a machine: every capability, --mem (GB of available memory), and whether its arguments are unsafe for Windows cmd shims.</summary>
public sealed record JobNeeds(IReadOnlyCollection<string> Capabilities, double? MemGb, bool WindowsUnsafe);

/// <summary>
/// Why a machine cannot take a job, in the order the reasons are looked at: the permanent ones (the config or the machine itself rules it
/// out) first, then unreachable and holds, then --mem, then a busy machine. <see cref="None"/> means it can take the job now.
/// </summary>
public enum Exclusion
{
    None,
    MissingCapability,
    WindowsUnsafeArgs,
    MemExceedsTotal,
    MemUnknown,
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

    /// <summary>True for the reasons waiting can never cure: the config (capabilities, Windows) or the machine itself (its RAM, its tools).</summary>
    public static bool IsPermanent(Exclusion reason) =>
        reason is Exclusion.MissingCapability or Exclusion.WindowsUnsafeArgs or Exclusion.MemExceedsTotal or Exclusion.MemUnknown or Exclusion.ToolMissing;

    /// <summary>The capabilities of <paramref name="needs"/> that the machine does not have (case-insensitive), in the order asked for.</summary>
    public static List<string> Missing(Machine m, IEnumerable<string> needs) =>
        [.. needs.Where(n => !m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase))];

    /// <summary>"lacks gpu (capabilities: dotnet, linux)".</summary>
    public static string Lacks(Machine m, IEnumerable<string> missing) =>
        $"lacks {string.Join(", ", missing)} (capabilities: {string.Join(", ", m.Capabilities)})";
}
