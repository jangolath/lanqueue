using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Memory, memory pressure and the PAUSE file: what the probe reports and how it holds a machine.</summary>
public class WorkerHealthTests
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private const string Base = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\n";

    private static Task<MachineHealth> Probe(string output, Machine? machine = null, PolicyConfig? policy = null)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, output) };
        return new HealthProbe(ssh, new FakeClock(Noon)).ProbeAsync(machine ?? T.Linux(), policy ?? T.Config().Policy, CancellationToken.None);
    }

    [Fact]
    public async Task LowAvailableMemoryHoldsWithTheAmount()
    {
        var h = await Probe(Base + "mem_free_gb=0.62");
        Assert.Equal("low memory (0.6 GB free)", h.Hold);
        Assert.Equal(0.62, h.MemFreeGb);
    }

    [Fact]
    public async Task EnoughMemoryDoesNotHoldAndIsReported()
    {
        var h = await Probe(Base + "mem_free_gb=3.25\nswap_free_gb=1.5\nmem_pressure=0.00");
        Assert.Null(h.Hold);
        Assert.Equal(3.25, h.MemFreeGb);
        Assert.Equal(1.5, h.SwapFreeGb);
        Assert.Equal(0, h.MemPressure);
    }

    [Fact]
    public async Task MachineMinFreeMemOverridesThePolicy()
    {
        var m = T.Linux();
        m.MinFreeMemGb = 0.5;
        Assert.Null((await Probe(Base + "mem_free_gb=0.62", m)).Hold);
        m.MinFreeMemGb = 4;
        Assert.Equal("low memory (3.0 GB free)", (await Probe(Base + "mem_free_gb=3", m)).Hold);
    }

    [Fact]
    public async Task PolicyMinFreeMemIsConfigurable()
    {
        var policy = T.Config().Policy;
        policy.MinFreeMemGb = 0.25;
        Assert.Null((await Probe(Base + "mem_free_gb=0.5", policy: policy)).Hold);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mem_free_gb=")]
    [InlineData("mem_free_gb=abc")]
    [InlineData("mem_free_gb=-1")]
    [InlineData("mem_free_gb=NaN\nmem_pressure=Infinity")]
    [InlineData("mem_pressure=lots")]
    public async Task UnknownOrGarbledMemoryIsUnknownAndDoesNotHold(string extra)
    {
        var h = await Probe(Base + extra);
        Assert.Null(h.Hold);
        Assert.Null(h.MemFreeGb);
        Assert.Null(h.MemPressure);
    }

    [Fact]
    public async Task MemoryPressureAboveTheLimitHolds()
    {
        var h = await Probe(Base + "mem_free_gb=4\nmem_pressure=35.50");
        Assert.NotNull(h.Hold);
        Assert.StartsWith("memory pressure", h.Hold, StringComparison.Ordinal);
        Assert.Null((await Probe(Base + "mem_free_gb=4\nmem_pressure=19.99")).Hold);
    }

    [Fact]
    public async Task MaxMemPressureIsConfigurable()
    {
        var policy = T.Config().Policy;
        policy.MaxMemPressure = 50;
        Assert.Null((await Probe(Base + "mem_pressure=35.5", policy: policy)).Hold);
    }

    [Fact]
    public void LinuxProbeReadsMemAvailableSwapAndPressure()
    {
        var s = RemoteScripts.Probe(T.Linux());
        Assert.Contains("MemAvailable", s, StringComparison.Ordinal);
        Assert.Contains("SwapFree", s, StringComparison.Ordinal);
        Assert.Contains("/proc/pressure/memory", s, StringComparison.Ordinal);
        Assert.Contains("avg10", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsProbeReadsFreePhysicalMemory() =>
        Assert.Contains("FreePhysicalMemory", RemoteScripts.Probe(T.Windows()), StringComparison.Ordinal);

    [Fact]
    public async Task HealthPrintsFreeMemory()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Base + "mem_free_gb=3.21") };
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var o = new StringWriter();
        await Cli.Health(config, new HealthProbe(ssh, new FakeClock(Noon)), o, new StringWriter(), CancellationToken.None);
        Assert.Contains("mem free 3.2 GB", o.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The real probe on this host: Windows PowerShell on Windows, sh elsewhere. It must report memory the parser accepts.</summary>
    [Fact]
    public void RealProbeOnThisHostReportsMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanq-probe-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(root);
        try
        {
            var output = RunProbe(root);

            var v = HealthProbe.Parse(output);
            Assert.True(
                double.TryParse(v.GetValueOrDefault("mem_free_gb"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mem) && mem > 0,
                "probe output: " + output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PauseFileHoldsTheMachine()
    {
        var h = await Probe("load=0.95\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=1");
        Assert.True(h.Paused);
        Assert.Equal("paused on the machine", h.Hold); // reported ahead of the high load
    }

    [Theory]
    [InlineData("paused=0")]
    [InlineData("")]
    [InlineData("paused=yes")]
    public async Task NoPauseFileOrOlderProbeOutputIsNotPaused(string extra)
    {
        var h = await Probe(Base + extra);
        Assert.False(h.Paused);
        Assert.Null(h.Hold);
    }

    [Fact]
    public void ProbesLookForThePauseFileInTheWorkRoot()
    {
        Assert.Contains("\"$R/PAUSE\"", RemoteScripts.Probe(T.Linux()), StringComparison.Ordinal);
        Assert.Contains("\"$root/PAUSE\"", RemoteScripts.Probe(T.Windows()), StringComparison.Ordinal);
    }

    [Fact]
    public void RealProbeOnThisHostSeesThePauseFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanq-pause-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(root);
        try
        {
            var before = RunProbe(root);

            Assert.Equal("0", HealthProbe.Parse(before).GetValueOrDefault("paused"));
            File.WriteAllText(Path.Combine(root, "PAUSE"), "");
            Assert.Equal("1", HealthProbe.Parse(RunProbe(root)!).GetValueOrDefault("paused"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Runs the generated probe for a machine whose work_root is <paramref name="root"/>: PowerShell on Windows, sh elsewhere.</summary>
    internal static string RunProbe(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var m = T.Windows();
            m.WorkRoot = root;
            var file = Path.Combine(Path.GetTempPath(), "lanq-probe-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(file, "﻿" + RemoteScripts.Probe(m));
            try
            {
                return Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file);
            }
            finally
            {
                File.Delete(file);
            }
        }

        var linux = T.Linux();
        linux.WorkRoot = root;
        return Capture("sh", "-c", RemoteScripts.Probe(linux));
    }

    internal static string Capture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout + stderr.GetAwaiter().GetResult();
    }
}
