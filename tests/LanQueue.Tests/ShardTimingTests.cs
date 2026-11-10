using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Scheduling;
using Xunit;

namespace LanQueue.Tests;

/// <summary>--shards auto --target-minutes (pure): the history jobs.jsonl gives, the estimate, the speed factors and the pick.</summary>
public sealed class ShardTimingTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    internal const string Key = "npx jest --shard={shard}/{shards}";

    private static int _ids;

    /// <summary>One job as 0.7 records it; wallSeconds defaults to the run time + 30 s of overhead.</summary>
    internal static JobRecord Job(
        string machine, double run, string status = "Completed", string? runId = null, int? shard = null, int? shards = null, double daysAgo = 1,
        double? wall = null, string key = Key, string dir = ".") =>
        new($"20261101-100000-{Interlocked.Increment(ref _ids):x6}", machine, key, Now.AddDays(-daysAgo), wall ?? run + 30, status == "Completed" ? 0 : 1, status)
        {
            V = 2,
            Key = key,
            Dir = dir,
            RunId = runId,
            Shard = shard,
            Shards = shards,
            RunSeconds = run,
        };

    /// <summary>Every shard of one --shards run, each with its run time (index order), on <paramref name="machine"/>.</summary>
    internal static IEnumerable<JobRecord> Run(string runId, double daysAgo, string machine, params double[] runSeconds) =>
        runSeconds.Select((s, i) => Job(machine, s, runId: runId, shard: i + 1, shards: runSeconds.Length, daysAgo: daysAgo));

    /// <summary>
    /// Healthy machines (name, max_jobs, load, total memory, cores), judged for a job that needs nothing, and the free slots a fresh run deals
    /// to, in rank order (least loaded first).
    /// </summary>
    internal static (List<Assessment> Assessed, List<(Assessment Machine, int Free)> Free) Fleet(
        params (string Name, int Slots, double Load, double? MemGb, int? Cores)[] machines)
    {
        var assessed = Scheduler.Assess(
            machines.Select(m => (new Machine { Name = m.Name, MaxJobs = m.Slots }, new MachineHealth(m.Name, true, m.Load, 50, 0, null, null) { MemTotalGb = m.MemGb, Cores = m.Cores })),
            new JobNeeds([], null, false));
        return (assessed, [.. Scheduler.Rank(assessed).Select(a => (a, a.JobSlots)).Where(x => x.JobSlots > 0)]);
    }

    private static (List<Assessment> Assessed, List<(Assessment Machine, int Free)> Free) Slots(int n) => Fleet(("build-01", n, 0.1, null, null));

    private static TimingHistory History(IEnumerable<JobRecord> records) => JobHistory.From([.. records], Key, ".", Now);

    private static TimingEstimate Estimate(IEnumerable<JobRecord> records) => ShardTiming.Estimate(History(records), _ => 1)!;

    private static ShardTimingResult Pick(TimingHistory history, int minutes, (List<Assessment> Assessed, List<(Assessment Machine, int Free)> Free) fleet) =>
        ShardTiming.Pick(history, minutes, fleet.Free, fleet.Assessed, ShardInfo.MaxShards);

    [Fact]
    public void NoHistoryKeepsTodaysAutoCount()
    {
        var fleet = Fleet(("build-01", 2, 0.1, null, null), ("micro-01", 1, 0.2, null, null), ("micro-02", 2, 0.3, null, null));
        var r = Pick(History([]), 5, fleet);
        Assert.Equal(Scheduler.AutoCount(fleet.Assessed, null, ShardInfo.MaxShards).Count, r.Picked);
        Assert.Equal((ShardTiming.NoHistory, 5, 5, 0), (r.Basis, r.Picked, r.FreeSlots, r.RunsUsed));
        Assert.Null(r.WorkMinutes);
        Assert.Null(r.Wanted);
        Assert.Null(r.Capped);
        Assert.Null(r.ExpectedShardMinutes);
        Assert.Equal("--target-minutes 5: no completed run of this command in the last 90 days; --shards auto falls back to the free slots", r.Why);

        // The dir is named when it is not the repository's top; more than 64 free slots are capped as plain auto caps them.
        var tests = ShardTiming.Pick(TimingHistory.None(Key, "tests"), 5, Slots(70).Free, Slots(70).Assessed, ShardInfo.MaxShards);
        Assert.Equal((64, ShardTiming.CappedByMaxShards), (tests.Picked, tests.Capped));
        Assert.Contains("no completed run of this command (in tests) in the last 90 days", tests.Why, StringComparison.Ordinal);

        // No free slot at all: 1, as plain auto.
        Assert.Equal(1, Pick(History([]), 5, Fleet()).Picked);
    }

    [Fact]
    public void SumOfShardsWhenEveryShardCompleted()
    {
        var e = Estimate(Run("r1", 1, "build-01", 60, 120, 180, 240));
        Assert.Equal((600.0, 30.0, 1), (e.WorkSeconds, e.OverheadSeconds, e.RunsUsed));
    }

    [Fact]
    public void NTimesMedianWhenSomeShardsDidNotComplete()
    {
        // Shard 4 failed: N x the median of the three that completed (60, 120, 600), not their sum.
        var e = Estimate([.. Run("r1", 1, "build-01", 60, 120, 600, 240).Take(3), Job("build-01", 240, "Failed", "r1", 4, 4)]);
        Assert.Equal(480.0, e.WorkSeconds);
    }

    [Fact]
    public void HistoryFromADifferentNGivesTheSameWork()
    {
        // 4 x 4 min and 8 x 2 min are both 16 min of work.
        Assert.Equal(960.0, Estimate(Run("r4", 2, "build-01", 240, 240, 240, 240)).WorkSeconds);
        Assert.Equal(960.0, Estimate(Run("r8", 1, "build-01", 120, 120, 120, 120, 120, 120, 120, 120)).WorkSeconds);
        var both = Estimate([.. Run("r4", 2, "build-01", 240, 240, 240, 240), .. Run("r8", 1, "build-01", 120, 120, 120, 120, 120, 120, 120, 120)]);
        Assert.Equal((960.0, 2), (both.WorkSeconds, both.RunsUsed));
    }

    [Fact]
    public void RunsWithFewerThanHalfTheirShardsCompletedAreIgnored()
    {
        JobRecord[] records =
        [
            .. Run("one-of-four", 1, "build-01", 100, 100, 100, 100).Take(1), // 1 of 4: ignored
            .. Run("two-of-four", 2, "build-01", 100, 100, 100, 100).Take(2), // 2 of 4: counts
            .. Run("one-of-three", 3, "build-01", 100, 100, 100).Take(1), // 1 of 3: ignored (ceil(3/2) = 2)
            .. Run("two-of-three", 4, "build-01", 100, 100, 100).Take(2), // 2 of 3: counts
        ];
        var h = History(records);
        Assert.Equal([4, 3], h.Runs.Select(r => r.Shards));
        Assert.Equal([2, 2], h.Runs.Select(r => r.Completed.Count));
    }

    [Fact]
    public void RetriedShardCountsOnce()
    {
        // Shard 1 lost its machine once (InfraError), then completed twice in the log: the last Completed attempt counts, once.
        JobRecord[] records =
        [
            Job("micro-01", 50, "InfraError", "r1", 1, 2),
            Job("build-01", 100, runId: "r1", shard: 1, shards: 2),
            Job("build-01", 300, runId: "r1", shard: 1, shards: 2),
            Job("build-01", 100, runId: "r1", shard: 2, shards: 2),
        ];
        var run = Assert.Single(History(records).Runs);
        Assert.Equal([1, 2], run.Completed.Select(s => s.Index));
        Assert.Equal(400.0, ShardTiming.Estimate(History(records), _ => 1)!.WorkSeconds);
    }

    [Fact]
    public void OnlyTheNewestTenRunsInNinetyDays()
    {
        // Twelve runs inside the window (the two oldest of them huge), and a huge one just outside it.
        var records = new List<JobRecord>();
        for (var i = 1; i <= 12; i++)
        {
            records.Add(Job("build-01", i <= 10 ? 600 : 60_000, daysAgo: i * 7));
        }

        records.Add(Job("build-01", 90_000, daysAgo: 90.5));
        records.Add(Job("build-01", 90_000, daysAgo: 0.5, key: "npx jest")); // another command
        records.Add(Job("build-01", 90_000, daysAgo: 0.5, dir: "tests")); // the same command in another dir

        var h = History(records);
        Assert.Equal(JobHistory.MaxRuns, h.Runs.Count);
        Assert.Equal(Now.AddDays(-7), h.Runs[0].LastUtc);
        var e = ShardTiming.Estimate(h, _ => 1)!;
        Assert.Equal((600.0, 10, Now.AddDays(-7)), (e.WorkSeconds, e.RunsUsed, e.LastRunUtc));
    }

    [Fact]
    public void FailedAndTimedOutRunsAreIgnored()
    {
        JobRecord[] records =
        [
            Job("build-01", 600, "Failed"),
            Job("build-01", 600, "TimedOut"),
            Job("build-01", 600, "InfraError"),
            .. Run("r1", 1, "build-01", 600, 600).Select(r => r with { Status = "Failed", ExitCode = 1 }),
            Job("build-01", 600) with { RunSeconds = null }, // never started its command
            Job("build-01", 600) with { Key = null, V = null }, // a line written before 0.7.0
        ];
        Assert.Empty(History(records).Runs);
        Assert.Equal(ShardTiming.NoHistory, Pick(History(records), 5, Slots(4)).Basis);
    }

    [Fact]
    public void PicksCeilWorkOverBudget()
    {
        // W 16.8 min, O 41 s, M 5: budget 259 s, 1008 / 259 = 3.9 -> 4 shards, each about (41 + 1008/4) / 60 = 4.9 min.
        var r = Pick(History(Run("r1", 1, "build-01", 252, 252, 252, 252).Select(j => j with { WallSeconds = 252 + 41 })), 5, Slots(10));
        Assert.Equal((ShardTiming.History, 4, 4, 10, 1), (r.Basis, r.Picked, r.Wanted, r.FreeSlots, r.RunsUsed));
        Assert.Equal((16.8, 41.0, 4.9), (r.WorkMinutes, r.OverheadSeconds, r.ExpectedShardMinutes));
        Assert.Null(r.Capped);
        Assert.Equal("--target-minutes 5: 1 earlier run, about 16.8 min of work + 41 s per shard, about 4.9 min each", r.Why);
        Assert.Equal(Now.AddDays(-1), r.LastRunUtc);
        Assert.Equal((Key, "."), (r.Command, r.Dir));
    }

    [Fact]
    public void CappedByFreeSlotsAndBy64()
    {
        // 60 min of work in 5 min (30 s overhead): ceil(3600 / 270) = 14 shards wanted, 5 free slots.
        var hour = History(Run("r1", 1, "build-01", 900, 900, 900, 900));
        var r = Pick(hour, 5, Slots(5));
        Assert.Equal((5, 14, ShardTiming.CappedByFreeSlots), (r.Picked, r.Wanted, r.Capped));
        Assert.EndsWith("; 14 shards would be needed for 5 min; capped at 5 free slots: about 12.5 min each", r.Why, StringComparison.Ordinal);

        // 100 h of work in 1 min, 70 free slots: 64, the most one run may have.
        var huge = Pick(History([Job("build-01", 360_000, wall: 360_001)]), 1, Slots(70));
        Assert.Equal((64, ShardTiming.CappedByMaxShards, 70), (huge.Picked, huge.Capped, huge.FreeSlots));
        Assert.Contains("capped at 64 shards (the most one run may have)", huge.Why, StringComparison.Ordinal);

        // Tiny M is still capped by the free slots, and no free slot at all is 1 shard.
        Assert.Equal(1, Pick(hour, 1, Fleet()).Picked);
        Assert.Equal(2, Pick(hour, 1, Slots(2)).Picked);
    }

    [Fact]
    public void OverheadAboveTargetUsesEveryFreeSlot()
    {
        // 6.2 min of overhead per shard is above 5 min: more shards cannot reach it.
        var slow = History(Run("r1", 1, "build-01", 60, 60).Select(j => j with { WallSeconds = 60 + 372 }));
        var r = Pick(slow, 5, Slots(5));
        Assert.Equal((ShardTiming.Overhead, 5, (int?)null, (string?)null), (r.Basis, r.Picked, r.Wanted, r.Capped));
        Assert.Contains("; the per-shard overhead (6.2 min) is above --target-minutes 5; more shards cannot reach it, so all 5 free slots: about 6.6 min each", r.Why, StringComparison.Ordinal);

        // Under a tenth of the target left for the work counts the same.
        var close = History(Run("r1", 1, "build-01", 60, 60).Select(j => j with { WallSeconds = 60 + 280 }));
        Assert.Equal(ShardTiming.Overhead, Pick(close, 5, Slots(5)).Basis);
        Assert.Contains("leaves less than a tenth of --target-minutes 5", Pick(close, 5, Slots(5)).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TinySuiteGetsOneShard()
    {
        var r = Pick(History([Job("build-01", 20, wall: 35)]), 5, Slots(8));
        Assert.Equal((1, 1), (r.Picked, r.Wanted));
    }

    [Fact]
    public void SlowestDealtMachineDecides()
    {
        // fast-01 is ranked first (least loaded): one shard goes there alone; more reach slow-01, which then decides.
        var fleet = Fleet(("fast-01", 2, 0.1, null, null), ("slow-01", 2, 0.5, null, null));
        var speeds = new Dictionary<string, MachineSpeed>(StringComparer.Ordinal) { ["fast-01"] = new(0.5, 9), ["slow-01"] = new(2, 9) };
        TimingHistory With(double runSeconds) => History([Job("fast-01", runSeconds, wall: runSeconds)]) with { Speeds = speeds };

        // W = 400 / 0.5 = 800 reference seconds. M 5 (300 s): N0 = 3 reaches slow-01 (f 2): N1 = ceil(1600 / 300) = 6, capped at 4 free slots.
        var r = Pick(With(400), 5, fleet);
        Assert.Equal(("slow-01", 2.0, 6, 4), (r.SlowestMachine, r.SpeedFactor, r.Wanted, r.Picked));
        Assert.Contains("slowest machine slow-01 x2.00: about 6.7 min each", r.Why, StringComparison.Ordinal);

        // W = 100 / 0.5 = 200: one shard, dealt to fast-01 only, so slow-01 does not count.
        var one = Pick(With(100), 5, fleet);
        Assert.Equal(("fast-01", 0.5, 1), (one.SlowestMachine, one.SpeedFactor, one.Picked));
        Assert.Equal(1.7, one.ExpectedShardMinutes);
    }

    [Fact]
    public void ExpectedMinutesMatchTheFormula()
    {
        var fleet = Fleet(("build-01", 3, 0.1, null, null), ("micro-01", 3, 0.2, null, null));
        var speeds = new Dictionary<string, MachineSpeed>(StringComparer.Ordinal) { ["micro-01"] = new(1.62, 37) };
        var h = History(Run("r1", 1, "build-01", 300, 300, 300).Select(j => j with { WallSeconds = 300 + 41 })) with { Speeds = speeds };
        var r = Pick(h, 5, fleet);
        var e = ShardTiming.Estimate(h, m => ShardTiming.SpeedOf(m, speeds, fleet.Assessed).Factor)!;
        Assert.Equal(Math.Round((e.OverheadSeconds + (r.SpeedFactor!.Value * e.WorkSeconds / r.Picked)) / 60, 1), r.ExpectedShardMinutes);
        Assert.Equal(("micro-01", 1.62), (r.SlowestMachine, r.SpeedFactor));
    }
}
