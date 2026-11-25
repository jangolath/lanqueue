using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Scheduling;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 B2: two wait budgets. Each look is a slot wait (a machine could take the job but its slots are full, or its memory is held by
/// running lanq jobs) or an eligible wait (nothing can take this kind of job now). Both clocks are idle clocks that restart whenever the run
/// or plan holds or gives back a slot, each interval is charged to the state seen at its start, and the total never exceeds the larger
/// budget, so --wait S (both budgets S) keeps the 0.12 timeline exactly.
/// </summary>
public sealed class WaitBudgetTests
{
    private static Machine Box(string name, int maxJobs = 2, double? floor = null) =>
        new() { Name = name, Os = MachineOs.Linux, Capabilities = ["dotnet"], MaxJobs = maxJobs, MinFreeMemGb = floor };

    private static MachineHealth Up(string name, int slots = 0, double? free = 30, double? total = 32, string? hold = null, HoldKind kind = HoldKind.None, bool up = true) =>
        new(name, up, 0.1, 50, slots, hold, null) { MemFreeGb = free, MemTotalGb = total, HoldKind = kind };

    private static readonly JobNeeds Needs = new(["dotnet"], null, false) { Policy = new PolicyConfig() };

    private static WaitKind Kind(JobNeeds needs, IReadOnlySet<string>? sshFailed, params (Machine, MachineHealth)[] pool) =>
        Scheduler.WaitKindOf(Scheduler.Assess(pool, needs), sshFailed);

    private static WaitKind Kind(params (Machine, MachineHealth)[] pool) => Kind(Needs, null, pool);

    [Fact]
    public void BusyOnlyIsASlotWait() => Assert.Equal(WaitKind.Slot, Kind((Box("a"), Up("a", slots: 2)), (Box("b"), Up("b", slots: 2))));

    [Fact]
    public void HeldOnlyIsAnEligibleWait() =>
        Assert.Equal(WaitKind.Eligible, Kind((Box("a"), Up("a", hold: "paused (PAUSE file)", kind: HoldKind.Paused)), (Box("b"), Up("b", up: false))));

    [Fact]
    public void MixedIsASlotWait() =>
        Assert.Equal(WaitKind.Slot, Kind((Box("a"), Up("a", hold: "outside allowed hours", kind: HoldKind.OutsideHours)), (Box("b"), Up("b", slots: 2))));

    [Fact]
    public void LowMemoryHeldByRunningLanqJobsIsASlotWait()
    {
        // M4: below its floor (a LowMemory hold, judged before the job's memory), but lanq jobs run there and its total covers the need.
        var needs = Needs with { MemGb = 4 };
        Assert.Equal(WaitKind.Slot, Kind(needs, null, (Box("a", maxJobs: 4, floor: 2), Up("a", slots: 1, free: 1.5, hold: "low memory", kind: HoldKind.LowMemory))));
        Assert.Equal(WaitKind.Slot, Kind(needs, null, (Box("a", maxJobs: 4, floor: 2), Up("a", slots: 1, free: 4)))); // mem_below_request
        // No lanq job there: the memory is someone else's; waiting for it is an eligible wait.
        Assert.Equal(WaitKind.Eligible, Kind(needs, null, (Box("a", maxJobs: 4, floor: 2), Up("a", slots: 0, free: 1.5, hold: "low memory", kind: HoldKind.LowMemory))));
        // A total that can never cover the need is permanent, not a wait.
        Assert.Equal(WaitKind.Eligible, Kind(needs, null, (Box("a", maxJobs: 4, floor: 2), Up("a", slots: 1, free: 1, total: 5, hold: "low memory", kind: HoldKind.LowMemory))));
    }

    [Fact]
    public void LoadOrPressureFromRunningLanqJobsIsASlotWait()
    {
        // p1 review H1 (owner decision): held for load or memory pressure while lanq jobs run there: a finishing job gives it back.
        Assert.Equal(WaitKind.Slot, Kind((Box("a", maxJobs: 4), Up("a", slots: 2, hold: "load 0.95", kind: HoldKind.Load))));
        Assert.Equal(WaitKind.Slot, Kind((Box("a", maxJobs: 4), Up("a", slots: 1, hold: "memory pressure", kind: HoldKind.MemoryPressure))));
        // No lanq job there: someone else's work holds it, an eligible wait.
        Assert.Equal(WaitKind.Eligible, Kind((Box("a", maxJobs: 4), Up("a", slots: 0, hold: "load 0.95", kind: HoldKind.Load))));
        Assert.Equal(WaitKind.Eligible, Kind((Box("a", maxJobs: 4), Up("a", slots: 0, hold: "memory pressure", kind: HoldKind.MemoryPressure))));
    }

    [Fact]
    public void AnEligibleMachineWhoseClaimLostTheRaceIsASlotWaitButAnSshFailureIsNot()
    {
        Assert.Equal(WaitKind.Slot, Kind(Needs, new HashSet<string>(), (Box("a"), Up("a"))));
        Assert.Equal(WaitKind.Eligible, Kind(Needs, new HashSet<string> { "a" }, (Box("a"), Up("a"))));
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void BothBudgetsEqualIsThe012Timeline()
    {
        // 0.12: NoCapacity once idle time >= --wait, whatever the machines said. States alternate here; the end is still at 600 s.
        var budgets = new WaitBudgets(S(600), S(600)) { Legacy = true };
        var clock = new WaitClock();
        var t = 0.0;
        WaitKind? spent = null;
        while (spent is null)
        {
            clock.Look(S(t), S(0), (int)(t / 15) % 2 == 0 ? WaitKind.Eligible : WaitKind.Slot);
            spent = clock.SpentOut(budgets);
            if (spent is null)
            {
                t += 15;
            }
        }

        Assert.Equal(600, t);
        Assert.Equal(S(600), clock.Eligible + clock.Slot);
    }

    [Fact]
    public void ZeroBudgetsEndAtTheFirstLook()
    {
        var clock = new WaitClock();
        clock.Look(S(2), S(0), WaitKind.Slot);
        Assert.Equal(WaitKind.Slot, clock.SpentOut(new WaitBudgets(TimeSpan.Zero, TimeSpan.Zero)));
    }

    [Fact]
    public void EachIntervalIsChargedToTheStateSeenAtItsStart()
    {
        var clock = new WaitClock();
        clock.Look(S(0), S(0), WaitKind.Slot);
        clock.Look(S(15), S(0), WaitKind.Eligible); // 0-15 was a slot wait
        clock.Look(S(30), S(0), WaitKind.Eligible); // 15-30 eligible
        Assert.Equal((S(15), S(15)), (clock.Eligible, clock.Slot));
    }

    [Fact]
    public void TheClocksRestartWhenTheRunHoldsOrGivesBackASlot()
    {
        var clock = new WaitClock();
        clock.Look(S(0), S(0), WaitKind.Slot);
        clock.Look(S(100), S(0), WaitKind.Slot);
        Assert.Equal(S(100), clock.Slot);

        // A slot was given back at 130 s: the idle clocks start there (0.12's idle.Restart / _idleSince); 130-150 goes to the state now seen.
        clock.Look(S(150), S(130), WaitKind.Eligible);
        Assert.Equal((S(20), TimeSpan.Zero), (clock.Eligible, clock.Slot));
    }

    [Fact]
    public void SeparateBudgetsEndOnTheBudgetOfTheCurrentState()
    {
        // --wait-eligible 60 --wait-slot 600: a full pool waits; a held pool ends after 60 s of eligible waiting.
        var budgets = new WaitBudgets(S(60), S(600));
        var clock = new WaitClock();
        for (var t = 0; t <= 300; t += 15)
        {
            clock.Look(S(t), S(0), WaitKind.Slot);
            Assert.Null(clock.SpentOut(budgets));
        }

        clock.Look(S(315), S(0), WaitKind.Eligible); // 300-315 was still a slot wait
        Assert.Null(clock.SpentOut(budgets));
        var t2 = 315;
        while (clock.SpentOut(budgets) is null)
        {
            t2 += 15;
            clock.Look(S(t2), S(0), WaitKind.Eligible);
        }

        Assert.Equal((375, WaitKind.Eligible), (t2, clock.SpentOut(budgets)));
        Assert.Equal(S(60), clock.Eligible);
    }

    [Fact]
    public void SplitBudgetsEachEndOnTheirOwnClock()
    {
        // p1 review M1: --wait-eligible 3600 --wait-slot 14400; 3000 s held, then busy: the slot budget gets its full 14400 s.
        var budgets = new WaitBudgets(S(3600), S(14400));
        var clock = new WaitClock();
        clock.Look(S(0), S(0), WaitKind.Eligible);
        clock.Look(S(3000), S(0), WaitKind.Slot);
        clock.Look(S(3000 + 11400), S(0), WaitKind.Slot);
        Assert.Null(clock.SpentOut(budgets));
        clock.Look(S(3000 + 14400), S(0), WaitKind.Slot);
        Assert.Equal(WaitKind.Slot, clock.SpentOut(budgets));
        Assert.Equal((S(3000), S(14400)), (clock.Eligible, clock.Slot));
    }

    [Fact]
    public void ThePlainWaitTotalNeverExceedsItsBudget()
    {
        // --wait W only (both budgets W): the total caps, so alternating states never wait longer than 0.12 did.
        var budgets = new WaitBudgets(S(60), S(90)) { Legacy = true };
        var clock = new WaitClock();
        var t = 0;
        var kinds = new[] { WaitKind.Slot, WaitKind.Slot, WaitKind.Eligible, WaitKind.Eligible, WaitKind.Eligible, WaitKind.Slot, WaitKind.Slot, WaitKind.Slot };
        WaitKind? spent = null;
        foreach (var k in kinds)
        {
            clock.Look(S(t), S(0), k);
            spent = clock.SpentOut(budgets);
            if (spent is not null)
            {
                break;
            }

            t += 15;
        }

        Assert.Equal(90, t); // 30 s slot + 45 s eligible + 15 s slot: neither clock spent, but 90 s in all
        Assert.Equal(WaitKind.Slot, spent);
    }

    [Fact]
    public void ThePlanClockCountsOnlyWhileNoJobOfThePlanHoldsASlot()
    {
        var plan = new PlanProgress();
        var start = plan.IdleSince;
        Assert.NotNull(start);
        plan.Hold();
        Assert.Null(plan.IdleSince); // a sibling holds a slot: no job of the plan is idle-waiting
        plan.Release();
        Assert.True(plan.IdleSince > start); // the clocks restart at the release (0.12's _idleSince)
    }

    [Theory]
    [InlineData("--wait", "-1")]
    [InlineData("--wait", "abc")]
    [InlineData("--wait", "1.5")]
    [InlineData("--wait-eligible", "-5")]
    [InlineData("--wait-slot", "x")]
    [InlineData("--wait-slot", "604801")]
    public void BadWaitValuesAreArgumentErrors(string option, string value) =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--needs", "dotnet", option, value, "--", "dotnet", "test"]));

    [Theory]
    [InlineData("604801")]
    [InlineData("1000000")]
    [InlineData("99999999999999999999")]
    public void PlainWaitAboveAWeekWaitsAWeekAsBefore(string value)
    {
        // p1 review L1: 0.12 took any --wait; a "wait for ever" script keeps working, clamped to one week with a note.
        var p = ParsedArgs.Parse(["run", "--wait", value, "--", "dotnet", "test"]);
        Assert.Equal((ParsedArgs.MaxWaitSeconds, true), (p.WaitSeconds, p.WaitClamped));
        Assert.False(ParsedArgs.Parse(["run", "--wait", "600", "--", "dotnet", "test"]).WaitClamped);
    }

    [Fact]
    public void ShardSetNextStepsNameTheBudgetThatRanOut()
    {
        // p1 review M3: a shard set that ran out of its eligible budget is told to look at health, then raise --wait-eligible.
        var p = ParsedArgs.Parse(["run", "--json", "--shards", "2", "--wait-eligible", "30", "--wait-slot", "600", "--needs", "dotnet", "--", "dotnet", "test"]);
        var shard = new ShardResult(1, nameof(JobStatus.NoCapacity), 75, null, null, TimeSpan.Zero, 0, "x") { Retryable = true, Excluded = [], Waited = new WaitedInfo(S(30), S(0), WaitKind.Eligible) };
        var r = JobRunner.Aggregate(2, [shard, shard with { Index = 2 }], false);
        var steps = NextSteps.ForShards(p, r);
        Assert.Equal("run_health", steps[0].Action);
        Assert.Contains("--wait-eligible", steps[1].Argv!);
        Assert.Contains("120", steps[1].Argv!);
    }

    [Fact]
    public void PlanNextStepsNameTheBudgetThatRanOut()
    {
        var p = ParsedArgs.Parse(["run", "--plan", "--json", "--wait-eligible", "30", "--wait-slot", "600"]);
        var job = new PlanJobOutcome("unit", nameof(JobStatus.NoCapacity), 75, "x", []) { Retryable = true, Excluded = [], Waited = new WaitedInfo(S(30), S(0), WaitKind.Eligible) };
        var r = new PlanRunResult(nameof(JobStatus.NoCapacity), 75, "x", [job]) { Decided = "unit", Retryable = true, Excluded = [], Waited = job.Waited };
        var steps = NextSteps.ForPlanFile(p, r, new Dictionary<string, ParsedArgs>());
        Assert.Equal("run_health", steps[0].Action);
        Assert.Equal("wait", steps[1].Action);
        Assert.Contains("the --wait-eligible budget (30 s) ran out", steps[1].Reason, StringComparison.Ordinal);
        Assert.Equal(["lanq", "run", "--plan", "--json", "--wait-eligible", "120", "--wait-slot", "600", "--only", "unit"], steps[1].Argv);
    }

    [Fact]
    public void WaitAndASplitBudgetTogetherAreRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--wait", "60", "--wait-slot", "600", "--", "dotnet", "test"]));
        Assert.Contains("--wait-eligible", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitBudgetsParseAndDefaultToZero()
    {
        var p = ParsedArgs.Parse(["run", "--wait-eligible", "30", "--wait-slot", "600", "--", "dotnet", "test"]);
        Assert.Equal((30, 600, true, 600), (p.EligibleSeconds, p.SlotSeconds, p.SplitWait, p.WaitSeconds));
        var slotOnly = ParsedArgs.Parse(["run", "--wait-slot", "600", "--", "dotnet", "test"]);
        Assert.Equal((0, 600), (slotOnly.EligibleSeconds, slotOnly.SlotSeconds));
        var legacy = ParsedArgs.Parse(["run", "--wait", "120", "--", "dotnet", "test"]);
        Assert.Equal((120, 120, false, 120), (legacy.EligibleSeconds, legacy.SlotSeconds, legacy.SplitWait, legacy.WaitSeconds));
        Assert.Equal((0, 0), (ParsedArgs.Parse(["run", "--", "dotnet", "test"]).EligibleSeconds, ParsedArgs.Parse(["run", "--", "dotnet", "test"]).SlotSeconds));
        Assert.Equal(604800, ParsedArgs.Parse(["run", "--wait", "604800", "--", "dotnet", "test"]).WaitSeconds);
    }

    [Fact]
    public void SplitBudgetsAreAllowedWithAPlanButNotOnHealth()
    {
        var p = ParsedArgs.Parse(["run", "--plan", "--wait-eligible", "30", "--wait-slot", "600"]);
        Assert.Equal((30, 600), (p.EligibleSeconds, p.SlotSeconds));
        Assert.Contains("--wait-slot", ParsedArgs.PlanTakes, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--wait-slot", "5"]));
    }

    [Fact]
    public void WaitedIsATopLevelJsonField()
    {
        var run = new RunJson("NoCapacity", 75, null, "j1", 31, "No eligible machine.")
        {
            Retryable = true,
            Waited = new WaitedJson(30, 0, "eligible"),
        };
        var json = JsonSerializer.Serialize(run, LanqJson.Default.RunJson);
        Assert.Contains("\"retryable\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"waited\":{\"eligibleSeconds\":30,\"slotSeconds\":0,\"budget\":\"eligible\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("noCapacity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("waited", JsonSerializer.Serialize(new RunJson("Completed", 0, "a", "j1", 1, "ok"), LanqJson.Default.RunJson), StringComparison.Ordinal);
    }

    [Fact]
    public void NextStepRaisesTheBudgetThatRanOut()
    {
        var slot = ParsedArgs.Parse(["run", "--needs", "dotnet", "--wait-eligible", "30", "--wait-slot", "60", "--json", "--", "dotnet", "test"]) with { Raw = ["run", "--needs", "dotnet", "--wait-eligible", "30", "--wait-slot", "60", "--json", "--", "dotnet", "test"] };
        var slotSteps = NextSteps.ForNoCapacityWait(slot, WaitKind.Slot);
        Assert.Contains(slotSteps, s => s.Argv is { } c && c.Contains("--wait-slot") && c[c.ToList().IndexOf("--wait-slot") + 1] == "120");
        Assert.DoesNotContain(slotSteps, s => s.Argv is { } c && c.Contains("--wait"));

        var eligible = slot with { };
        var steps = NextSteps.ForNoCapacityWait(eligible, WaitKind.Eligible);
        Assert.Equal("run_health", steps[0].Action);
        Assert.Contains(steps, s => s.Argv is { } c && c.Contains("--wait-eligible") && c[c.ToList().IndexOf("--wait-eligible") + 1] == "120");
    }

    [Fact]
    public void ALegacyWaitRerunNeverAddsASplitFlagAndASplitRerunNeverAddsWait()
    {
        string[] raw = ["run", "--needs", "dotnet", "--wait-slot", "60", "--", "dotnet", "test"];
        var split = ParsedArgs.Parse(raw) with { Raw = raw };
        var rerun = SuggestedCommand.Rerun(split, waitSeconds: 120)!;
        Assert.DoesNotContain("--wait", rerun.Argv);
        Assert.Contains("--wait-eligible", rerun.Argv);
        Assert.Contains("--no-wake", rerun.Argv); // eligible budget 0: the original could not wake a machine
    }
}
