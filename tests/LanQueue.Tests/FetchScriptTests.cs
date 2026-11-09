using System.Text;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The worker side of --fetch, run by the real shells (sh, and Windows PowerShell 5.1) against a job folder in the temp folder, with the
/// output read by the real line reader and receiver. The hostile cases (links, loops, odd names, devices) need a real Linux file system.
/// </summary>
public sealed class FetchScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-fetchsh-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public FetchScriptTests() => Directory.CreateDirectory(Job);

    private string Job => _root + "/jobs/j1";

    private string Out => _root + "/out/j1";

    /// <summary>Never enumerate this tree recursively: the symlink loops some tests plant would make that walk effectively endless.</summary>
    public void Dispose() => TestTree.Delete(_root);

    private Machine Worker(MachineOs os)
    {
        var m = os == MachineOs.Windows ? T.Windows() : T.Linux();
        m.WorkRoot = _root;
        return m;
    }

    private void Write(string rel, byte[] data)
    {
        var p = Path.Combine(Job, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    private static byte[] AllBytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 % 256)).ToArray();

    private async Task<FetchReport> Fetch(MachineOs os, string[] patterns, FetchLimits? limits = null, string dir = ".")
    {
        var m = Worker(os);
        var script = FetchScripts.Fetch(m, "j1", dir, patterns, limits ?? FetchLimits.Default);
        var (_, output) = Shells.Run(os == MachineOs.Windows ? "windows" : "linux", script);
        using var rx = new FetchReceiver(Out, patterns, limits ?? FetchLimits.Default, ignoreCase: os == MachineOs.Windows);
        await LineReader.PumpAsync(new StringReader(output), rx.OnLine);
        return rx.Finish();
    }

    private void AssertRoundTrip(FetchReport r, params string[] rels)
    {
        Assert.Null(r.Error);
        Assert.Equal(rels, r.Fetched.Select(f => f.Path));
        foreach (var rel in rels)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(Job, rel)), File.ReadAllBytes(Path.Combine(Out, rel)));
        }
    }

    // ---- sh (Linux workers; on Windows the Git for Windows sh) ----

    [ShFact]
    public async Task ShFetchesMatchingFilesByteForByteInSortedOrder()
    {
        Write("TestResults/b/run.trx", AllBytes(5000));
        Write("TestResults/a.trx", AllBytes(1));
        Write("TestResults/empty.trx", []);
        Write("TestResults/notes.txt", AllBytes(3));
        Write("src/app.cs", AllBytes(3));

        var r = await Fetch(MachineOs.Linux, ["TestResults/**/*.trx"]);

        AssertRoundTrip(r, "TestResults/a.trx", "TestResults/b/run.trx", "TestResults/empty.trx");
        Assert.Empty(r.Skipped);
    }

    [ShFact]
    public async Task ShStarStaysInOneFolderAndPatternsAreRelativeToTheWorkingFolder()
    {
        Write("src/app/out/x.log", AllBytes(3));
        Write("src/app/out/deep/y.log", AllBytes(3));
        Write("src/z.log", AllBytes(3));

        var r = await Fetch(MachineOs.Linux, ["out/*.log"], dir: "src/app");

        Assert.Null(r.Error);
        Assert.Equal(["out/x.log"], r.Fetched.Select(f => f.Path));
        Assert.Equal(AllBytes(3), File.ReadAllBytes(Path.Combine(Out, "out", "x.log")));
    }

    [ShFact]
    public async Task ShCapsFetchWhatFitsInSortedOrderAndReportTheRest()
    {
        Write("o/a.bin", AllBytes(40));
        Write("o/b.bin", AllBytes(40));
        Write("o/c.bin", AllBytes(10));
        Write("o/d.bin", AllBytes(10));
        Write("o/e.bin", AllBytes(10));
        Write("o/huge.bin", AllBytes(200));

        var r = await Fetch(MachineOs.Linux, ["o/*"], new FetchLimits(3, 100, 100));

        Assert.Equal(["o/a.bin", "o/b.bin", "o/c.bin"], r.Fetched.Select(f => f.Path));
        Assert.Equal(["o/d.bin", "o/e.bin", "o/huge.bin"], r.Skipped.Select(s => s.Path));
        Assert.Equal("over the file count cap", r.Skipped[0].Reason);
    }

    [ShFact]
    public async Task ShReportsAMissingWorkingFolder()
    {
        var r = await Fetch(MachineOs.Linux, ["*.trx"], dir: "nope");
        Assert.Empty(r.Fetched);
        Assert.Contains("could not list", r.Error, StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task ShSkipsSymlinksToFilesOutsideAndNeverFollowsThem()
    {
        var outside = _root + "/outside.txt";
        File.WriteAllText(outside, "worker secret");
        Write("TestResults/real.trx", AllBytes(10));
        File.CreateSymbolicLink(Job + "/TestResults/passwd.trx", "/etc/passwd");
        File.CreateSymbolicLink(Job + "/TestResults/outside.trx", outside);
        File.CreateSymbolicLink(Job + "/TestResults/inside.trx", Job + "/TestResults/real.trx");

        var r = await Fetch(MachineOs.Linux, ["TestResults/*.trx"]);

        AssertRoundTrip(r, "TestResults/real.trx");
        Assert.Equal(["TestResults/inside.trx", "TestResults/outside.trx", "TestResults/passwd.trx"], r.Skipped.Select(s => s.Path));
        Assert.All(r.Skipped, s => Assert.Contains("symbolic link", s.Reason, StringComparison.Ordinal));
        Assert.False(File.Exists(Out + "/TestResults/passwd.trx"));
    }

    [LinuxFact]
    public async Task ShDoesNotFollowSymlinkedFoldersOrLoops()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Write("out/a.trx", AllBytes(5));
        Directory.CreateSymbolicLink(Job + "/out/loop", Job + "/out");
        Directory.CreateSymbolicLink(Job + "/out/up", "..");
        Directory.CreateSymbolicLink(Job + "/out/elsewhere", _root + "/elsewhere");
        Directory.CreateSymbolicLink(Job + "/linked", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["**/*.trx", "linked/*.trx"]);

        AssertRoundTrip(r, "out/a.trx");
        Assert.DoesNotContain(r.Skipped, s => s.Path.Contains("leak", StringComparison.Ordinal));
        Assert.False(File.Exists(Out + "/out/elsewhere/leak.trx"));
    }

    [LinuxFact]
    public async Task ShRefusesAWalkRootThatIsASymlink()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Directory.CreateSymbolicLink(Job + "/TestResults", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["TestResults/*.trx"]);

        Assert.Empty(r.Fetched);
        Assert.Equal("TestResults", Assert.Single(r.Skipped).Path);
    }

    [LinuxFact]
    public async Task ShSkipsNamesWithControlCharactersHardLinksAndFifos()
    {
        Write("o/ok.bin", AllBytes(5));
        Write("o/evil\nname.bin", AllBytes(5));
        Write("o/source.bin", AllBytes(5));
        Directory.CreateDirectory(Job + "/o/dir\nx");
        File.WriteAllText(Job + "/o/dir\nx/inner.bin", "x");
        var (code, output) = Shells.Exec("sh", ["-c", "cd \"$1\" && ln o/source.bin o/hard.bin && mkfifo o/pipe.bin", "sh", Job]);
        Assert.True(code == 0, output);

        var r = await Fetch(MachineOs.Linux, ["o/**"]);

        Assert.Equal(["o/ok.bin"], r.Fetched.Select(f => f.Path));
        Assert.Contains(r.Skipped, s => s.Path == "o/evil?name.bin" && s.Reason.Contains("control", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/dir?x" && s.Reason.Contains("control", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/hard.bin" && s.Reason.Contains("hard link", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/source.bin" && s.Reason.Contains("hard link", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/pipe.bin" && s.Reason.Contains("not a regular file", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Skipped, s => s.Path.Contains("inner", StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task ShRefusesAWorkingFolderThatResolvesOutsideTheJob()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Directory.CreateSymbolicLink(Job + "/src", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["*.trx"], dir: "src");

        Assert.Empty(r.Fetched);
        Assert.Contains("outside", r.Error, StringComparison.Ordinal);
    }

    // ---- Windows PowerShell 5.1 ----

    [WindowsFact]
    public async Task PowerShellFetchesMatchingFilesByteForByteInSortedOrder()
    {
        Write("TestResults/b/run.trx", AllBytes(10000)); // more than one 3072-byte chunk
        Write("TestResults/a.trx", AllBytes(1));
        Write("TestResults/empty.trx", []);
        Write("TestResults/c.trx", AllBytes(3072));
        Write("TestResults/notes.txt", AllBytes(3));

        var r = await Fetch(MachineOs.Windows, ["TestResults/**/*.trx"]);

        AssertRoundTrip(r, "TestResults/a.trx", "TestResults/b/run.trx", "TestResults/c.trx", "TestResults/empty.trx");
        Assert.Empty(r.Skipped);
    }

    [WindowsFact]
    public async Task PowerShellMatchesIgnoringCaseAndStarStaysInOneFolder()
    {
        Write("src/Out/X.LOG", AllBytes(3));
        Write("src/Out/deep/y.log", AllBytes(3));

        var r = await Fetch(MachineOs.Windows, ["out/*.log"], dir: "src");

        Assert.Null(r.Error);
        Assert.Single(r.Fetched);
        Assert.Equal(AllBytes(3), File.ReadAllBytes(Path.Combine(Out, r.Fetched[0].Path)));
    }

    [WindowsFact]
    public async Task PowerShellCapsFetchWhatFitsInSortedOrder()
    {
        Write("o/a.bin", AllBytes(40));
        Write("o/b.bin", AllBytes(40));
        Write("o/c.bin", AllBytes(10));
        Write("o/d.bin", AllBytes(10));
        Write("o/huge.bin", AllBytes(200));

        var r = await Fetch(MachineOs.Windows, ["o/*"], new FetchLimits(3, 100, 100));

        Assert.Equal(["o/a.bin", "o/b.bin", "o/c.bin"], r.Fetched.Select(f => f.Path));
        Assert.Equal(["o/d.bin", "o/huge.bin"], r.Skipped.Select(s => s.Path));
    }

    [WindowsFact]
    public async Task PowerShellSkipsJunctionsAndNeverFollowsThem()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Write("TestResults/real.trx", AllBytes(5));
        FetchReceiverTests.MakeDirLink(Job + "/TestResults/linked", _root + "/elsewhere");
        FetchReceiverTests.MakeDirLink(Job + "/Other", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Windows, ["TestResults/**", "Other/*.trx"]);

        AssertRoundTrip(r, "TestResults/real.trx");
        Assert.Equal(["Other", "TestResults/linked"], r.Skipped.Select(s => s.Path).Order(StringComparer.Ordinal));
        Assert.All(r.Skipped, s => Assert.Contains("junction", s.Reason, StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task PowerShellRefusesAWorkingFolderThatIsAJunction()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        FetchReceiverTests.MakeDirLink(Job + "/src", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Windows, ["*.trx"], dir: "src");

        Assert.Empty(r.Fetched);
        Assert.Contains("outside", r.Error, StringComparison.Ordinal);
    }

    /// <summary>The real transport for a Windows worker: the script goes in on stdin and the frames stream back line by line.</summary>
    [WindowsFact]
    public async Task TheWindowsScriptRunsFromStdinAndStreamsBack()
    {
        Write("TestResults/run.trx", AllBytes(20000));
        var m = Worker(MachineOs.Windows);
        string[] patterns = ["TestResults/*.trx"];
        var script = FetchScripts.Fetch(m, "j1", ".", patterns, FetchLimits.Default);
        var command = FetchScripts.WindowsStdinCommand;
        var inner = command[(command.IndexOf('"', StringComparison.Ordinal) + 1)..command.LastIndexOf('"')];
        using var rx = new FetchReceiver(Out, patterns, FetchLimits.Default, ignoreCase: true);

        var result = await ProcessSshClient.Execute(
            "powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", inner], rx.OnLine, TimeSpan.FromMinutes(2), CancellationToken.None,
            FetchScripts.WindowsStdin(script));

        Assert.True(result.Ok, result.Output);
        AssertRoundTrip(rx.Finish(), "TestResults/run.trx");
    }

    /// <summary>A timed-out job may still be running when lanq fetches: the stop script kills its recorded process group first.</summary>
    [LinuxFact]
    public void TheLinuxStopScriptKillsTheRecordedProcessGroup()
    {
        Directory.CreateDirectory(_root + "/locks");
        var pf = _root + "/locks/job-j1.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = Job }; // a job runs in its folder
        foreach (var a in new[] { "sh", "-c", "echo $$ > \"$1\"; sleep 60 & wait", "sh", pf })
        {
            psi.ArgumentList.Add(a);
        }

        using var job = System.Diagnostics.Process.Start(psi)!;
        Assert.True(SpinWait.SpinUntil(() => File.Exists(pf) && File.ReadAllText(pf).Trim().Length > 0, TimeSpan.FromSeconds(10)));

        try
        {
            var (code, output) = Shells.Run("linux", RemoteScripts.StopJob(Worker(MachineOs.Linux), "j1"));

            Assert.True(code == 0, output);
            Assert.True(job.WaitForExit(TimeSpan.FromSeconds(30)), "the job's process group is still running");
            Assert.False(File.Exists(pf));
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL -- -" + job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>
    /// The recorded pid may have been reused by an unrelated process group (the job ended and the worker has run for days). Neither stop nor
    /// cleanup may signal it: its cwd is not inside the job's folder.
    /// </summary>
    [LinuxFact]
    public void TheLinuxKillLeavesAReusedPidWhoseCwdIsElsewhereAlone()
    {
        Directory.CreateDirectory(_root + "/locks");
        Directory.CreateDirectory(_root + "/elsewhere");
        var pf = _root + "/locks/job-j1.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = _root + "/elsewhere" };
        foreach (var a in new[] { "sh", "-c", "sleep 60 & wait" })
        {
            psi.ArgumentList.Add(a);
        }

        using var other = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var id = other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var script in new[] { RemoteScripts.StopJob(Worker(MachineOs.Linux), "j1"), RemoteScripts.Cleanup(Worker(MachineOs.Linux), "j1", "1", T.RepoId) })
            {
                foreach (var record in new[] { id + "\n", id + "\n1\n" }) // an old pid file (no start time), and one whose start time is not this process's
                {
                    Directory.CreateDirectory(Job);
                    File.WriteAllText(pf, record);
                    var (code, output) = Shells.Run("linux", script);
                    Assert.True(code == 0, output);
                    Assert.False(other.WaitForExit(TimeSpan.FromSeconds(3)), "an unrelated process group was killed");
                    Assert.False(File.Exists(pf));
                }
            }
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL -- -" + other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>
    /// Records "pid", "start time" (field 22 of /proc/PID/stat) and the boot id of the shell itself into $1, as the job wrapper does, and marks
    /// the shell's later children as job j1 (LANQ_JOB, as the wrapper's env does).
    /// </summary>
    private const string RecordSelf =
        "printf '%s\\n%s\\n%s\\n' $$ \"$(sed -n 's/.*) //p' /proc/$$/stat | cut -d' ' -f20)\" \"$(cat /proc/sys/kernel/random/boot_id)\" > \"$1\"; export LANQ_JOB=j1";

    private static bool Alive(int pid) => Directory.Exists("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
        && !File.ReadAllText("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/stat").Contains(") Z", StringComparison.Ordinal);

    /// <summary>
    /// The wrapper died before removing its pid file and the group leader has exited, but a process of the job's group lives on. Cleanup must
    /// still kill it: Linux never reuses a pid number while a process group of that number exists, so the live group is the job's.
    /// </summary>
    [LinuxFact]
    public void CleanupKillsWhatIsLeftOfTheGroupAfterItsLeaderExited()
    {
        Directory.CreateDirectory(_root + "/locks");
        var pf = _root + "/locks/job-j1.pid";
        var child = _root + "/child.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = Job };
        foreach (var a in new[] { "sh", "-c", RecordSelf + "; sleep 300 </dev/null >/dev/null 2>&1 & echo $! > \"$2\"; exit 0", "sh", pf, child })
        {
            psi.ArgumentList.Add(a);
        }

        using (var leader = System.Diagnostics.Process.Start(psi)!)
        {
            Assert.True(leader.WaitForExit(TimeSpan.FromSeconds(30)));
        }

        var sleeper = int.Parse(File.ReadAllText(child).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Assert.True(Alive(sleeper));
            var (code, output) = Shells.Run("linux", RemoteScripts.Cleanup(Worker(MachineOs.Linux), "j1", "1", T.RepoId));
            Assert.True(code == 0, output);
            Assert.True(SpinWait.SpinUntil(() => !Alive(sleeper), TimeSpan.FromSeconds(30)), "the job's orphaned group member survived cleanup");
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL " + sleeper.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>
    /// The job's group is long dead and its number now belongs to an unrelated group (its leader gone, a member alive, no LANQ_JOB marker): the
    /// stale pid file must not make cleanup or stale-slot reclaim kill it.
    /// </summary>
    [LinuxFact]
    public void AReusedGroupNumberWithoutTheJobMarkerIsNotKilled()
    {
        Directory.CreateDirectory(_root + "/locks");
        var pf = _root + "/locks/job-j1.pid";
        var child = _root + "/child.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = _root };
        foreach (var a in new[] { "sh", "-c", "echo $$ > \"$1\"; sleep 300 </dev/null >/dev/null 2>&1 & echo $! > \"$2\"; exit 0", "sh", _root + "/other.pid", child })
        {
            psi.ArgumentList.Add(a);
        }

        using (var leader = System.Diagnostics.Process.Start(psi)!)
        {
            Assert.True(leader.WaitForExit(TimeSpan.FromSeconds(30)));
        }

        var group = File.ReadAllText(_root + "/other.pid").Trim();
        var sleeper = int.Parse(File.ReadAllText(child).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        try
        {
            foreach (var script in new[] { RemoteScripts.Cleanup(Worker(MachineOs.Linux), "j1", "1", T.RepoId), RemoteScripts.StopJob(Worker(MachineOs.Linux), "j1") })
            {
                Directory.CreateDirectory(Job);
                File.WriteAllText(pf, group + "\n12345\n" + boot + "\n"); // the dead job's start time, this boot
                var (code, output) = Shells.Run("linux", script);
                Assert.True(code == 0, output);
                Thread.Sleep(3000);
                Assert.True(Alive(sleeper), "an unrelated group that reused the job's number was killed");
                Assert.False(File.Exists(pf));
            }
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL " + sleeper.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>A pid file from before a reboot (another boot id) never kills anything, even if the pid and start time happen to match.</summary>
    [LinuxFact]
    public void APidFileFromAnotherBootKillsNothing()
    {
        Directory.CreateDirectory(_root + "/locks");
        var pf = _root + "/locks/job-j1.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = Job };
        foreach (var a in new[] { "sh", "-c", RecordSelf + "; sleep 300 & wait", "sh", pf })
        {
            psi.ArgumentList.Add(a);
        }

        using var leader = System.Diagnostics.Process.Start(psi)!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(pf) && File.ReadAllLines(pf).Length >= 3, TimeSpan.FromSeconds(30)));
            var lines = File.ReadAllLines(pf);
            File.WriteAllText(pf, lines[0] + "\n" + lines[1] + "\n00000000-0000-0000-0000-000000000000\n");
            var (code, output) = Shells.Run("linux", RemoteScripts.StopJob(Worker(MachineOs.Linux), "j1"));
            Assert.True(code == 0, output);
            Assert.False(leader.WaitForExit(TimeSpan.FromSeconds(3)), "a job recorded under another boot was killed");
            Assert.False(File.Exists(pf));
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL -- -" + leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>A job leader that changed its folder (cd /tmp) is still recognised by its recorded start time, and killed.</summary>
    [LinuxFact]
    public void AJobLeaderThatLeftItsFolderIsStillKilledByItsStartTime()
    {
        Directory.CreateDirectory(_root + "/locks");
        var pf = _root + "/locks/job-j1.pid";
        var psi = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false, WorkingDirectory = Job };
        foreach (var a in new[] { "sh", "-c", RecordSelf + "; cd /tmp; sleep 300 & wait", "sh", pf })
        {
            psi.ArgumentList.Add(a);
        }

        using var leader = System.Diagnostics.Process.Start(psi)!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(pf) && File.ReadAllLines(pf).Length >= 3, TimeSpan.FromSeconds(30)));
            Thread.Sleep(500); // let it reach /tmp
            var (code, output) = Shells.Run("linux", RemoteScripts.StopJob(Worker(MachineOs.Linux), "j1"));
            Assert.True(code == 0, output);
            Assert.True(leader.WaitForExit(TimeSpan.FromSeconds(30)), "the job leader that left its folder survived");
        }
        finally
        {
            Shells.Exec("sh", ["-c", "kill -s KILL -- -" + leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 2>/dev/null; true"]);
        }
    }

    /// <summary>The Linux job wrapper records the job's process group, its leader's start time and the boot id, and marks the job with LANQ_JOB.</summary>
    [LinuxFact]
    public void TheLinuxWrapperRecordsTheLeadersStartTime()
    {
        Directory.CreateDirectory(_root + "/locks");
        var copy = _root + "/copy.pid";
        var (code, output) = Shells.Run(
            "linux",
            RemoteScripts.Execute(Worker(MachineOs.Linux), "j1", ".", ["sh", "-c", "cp \"$0\" \"$1\" && printf '%s' \"$LANQ_JOB\" > \"$1.job\"", _root + "/locks/job-j1.pid", copy]));
        Assert.True(code == 0, output);
        var lines = File.ReadAllLines(copy);
        Assert.Equal(3, lines.Length);
        Assert.Matches("^[0-9]+$", lines[0]);
        Assert.Matches("^[0-9]+$", lines[1]);
        Assert.Equal(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), lines[2]);
        Assert.Equal("j1", File.ReadAllText(copy + ".job"));
    }

    [WindowsFact]
    public void TheWindowsStopScriptKillsTheRecordedProcess()
    {
        Directory.CreateDirectory(_root + "/locks");
        var psi = new System.Diagnostics.ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" })
        {
            psi.ArgumentList.Add(a);
        }

        using var job = System.Diagnostics.Process.Start(psi)!;
        File.WriteAllLines(_root + "/locks/job-j1.pid", [job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), job.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        try
        {
            var (code, output) = Shells.Run("windows", RemoteScripts.StopJob(Worker(MachineOs.Windows), "j1"));

            Assert.True(code == 0, output);
            Assert.True(job.WaitForExit(TimeSpan.FromSeconds(60)), "the recorded job process is still running");
            Assert.False(File.Exists(_root + "/locks/job-j1.pid"));
        }
        finally
        {
            if (!job.HasExited)
            {
                job.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>The real process plumbing behind RunSplitAsync: stderr written between two stdout lines never reaches the stdout callback.</summary>
    [ShFact]
    public async Task StderrIsKeptApartFromStdoutLineByLine()
    {
        var stdout = new List<string>();
        var stderr = new List<string>();
        var result = await ProcessSshClient.Execute(
            Shells.Sh!, ["-c", "echo one; echo warning >&2; sleep 0.2; echo two; echo late >&2"], stdout.Add, TimeSpan.FromMinutes(1), CancellationToken.None,
            null, stderr.Add);

        Assert.True(result.Ok);
        Assert.Equal(["one", "two"], stdout);
        Assert.Equal(["warning", "late"], stderr);
        Assert.DoesNotContain("warning", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowsCommandLineStaysFarUnderTheCapWhateverThePatterns()
    {
        Assert.True(FetchScripts.WindowsStdinCommand.Length < 300);
        var many = Enumerable.Range(0, FetchPatterns.MaxPatterns).Select(i => "**/" + new string('x', 190) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var script = FetchScripts.Fetch(Worker(MachineOs.Windows), "j1", ".", many, FetchLimits.Default);
        Assert.True(FetchScripts.WindowsStdin(script).Length < ProcessSshClient.MaxInputBytes);
    }

    [Fact]
    public void ThePatternsReachTheScriptsOnlyAsQuotedData()
    {
        string[] patterns = ["a b/*.trx"];
        var linux = FetchScripts.Fetch(Worker(MachineOs.Linux), "j1", ".", patterns, FetchLimits.Default);
        Assert.Contains("LQ_RE='^((a b/[^/]*[.]trx))$'", linux, StringComparison.Ordinal);
        var windows = FetchScripts.Fetch(Worker(MachineOs.Windows), "j1", ".", patterns, FetchLimits.Default);
        Assert.Contains("'^((a b/[^/]*[.]trx))\\z'", windows, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => FetchScripts.Fetch(Worker(MachineOs.Linux), "j1", "../x", patterns, FetchLimits.Default));
    }
}
