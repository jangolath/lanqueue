using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Scheduling;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 B1: memory-aware placement. A job's need on a machine is max(--mem, the machine's job_mem_gb or policy.job_mem_gb); the
/// machine's free-memory floor (min_free_mem_gb) stays free; a job_mem_gb also caps the slots by total memory (shared across lanq runs);
/// a machine that never reports memory keeps slots-only placement unless the job itself gave --mem.
/// </summary>
public sealed class MemoryPlacementTests
{
    private static Machine Box(string name = "big-01", int maxJobs = 4, double? jobMem = null, double? floor = null) =>
        new() { Name = name, Os = MachineOs.Linux, Capabilities = ["dotnet"], MaxJobs = maxJobs, JobMemGb = jobMem, MinFreeMemGb = floor };

    private static MachineHealth Up(string name = "big-01", double? free = null, double? total = null, int slots = 0) =>
        new(name, true, 0.1, 50, slots, null, null) { MemFreeGb = free, MemTotalGb = total };

    private static JobNeeds Needs(double? mem = null, double policyFloor = 1.0, double? policyJobMem = null, bool exclusive = false) =>
        new(["dotnet"], mem, false) { Policy = new PolicyConfig { MinFreeMemGb = policyFloor, JobMemGb = policyJobMem }, Exclusive = exclusive };

    private static Assessment One(Machine m, MachineHealth h, JobNeeds needs) => Scheduler.Assess([(m, h)], needs)[0];

    [Fact]
    public void TheFloorIsSubtractedFromFreeMemory()
    {
        // The design's example: 12.5 GB free, a 2 GB floor, --mem 4: 0.12 started 3 and left 0.5 GB; now 2 and the floor stays free.
        var m = Box(maxJobs: 3, floor: 2);
        Assert.Equal(2, Scheduler.FreeFor(m, Up(free: 12.5, total: 16), Needs(mem: 4), 0, 0));
        Assert.Equal(1, Scheduler.FreeFor(m, Up(free: 12.5, total: 16), Needs(mem: 4), 1, 1)); // one of ours placed, not running yet
        Assert.Equal(2, One(m, Up(free: 12.5, total: 16), Needs(mem: 4)).JobSlots);
    }

    [Fact]
    public void WithoutAnyMemoryDeclarationNothingChanges()
    {
        var m = Box(maxJobs: 3, floor: 2);
        Assert.Equal(3, Scheduler.FreeFor(m, Up(free: 2.1, total: 16), Needs(), 0, 0));
        var a = One(m, Up(free: 2.1, total: 16), Needs());
        Assert.True(a.Eligible);
        Assert.Null(a.NeedGb);
    }

    [Theory]
    [InlineData(2.0, 4.0, null, 4.0)] // the machine's size wins when larger
    [InlineData(6.0, 4.0, null, 6.0)] // the job's --mem wins when larger
    [InlineData(null, 4.0, null, 4.0)] // job_mem_gb alone
    [InlineData(null, null, 3.0, 3.0)] // policy default
    [InlineData(null, 5.0, 3.0, 5.0)] // the machine overrides the policy default
    [InlineData(2.0, null, 3.0, 3.0)] // --mem against the policy default
    [InlineData(null, null, null, null)] // nothing declared: no accounting
    public void TheNeedIsTheLargerOfMemAndTheJobSize(double? mem, double? machineJobMem, double? policyJobMem, double? need) =>
        Assert.Equal(need, Scheduler.NeedGb(Box(jobMem: machineJobMem), Needs(mem: mem, policyJobMem: policyJobMem)));

    [Fact]
    public void TheMachineFloorOverridesThePolicyFloor()
    {
        Assert.Equal(1.0, Scheduler.FloorGb(Box(), Needs()));
        Assert.Equal(3.0, Scheduler.FloorGb(Box(floor: 3), Needs()));
        Assert.Equal(0.0, Scheduler.FloorGb(Box(), new JobNeeds(["dotnet"], 2, false))); // no policy given: no floor (the 0.12 arithmetic)
    }

    [Fact]
    public void ANeedAboveTotalLessFloorIsPermanent()
    {
        // A "16 GB" machine reports about 15.9 GB; with a 2 GB floor a 14 GB job never fits.
        var a = One(Box(jobMem: 14, floor: 2), Up(free: 15, total: 15.9), Needs());
        Assert.Equal((Exclusion.MemExceedsTotal, true), (a.Reason, a.Permanent));
        Assert.Contains("job_mem_gb", a.Why, StringComparison.Ordinal);

        var b = One(Box(floor: 2), Up(free: 15, total: 15.9), Needs(mem: 14));
        Assert.Equal((Exclusion.MemExceedsTotal, true), (b.Reason, b.Permanent));
        Assert.Contains("--mem", b.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeLessFloorBelowTheNeedIsRetryable()
    {
        // 5 GB free, 2 GB floor, 4 GB need: above the floor (no LowMemory hold) but 3 GB usable.
        var a = One(Box(floor: 2), Up(free: 5, total: 32), Needs(mem: 4));
        Assert.Equal((Exclusion.MemBelowRequest, false), (a.Reason, a.Permanent));
        Assert.Equal(0, a.JobSlots);
    }

    [Fact]
    public void UnknownFreeMemoryWithOnlyAJobSizeKeepsSlotsOnly()
    {
        // M6: a worker that never reports memory must not drop out of every job when the owner adds policy.job_mem_gb.
        var a = One(Box(maxJobs: 3), Up(), Needs(policyJobMem: 4));
        Assert.True(a.Eligible, a.Why);
        Assert.Equal(3, a.JobSlots);

        // An explicit --mem still needs the report (unchanged).
        var b = One(Box(maxJobs: 3), Up(), Needs(mem: 4));
        Assert.Equal((Exclusion.MemUnknown, true), (b.Reason, b.Permanent));
    }

    [Fact]
    public void AJobSizeCapsTheSlotsByTotalMemory()
    {
        // M5: 31.2 GB total, 2 GB floor, 9 GB jobs: 3 fit, though max_jobs is 4. The cap holds across lanq runs (it limits the claim).
        var m = Box(maxJobs: 4, jobMem: 9, floor: 2);
        Assert.Equal(3, Scheduler.SlotCap(m, Up(free: 30, total: 31.2), Needs()));
        Assert.Equal(3, Scheduler.FreeFor(m, Up(free: 30, total: 31.2), Needs(), 0, 0));
        var busy = One(m, Up(free: 30, total: 31.2, slots: 3), Needs());
        Assert.Equal(Exclusion.Busy, busy.Reason);
        Assert.Contains("job_mem_gb", busy.Why, StringComparison.Ordinal);

        // Only a job size caps: --mem alone is this job's own declaration and the free test gates it.
        Assert.Equal(4, Scheduler.SlotCap(Box(maxJobs: 4, floor: 2), Up(free: 30, total: 31.2), Needs(mem: 9)));

        // Unknown total: no cap.
        Assert.Equal(4, Scheduler.SlotCap(m, Up(), Needs()));
    }

    [Fact]
    public void TheClaimScriptTakesTheCap()
    {
        var m = Box(maxJobs: 4, jobMem: 9, floor: 2);
        var capped = RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromMinutes(10), maxSlots: 3);
        Assert.Contains("-le 3 ]", capped, StringComparison.Ordinal);
        Assert.Contains("-le 4 ]", RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromMinutes(10)), StringComparison.Ordinal);
        Assert.Contains("-le 4 ]", RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromMinutes(10), maxSlots: 9), StringComparison.Ordinal); // never above max_jobs
    }

    [Fact]
    public void WholeMegabytesKeepASlotThatFloatingPointWouldLose()
    {
        // L4: (8.7 - 2.7) / 3 is 1.9999999999999998 in doubles.
        Assert.Equal(2, Scheduler.FreeFor(Box(maxJobs: 4, floor: 2.7), Up(free: 8.7, total: 16), Needs(mem: 3), 0, 0));
    }

    [Fact]
    public void ExclusiveStillHonoursTheFloor()
    {
        var m = Box(maxJobs: 4, floor: 2);
        Assert.Equal(1, Scheduler.FreeFor(m, Up(free: 6.5, total: 16), Needs(mem: 4, exclusive: true), 0, 0));
        Assert.Equal(0, Scheduler.FreeFor(m, Up(free: 5.5, total: 16), Needs(mem: 4, exclusive: true), 0, 0));
    }

    [Fact]
    public void ShardsAutoCountsFloorAwareSlots()
    {
        var needs = Needs(mem: 4);
        var assessed = Scheduler.Assess(
            [(Box("a", maxJobs: 4, floor: 2), Up("a", free: 12.5, total: 16)), (Box("b", maxJobs: 4, floor: 2), Up("b", free: 30, total: 32))], needs);
        var (n, total, free) = Scheduler.AutoCount(assessed, needs, 64);
        Assert.Equal((6, 6), (n, total)); // a: floor(10.5/4) = 2; b: min(4, floor(28/4)) = 4
        Assert.Equal([("b", 4), ("a", 2)], free.Select(f => (f.Machine.Name, f.Free)).OrderByDescending(f => f.Item2));
    }

    [Fact]
    public void TheAssessmentCarriesThePerMachineNeed()
    {
        var needs = Needs(mem: 2);
        var assessed = Scheduler.Assess([(Box("a", jobMem: 6), Up("a", free: 30, total: 32)), (Box("b"), Up("b", free: 30, total: 32))], needs);
        Assert.Equal([6.0, 2.0], assessed.Select(a => a.NeedGb!.Value));
    }

    [Fact]
    public void HealthNeedsRowsShowTheNeed()
    {
        var a = One(Box(jobMem: 6), Up(free: 30, total: 32), Needs(mem: 2));
        var row = Cli.HealthNeedsRow(Cli.HealthRow(a.Machine, a.Health), a);
        Assert.Equal(6.0, row.NeedGb);
        var json = JsonSerializer.Serialize(row, LanQueue.Output.LanqJson.Default.HealthNeedsJson);
        Assert.Contains("\"needGb\":6", json, StringComparison.Ordinal);

        var none = One(Box(), Up(free: 30, total: 32), Needs());
        Assert.DoesNotContain("needGb", JsonSerializer.Serialize(Cli.HealthNeedsRow(Cli.HealthRow(none.Machine, none.Health), none), LanQueue.Output.LanqJson.Default.HealthNeedsJson), StringComparison.Ordinal);
    }

    [Fact]
    public void JobMemConfigKeys()
    {
        var c = ConfigLoader.Parse(T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8\n  job_mem_gb: 3", StringComparison.Ordinal)
            .Replace("max_jobs: 2", "max_jobs: 2\n    job_mem_gb: 9.5", StringComparison.Ordinal));
        Assert.Equal(3, c.Policy.JobMemGb);
        Assert.Equal(9.5, c.Machines[0].JobMemGb);
        Assert.Null(T.Config().Policy.JobMemGb);
        Assert.Null(T.Linux().JobMemGb);
        Assert.Null(ConfigLoader.Parse(T.Yaml.Replace("max_jobs: 2", "max_jobs: 2\n    job_mem_gb:", StringComparison.Ordinal)).Machines[0].JobMemGb);
    }

    [Theory]
    [InlineData("job_mem_gb: 0")]
    [InlineData("job_mem_gb: -1")]
    [InlineData("job_mem_gb: 4097")]
    [InlineData("job_mem_gb: .nan")]
    [InlineData("job_mem_gb: lots")]
    [InlineData("job_mem: 4")] // strict: unknown key
    public void BadJobMemIsRefused(string line)
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("max_jobs: 2", "max_jobs: 2\n    " + line, StringComparison.Ordinal)));
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8\n  " + line, StringComparison.Ordinal)));
    }
}
