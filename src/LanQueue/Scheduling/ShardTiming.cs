using System.Globalization;
using LanQueue.Health;
using LanQueue.Jobs;
using MachineClass = LanQueue.Health.SizeClass;

namespace LanQueue.Scheduling;

/// <summary>One completed shard of an earlier run: the last Completed attempt of its index.</summary>
/// <param name="RunSeconds">How long the shard's own command ran (jobs.jsonl runSeconds).</param>
/// <param name="WallSeconds">The whole job, slot claim to cleanup (jobs.jsonl wallSeconds).</param>
public sealed record TimingShard(int Index, string Machine, double RunSeconds, double WallSeconds);

/// <summary>One earlier run of a command that counts for timing (at least half of its shards completed).</summary>
/// <param name="Shards">The run's shard count N (1 for a single run).</param>
/// <param name="LastUtc">The start of its newest job.</param>
/// <param name="Completed">Its completed shards, one per index, in index order.</param>
public sealed record TimingRun(int Shards, DateTimeOffset LastUtc, IReadOnlyList<TimingShard> Completed);

/// <summary>What jobs.jsonl says about one machine's speed (see <see cref="ShardTiming.Speeds"/>).</summary>
/// <param name="Factor">
/// How much longer the machine takes than the typical machine for the same work (1.62 = 62% slower, 0.5 = twice as fast), from at least
/// <see cref="ShardTiming.MinSpeedSamples"/> samples, clamped and rounded; null with fewer samples.
/// </param>
/// <param name="Samples">How many jobs the factor comes from.</param>
public sealed record MachineSpeed(double? Factor, int Samples);

/// <summary>What --target-minutes reads from jobs.jsonl for one command (see <see cref="JobHistory.For"/>).</summary>
/// <param name="Key">The command's key (<see cref="JobHistory.Key"/>).</param>
/// <param name="Dir">The normalised --dir (<see cref="JobHistory.Dir"/>).</param>
/// <param name="Runs">The newest qualifying runs, newest first (at most <see cref="JobHistory.MaxRuns"/>).</param>
/// <param name="Speeds">Every machine's measured speed, by name, from every command in the log.</param>
public sealed record TimingHistory(string Key, string Dir, IReadOnlyList<TimingRun> Runs, IReadOnlyDictionary<string, MachineSpeed> Speeds)
{
    /// <summary>A command with no usable history.</summary>
    public static TimingHistory None(string key, string dir) => new(key, dir, [], new Dictionary<string, MachineSpeed>(StringComparer.Ordinal));
}

/// <summary>The work and per-shard overhead earlier runs of a command show (<see cref="ShardTiming.Estimate"/>).</summary>
/// <param name="WorkSeconds">W: the whole command's own run time on a machine of speed factor 1 (the median over the runs).</param>
/// <param name="OverheadSeconds">O: per shard, wall-clock minus run time (slot, upload, prepare, fetch, cleanup; the median over the runs).</param>
public sealed record TimingEstimate(double WorkSeconds, double OverheadSeconds, int RunsUsed, DateTimeOffset LastRunUtc);

/// <summary>A machine's speed factor as --target-minutes uses it, and where it came from.</summary>
/// <param name="Source"><see cref="ShardTiming.Measured"/>, <see cref="ShardTiming.SizeClass"/> or <see cref="ShardTiming.Unknown"/>.</param>
public sealed record SpeedFactor(double Factor, string Source);

/// <summary>
/// --shards auto --target-minutes M: N and why (lanq run's line, the dry run's Timing line, and the shardTiming JSON object).
/// </summary>
/// <param name="Basis"><see cref="ShardTiming.History"/>, <see cref="ShardTiming.NoHistory"/> or <see cref="ShardTiming.Overhead"/> (more may be added).</param>
/// <param name="RunsUsed">How many earlier runs the estimate comes from (0 without history).</param>
/// <param name="LastRunUtc">The newest of those runs, or null.</param>
/// <param name="WorkMinutes">W in minutes, one decimal; null without history.</param>
/// <param name="OverheadSeconds">O in seconds, one decimal; null without history.</param>
/// <param name="SlowestMachine">The slowest machine the shards would be dealt to now (it decides N), or null.</param>
/// <param name="SpeedFactor">Its speed factor, or null.</param>
/// <param name="Wanted">The N the target needs before any cap; null without history or when the overhead alone is over the target.</param>
/// <param name="FreeSlots">The free slots that can take the job now (before the cap of 64).</param>
/// <param name="Capped">free_slots or max_shards when N is lower than wanted (or, without a wanted N, when 64 capped the free slots); else null.</param>
/// <param name="Picked">N.</param>
/// <param name="ExpectedShardMinutes">(O + f*W/N) / 60 on the slowest machine, one decimal; null without history.</param>
/// <param name="Why">The reasoning for people, starting "--target-minutes M: ".</param>
public sealed record ShardTimingResult(
    int TargetMinutes, string Command, string Dir, string Basis, int RunsUsed, DateTimeOffset? LastRunUtc, double? WorkMinutes, double? OverheadSeconds,
    string? SlowestMachine, double? SpeedFactor, int? Wanted, int FreeSlots, string? Capped, int Picked, double? ExpectedShardMinutes, string Why);

/// <summary>
/// --shards auto --target-minutes M (pure: no I/O). From earlier Completed runs of the same command (same key and --dir) it estimates the
/// work W (on a machine of speed factor 1) and the per-shard overhead O, then picks the fewest shards that finish within M minutes on the
/// slowest machine they would be dealt to now, never more than the free slots (nor 64):
/// <list type="number">
/// <item>Each run's work: every shard completed: the sum of runSeconds / f(machine); else N x the median of runSeconds / f. Each run's
/// overhead: the median of wallSeconds - runSeconds over its shards. W and O are the medians over the runs.</item>
/// <item>No run: N = the free slots (plain --shards auto). budget = 60*M - O below a tenth of 60*M: N = the free slots (more shards cannot
/// help). Else N0 = ceil(W / budget), dealt round-robin as a run deals; f = the largest speed factor among the dealt machines;
/// N1 = ceil(f*W / budget); when N1 &gt; N0 it is dealt once more and f taken again. N = N1, at most the free slots and 64.</item>
/// </list>
/// Medians here are the plain median (the mean of the two middle values for an even count).
/// </summary>
public static class ShardTiming
{
    public const string History = "history";

    public const string NoHistory = "no_history";

    public const string Overhead = "overhead";

    /// <summary>The speed factor came from the machine's own jobs.</summary>
    public const string Measured = "measured";

    /// <summary>The speed factor is the median of the measured machines of the same size class (<see cref="Health.MachineSize"/>).</summary>
    public const string SizeClass = "size_class";

    /// <summary>Nothing is known: 1.0.</summary>
    public const string Unknown = "unknown";

    public const string CappedByFreeSlots = "free_slots";

    public const string CappedByMaxShards = "max_shards";

    /// <summary>The longest --target-minutes (one day).</summary>
    public const int MaxTargetMinutes = 1440;

    /// <summary>Below this share of the target left after the overhead, more shards cannot reach the target: every free slot is used.</summary>
    internal const double MinBudgetShare = 0.1;

    /// <summary>A machine needs this many samples before its own speed factor is used.</summary>
    public const int MinSpeedSamples = 5;

    /// <summary>Jobs that ran less than this many seconds say nothing about a machine's speed (start-up dominates).</summary>
    public const double MinSpeedRunSeconds = 30;

    public const double MinFactor = 0.25;

    public const double MaxFactor = 4;

    /// <summary>
    /// W and O from the runs of <paramref name="history"/>, each shard's run time divided by its machine's speed factor; null without runs.
    /// </summary>
    public static TimingEstimate? Estimate(TimingHistory history, Func<string, double> speed)
    {
        if (history.Runs.Count == 0)
        {
            return null;
        }

        var work = new List<double>(history.Runs.Count);
        var overhead = new List<double>(history.Runs.Count);
        foreach (var run in history.Runs)
        {
            var reference = run.Completed.Select(s => s.RunSeconds / speed(s.Machine)).ToList();
            work.Add(run.Completed.Count >= run.Shards ? reference.Sum() : run.Shards * Median(reference));
            overhead.Add(Median([.. run.Completed.Select(s => Math.Max(0, s.WallSeconds - s.RunSeconds))]));
        }

        return new TimingEstimate(Median(work), Median(overhead), history.Runs.Count, history.Runs.Max(r => r.LastUtc));
    }

    /// <summary>
    /// N for --shards auto --target-minutes <paramref name="targetMinutes"/> (see the class summary).
    /// </summary>
    /// <param name="free">The machines that can take a shard now, in rank order, with their free slots (as a fresh run deals them).</param>
    /// <param name="assessed">Every machine judged for the job (the probe's memory and cores give a machine without samples its size class).</param>
    /// <param name="max">The most shards one run may have (64).</param>
    public static ShardTimingResult Pick(
        TimingHistory history, int targetMinutes, IReadOnlyList<(Assessment Machine, int Free)> free, IReadOnlyList<Assessment> assessed, int max)
    {
        var total = free.Sum(f => f.Free);
        var cap = Math.Clamp(total, 1, max);
        var lead = string.Create(CultureInfo.InvariantCulture, $"--target-minutes {targetMinutes}: ");
        double Speed(string machine) => SpeedOf(machine, history.Speeds, assessed).Factor;
        var estimate = Estimate(history, Speed);
        if (estimate is null)
        {
            var where = history.Dir == "." ? "" : $" (in {history.Dir})";
            return new ShardTimingResult(
                targetMinutes, history.Key, history.Dir, NoHistory, 0, null, null, null, null, null, null, total, total > max ? CappedByMaxShards : null, cap, null,
                $"{lead}no completed run of this command{where} in the last {JobHistory.HistoryDays} days; --shards auto falls back to the free slots");
        }

        var (w, o) = (estimate.WorkSeconds, estimate.OverheadSeconds);
        var target = 60.0 * targetMinutes;
        var budget = target - o;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{estimate.RunsUsed} earlier run{(estimate.RunsUsed == 1 ? "" : "s")}, about {JobStats.Duration(w)} of work + {JobStats.Duration(o)} per shard");

        // The slowest machine N shards would be dealt to now (round-robin over the free slots, as a fresh run deals them).
        (string? Machine, SpeedFactor Speed) Slowest(int n)
        {
            var dealt = Scheduler.Deal(free, Enumerable.Range(1, Math.Min(n, Math.Max(total, 1))).ToList(), (_, _) => true);
            (string? Machine, SpeedFactor Speed) slowest = (null, new SpeedFactor(1, Unknown));
            foreach (var name in dealt.Select(d => d.Machine.Machine.Name).Distinct(StringComparer.Ordinal))
            {
                var s = SpeedOf(name, history.Speeds, assessed);
                if (slowest.Machine is null || s.Factor > slowest.Speed.Factor)
                {
                    slowest = (name, s);
                }
            }

            return slowest;
        }

        string Each(int n, (string? Machine, SpeedFactor Speed) slow, out double expected)
        {
            expected = Round1((o + (slow.Speed.Factor * w / n)) / 60);
            var by = slow.Machine is { } m && slow.Speed.Source != Unknown
                ? string.Create(CultureInfo.InvariantCulture, $"slowest machine {m} x{slow.Speed.Factor:0.00}: ")
                : "";
            return string.Create(CultureInfo.InvariantCulture, $"{by}about {expected:0.0} min each");
        }

        string basis;
        int? wanted = null;
        int picked;
        string decision;
        (string? Machine, SpeedFactor Speed) slowest;
        double expectedMinutes;
        if (SuggestedShards(estimate, targetMinutes) is not { } n0)
        {
            basis = Overhead;
            picked = cap;
            slowest = Slowest(picked);
            var how = o >= target
                ? string.Create(CultureInfo.InvariantCulture, $"is above --target-minutes {targetMinutes}")
                : string.Create(CultureInfo.InvariantCulture, $"leaves less than a tenth of --target-minutes {targetMinutes} for the work");
            decision = $"the per-shard overhead ({JobStats.Duration(o)}) {how}; more shards cannot reach it, so {Slots(cap, total, max, all: true)}: {Each(picked, slowest, out expectedMinutes)}";
        }
        else
        {
            basis = History;
            var f = Slowest(n0).Speed.Factor;
            var n1 = Ceiling(f * w / budget);
            if (n1 > n0)
            {
                // Dealt once more: more shards can reach a slower machine. Two passes at most, so the choice is deterministic.
                n1 = Math.Max(n1, Ceiling(Slowest(n1).Speed.Factor * w / budget));
            }

            wanted = n1;
            picked = Math.Clamp(n1, 1, cap);
            slowest = Slowest(picked);
            var each = Each(picked, slowest, out expectedMinutes);
            decision = n1 > cap
                ? string.Create(CultureInfo.InvariantCulture, $"; {n1} shards would be needed for {targetMinutes} min; capped at {Slots(cap, total, max, all: false)}: {each}")
                : ", " + each;
        }

        var capped = wanted is { } want
            ? want > cap ? total > max ? CappedByMaxShards : CappedByFreeSlots : null
            : total > max ? CappedByMaxShards : null;
        return new ShardTimingResult(
            targetMinutes, history.Key, history.Dir, basis, estimate.RunsUsed, estimate.LastRunUtc, Round1(w / 60), Round1(o), slowest.Machine,
            slowest.Machine is null ? null : slowest.Speed.Factor, wanted, total, capped, picked, expectedMinutes,
            lead + summary + (basis == Overhead ? "; " : "") + decision);
    }

    /// <summary>
    /// The N <paramref name="targetMinutes"/> needs on a machine of speed factor 1, before any cap: ceil(W / (60 x M - O)); null when the
    /// overhead leaves less than a tenth of the target for the work (more shards cannot reach it). lanq stats --target-minutes shows it as
    /// suggestedShards (no probe, so no slowest machine and no free slots); <see cref="Pick"/> starts from it.
    /// </summary>
    public static int? SuggestedShards(TimingEstimate estimate, int targetMinutes)
    {
        var target = 60.0 * targetMinutes;
        var budget = target - estimate.OverheadSeconds;
        return budget < MinBudgetShare * target ? null : Ceiling(estimate.WorkSeconds / budget);
    }

    /// <summary>One decimal, halves away from zero (as the text formats them), so the JSON and the text agree.</summary>
    public static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The text and JSON reason for an auto N: the free slots alone (plain --shards auto, exactly as before), or with --target-minutes the
    /// timing reasoning first.
    /// </summary>
    public static string ShardCountWhy(ShardTimingResult? timing, string freeSlotsWhy) => timing is null ? freeSlotsWhy : timing.Why + "; " + freeSlotsWhy;

    /// <summary>
    /// A machine's speed factor: its own (<see cref="MachineSpeed.Factor"/>, from enough samples), else the median of the measured machines of
    /// the same size class in <paramref name="assessed"/> (this probe), else 1.0.
    /// </summary>
    public static SpeedFactor SpeedOf(string machine, IReadOnlyDictionary<string, MachineSpeed> speeds, IReadOnlyList<Assessment> assessed)
    {
        if (speeds.TryGetValue(machine, out var own) && own.Factor is { } f)
        {
            return new SpeedFactor(f, Measured);
        }

        var me = assessed.FirstOrDefault(a => a.Machine.Name == machine);
        var size = me is null ? MachineClass.Unknown : MachineSize.Classify(me.Health.MemTotalGb, me.Health.Cores);
        if (size != MachineClass.Unknown)
        {
            List<double> peers = [.. assessed
                .Where(a => a.Machine.Name != machine && MachineSize.Classify(a.Health.MemTotalGb, a.Health.Cores) == size)
                .Select(a => speeds.TryGetValue(a.Machine.Name, out var s) ? s.Factor : null)
                .OfType<double>()];
            if (peers.Count > 0)
            {
                return new SpeedFactor(Math.Round(Median(peers), 2), SizeClass);
            }
        }

        return new SpeedFactor(1, Unknown);
    }

    /// <summary>
    /// Every machine's speed from <paramref name="records"/> (the whole job log, every command): each keyed Completed job of the last
    /// <see cref="JobHistory.HistoryDays"/> days whose own command ran at least <see cref="MinSpeedRunSeconds"/> is one sample, its ratio
    /// (runSeconds x N) / the median of (runSeconds x N) over its command (key and dir): above 1 the machine took longer than is typical for
    /// that command. A machine's factor is the median of its ratios once it has <see cref="MinSpeedSamples"/>, clamped to
    /// <see cref="MinFactor"/>..<see cref="MaxFactor"/> and rounded to two decimals. The factors come from the raw run times; the work W is then
    /// divided by them (two passes, never iterated).
    /// </summary>
    public static Dictionary<string, MachineSpeed> Speeds(IReadOnlyList<JobRecord> records, DateTimeOffset now)
    {
        var since = now.AddDays(-JobHistory.HistoryDays);
        return records
            .Where(r => r.Key is not null && r.Machine is not null && r.Status == nameof(JobStatus.Completed) && r.RunSeconds >= MinSpeedRunSeconds
                && r.StartedUtc >= since)
            .GroupBy(r => (r.Key, r.Dir))
            .SelectMany(g =>
            {
                List<(string Machine, double Work)> work = [.. g.Select(r => (r.Machine!, r.RunSeconds!.Value * Math.Max(1, r.Shards ?? 1)))];
                var typical = Median([.. work.Select(w => w.Work)]);
                return typical > 0 ? work.Select(w => (w.Machine, Ratio: w.Work / typical)) : [];
            })
            .GroupBy(x => x.Machine, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    List<double> ratios = [.. g.Select(x => x.Ratio)];
                    return new MachineSpeed(ratios.Count >= MinSpeedSamples ? Math.Round(Math.Clamp(Median(ratios), MinFactor, MaxFactor), 2) : null, ratios.Count);
                },
                StringComparer.Ordinal);
    }

    /// <summary>The plain median (the mean of the two middle values for an even count); 0 for no values.</summary>
    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>ceil(x), at least 1 and never past what an int (or any fleet) needs.</summary>
    private static int Ceiling(double x) => double.IsFinite(x) ? (int)Math.Clamp(Math.Ceiling(x), 1, 1_000_000) : 1_000_000;

    /// <summary>"(all) 5 free slots", "the 1 free slot", "64 shards (the most one run may have)" or "1 shard (no free slot right now)".</summary>
    private static string Slots(int cap, int total, int max, bool all) => total switch
    {
        0 => "1 shard (no free slot right now)",
        _ when total > max => string.Create(CultureInfo.InvariantCulture, $"{max} shards (the most one run may have)"),
        1 => all ? "the 1 free slot" : "1 free slot",
        _ => string.Create(CultureInfo.InvariantCulture, $"{(all ? "all " : "")}{cap} free slots"),
    };
}
