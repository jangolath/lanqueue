using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;

namespace LanQueue.Scheduling;

public static class Scheduler
{
    /// <summary>The hold put on a Windows machine for a job whose arguments cmd.exe shims would re-parse.</summary>
    public const string WindowsUnsafeHold = "arguments not safe for Windows cmd shims";

    /// <summary>
    /// Eligible machines (all required capabilities, reachable, not on hold, a free slot), least loaded first.
    /// Ties break on fewer used slots, then name, so the order is deterministic.
    /// </summary>
    public static List<Machine> Rank(IEnumerable<(Machine Machine, MachineHealth Health)> candidates, IReadOnlyCollection<string> needs) =>
        [.. Rank(Assess(candidates, new JobNeeds(needs, null, false))).Select(a => a.Machine)];

    /// <summary>The eligible machines of <paramref name="assessed"/>, least loaded first, then fewer used slots, then name.</summary>
    public static List<Assessment> Rank(IReadOnlyList<Assessment> assessed) =>
        [.. assessed
            .Where(a => a.Eligible)
            .OrderBy(a => a.Health.LoadRatio)
            .ThenBy(a => a.Health.SlotsUsed)
            .ThenBy(a => a.Machine.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Judges every machine for one job from its probe result (pure: no network). Each machine gets the first reason it cannot take the
    /// job, in this order: the permanent ones (a missing capability, Windows for arguments unsafe there, --mem above its total memory or a
    /// machine that never reports memory, git or tar missing), then unreachable or asleep, then the probe's hold, then --mem above its free
    /// memory, then <paramref name="avoid"/>, then no free slot. The result keeps the input order.
    /// </summary>
    /// <param name="raw">Each machine with its probe result as the probe returned it (no job holds added).</param>
    /// <param name="avoid">Machines to mark <see cref="Exclusion.Avoided"/> (sharded runs: every waiting shard must avoid them), or null.</param>
    public static List<Assessment> Assess(IEnumerable<(Machine Machine, MachineHealth Health)> raw, JobNeeds needs, IReadOnlySet<string>? avoid = null) =>
        [.. raw.Select(c => AssessOne(c.Machine, c.Health, needs, avoid))];

    private static Assessment AssessOne(Machine m, MachineHealth h, JobNeeds needs, IReadOnlySet<string>? avoid)
    {
        var (reason, why) = Judge(m, h, needs, avoid);
        var jobHold = reason is Exclusion.WindowsUnsafeArgs or Exclusion.MemExceedsTotal or Exclusion.MemUnknown or Exclusion.MemBelowRequest;
        var health = jobHold ? h with { Hold = why, HoldKind = HoldKind.Job } : h;
        return new Assessment(
            m,
            health,
            reason,
            why,
            Eligibility.IsPermanent(reason),
            Math.Max(0, m.MaxJobs - h.SlotsUsed),
            reason == Exclusion.None ? Math.Max(0, FreeFor(m, h, needs.MemGb, 0, 0)) : 0);
    }

    private static (Exclusion Reason, string? Why) Judge(Machine m, MachineHealth h, JobNeeds needs, IReadOnlySet<string>? avoid)
    {
        var missing = Eligibility.Missing(m, needs.Capabilities);
        if (missing.Count > 0)
        {
            return (Exclusion.MissingCapability, Eligibility.Lacks(m, missing));
        }

        if (needs.WindowsUnsafe && m.Os == MachineOs.Windows)
        {
            return (Exclusion.WindowsUnsafeArgs, WindowsUnsafeHold);
        }

        // --mem: a reachable machine whose total memory is below it, or that never reports memory, can never take the job. (A machine whose
        // probe output was unreadable reports nothing at all: that is its hold, not its memory.)
        var reportsHealth = h.Reachable && h.HoldKind != HoldKind.Unreadable;
        if (needs.MemGb is { } need && reportsHealth && !(h.MemFreeGb >= need))
        {
            if (h.MemTotalGb is { } total && total < need)
            {
                return (Exclusion.MemExceedsTotal, string.Create(CultureInfo.InvariantCulture, $"{total:0.0} GB total memory, --mem needs {need:0.0} GB"));
            }

            if (h.MemFreeGb is null)
            {
                return (Exclusion.MemUnknown, "free memory unknown (--mem needs the machine to report it)");
            }
        }

        if (h.HoldKind == HoldKind.ToolMissing && h.Hold is { } tool)
        {
            return (Exclusion.ToolMissing, tool);
        }

        if (!h.Reachable)
        {
            // The ssh error says what to fix (host key, key, sshd); the bare hold would only say "unreachable".
            return (h.LooksAsleep ? Exclusion.Asleep : Exclusion.Unreachable, "unreachable: " + (h.Error ?? h.Hold ?? "no answer"));
        }

        if (h.Hold is { } hold)
        {
            return (h.HoldKind switch
            {
                HoldKind.Unreadable => Exclusion.Unreadable,
                HoldKind.Paused => Exclusion.Paused,
                HoldKind.OutsideHours => Exclusion.OutsideHours,
                HoldKind.WindowClosing => Exclusion.WindowClosing,
                HoldKind.LowDisk => Exclusion.LowDisk,
                HoldKind.LowMemory => Exclusion.LowMemory,
                HoldKind.MemoryPressure => Exclusion.MemoryPressure,
                HoldKind.Load => Exclusion.Load,
                HoldKind.Unreachable => Exclusion.Unreachable,
                _ => Exclusion.Held,
            }, hold);
        }

        if (needs.MemGb is { } want && h.MemFreeGb is { } free && free < want)
        {
            return (Exclusion.MemBelowRequest, string.Create(CultureInfo.InvariantCulture, $"{free:0.0} GB free memory, --mem needs {want:0.0} GB"));
        }

        if (avoid is not null && avoid.Contains(m.Name))
        {
            return (Exclusion.Avoided, "avoided: an earlier attempt of the waiting shard(s) lost the connection there and may still be running");
        }

        return h.SlotsUsed >= m.MaxJobs
            ? (Exclusion.Busy, string.Create(CultureInfo.InvariantCulture, $"busy (slots {h.SlotsUsed}/{m.MaxJobs})"))
            : (Exclusion.None, null);
    }

    /// <summary>
    /// How many copies of a job a machine can take now: max_jobs minus the slots in use (or this run's own shards there, if more), and with --mem
    /// no more than its free memory covers: floor((free memory - --mem for each of this run's shards placed there but not running yet) / --mem).
    /// A placed shard still uploading or preparing uses no memory yet, so the probe cannot see what it will need. A machine that does not
    /// report memory is already excluded under --mem.
    /// </summary>
    public static int FreeFor(Machine machine, MachineHealth h, double? memGb, int ours, int oursNotRunning)
    {
        var slots = machine.MaxJobs - Math.Max(h.SlotsUsed, ours);
        return memGb is { } need && h.MemFreeGb is { } free ? Math.Min(slots, Math.Max(0, (int)Math.Floor((free - (oursNotRunning * need)) / need))) : slots;
    }

    /// <summary>
    /// Deals waiting items round-robin over machines with free slots (in the given order, normally <see cref="Rank(IReadOnlyList{Assessment})"/>):
    /// one to each machine per round, while it has slots left, so a few items spread out and many fill every slot. An item goes only where
    /// <paramref name="allowed"/> says it may; the first allowed item in <paramref name="pending"/> order is taken. Returns the placements in
    /// launch order; <paramref name="pending"/> is not changed.
    /// </summary>
    public static List<(T Item, Assessment Machine)> Deal<T>(IReadOnlyList<(Assessment Machine, int Free)> free, IReadOnlyList<T> pending, Func<T, Machine, bool> allowed)
    {
        var left = new List<T>(pending);
        var dealt = new List<(T, Assessment)>();
        for (var round = 0; left.Count > 0 && free.Any(f => f.Free > round); round++)
        {
            foreach (var f in free.Where(f => f.Free > round))
            {
                var i = left.FindIndex(p => allowed(p, f.Machine.Machine));
                if (i < 0)
                {
                    continue;
                }

                dealt.Add((left[i], f.Machine));
                left.RemoveAt(i);
            }
        }

        return dealt;
    }

    /// <summary>
    /// --shards auto: the free slots (<see cref="FreeFor"/>) of every machine that can take the job now, in rank order, and N = their sum, at
    /// least 1 and at most <paramref name="max"/>.
    /// </summary>
    public static (int Count, int Total, List<(Machine Machine, int Free)> Free) AutoCount(IReadOnlyList<Assessment> assessed, double? memGb, int max)
    {
        List<(Machine Machine, int Free)> free = [.. Rank(assessed).Select(a => (a.Machine, FreeFor(a.Machine, a.Health, memGb, 0, 0))).Where(f => f.Item2 > 0)];
        var total = free.Sum(f => f.Free);
        return (Math.Clamp(total, 1, max), total, free);
    }
}
