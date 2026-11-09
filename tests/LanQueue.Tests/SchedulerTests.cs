using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Scheduling;
using Xunit;

namespace LanQueue.Tests;

public class SchedulerTests
{
    private static (Machine, MachineHealth) C(string name, double load, string[] caps, int slots = 0, string? hold = null, bool up = true) =>
        (new Machine { Name = name, Capabilities = [.. caps], MaxJobs = 2 }, new MachineHealth(name, up, load, 10, slots, hold, null));

    [Fact]
    public void PicksLeastLoadedEligible()
    {
        var ranked = Scheduler.Rank([C("a", 0.5, ["dotnet"]), C("b", 0.1, ["dotnet"]), C("c", 0.3, ["dotnet"])], ["dotnet"]);
        Assert.Equal(["b", "c", "a"], ranked.Select(m => m.Name));
    }

    [Fact]
    public void RequiredCapabilitiesMustAllMatch()
    {
        var ranked = Scheduler.Rank([C("a", 0.1, ["dotnet"]), C("b", 0.2, ["dotnet", "docker"])], ["dotnet", "docker"]);
        Assert.Equal(["b"], ranked.Select(m => m.Name));
    }

    [Fact]
    public void HeldDownAndFullMachinesAreSkipped()
    {
        var ranked = Scheduler.Rank(
            [C("held", 0.1, ["x"], hold: "outside allowed hours"), C("down", 0.1, ["x"], up: false), C("full", 0.1, ["x"], slots: 2), C("ok", 0.9, ["x"])],
            ["x"]);
        Assert.Equal(["ok"], ranked.Select(m => m.Name));
    }

    [Fact]
    public void TiesBreakOnSlotsThenName()
    {
        var ranked = Scheduler.Rank([C("b", 0.2, ["x"]), C("a", 0.2, ["x"], slots: 1), C("c", 0.2, ["x"])], ["x"]);
        Assert.Equal(["b", "c", "a"], ranked.Select(m => m.Name));
    }

    private static Machine Box(string name, string[] caps, int maxJobs = 2, MachineOs os = MachineOs.Linux) =>
        new() { Name = name, Os = os, Capabilities = [.. caps], MaxJobs = maxJobs };

    private static MachineHealth Up(string name, double load = 0.1, int slots = 0, double? memFree = null, double? memTotal = null) =>
        new(name, true, load, 50, slots, null, null) { MemFreeGb = memFree, MemTotalGb = memTotal };

    /// <summary>The 0.5.1 Rank filter, kept here as the oracle the shared assessment must agree with.</summary>
    private static List<string> OldRank(IEnumerable<(Machine Machine, MachineHealth Health)> c, IReadOnlyCollection<string> needs) =>
        [.. c.Where(x => x.Health.Reachable && x.Health.Hold is null && x.Health.SlotsUsed < x.Machine.MaxJobs
                && needs.All(n => x.Machine.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(x => x.Health.LoadRatio).ThenBy(x => x.Health.SlotsUsed).ThenBy(x => x.Machine.Name, StringComparer.Ordinal)
            .Select(x => x.Machine.Name)];

    [Fact]
    public void AssessAgreesWithRankOnEveryCombination()
    {
        var holds = new (string? Text, HoldKind Kind)[]
        {
            (null, HoldKind.None), ("outside allowed hours", HoldKind.OutsideHours), ("git is not installed", HoldKind.ToolMissing), ("paused on the machine", HoldKind.Paused),
        };
        var i = 0;
        var fleet = new List<(Machine, MachineHealth)>();
        foreach (var up in new[] { true, false })
        {
            foreach (var hold in holds)
            {
                foreach (var slots in new[] { 0, 1, 2 })
                {
                    foreach (var caps in new[] { new[] { "dotnet" }, ["dotnet", "GPU"], ["node"] })
                    {
                        var name = "m" + (i++).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                        fleet.Add((Box(name, caps), new MachineHealth(name, up, i % 5 / 10.0, 50, slots, up ? hold.Text : "unreachable", up ? null : "no route")
                        {
                            HoldKind = up ? hold.Kind : HoldKind.Unreachable,
                        }));
                    }
                }
            }
        }

        foreach (var needs in new[] { Array.Empty<string>(), ["dotnet"], ["dotnet", "gpu"], ["node"], ["docker"] })
        {
            var assessed = Scheduler.Assess(fleet, new JobNeeds(needs, null, false));
            Assert.Equal(OldRank(fleet, needs), Scheduler.Rank(assessed).Select(a => a.Machine.Name));
            Assert.Equal(OldRank(fleet, needs), Scheduler.Rank(fleet, needs).Select(m => m.Name));
            Assert.Equal(fleet.Select(f => f.Item1.Name), assessed.Select(a => a.Machine.Name)); // config order kept
            Assert.All(assessed, a => Assert.Equal(a.Eligible, a.Why is null));
        }
    }

    [Fact]
    public void MissingCapabilityIsPermanentAndNamesTheCapability()
    {
        var a = Assert.Single(Scheduler.Assess([(Box("micro-01", ["dotnet", "linux"]), Up("micro-01"))], new JobNeeds(["dotnet", "gpu"], null, false)));

        Assert.Equal(Exclusion.MissingCapability, a.Reason);
        Assert.Equal("missing_capability", a.Code);
        Assert.True(a.Permanent);
        Assert.False(a.Eligible);
        Assert.Equal("lacks gpu (capabilities: dotnet, linux)", a.Why);
        Assert.Equal(0, a.JobSlots);
        Assert.Equal(2, a.FreeSlots);
    }

    [Fact]
    public void MemAboveTotalRamIsPermanentButBelowFreeIsNot()
    {
        var box = Box("build-01", ["dotnet"]);
        var needs = new JobNeeds(["dotnet"], 8, false);

        var tooSmall = Scheduler.Assess([(box, Up("build-01", memFree: 3, memTotal: 7.6))], needs)[0];
        Assert.Equal((Exclusion.MemExceedsTotal, true), (tooSmall.Reason, tooSmall.Permanent));
        Assert.Equal("7.6 GB total memory, --mem needs 8.0 GB", tooSmall.Why);

        var busyNow = Scheduler.Assess([(box, Up("build-01", memFree: 3, memTotal: 16))], needs)[0];
        Assert.Equal((Exclusion.MemBelowRequest, false), (busyNow.Reason, busyNow.Permanent));
        Assert.Equal("3.0 GB free memory, --mem needs 8.0 GB", busyNow.Why);

        var unknown = Scheduler.Assess([(box, Up("build-01"))], needs)[0];
        Assert.Equal((Exclusion.MemUnknown, true), (unknown.Reason, unknown.Permanent));

        var fine = Scheduler.Assess([(box, Up("build-01", memFree: 20, memTotal: 32))], needs)[0];
        Assert.True(fine.Eligible);
        Assert.Equal(2, fine.JobSlots);

        // One copy fits in 12 GB free at --mem 8, so the job slots are fewer than the free slots.
        var oneCopy = Scheduler.Assess([(box, Up("build-01", memFree: 12, memTotal: 32))], needs)[0];
        Assert.Equal((2, 1), (oneCopy.FreeSlots, oneCopy.JobSlots));
    }

    [Fact]
    public void AnUnreachableMachineIsNotPermanentUnlessItsConfigExcludesIt()
    {
        var down = new MachineHealth("win-01", false, 0, 0, 0, "unreachable", "ssh: connect to host 10.20.30.40 port 22: Connection refused") { HoldKind = HoldKind.Unreachable };
        var win = Box("win-01", ["dotnet"], os: MachineOs.Windows);

        var plain = Scheduler.Assess([(win, down)], new JobNeeds(["dotnet"], 4, false))[0];
        Assert.Equal((Exclusion.Unreachable, false), (plain.Reason, plain.Permanent));
        Assert.StartsWith("unreachable: ssh: connect to host", plain.Why, StringComparison.Ordinal);
        Assert.Equal(Exclusion.Asleep, Scheduler.Assess([(win, down with { LooksAsleep = true })], new JobNeeds(["dotnet"], null, false))[0].Reason);

        // The capability is missing whatever the ssh error: that is said first, and waiting cannot help.
        var lacks = Scheduler.Assess([(win, down)], new JobNeeds(["gpu"], null, false))[0];
        Assert.Equal((Exclusion.MissingCapability, true), (lacks.Reason, lacks.Permanent));
        Assert.StartsWith("lacks gpu", lacks.Why, StringComparison.Ordinal);

        var unsafeArgs = Scheduler.Assess([(win, down)], new JobNeeds(["dotnet"], null, true))[0];
        Assert.Equal((Exclusion.WindowsUnsafeArgs, true), (unsafeArgs.Reason, unsafeArgs.Permanent));
        Assert.Equal(Scheduler.WindowsUnsafeHold, unsafeArgs.Health.Hold);
    }

    [Fact]
    public void ToolMissingIsPermanent()
    {
        var h = Up("build-01") with { Hold = "git is not installed on the machine", HoldKind = HoldKind.ToolMissing };
        var a = Scheduler.Assess([(Box("build-01", ["dotnet"]), h)], new JobNeeds(["dotnet"], null, false))[0];

        Assert.Equal((Exclusion.ToolMissing, true, "tool_missing"), (a.Reason, a.Permanent, a.Code));
        Assert.Equal("git is not installed on the machine", a.Why);
    }

    [Fact]
    public void TemporaryReasonsAreNotPermanentAndBusyComesLast()
    {
        var box = Box("build-01", ["dotnet"]);
        var needs = new JobNeeds(["dotnet"], null, false);
        var held = Up("build-01", slots: 2) with { Hold = "load 0.95 above threshold 0.80", HoldKind = HoldKind.Load };
        var a = Scheduler.Assess([(box, held)], needs)[0];
        Assert.Equal((Exclusion.Load, false), (a.Reason, a.Permanent));

        var busy = Scheduler.Assess([(box, Up("build-01", slots: 2))], needs)[0];
        Assert.Equal((Exclusion.Busy, false, "busy (slots 2/2)"), (busy.Reason, busy.Permanent, busy.Why));

        var avoided = Scheduler.Assess([(box, Up("build-01", slots: 2))], needs, new HashSet<string>(["build-01"]))[0];
        Assert.Equal((Exclusion.Avoided, "avoided"), (avoided.Reason, avoided.Code));
    }

    [Fact]
    public void DealSpreadsRoundRobinAndFillsEverySlot()
    {
        var assessed = Scheduler.Assess(
            [(Box("tiny-01", ["node"], maxJobs: 1), Up("tiny-01")), (Box("big-01", ["node"], maxJobs: 4), Up("big-01", load: 0.2))], new JobNeeds(["node"], null, false));
        List<(Assessment, int)> free = [.. Scheduler.Rank(assessed).Select(a => (a, a.JobSlots))];

        var dealt = Scheduler.Deal(free, [1, 2, 3, 4, 5], (_, _) => true);
        Assert.Equal([(1, "tiny-01"), (2, "big-01"), (3, "big-01"), (4, "big-01"), (5, "big-01")], dealt.Select(d => (d.Item, d.Machine.Machine.Name)));

        var few = Scheduler.Deal(free, [1, 2], (_, _) => true);
        Assert.Equal(["tiny-01", "big-01"], few.Select(d => d.Machine.Machine.Name)); // a few items spread out

        // An item that must avoid a machine waits for another; the next allowed item takes the slot.
        var avoiding = Scheduler.Deal(free, [1, 2, 3], (item, m) => !(item == 1 && m.Name == "tiny-01"));
        Assert.Equal([(2, "tiny-01"), (1, "big-01"), (3, "big-01")], avoiding.Select(d => (d.Item, d.Machine.Machine.Name)));
    }

    [Fact]
    public void AutoCountIsTheJobSlotsOfEligibleMachinesBetweenOneAndTheCap()
    {
        var assessed = Scheduler.Assess(
            [(Box("tiny-01", ["node"], maxJobs: 1), Up("tiny-01", memFree: 1.5)), (Box("big-01", ["node"], maxJobs: 4), Up("big-01", memFree: 12))],
            new JobNeeds(["node"], 2, false));
        var (n, total, free) = Scheduler.AutoCount(assessed, 2, 64);
        Assert.Equal((4, 4), (n, total));
        Assert.Equal([("big-01", 4)], free.Select(f => (f.Machine.Name, f.Free)));
        Assert.Equal(3, Scheduler.AutoCount(assessed, 2, 3).Count);

        var none = Scheduler.AutoCount(Scheduler.Assess([(Box("x", ["node"]), Up("x", slots: 2))], new JobNeeds(["node"], null, false)), null, 64);
        Assert.Equal((1, 0), (none.Count, none.Total));
    }
}
