using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Machine size (total memory, cores) in the probe, "suited for" in lanq health and health --json.</summary>
public sealed class MachineSizeTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-size-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public MachineSizeTests() => Directory.CreateDirectory(Path.Combine(_root, "locks"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<MachineHealth> Probe(string extra)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=1\nmem_free_gb=3\ntool=ok\n" + extra) };
        return await new HealthProbe(ssh, TimeProvider.System).ProbeOnceAsync(T.Linux(), T.Config().Policy, CancellationToken.None);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheProbeReadsTotalMemoryAndCores()
    {
        var h = await Probe("mem_total_gb=7.62\ncores=4");
        Assert.Equal(7.62, h.MemTotalGb);
        Assert.Equal(4, h.Cores);
        Assert.Null(h.Hold);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("mem_total_gb=abc\ncores=x")]
    [InlineData("mem_total_gb=-1\ncores=0")]
    [InlineData("mem_total_gb=0\ncores=2.5")]
    [InlineData("mem_total_gb=NaN\ncores=99999999")]
    [InlineData("")]
    public async Task GarbledOrMissingSizeIsUnknownAndNeverHolds(string extra)
    {
        var h = await Probe(extra);
        Assert.Null(h.MemTotalGb);
        Assert.Null(h.Cores);
        Assert.Null(h.Hold);
    }

    [Theory]
    [InlineData(3.7, 4, SizeClass.Light)]
    [InlineData(4.9, 8, SizeClass.Light)]
    [InlineData(5.0, 8, SizeClass.Light)]
    [InlineData(5.8, 4, SizeClass.Medium)] // visible memory under installed RAM: lanq-makeready rounds it up to 6 GB
    [InlineData(5.99, 8, SizeClass.Medium)]
    [InlineData(6.0, 2, SizeClass.Medium)]
    [InlineData(7.6, 4, SizeClass.Medium)]
    [InlineData(15.0, 8, SizeClass.Medium)]
    [InlineData(15.8, 8, SizeClass.Heavy)] // a 16 GB PC shows about 15.8 GB visible
    [InlineData(15.8, 3, SizeClass.Medium)]
    [InlineData(15.9, 8, SizeClass.Heavy)]
    [InlineData(16.0, 2, SizeClass.Medium)]
    [InlineData(16.0, 3, SizeClass.Medium)]
    [InlineData(16.0, 4, SizeClass.Heavy)]
    [InlineData(31.2, 16, SizeClass.Heavy)]
    [InlineData(31.2, null, SizeClass.Medium)]
    [InlineData(null, 8, SizeClass.Unknown)]
    public void SizeClassThresholds(double? ram, int? cores, SizeClass expected) => Assert.Equal(expected, MachineSize.Classify(ram, cores));

    [Fact]
    public void SuitedForNamesTheWorkAndDocker()
    {
        var m = T.Linux();
        Assert.Equal("light: lint and unit-test shards", MachineSize.SuitedFor(m, 3.7, 4));
        Assert.Equal("medium: test shards and builds", MachineSize.SuitedFor(m, 7.6, 4));
        m.Capabilities = [.. m.Capabilities, "Docker"];
        Assert.Equal("heavy: full suites and large builds + container builds", MachineSize.SuitedFor(m, 31, 8));
    }

    /// <summary>The same text, word for word, as lanq-makeready's "Suited for" line for the same tier.</summary>
    [Theory]
    [InlineData(3.7, 4, "light: lint and unit-test shards")]
    [InlineData(15.8, 8, "heavy: full suites and large builds")]
    [InlineData(7.6, 4, "medium: test shards and builds")]
    public void SuitedForMatchesTheScripts(double ram, int cores, string text)
    {
        Assert.Equal(text, MachineSize.SuitedFor(T.Linux(), ram, cores));
        var sh = System.Text.Encoding.UTF8.GetString(Cli.MakereadyScript(MachineOs.Linux));
        var ps1 = System.Text.Encoding.UTF8.GetString(Cli.MakereadyScript(MachineOs.Windows));
        Assert.Contains(text, sh, StringComparison.Ordinal);
        Assert.Contains(text, ps1, StringComparison.Ordinal);
        Assert.Contains("' + container builds'", ps1, StringComparison.Ordinal);
    }

    /// <summary>lanq health shows cores, total RAM, free memory, free/total slots and what each machine suits.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task HealthShowsSizeSlotsAndSuitedFor()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=1\nmem_free_gb=3.2\nmem_total_gb=7.6\ncores=4\ntool=ok") };
        var config = T.Config();
        config.Machines.RemoveAt(1);
        var stdout = new StringWriter();
        Assert.Equal(0, await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), stdout, new StringWriter(), CancellationToken.None));
        var line = stdout.ToString();
        Assert.Contains("cores 4  ram 7.6 GB", line, StringComparison.Ordinal);
        Assert.Contains("mem free 3.2 GB", line, StringComparison.Ordinal);
        Assert.Contains("slots free 1/2", line, StringComparison.Ordinal);
        Assert.Contains("suited: medium: test shards and builds", line, StringComparison.Ordinal);
    }

    /// <summary>health --json gains cores, memTotalGb, freeSlots, size and suitedFor; every earlier field is still there.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task HealthJsonAddsSizeFieldsOnly()
    {
        var ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name == "build-01"
                ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\nmem_free_gb=3.2\nmem_total_gb=3.8\ncores=2\ntool=ok")
                : new SshResult(255, "ssh: connect to host 10.20.30.40 port 22: Connection refused"),
        };
        var stdout = new StringWriter();
        await Cli.Health(T.Config(), new HealthProbe(ssh, TimeProvider.System), stdout, new StringWriter(), CancellationToken.None, json: true);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var up = doc.RootElement[0];
        foreach (var old in new[] { "name", "state", "reasons", "load", "diskFreeGb", "memFreeGb", "slotsUsed", "maxJobs", "localTime" })
        {
            Assert.True(up.TryGetProperty(old, out _), old);
        }

        Assert.Equal(2, up.GetProperty("cores").GetInt32());
        Assert.Equal(3.8, up.GetProperty("memTotalGb").GetDouble());
        Assert.Equal(2, up.GetProperty("freeSlots").GetInt32());
        Assert.Equal("light", up.GetProperty("size").GetString());
        Assert.Equal("light: lint and unit-test shards", up.GetProperty("suitedFor").GetString());
        var down = doc.RootElement[1];
        Assert.Equal(JsonValueKind.Null, down.GetProperty("cores").ValueKind);
        Assert.Equal(0, down.GetProperty("freeSlots").GetInt32());
        Assert.Equal("unknown", down.GetProperty("size").GetString());
    }

    /// <summary>The real Linux probe reports total memory (from /proc/meminfo) and cores.</summary>
    [LinuxFact]
    public void TheRealLinuxProbeReportsSize()
    {
        var m = T.Linux();
        m.WorkRoot = _root;
        var values = HealthProbe.Parse(WorkerHealthTests.Capture("sh", "-c", RemoteScripts.Probe(m)));
        Assert.True(double.Parse(values["mem_total_gb"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(int.Parse(values["cores"], System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Contains(values["mem_source"], new[] { "cim", "vb", "native" }); // where WMI is denied, the fallbacks still read memory
        Assert.Contains(values["load_source"], new[] { "cim", "native" });
    }

    /// <summary>The real Windows probe reports total memory (CIM) and cores.</summary>
    [WindowsFact]
    public void TheRealWindowsProbeReportsSize()
    {
        var m = T.Windows();
        m.WorkRoot = _root;
        var values = HealthProbe.Parse(Shells.Run("windows", RemoteScripts.Probe(m)).Output);
        Assert.True(double.Parse(values["mem_total_gb"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(int.Parse(values["cores"], System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Contains(values["mem_source"], new[] { "cim", "vb", "native" }); // where WMI is denied, the fallbacks still read memory
        Assert.Contains(values["load_source"], new[] { "cim", "native" });
    }
}
