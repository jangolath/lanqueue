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
    public void TheWindowsWrapperEndsWithTheJobsExitMarker() => TheWrapperEndsWithTheJobsExitMarker("windows", [OperatingSystem.IsWindows() ? "powershell" : "pwsh", "-NoProfile", "-Command", "[Console]::Out.Write('partial'); exit 255"]);

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
        Assert.Contains("$jo=if($LqW){LqJob $pf $cp.Handle}", s, StringComparison.Ordinal); // the child and everything it starts are in one job object
        // The tree walk first, while the child is alive (it also reaches a grandchild started before the child was put in the job object),
        // then the job object for anything started during the walk.
        Assert.Contains("else{$code=124;LqKill $cp.Id;if($jo){[void]$LqW::TerminateJobObject($jo,124)} }", s, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-CimInstance", s, StringComparison.Ordinal); // denied to a non-admin account on some workers
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
        var m = Worker(MachineOs.Windows);
        InstallHelper(m); // as before every job (EnsureIncoming): the wrapper puts the job in a job object
        var (code, output) = Shells.Run("windows", RemoteScripts.Execute(m, "j1", ".", ["powershell", "-NoProfile", "-Command", detached], TimeSpan.FromSeconds(30)));
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

    /// <summary>Writes the job wrapper's helper into the worker's work_root/bin, as the runner's EnsureIncoming step does before every job.</summary>
    private static void InstallHelper(Machine m)
    {
        var (code, output) = Shells.Run("windows", RemoteScripts.EnsureIncoming(m));
        Assert.True(code == 0 && File.Exists(RemoteScripts.WindowsHelperPath(m)), "the helper was not written: " + output);
    }

    /// <summary>
    /// A job given &amp; or | whose program resolves (through path_prepend:, as the job's own lookup would) to a .cmd shim is refused by the
    /// wrapper before anything starts: the shim never runs, no pid file is written, and the output is the refusal marker naming the .cmd
    /// followed by the exit marker with 2, which lanq reports as Rejected. (pwsh elsewhere does not resolve a .cmd at all: refused too.)
    /// </summary>
    [PowerShellFact]
    public void AProgramResolvingToACmdShimIsRefusedBeforeItStarts()
    {
        var shims = Path.Combine(_root, "shims");
        Directory.CreateDirectory(shims);
        var ran = Path.Combine(_root, "ran.txt").Replace('/', (char)92);
        File.WriteAllText(Path.Combine(shims, "lanqtool.cmd"), "@echo ran> \"" + ran + "\"\r\n");
        var m = Worker(MachineOs.Windows);
        m.PathPrepend.Add(shims);

        foreach (var arg in new[] { "Category=Unit|Category=Arch", "a&b" })
        {
            var (code, output) = Shells.Run("windows", RemoteScripts.Execute(m, "j1", ".", ["lanqtool", "--filter", arg], TimeSpan.FromMinutes(5)));

            Assert.Equal(2, code);
            var lines = output.Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
            Assert.Equal(2, JobRunner.ReadExitMarker(lines[^1], "j1").Code);
            var resolved = JobRunner.ReadWindowsArgsRefusal(lines[^2], "j1");
            Assert.NotNull(resolved);
            if (OperatingSystem.IsWindows())
            {
                Assert.EndsWith("lanqtool.cmd", resolved, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(File.Exists(ran), "the .cmd shim ran: " + output);
            Assert.False(File.Exists(Path.Combine(_root, "locks", "job-j1.pid")));
        }
    }

    /// <summary>
    /// A native .exe receives &amp; and | verbatim, spaces included, through Windows PowerShell 5.1's native argument passing (git rev-parse
    /// --sq-quote prints its arguments back, quoted). dotnet test --filter "Category=Unit|Category=Arch" reaches dotnet.exe the same way.
    /// </summary>
    [WindowsFact]
    public void ANativeExeReceivesAmpersandAndBarVerbatim()
    {
        var (code, output) = Shells.Run(
            "windows",
            RemoteScripts.Execute(Worker(MachineOs.Windows), "j1", ".", ["git", "rev-parse", "--sq-quote", "A|B", "A B|C", "x&y", "Name~A B|Name~C", "it's"], TimeSpan.FromMinutes(5)));

        Assert.Equal(0, code);
        Assert.Equal("'A|B' 'A B|C' 'x&y' 'Name~A B|Name~C' 'it'\\''s'", T.JobOutput(output).Trim());
    }

    /// <summary>
    /// The wrapper dies (killed, or its session dropped) while the job runs on: the job process (the wrapper's grandchild) is stopped, by the
    /// job object ending with its wrapper or at the latest by cleanup, which names the child with its start time in the pid file although
    /// taskkill /T on the dead wrapper's pid would not reach it (and where WMI is denied, taskkill cannot stop anything at all).
    /// </summary>
    [WindowsFact]
    public void CleanupStopsTheJobChildEvenWhenItsWrapperIsGone()
    {
        var m = Worker(MachineOs.Windows);
        InstallHelper(m);
        var job = Path.Combine(_root, "job.pid").Replace('/', (char)92);
        var script = Path.Combine(Path.GetTempPath(), "lanq-orphan-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "﻿" + RemoteScripts.Execute(m, "j1", ".", ["powershell", "-NoProfile", "-Command", Recorded.PowerShellWrite(job) + "; Start-Sleep -Seconds 600"], TimeSpan.FromMinutes(30)));
        var pidFile = Path.Combine(_root, "locks", "job-j1.pid");
        using var wrapper = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }, UseShellExecute = false })!;
        try
        {
            // Event-based waits with generous bounds: three PowerShells start here, which takes seconds on a busy machine. The job would sleep
            // ten minutes, so it being gone within a minute of cleanup still proves it was stopped.
            Assert.True(SpinWait.SpinUntil(() => File.Exists(job) && new FileInfo(job).Length > 0, TimeSpan.FromMinutes(2)), "the job never started");
            Assert.Equal(4, File.ReadAllLines(pidFile).Count(l => l.Trim().Length > 0)); // the wrapper and its child, each with its start time
            wrapper.Kill(entireProcessTree: false);
            Assert.True(wrapper.WaitForExit(TimeSpan.FromSeconds(60)));

            var (code, output) = Shells.Run("windows", RemoteScripts.Cleanup(m, "j1", "1", null));
            Assert.True(code == 0, output);
            Assert.True(Recorded.Gone(job, TimeSpan.FromSeconds(60)), "the orphaned job is still running");
        }
        finally
        {
            Stop(wrapper, script);
        }
    }

    /// <summary>
    /// Without a job object (a tree lanq did not start in one, or a worker where assigning failed): cleanup stops the recorded process and
    /// every process below it, found with a process snapshot, so no WMI is needed. The grandchild proves the walk.
    /// </summary>
    [WindowsFact]
    public void CleanupWalksTheRecordedTreeWithoutAJobObject()
    {
        var m = Worker(MachineOs.Windows);
        InstallHelper(m);
        var grandchild = Path.Combine(_root, "grandchild.pid").Replace('/', (char)92);
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Recorded.PowerShellWrite(grandchild) + "; Start-Sleep -Seconds 600"));
        using var parent = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-Command", "Start-Process -NoNewWindow -FilePath powershell -ArgumentList '-NoProfile -EncodedCommand " + encoded + "'; Start-Sleep -Seconds 600" },
            UseShellExecute = false,
        })!;
        try
        {
            File.WriteAllText(Path.Combine(_root, "locks", "job-j1.pid"), StaleSlotTests.PidRecord(parent));
            Assert.True(SpinWait.SpinUntil(() => File.Exists(grandchild) && new FileInfo(grandchild).Length > 0, TimeSpan.FromMinutes(2)), "the grandchild never started");

            var (code, output) = Shells.Run("windows", RemoteScripts.Cleanup(m, "j1", "1", null));
            Assert.True(code == 0, output);
            Assert.True(parent.WaitForExit(TimeSpan.FromSeconds(60)), "the recorded process is still running");
            Assert.True(Recorded.Gone(grandchild, TimeSpan.FromSeconds(60)), "the recorded process's child is still running");
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// With the helper the job runs in a job object that ends with its wrapper (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE): killing the wrapper alone
    /// takes the job's whole tree with it, grandchildren included, without WMI and before any cleanup runs.
    /// </summary>
    [WindowsFact]
    public void KillingTheWrapperEndsTheWholeJobTree()
    {
        var m = Worker(MachineOs.Windows);
        InstallHelper(m);
        var grandchild = Path.Combine(_root, "grandchild.pid").Replace('/', (char)92);
        using var wrapper = StartWrapper(m, GrandchildJob(grandchild), out var script);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(grandchild) && new FileInfo(grandchild).Length > 0, TimeSpan.FromMinutes(2)), "the grandchild never started");
            Thread.Sleep(500);
            wrapper.Kill(entireProcessTree: false);
            Assert.True(Recorded.Gone(grandchild, TimeSpan.FromSeconds(60)), "the job's grandchild outlived its wrapper");
        }
        finally
        {
            Stop(wrapper, script);
        }
    }

    /// <summary>
    /// lanq's stop step (another ssh call, another PowerShell) opens the running job's job object by name and ends the whole tree, grandchild
    /// included, while the wrapper is still alive; then the wrapper itself is stopped and the pid file removed.
    /// </summary>
    [WindowsFact]
    public void TheStopScriptEndsTheRunningJobsWholeTree()
    {
        var m = Worker(MachineOs.Windows);
        InstallHelper(m);
        var grandchild = Path.Combine(_root, "grandchild.pid").Replace('/', (char)92);
        using var wrapper = StartWrapper(m, GrandchildJob(grandchild), out var script);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(grandchild) && new FileInfo(grandchild).Length > 0, TimeSpan.FromMinutes(2)), "the grandchild never started");
            var (code, output) = Shells.Run("windows", RemoteScripts.StopJob(m, "j1"));
            Assert.True(code == 0, output);
            Assert.True(Recorded.Gone(grandchild, TimeSpan.FromSeconds(60)), "the job's grandchild survived the stop step");
            Assert.True(wrapper.WaitForExit(TimeSpan.FromSeconds(60)), "the wrapper survived the stop step");
            Assert.False(File.Exists(Path.Combine(_root, "locks", "job-j1.pid")));
        }
        finally
        {
            Stop(wrapper, script);
        }
    }

    /// <summary>
    /// A job runs as the same account as lanq's scripts, so it can rewrite the helper file in work_root/bin. A file that is not byte for byte
    /// lanq's helper is never run: not by the job wrapper, not by the stop step, not by cleanup. "exit 0" would otherwise end cleanup before it
    /// removes the slot (a slot leaked for ever) and a write would run the job's code in other jobs' scripts. Cleanup still frees the slot.
    /// </summary>
    [WindowsFact]
    public void AChangedHelperIsNeverRunAndCleanupStillFreesTheSlot()
    {
        var m = Worker(MachineOs.Windows);
        var helper = RemoteScripts.WindowsHelperPath(m);
        var marker = Path.Combine(_root, "pwned.txt");
        var slot = Path.Combine(_root, "locks", "slot-1");
        foreach (var tampered in new[] { "exit 0", "function {{{ (( ) garbage", "Set-Content -LiteralPath '" + marker.Replace('/', (char)92) + "' -Value pwned" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "jobs", "j1"));
            InstallHelper(m);
            File.WriteAllText(helper, tampered);

            var (jobCode, jobOutput) = Shells.Run("windows", RemoteScripts.Execute(m, "j1", ".", ["powershell", "-NoProfile", "-Command", "exit 3"], TimeSpan.FromMinutes(5)));
            Assert.True(jobCode == 3, tampered + ": " + jobOutput);
            var (stopCode, stopOutput) = Shells.Run("windows", RemoteScripts.StopJob(m, "j1"));
            Assert.True(stopCode == 0, tampered + ": " + stopOutput);

            File.WriteAllText(slot, "j1\n1\n");
            File.WriteAllText(Path.Combine(_root, "locks", "job-j1.pid"), "1\n1\n"); // so cleanup goes through KillRecorded
            var (code, output) = Shells.Run("windows", RemoteScripts.Cleanup(m, "j1", "1", null));
            Assert.True(code == 0, tampered + ": " + output);
            Assert.False(File.Exists(slot), tampered + ": the slot was not removed: " + output);
            Assert.False(File.Exists(marker), tampered + ": the changed helper ran");
        }
    }

    /// <summary>The helper as lanq wrote it is loaded (LqName exists only in the real helper).</summary>
    [WindowsFact]
    public void TheVerifiedHelperIsLoaded()
    {
        var m = Worker(MachineOs.Windows);
        InstallHelper(m);
        var probe = RemoteScripts.WindowsHelperLoad(LanQueue.Policy.ShellQuote.PowerShell(RemoteScripts.WindowsHelperPath(m))) + "\nif (Get-Command LqName -EA 0) { 'loaded' } else { 'missing' }";
        Assert.Equal("loaded", Shells.Run("windows", probe).Output.Trim());
    }

    /// <summary>EnsureIncoming writes the helper's exact bytes, and writes them again over a file that is not exactly them.</summary>
    [PowerShellFact]
    public void EnsureIncomingRewritesAChangedHelper()
    {
        var m = Worker(MachineOs.Windows);
        var helper = RemoteScripts.WindowsHelperPath(m);
        var expected = System.Text.Encoding.UTF8.GetBytes(RemoteScripts.WindowsJobHelper);
        InstallHelper(m);
        Assert.Equal(expected, File.ReadAllBytes(helper));
        File.WriteAllText(helper, "exit 0");
        InstallHelper(m);
        Assert.Equal(expected, File.ReadAllBytes(helper));
        File.WriteAllBytes(helper, [.. expected, (byte)'\n']); // one byte more is a different file too
        InstallHelper(m);
        Assert.Equal(expected, File.ReadAllBytes(helper));
    }

    [Fact]
    public void EveryScriptChecksTheHelpersFullHashBeforeRunningIt()
    {
        var m = T.Windows();
        m.Capabilities = ["dotnet"];
        foreach (var script in new[] { RemoteScripts.Execute(m, "j1", ".", ["dotnet", "test"]), RemoteScripts.StopJob(m, "j1"), RemoteScripts.Cleanup(m, "j1", "1", T.RepoId), RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromHours(1)), RemoteScripts.Doctor(m), RemoteScripts.Unlock(m, all: false) })
        {
            Assert.Contains("-ceq'" + RemoteScripts.WindowsJobHelperBase64 + "'", script, StringComparison.Ordinal);
            Assert.Contains("[scriptblock]::Create(", script, StringComparison.Ordinal);
            Assert.DoesNotContain(". $LqH", script, StringComparison.Ordinal);
        }

        Assert.Equal(64, RemoteScripts.WindowsJobHelperHash.Length);
    }

    /// <summary>A job that starts a grandchild PowerShell (output detached to files) which records its pid and start time, then sleeps ten minutes.</summary>
    private string[] GrandchildJob(string grandchild)
    {
        var inner = Recorded.PowerShellWrite(grandchild) + "; Start-Sleep -Seconds 600";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(inner));
        return
        [
            "powershell", "-NoProfile", "-Command",
            "Start-Process -Wait -NoNewWindow -FilePath powershell -ArgumentList '-NoProfile -EncodedCommand " + encoded + "'"
                + " -RedirectStandardOutput '" + Path.Combine(_root, "gc.out").Replace('/', (char)92) + "'"
                + " -RedirectStandardError '" + Path.Combine(_root, "gc.err").Replace('/', (char)92) + "'",
        ];
    }

    private static Process StartWrapper(Machine m, string[] argv, out string script)
    {
        script = Path.Combine(Path.GetTempPath(), "lanq-wrapper-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "﻿" + RemoteScripts.Execute(m, "j1", ".", argv, TimeSpan.FromMinutes(30)));
        return Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }, UseShellExecute = false })!;
    }

    private static void Stop(Process wrapper, string script)
    {
        if (!wrapper.HasExited)
        {
            wrapper.Kill(entireProcessTree: true);
        }

        File.Delete(script);
    }
}
