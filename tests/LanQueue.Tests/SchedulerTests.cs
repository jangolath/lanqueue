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
}
