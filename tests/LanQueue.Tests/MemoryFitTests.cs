using LanQueue.Config;
using LanQueue.Health;
using Xunit;

namespace LanQueue.Tests;

/// <summary>0.13.0 B4: lanq doctor --remote compares max_jobs with how many jobs of the machine's job size its memory covers.</summary>
public sealed class MemoryFitTests
{
    private static Machine Big(int maxJobs = 4, double? jobMem = 9) => new() { Name = "big-01", Os = MachineOs.Linux, MaxJobs = maxJobs, JobMemGb = jobMem, MinFreeMemGb = 2 };

    private static MachineHealth Probe(double? total) => new("big-01", true, 0.1, 50, 0, null, null) { MemTotalGb = total, MemFreeGb = total };

    [Fact]
    public void MaxJobsOneAboveTheFitIsAnInfoLine()
    {
        var c = Cli.MemoryFitCheck(Big(), Probe(31.2), new PolicyConfig())!;
        Assert.Equal(("job_mem", "ok"), (c.Name, c.Level));
        Assert.Equal("max_jobs 4, and 31.2 GB total - 2 GB floor covers 3 jobs of 9 GB", c.Detail);
    }

    [Fact]
    public void MaxJobsTwoAboveTheFitWarnsAndNamesTheKey()
    {
        var c = Cli.MemoryFitCheck(Big(maxJobs: 5), Probe(31.2), new PolicyConfig())!;
        Assert.Equal("warn", c.Level);
        Assert.Equal("max_jobs 5, and 31.2 GB total - 2 GB floor covers 3 jobs of 9 GB (lanq caps it at 3)", c.Detail);
        Assert.Equal("set max_jobs: 3 for big-01 in machines.yml (doctor never edits the file)", c.Fix);
    }

    [Fact]
    public void AJobSizeLargerThanTheMachineWarnsThatEveryJobIsRuledOut()
    {
        var c = Cli.MemoryFitCheck(Big(maxJobs: 1, jobMem: 16), Probe(15.9), new PolicyConfig())!;
        Assert.Equal("warn", c.Level);
        Assert.EndsWith("every job is mem_exceeds_total here", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicySizeAppliesAndNoMemoryReportIsSaid()
    {
        var policy = new PolicyConfig { JobMemGb = 4 };
        Assert.Equal("ok", Cli.MemoryFitCheck(Big(maxJobs: 2, jobMem: null), Probe(16), policy)!.Level);
        Assert.Contains("cannot be checked on big-01 (no memory report)", Cli.MemoryFitCheck(Big(), Probe(null), new PolicyConfig())!.Detail, StringComparison.Ordinal);
        Assert.Null(Cli.MemoryFitCheck(Big(jobMem: null), Probe(32), new PolicyConfig()));
    }
}
