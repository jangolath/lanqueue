using System.Diagnostics;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>The job wrapper on the worker: /bin/sh, the exit code, the worker-side deadline, and the PAUSE file name.</summary>
public sealed class WorkerScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-wscript-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public WorkerScriptTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
    }

    public void Dispose()
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (i < 19)
            {
                Thread.Sleep(250); // a killed Windows process can hold its folder for a moment
            }
        }
    }

    private Machine Worker(MachineOs os, JobPriority priority = JobPriority.Low)
    {
        var m = os == MachineOs.Windows ? T.Windows() : T.Linux();
        m.WorkRoot = _root;
        m.Priority = priority;
        return m;
    }

    /// <summary>env resolves its program through the PATH it has just set, so the job wrapper names /bin/sh itself.</summary>
    [Fact]
    public void TheLinuxJobWrapperRunsBinSh()
    {
        var m = T.Linux();
        m.Env["FOO"] = "1";
        m.PathPrepend.Add("/opt/evil/bin");
        var s = RemoteScripts.Execute(m, "j1", ".", ["npm", "test"]);
        Assert.Contains("/opt/evil/bin'\"${PATH:+:$PATH}\" /bin/sh -c", s, StringComparison.Ordinal);
        Assert.DoesNotContain(" sh -c", s.Replace("/bin/sh -c", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("/bin/sh -c", RemoteScripts.Doctor(m), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinuxProbeAcceptsPauseInUpperOrLowerCase()
    {
        var probe = RemoteScripts.Probe(T.Linux());
        Assert.Contains("\"$R/PAUSE\"", probe, StringComparison.Ordinal);
        Assert.Contains("\"$R/pause\"", probe, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void RealLinuxProbeSeesALowerCasePauseFile()
    {
        File.WriteAllText(Path.Combine(_root, "pause"), "");
        var (_, output) = Shells.Run("linux", RemoteScripts.Probe(Worker(MachineOs.Linux)));
        Assert.Equal("1", HealthProbe.Parse(output).GetValueOrDefault("paused"));
        File.Delete(Path.Combine(_root, "pause"));
        File.WriteAllText(Path.Combine(_root, "Pause"), ""); // only PAUSE or pause on a case-sensitive file system
        Assert.Equal("0", HealthProbe.Parse(Shells.Run("linux", RemoteScripts.Probe(Worker(MachineOs.Linux))).Output).GetValueOrDefault("paused"));
    }

    /// <summary>The job's own exit code survives the nice / ionice / env / setsid / timeout chain.</summary>
    [ShFact]
    public void ANonZeroExitPassesThroughTheLowPriorityChain()
    {
        var m = Worker(MachineOs.Linux);
        m.Env["FOO"] = "bar";
        var (code, _) = Shells.Run("linux", RemoteScripts.Execute(m, "j1", ".", ["sh", "-c", "exit 7"], TimeSpan.FromMinutes(5)));
        Assert.Equal(7, code);
        Assert.False(File.Exists(Path.Combine(_root, "locks", "job-j1.pid")));
    }

    [Fact]
    public void TheLinuxJobRunsUnderTimeoutWhenALimitIsGiven()
    {
        var s = RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], TimeSpan.FromSeconds(90.5));
        Assert.Contains("if timeout -k 1 5 true >/dev/null 2>&1; then T=\"timeout -k 30 91\"; fi", s, StringComparison.Ordinal);
        Assert.Contains("$S $T $N $I /bin/sh -c", s, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunnerGivesTheWorkerTheJobLimitPlusAMargin() =>
        Assert.Equal(TimeSpan.FromMinutes(1), JobRunner.WorkerDeadlineMargin);

    /// <summary>A job past its worker-side deadline is stopped on the worker itself, with its children, even if lanq is gone.</summary>
    [LinuxFact]
    public void RealShJobPastItsDeadlineIsStoppedOnTheWorker()
    {
        var marker = Path.Combine(_root, "survivor");
        var watch = Stopwatch.StartNew();
        var (code, _) = Shells.Run("linux", RemoteScripts.Execute(Worker(MachineOs.Linux), "j1", ".", ["sh", "-c", "sleep 20 & wait; touch " + marker], TimeSpan.FromSeconds(2)));
        Assert.Equal(124, code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "took " + watch.Elapsed);
        Thread.Sleep(500);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(_root, "locks", "job-j1.pid")));
    }

    [Fact]
    public void TheWindowsJobWaitsForItsDeadlineAndKillsTheTree()
    {
        var s = RemoteScripts.Execute(T.Windows(), "j1", ".", ["dotnet", "test"], TimeSpan.FromSeconds(90.5));
        Assert.Contains("WaitForExit(91000)", s, StringComparison.Ordinal);
        Assert.Contains("taskkill /PID $cp.Id /T /F", s, StringComparison.Ordinal);
        Assert.Contains("& 'dotnet' 'test'", s, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void RealPowerShellJobPastItsDeadlineIsStoppedWithItsTree()
    {
        var marker = Path.Combine(_root, "survivor").Replace('/', (char)92);
        var watch = Stopwatch.StartNew();
        var (code, _) = Shells.Run(
            "windows",
            RemoteScripts.Execute(Worker(MachineOs.Windows), "j1", ".", ["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 20; New-Item -ItemType File " + marker], TimeSpan.FromSeconds(3)));
        Assert.Equal(124, code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "took " + watch.Elapsed);
        Thread.Sleep(1000);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(_root, "locks", "job-j1.pid")));
    }

    [PowerShellFact]
    public void RealPowerShellJobExitCodePassesThrough()
    {
        var (code, output) = Shells.Run("windows", RemoteScripts.Execute(Worker(MachineOs.Windows), "j1", ".", [OperatingSystem.IsWindows() ? "powershell" : "pwsh", "-NoProfile", "-Command", "Write-Output hello; exit 7"], TimeSpan.FromMinutes(5)));
        Assert.Equal(7, code);
        Assert.Contains("hello", output, StringComparison.Ordinal);
    }

    [PowerShellFact]
    public void RealPowerShellJobThatCannotStartFailsWithOne()
    {
        var (code, _) = Shells.Run("windows", RemoteScripts.Execute(Worker(MachineOs.Windows), "j1", ".", ["lanq-no-such-program-xyz"], TimeSpan.FromMinutes(5)));
        Assert.Equal(1, code);
    }
}
