using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;

namespace LanQueue.Scheduling;

public static class Scheduler
{
    /// <summary>
    /// The hold put on a Windows machine for a job whose arguments are unsafe there (<see cref="LanQueue.Policy.JobPolicy.ValidateForWindows"/>): any of
    /// " &lt; &gt; ^ %, or &amp; | for a program that is not a native .exe (a .cmd/.bat shim would re-parse them through cmd.exe).
    /// </summary>
    public const string WindowsUnsafeHold = "arguments not safe for Windows (\" < > ^ %, or & | for a program that is not an .exe)";

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
            .ThenBy(a => a.Health.SlotsLive)
            .ThenBy(a => a.Machine.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Judges every machine for one job from its probe result (pure: no network). Each machine gets the first reason it cannot take the
    /// job, in this order: the permanent ones (a missing capability, a declared size: outside --size / --min-size, Windows for arguments
    /// unsafe there, the job's --env (a machine env: that sets one too, a Windows command too long), --mem above its total memory or a machine that never reports memory, a probed size outside the rule or unknown, git or
    /// tar missing), then unreachable or asleep, then the probe's hold, then --mem above its free
    /// memory, then <paramref name="avoid"/>, then no free slot. The result keeps the input order.
    /// </summary>
    /// <param name="raw">Each machine with its probe result as the probe returned it (no job holds added).</param>
    /// <param name="avoid">Machines to mark <see cref="Exclusion.Avoided"/> (sharded runs: every waiting shard must avoid them), or null.</param>
    public static List<Assessment> Assess(IEnumerable<(Machine Machine, MachineHealth Health)> raw, JobNeeds needs, IReadOnlySet<string>? avoid = null) =>
        [.. raw.Select(c => AssessOne(c.Machine, c.Health, needs, avoid))];

    private static Assessment AssessOne(Machine m, MachineHealth h, JobNeeds needs, IReadOnlySet<string>? avoid)
    {
        var (reason, why) = Judge(m, h, needs, avoid);
        var jobHold = reason is Exclusion.WindowsUnsafeArgs or Exclusion.EnvConflict or Exclusion.WindowsCommandTooLong or Exclusion.MemExceedsTotal or Exclusion.MemUnknown or Exclusion.MemBelowRequest
            or Exclusion.WrongSize or Exclusion.SizeUnknown;
        var health = jobHold ? h with { Hold = why, HoldKind = HoldKind.Job } : h;
        return new Assessment(
            m,
            health,
            reason,
            why,
            Eligibility.IsPermanent(reason),
            Math.Max(0, m.MaxJobs - h.SlotsLive),
            reason == Exclusion.None ? Math.Max(0, FreeFor(m, h, needs, 0, 0)) : 0)
        {
            NeedGb = NeedGb(m, needs),
            SlotCap = SlotCap(m, h, needs),
        };
    }

    private static (Exclusion Reason, string? Why) Judge(Machine m, MachineHealth h, JobNeeds needs, IReadOnlySet<string>? avoid)
    {
        var missing = Eligibility.Missing(m, needs.Capabilities);
        if (missing.Count > 0)
        {
            return (Exclusion.MissingCapability, Eligibility.Lacks(m, missing));
        }

        // A declared size: is known without a probe, so it is judged even for a machine that does not answer.
        if (Eligibility.DeclaredSizeMismatch(m, needs.Size) is { } wrongSize)
        {
            return (Exclusion.WrongSize, wrongSize);
        }

        if (needs.WindowsUnsafe && m.Os == MachineOs.Windows)
        {
            return (Exclusion.WindowsUnsafeArgs, WindowsUnsafeHold);
        }

        // --env: a machine whose env: sets one of the job's variables, or a Windows machine whose command the variables make too long.
        if (needs.EnvRuledOut.TryGetValue(m.Name, out var env))
        {
            return env;
        }

        // Memory (--mem, or a job size: job_mem_gb, 0.13.0): a reachable machine whose total memory less its floor is below the need can
        // never take the job. One that never reports memory cannot take a job that gave --mem; a need that comes only from a job size then
        // falls back to slots (a machine fact must not drop a machine that cannot report). (A machine whose probe output was unreadable
        // reports nothing at all: that is its hold, not its memory.)
        var reportsHealth = h.Reachable && h.HoldKind != HoldKind.Unreadable;
        var memNeed = NeedGb(m, needs);
        var floor = FloorGb(m, needs);
        if (memNeed is { } need && reportsHealth)
        {
            if (h.MemTotalGb is { } total && Mb(total) - Mb(floor) < Mb(need))
            {
                return (Exclusion.MemExceedsTotal, string.Create(
                    CultureInfo.InvariantCulture, $"{total:0.0} GB total memory, {NeedText(m, needs, need)}{FloorText(floor)}"));
            }

            if (h.MemFreeGb is null && needs.MemGb is not null)
            {
                return (Exclusion.MemUnknown, "free memory unknown (--mem needs the machine to report it)");
            }
        }

        // --size / --min-size on a machine without size: the probed class, only from a machine that answered (an unreachable one stays
        // unreachable, which may pass). No total memory means no class: that machine can never be judged, so it is permanent too.
        if (needs.Size is { } rule && MachineSize.Declared(m) is null && reportsHealth)
        {
            var probed = MachineSize.Classify(h.MemTotalGb, h.Cores);
            if (probed == SizeClass.Unknown)
            {
                return (Exclusion.SizeUnknown, $"size unknown (the machine did not report its total memory; give it a size: in machines.yml); {rule.Text}");
            }

            if (!rule.Allows(probed))
            {
                return (Exclusion.WrongSize, string.Create(
                    CultureInfo.InvariantCulture,
                    $"size {MachineSize.Word(probed)} (probe: {MachineSize.RoundedGb(h.MemTotalGb!.Value):0} GB, {(h.Cores is { } c ? c.ToString(CultureInfo.InvariantCulture) : "?")} cores); {rule.Text}"));
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

        if (memNeed is { } want && h.MemFreeGb is { } free && Mb(free) - Mb(floor) < Mb(want))
        {
            return (Exclusion.MemBelowRequest, string.Create(CultureInfo.InvariantCulture, $"{free:0.0} GB free memory, {NeedText(m, needs, want)}{FloorText(floor)}"));
        }

        if (avoid is not null && avoid.Contains(m.Name))
        {
            return (Exclusion.Avoided, "avoided: an earlier attempt of the waiting shard(s) lost the connection there and may still be running");
        }

        // Stale slots (a crashed run's, past their deadline plus the grace) do not count: the claim reclaims each one it passes.
        if (needs.Exclusive && h.SlotsLive > 0)
        {
            // --exclusive takes every slot: any slot in use rules the machine out for now (busy, retryable; no reservation).
            return (Exclusion.Busy, string.Create(CultureInfo.InvariantCulture, $"busy (slots {h.SlotsUsed}/{m.MaxJobs}{StaleNote(h)}; --exclusive needs all {m.MaxJobs} free)"));
        }

        var cap = SlotCap(m, h, needs);
        return h.SlotsLive >= cap
            ? (Exclusion.Busy, string.Create(CultureInfo.InvariantCulture, $"busy (slots {h.SlotsUsed}/{m.MaxJobs}{StaleNote(h)}{CapNote(m, cap)})"))
            : (Exclusion.None, null);
    }

    /// <summary>
    /// The job's memory need on <paramref name="m"/> (0.13.0 B1): the larger of its own --mem and the machine's job size (its job_mem_gb,
    /// else policy.job_mem_gb). Either may be absent; null when both are (no memory accounting, as before). Neither side can make the
    /// other's number smaller: the repository declares a minimum, the machine owner knows the local multiplier.
    /// </summary>
    public static double? NeedGb(Machine m, JobNeeds needs) =>
        (needs.MemGb, JobSizeGb(m, needs)) switch
        {
            ({ } mem, { } size) => Math.Max(mem, size),
            ({ } mem, null) => mem,
            (null, { } size) => size,
            _ => null,
        };

    /// <summary>The machine's job size: its job_mem_gb, else the policy's, else null.</summary>
    private static double? JobSizeGb(Machine m, JobNeeds needs) => m.JobMemGb ?? needs.Policy?.JobMemGb;

    /// <summary>The free memory the machine keeps: its min_free_mem_gb, else the policy's, else (no policy given) 0.</summary>
    public static double FloorGb(Machine m, JobNeeds needs) => m.MinFreeMemGb ?? needs.Policy?.MinFreeMemGb ?? 0;

    /// <summary>
    /// The slots the machine may run at once for this job: max_jobs, capped by floor((total - floor) / job size) when the machine has a job
    /// size and reports its total memory (0.13.0, M5). The claim script uses the same cap, and slot files are shared by every lanq run, so
    /// parallel orchestrators cannot overcommit a machine whose jobs reach their peak memory only minutes after they start. --mem alone
    /// does not cap: it is the job's own declaration, and the free-memory test gates each next job.
    /// </summary>
    public static int SlotCap(Machine m, MachineHealth h, JobNeeds needs)
    {
        if (JobSizeGb(m, needs) is not { } size || h.MemTotalGb is not { } total)
        {
            return m.MaxJobs;
        }

        var fit = (Mb(total) - Mb(FloorGb(m, needs))) / Math.Max(1, Mb(size));
        return (int)Math.Clamp(fit, 1, m.MaxJobs); // never 0: a machine the size cannot fit at all is mem_exceeds_total, not busy
    }

    /// <summary>Whole megabytes (rounded), so that (8.7 - 2.7) / 3 is 2 and not 1.9999999999999998 (L4).</summary>
    private static long Mb(double gb) => (long)Math.Round(gb * 1024, MidpointRounding.AwayFromZero);

    private static string FloorText(double floor) =>
        floor > 0 ? string.Create(CultureInfo.InvariantCulture, $" above its {floor:0.0} GB floor") : "";

    private static string NeedText(Machine m, JobNeeds needs, double need) =>
        needs.MemGb is { } mem && mem >= need
            ? string.Create(CultureInfo.InvariantCulture, $"--mem needs {need:0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"a job needs {need:0.0} GB here (job_mem_gb{(m.JobMemGb is null ? " in policy" : "")})");

    private static string CapNote(Machine m, int cap) =>
        cap < m.MaxJobs ? string.Create(CultureInfo.InvariantCulture, $"; job_mem_gb caps it at {cap}") : "";

    /// <summary>
    /// ", 1 stale: reclaimed by the next claim" when the probe saw stale slots, ", 1 stale, not reclaimable" for stale slots a claim of this run
    /// could not reclaim (<see cref="MachineHealth.SlotsStuck"/>); empty otherwise (the text stays as before).
    /// </summary>
    internal static string StaleNote(MachineHealth h) =>
        (h.SlotsStale > 0 ? string.Create(CultureInfo.InvariantCulture, $", {h.SlotsStale} stale: reclaimed by the next claim") : "")
        + (h.SlotsStuck > 0 ? string.Create(CultureInfo.InvariantCulture, $", {h.SlotsStuck} stale, not reclaimable") : "");

    /// <summary>
    /// How many copies of a job a machine can take now: max_jobs minus the slots in use (stale ones, <see cref="MachineHealth.SlotsStale"/>,
    /// not counted: the claim reclaims them) (or this run's own shards there, if more), and with --mem
    /// no more than its free memory covers: floor((free memory - --mem for each of this run's shards placed there but not running yet) / --mem).
    /// A placed shard still uploading or preparing uses no memory yet, so the probe cannot see what it will need. A machine that does not
    /// report memory is already excluded under --mem. An <paramref name="exclusive"/> job takes every slot of its machine: 1 when no slot
    /// is in use and none of this run's shards is there, else 0 (--mem still applies).
    /// </summary>
    public static int FreeFor(Machine machine, MachineHealth h, double? memGb, int ours, int oursNotRunning, bool exclusive = false) =>
        FreeFor(machine, h, new JobNeeds([], memGb, false) { Exclusive = exclusive }, ours, oursNotRunning);

    /// <summary>
    /// 0.13.0 B1: as above with the job's per-machine need (<see cref="NeedGb"/>), the machine's floor kept free (<see cref="FloorGb"/>), and
    /// the slots capped by <see cref="SlotCap"/>: min(cap - used, floor((free - floor - need x ours not running) / need)), in whole MB.
    /// Unknown free memory counts slots only (the judge already refused a --mem job there).
    /// </summary>
    public static int FreeFor(Machine machine, MachineHealth h, JobNeeds needs, int ours, int oursNotRunning)
    {
        var cap = SlotCap(machine, h, needs);
        var slots = needs.Exclusive
            ? (h.SlotsLive == 0 && ours == 0 && machine.MaxJobs > 0 ? 1 : 0)
            : cap - Math.Max(h.SlotsLive, ours);
        if (NeedGb(machine, needs) is not { } need || h.MemFreeGb is not { } free)
        {
            return slots;
        }

        var usable = Mb(free) - Mb(FloorGb(machine, needs)) - (oursNotRunning * Mb(need));
        return Math.Min(slots, (int)Math.Max(0, usable / Math.Max(1, Mb(need))));
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
    /// least 1 and at most <paramref name="max"/>. For an <paramref name="exclusive"/> job each idle machine counts once.
    /// </summary>
    public static (int Count, int Total, List<(Machine Machine, int Free)> Free) AutoCount(IReadOnlyList<Assessment> assessed, double? memGb, int max, bool exclusive = false) =>
        AutoCount(assessed, new JobNeeds([], memGb, false) { Exclusive = exclusive }, max);

    /// <summary>As above with the job's needs (0.13.0: per-machine need, floor and slot cap; see <see cref="FreeFor(Machine, MachineHealth, JobNeeds, int, int)"/>).</summary>
    public static (int Count, int Total, List<(Machine Machine, int Free)> Free) AutoCount(IReadOnlyList<Assessment> assessed, JobNeeds needs, int max)
    {
        List<(Machine Machine, int Free)> free = [.. Rank(assessed).Select(a => (a.Machine, FreeFor(a.Machine, a.Health, needs, 0, 0))).Where(f => f.Item2 > 0)];
        var total = free.Sum(f => f.Free);
        return (Math.Clamp(total, 1, max), total, free);
    }
}
