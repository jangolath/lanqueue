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

    /// <summary>The wrapper's last stdout line is its exit marker with the job's real code, also after output without a final newline.</summary>
    [ShFact]
    public void TheLinuxWrapperEndsWithTheJobsExitMarker() => TheWrapperEndsWithTheJobsExitMarker("linux", ["sh", "-c", "printf partial; exit 255"]);

    [PowerShellFact]
    public void TheWindowsWrapperEndsWithTheJobsExitMarker() => TheWrapperEndsWithTheJobsExitMarker("windows", ["powershell", "-NoProfile", "-Command", "[Console]::Out.Write('partial'); exit 255"]);

    private void TheWrapperEndsWithTheJobsExitMarker(string shell, string[] argv)
    {
        var (code, output) = Shells.Run(shell, RemoteScripts.Execute(Worker(shell == "linux" ? MachineOs.Linux : MachineOs.Windows), "j1", ".", argv, TimeSpan.FromMinutes(5)));
        Assert.Equal(255, code);
        var last = output.Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n')[^1];
        Assert.EndsWith(RemoteScripts.ExitMarker("j1") + "255", last, StringComparison.Ordinal);
        Assert.Equal(255, JobRunner.ReadExitMarker(last, "j1").Code);
    }

    [Fact]
    public void TheLinuxJobRunsUnderTimeoutWhenALimitIsGiven()
    {
        var s = RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], TimeSpan.FromSeconds(90.5));
        Assert.Contains("if timeout -k 1 30 true >/dev/null 2>&1; then T=\"timeout -k 30 91\"; fi", s, StringComparison.Ordinal);
        Assert.Contains("$S $T $N $I env 'LANQ_JOB=j1' /bin/sh -c", s, StringComparison.Ordinal);
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
        // The job starts a grandchild PowerShell with its stdout and stderr detached to files (so nothing depends on pipe inheritance); the
        // grandchild records its pid and start time, then would sleep five minutes. The worker's deadline must stop the
        // whole tree. The deadline (30 s) leaves room for both PowerShells to start on a busy machine, so the grandchild always exists before
        // it fires; the proof is that the recorded process is gone, not how long anything took.
        var grandchild = Path.Combine(_root, "grandchild.pid").Replace('/', (char)92);
        var inner = Recorded.PowerShellWrite(grandchild) + "; Start-Sleep -Seconds 300";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(inner));
        var detached = "Start-Process -Wait -NoNewWindow -FilePath powershell -ArgumentList '-NoProfile -EncodedCommand " + encoded + "'"
            + " -RedirectStandardOutput '" + Path.Combine(_root, "gc.out").Replace('/', (char)92) + "'"
            + " -RedirectStandardError '" + Path.Combine(_root, "gc.err").Replace('/', (char)92) + "'";
        var (code, output) = Shells.Run(
            "windows",
            RemoteScripts.Execute(Worker(MachineOs.Windows), "j1", ".", ["powershell", "-NoProfile", "-Command", detached], TimeSpan.FromSeconds(30)));
        Assert.Equal(124, code);
        Assert.True(File.Exists(grandchild), "the grandchild never started: " + output);
        Assert.True(Recorded.Gone(grandchild, TimeSpan.FromSeconds(60)), "the job's grandchild survived the deadline");
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

    /// <summary>
    /// The wrapper dies (killed, or its session dropped) while the job child runs on: the pid file also names the child with its start time,
    /// so cleanup still stops it, although taskkill /T on the dead wrapper's pid would not reach it.
    /// </summary>
    [WindowsFact]
    public void CleanupStopsTheJobChildEvenWhenItsWrapperIsGone()
    {
        var m = Worker(MachineOs.Windows);
        var script = Path.Combine(Path.GetTempPath(), "lanq-orphan-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "﻿" + RemoteScripts.Execute(m, "j1", ".", ["powershell", "-NoProfile", "-Command", "Start-Sleep -Seconds 600"], TimeSpan.FromMinutes(30)));
        var pidFile = Path.Combine(_root, "locks", "job-j1.pid");
        using var wrapper = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }, UseShellExecute = false })!;
        try
        {
            string[] lines = [];
            // Event-based waits with generous bounds: two PowerShells start here, which takes seconds on a busy machine. The child would sleep
            // ten minutes, so it being gone within a minute of cleanup still proves cleanup stopped it.
            var started = Stopwatch.StartNew();
            while (lines.Length < 4 && started.Elapsed < TimeSpan.FromMinutes(2))
            {
                Thread.Sleep(100);
                try
                {
                    lines = File.ReadAllLines(pidFile).Where(l => l.Trim().Length > 0).ToArray();
                }
                catch (IOException)
                {
                }
            }

            Assert.True(lines.Length >= 4, "the wrapper did not record its child: " + string.Join("|", lines));
            using var child = Process.GetProcessById(int.Parse(lines[2], System.Globalization.CultureInfo.InvariantCulture));
            wrapper.Kill(entireProcessTree: false);
            Assert.True(wrapper.WaitForExit(TimeSpan.FromSeconds(60)));
            Assert.False(child.HasExited);

            var (code, output) = Shells.Run("windows", RemoteScripts.Cleanup(m, "j1", "1", null));
            Assert.True(code == 0, output);
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(60)), "the orphaned job child is still running");
        }
        finally
        {
            if (!wrapper.HasExited)
            {
                wrapper.Kill(entireProcessTree: true);
            }

            File.Delete(script);
        }
    }
}
