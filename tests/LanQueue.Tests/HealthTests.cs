using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public class HealthTests
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Night = new(2026, 1, 5, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesProbeOutput()
    {
        var m = HealthProbe.Parse("load=0.250\r\ndisk_free_gb=120.5\nslots_used=1\n");
        Assert.Equal("0.250", m["load"]);
        Assert.Equal("1", m["slots_used"]);
    }

    private static async Task<MachineHealth> Probe(Machine m, string output, int exit, DateTimeOffset at)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(exit, output) };
        return await new HealthProbe(ssh, new FakeClock(at)).ProbeAsync(m, T.Config().Policy, CancellationToken.None);
    }

    [Fact]
    public async Task HealthyMachineHasNoHold()
    {
        var h = await Probe(T.Linux(), "load=0.20\ndisk_free_gb=50\nslots_used=0", 0, Noon);
        Assert.True(h.Reachable);
        Assert.Null(h.Hold);
        Assert.Equal(50, h.DiskFreeGb);
    }

    [Fact]
    public async Task LoadAboveThresholdIsHoldShort()
    {
        var h = await Probe(T.Linux(), "load=0.95\ndisk_free_gb=50\nslots_used=0", 0, Noon);
        Assert.Contains("above threshold", h.Hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideAllowedHoursIsHoldShort()
    {
        var h = await Probe(T.Windows(), "load=0.1\ndisk_free_gb=50\nslots_used=0", 0, Noon);
        Assert.Equal("outside allowed hours", h.Hold);
    }

    [Fact]
    public async Task InsideWrappedHoursIsAllowed()
    {
        var h = await Probe(T.Windows(), "load=0.1\ndisk_free_gb=50\nslots_used=0", 0, Night);
        Assert.Null(h.Hold);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("load=0.1\nslots_used=0")]
    [InlineData("disk_free_gb=50\nslots_used=0")]
    [InlineData("load=abc\ndisk_free_gb=50\nslots_used=0")]
    [InlineData("load=0.1\ndisk_free_gb=NaN\nslots_used=0")]
    [InlineData("load=0.1\ndisk_free_gb=50")]
    public async Task MissingOrGarbledHealthKeysHoldInsteadOfLookingIdle(string output)
    {
        var h = await Probe(T.Linux(), output, 0, Noon);
        Assert.NotNull(h.Hold);
        Assert.Contains("health", h.Hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LowFreeDiskIsHoldShort()
    {
        var h = await Probe(T.Linux(), "load=0.1\ndisk_free_gb=1.5\nslots_used=0", 0, Noon);
        Assert.Contains("disk", h.Hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinFreeDiskIsConfigurable()
    {
        var policy = T.Config().Policy;
        policy.MinFreeDiskGb = 1;
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, "load=0.1\ndisk_free_gb=1.5\nslots_used=0") };
        var h = await new HealthProbe(ssh, new FakeClock(Noon)).ProbeAsync(T.Linux(), policy, CancellationToken.None);
        Assert.Null(h.Hold);
    }

    [Fact]
    public async Task Ssh255MeansUnreachable()
    {
        var h = await Probe(T.Linux(), "Permission denied (publickey).", 255, Noon);
        Assert.False(h.Reachable);
        Assert.Equal("unreachable", h.Hold);
    }

    [Fact]
    public async Task MissingKeyEnvIsReportedAsUnreachableNotCrash()
    {
        var ssh = new FakeSsh { Handler = (_, _) => throw new InvalidOperationException("key missing") };
        var h = await new HealthProbe(ssh, new FakeClock(Noon)).ProbeAsync(T.Linux(), T.Config().Policy, CancellationToken.None);
        Assert.False(h.Reachable);
    }

    [Theory]
    [InlineData("22:00-07:00", 23, 0, true)]
    [InlineData("22:00-07:00", 3, 0, true)]
    [InlineData("22:00-07:00", 12, 0, false)]
    [InlineData("09:00-17:00", 12, 0, true)]
    [InlineData("09:00-17:00", 18, 0, false)]
    public void HoursWindowMath(string window, int h, int m, bool expected) =>
        Assert.Equal(expected, HoursWindow.IsAllowed(window, new TimeOnly(h, m)));
}
