using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Runs the real slot-claim and unlock scripts against prepared slot files: each case once in sh ("linux") and once in PowerShell
/// ("windows"); a shell this machine does not have is reported as skipped, never silently passed.
/// </summary>
public sealed class StaleSlotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-slots-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public StaleSlotTests() => Directory.CreateDirectory(Path.Combine(_root, "locks"));

    public void Dispose()
    {
        for (var i = 0; ; i++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (i < 20)
            {
                Thread.Sleep(250); // a just-killed process can hold its folder for a moment
            }
        }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Slot(int i) => Path.Combine(_root, "locks", "slot-" + i.ToString(CultureInfo.InvariantCulture));

    private void WriteSlot(int i, string content) => File.WriteAllText(Slot(i), content);

    private static string Lines(params object[] parts) => string.Join('\n', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))) + "\n";

    private Machine Worker(string shell, int maxJobs = 2) => new()
    {
        Name = "w",
        Os = shell == "windows" ? MachineOs.Windows : MachineOs.Linux,
        WorkRoot = _root,
        MaxJobs = maxJobs,
    };

    /// <summary>The script output (stdout and stderr).</summary>
    internal static string RunScript(string shell, string script) => Shells.Run(shell, script).Output;

    private string Acquire(string shell, string jobId, TimeSpan timeout, int maxJobs = 2) => RunScript(shell, RemoteScripts.AcquireSlot(Worker(shell, maxJobs), jobId, timeout));

    private string[] LockFiles() => Directory.GetFiles(Path.Combine(_root, "locks")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    // ---- the probe ----

    [ShFact]
    public void ProbeDoesNotCountTransientReclaimFilesAsUsedSlotsSh() => ProbeDoesNotCountTransientReclaimFiles("linux");

    [PowerShellFact]
    public void ProbeDoesNotCountTransientReclaimFilesAsUsedSlotsPowerShell() => ProbeDoesNotCountTransientReclaimFiles("windows");

    private void ProbeDoesNotCountTransientReclaimFiles(string shell)
    {
        WriteSlot(1, Lines("live-job", Now + 3600));
        File.WriteAllText(Slot(2) + ".stale.4242", Lines("crashed-job", Now - 7200)); // mid-reclaim: renamed away, about to be deleted
        File.WriteAllText(Slot(2) + ".reclaim", ""); // a reclaim's lock
        Assert.Equal("1", HealthProbe.Parse(RunScript(shell, RemoteScripts.Probe(Worker(shell))))["slots_used"]);
    }

    // ---- reclaim ----

    [ShFact]
    public void SlotPastItsDeadlinePlusGraceIsReclaimedSh() => SlotPastItsDeadlinePlusGraceIsReclaimed("linux");

    [PowerShellFact]
    public void SlotPastItsDeadlinePlusGraceIsReclaimedPowerShell() => SlotPastItsDeadlinePlusGraceIsReclaimed("windows");

    private void SlotPastItsDeadlinePlusGraceIsReclaimed(string shell)
    {
        WriteSlot(1, Lines("crashed-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));
        WriteSlot(2, Lines("live-job", Now + 3600));

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.StartsWith("new-job\n", File.ReadAllText(Slot(1)).Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(Lines("live-job", Now + 3600)[..9], File.ReadAllText(Slot(2))[..9]);
        Assert.Equal(["slot-1", "slot-2"], LockFiles());
    }

    [ShFact]
    public void SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimedSh() => SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimed("linux");

    [PowerShellFact]
    public void SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimedPowerShell() => SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimed("windows");

    private void SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimed(string shell)
    {
        var overdue = Lines("slow-job", Now - 600); // past its deadline, but inside the grace period
        var legacy = "job-from-an-older-lanq"; // no deadline recorded
        WriteSlot(1, overdue);
        WriteSlot(2, legacy);

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));

        Assert.Null(JobRunner.ParseSlot(output));
        Assert.Contains("SLOT=FULL", output, StringComparison.Ordinal);
        Assert.Equal(overdue, File.ReadAllText(Slot(1)));
        Assert.Equal(legacy, File.ReadAllText(Slot(2)));
    }

    /// <summary>A deadline is plain base-10 digits, at most 18 of them: "08" is 8 (long past), a 30-digit value is no deadline at all.</summary>
    [ShFact]
    public void DeadlinesAreBaseTenAndAtMostEighteenDigitsSh() => DeadlinesAreBaseTenAndAtMostEighteenDigits("linux");

    [PowerShellFact]
    public void DeadlinesAreBaseTenAndAtMostEighteenDigitsPowerShell() => DeadlinesAreBaseTenAndAtMostEighteenDigits("windows");

    private void DeadlinesAreBaseTenAndAtMostEighteenDigits(string shell)
    {
        WriteSlot(1, Lines("octal-looking-job", "08"));
        var huge = Lines("huge-job", new string('9', 30));
        WriteSlot(2, huge);

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.StartsWith("new-job", File.ReadAllText(Slot(1)), StringComparison.Ordinal);
        Assert.Equal(huge, File.ReadAllText(Slot(2)));
    }

    /// <summary>A reclaim in progress elsewhere (its lock file exists) is left alone: no second reclaimer touches that slot.</summary>
    [ShFact]
    public void ASlotWhoseReclaimIsInProgressIsLeftAloneSh() => ASlotWhoseReclaimIsInProgressIsLeftAlone("linux");

    [PowerShellFact]
    public void ASlotWhoseReclaimIsInProgressIsLeftAlonePowerShell() => ASlotWhoseReclaimIsInProgressIsLeftAlone("windows");

    private void ASlotWhoseReclaimIsInProgressIsLeftAlone(string shell)
    {
        var stale = Lines("crashed-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60);
        WriteSlot(1, stale);
        File.WriteAllText(Slot(1) + ".reclaim", "");

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10), maxJobs: 1);

        Assert.Contains("SLOT=FULL", output, StringComparison.Ordinal);
        Assert.Equal(stale, File.ReadAllText(Slot(1)));
        Assert.Equal(["slot-1", "slot-1.reclaim"], LockFiles());
    }

    /// <summary>A reclaim lock left by a reclaimer that died (older than ten minutes) does not block the slot for ever.</summary>
    [ShFact]
    public void AnAbandonedReclaimLockIsClearedSh() => AnAbandonedReclaimLockIsCleared("linux");

    [PowerShellFact]
    public void AnAbandonedReclaimLockIsClearedPowerShell() => AnAbandonedReclaimLockIsCleared("windows");

    private void AnAbandonedReclaimLockIsCleared(string shell)
    {
        WriteSlot(1, Lines("crashed-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));
        File.WriteAllText(Slot(1) + ".reclaim", "");
        File.SetLastWriteTimeUtc(Slot(1) + ".reclaim", DateTime.UtcNow.AddMinutes(-30));

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10), maxJobs: 1);

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.Equal(["slot-1"], LockFiles());
    }

    /// <summary>
    /// The reclaim is decided under the slot's lock and re-checked there: three claimers racing for two stale slots end with every
    /// claimer either holding its own slot file or told FULL, and no slot file ever holds two claimers (no "put it back" step).
    /// </summary>
    [ShFact]
    public void ConcurrentClaimersNeverShareASlotSh() => ConcurrentClaimersNeverShareASlot("linux");

    [PowerShellFact]
    public void ConcurrentClaimersNeverShareASlotPowerShell() => ConcurrentClaimersNeverShareASlot("windows");

    private void ConcurrentClaimersNeverShareASlot(string shell)
    {
        WriteSlot(1, Lines("crashed-1", Now - RemoteScripts.StaleSlotGraceSeconds - 60));
        WriteSlot(2, Lines("crashed-2", Now - RemoteScripts.StaleSlotGraceSeconds - 60));

        var results = Enumerable.Range(1, 3).AsParallel().WithDegreeOfParallelism(3)
            .Select(n => (Job: "job-" + n.ToString(CultureInfo.InvariantCulture), Out: Acquire(shell, "job-" + n.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMinutes(10))))
            .ToList();

        var won = results.Where(r => JobRunner.ParseSlot(r.Out) is not null).ToList();
        Assert.Equal(2, won.Count);
        Assert.Single(results, r => r.Out.Contains("SLOT=FULL", StringComparison.Ordinal));
        foreach (var w in won)
        {
            Assert.StartsWith(w.Job + "\n", File.ReadAllText(Slot(int.Parse(JobRunner.ParseSlot(w.Out)!, CultureInfo.InvariantCulture))).Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.Equal(["slot-1", "slot-2"], LockFiles());
    }

    // ---- a still-running orphan job is stopped before its slot is reused ----

    [LinuxFact]
    public void AStaleSlotsStillRunningJobIsKilledBeforeTheSlotIsReusedSh()
    {
        var jobDir = Path.Combine(_root, "jobs", "orphan-job");
        Directory.CreateDirectory(jobDir);
        using var orphan = Process.Start(new ProcessStartInfo("setsid") { ArgumentList = { "sleep", "120" }, WorkingDirectory = jobDir, UseShellExecute = false })!;
        File.WriteAllText(Path.Combine(_root, "locks", "job-orphan-job.pid"), orphan.Id.ToString(CultureInfo.InvariantCulture) + "\n");
        WriteSlot(1, Lines("orphan-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));

        var output = Acquire("linux", "new-job", TimeSpan.FromMinutes(10), maxJobs: 1);

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.True(orphan.WaitForExit(10000), "the orphan job is still running");
        Assert.False(File.Exists(Path.Combine(_root, "locks", "job-orphan-job.pid")));
    }

    /// <summary>A recorded pid that now belongs to some other process (not working in that job's folder) is never killed.</summary>
    [LinuxFact]
    public void ARecycledPidOutsideTheJobFolderIsNotKilledSh()
    {
        using var other = Process.Start(new ProcessStartInfo("sleep") { ArgumentList = { "120" }, WorkingDirectory = Path.GetTempPath(), UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(_root, "locks", "job-orphan-job.pid"), other.Id.ToString(CultureInfo.InvariantCulture) + "\n");
            WriteSlot(1, Lines("orphan-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));

            Assert.Equal("1", JobRunner.ParseSlot(Acquire("linux", "new-job", TimeSpan.FromMinutes(10), maxJobs: 1)));
            Assert.False(other.HasExited);
        }
        finally
        {
            other.Kill();
        }
    }

    [WindowsFact] // taskkill
    public void AStaleSlotsStillRunningJobIsKilledBeforeTheSlotIsReusedPowerShell()
    {
        using var orphan = Process.Start(new ProcessStartInfo(Shells.PowerShell!) { ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" }, UseShellExecute = false })!;
        File.WriteAllText(Path.Combine(_root, "locks", "job-orphan-job.pid"), PidRecord(orphan));
        WriteSlot(1, Lines("orphan-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));

        var output = Acquire("windows", "new-job", TimeSpan.FromMinutes(10), maxJobs: 1);

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.True(orphan.WaitForExit(10000), "the orphan job is still running");
        Assert.False(File.Exists(Path.Combine(_root, "locks", "job-orphan-job.pid")));
    }

    /// <summary>What the Windows job wrapper records: the pid, then the process start time in UTC ticks.</summary>
    internal static string PidRecord(Process p, long? ticks = null) =>
        p.Id.ToString(CultureInfo.InvariantCulture) + "\r\n" + (ticks ?? p.StartTime.ToUniversalTime().Ticks).ToString(CultureInfo.InvariantCulture) + "\r\n";

    /// <summary>
    /// A pid file whose pid now belongs to another PowerShell (the reclaim runs 30+ minutes after the deadline, so Windows may have reused
    /// it, for example for the owner's own shell): the start time does not match, so that process is never killed.
    /// </summary>
    [WindowsFact]
    public void AReusedPidOfAnotherPowerShellIsNotKilledPowerShell()
    {
        using var other = Process.Start(new ProcessStartInfo(Shells.PowerShell!) { ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" }, UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(_root, "locks", "job-orphan-job.pid"), PidRecord(other, other.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerHour));
            WriteSlot(1, Lines("orphan-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));

            Assert.Equal("1", JobRunner.ParseSlot(Acquire("windows", "new-job", TimeSpan.FromMinutes(10), maxJobs: 1)));
            Thread.Sleep(500);
            Assert.False(other.HasExited, "a PowerShell that merely reused the recorded pid was killed");

            File.WriteAllText(Path.Combine(_root, "locks", "job-legacy.pid"), other.Id.ToString(CultureInfo.InvariantCulture) + "\r\n"); // no start time
            WriteSlot(1, Lines("legacy", Now - RemoteScripts.StaleSlotGraceSeconds - 60));
            Assert.Equal("1", JobRunner.ParseSlot(Acquire("windows", "newer-job", TimeSpan.FromMinutes(10), maxJobs: 1)));
            Thread.Sleep(500);
            Assert.False(other.HasExited, "a reclaim killed on a pid file without a start time");
        }
        finally
        {
            other.Kill(entireProcessTree: true);
        }
    }

    // ---- the slot file itself ----

    [ShFact]
    public void NewSlotRecordsItsOwnDeadlineSh() => NewSlotRecordsItsOwnDeadline("linux");

    [PowerShellFact]
    public void NewSlotRecordsItsOwnDeadlinePowerShell() => NewSlotRecordsItsOwnDeadline("windows");

    private void NewSlotRecordsItsOwnDeadline(string shell)
    {
        var before = Now;
        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));

        Assert.Equal("1", JobRunner.ParseSlot(output));
        var lines = File.ReadAllText(Slot(1)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.Equal("new-job", lines[0]);
        var deadline = long.Parse(lines[1], CultureInfo.InvariantCulture);
        var lease = (long)(TimeSpan.FromMinutes(10) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        Assert.InRange(deadline, before + lease - 5, Now + lease + 5);
    }

    [Fact]
    public void RunnerLeasesTheSlotForTheJobTimeoutPlusTheTransferAllowance()
    {
        var script = RemoteScripts.AcquireSlot(T.Linux(), "j", TimeSpan.FromMinutes(30));
        var seconds = (long)(TimeSpan.FromMinutes(30) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        Assert.Contains("+ " + seconds.ToString(CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
        Assert.Contains("+ " + seconds.ToString(CultureInfo.InvariantCulture), RemoteScripts.AcquireSlot(T.Windows(), "j", TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
    }

    [Fact]
    public void ReclaimHasNoPutBackStepAndNoHardLink()
    {
        foreach (var script in new[] { RemoteScripts.AcquireSlot(T.Linux(), "j", TimeSpan.FromMinutes(5)), RemoteScripts.Unlock(T.Linux(), all: false) })
        {
            Assert.DoesNotContain("ln ", script, StringComparison.Ordinal);
        }

        foreach (var script in new[] { RemoteScripts.AcquireSlot(T.Windows(), "j", TimeSpan.FromMinutes(5)), RemoteScripts.Unlock(T.Windows(), all: false) })
        {
            Assert.DoesNotContain("Move($t, $f)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Move($t, $p)", script, StringComparison.Ordinal);
        }
    }

    // ---- unlock ----

    [ShFact]
    public void UnlockRemovesOnlySlotsPastTheirDeadlineSh() => UnlockRemovesOnlySlotsPastTheirDeadline("linux");

    [PowerShellFact]
    public void UnlockRemovesOnlySlotsPastTheirDeadlinePowerShell() => UnlockRemovesOnlySlotsPastTheirDeadline("windows");

    private void UnlockRemovesOnlySlotsPastTheirDeadline(string shell)
    {
        WriteSlot(1, Lines("crashed-job", Now - 60)); // past its deadline (no grace needed when a person asks)
        WriteSlot(2, Lines("live-job", Now + 3600));
        WriteSlot(3, "job-from-an-older-lanq"); // no deadline
        File.WriteAllText(Slot(4) + ".stale.4242", "x"); // someone else's reclaim in progress
        WriteSlot(5, Lines("octal-looking-job", "08"));

        var output = RunScript(shell, RemoteScripts.Unlock(Worker(shell), all: false));

        Assert.Equal(["slot-2", "slot-3", "slot-4.stale.4242"], LockFiles());
        var report = Cli.ParseUnlock(output);
        Assert.Equal([("slot-1", "crashed-job"), ("slot-5", "octal-looking-job")], report.Removed);
        Assert.Contains(("slot-2", "live"), report.Kept);
        Assert.Contains(("slot-3", "nodeadline"), report.Kept);
    }

    [ShFact]
    public void UnlockAllRemovesEverySlotFileSh() => UnlockAllRemovesEverySlotFile("linux");

    [PowerShellFact]
    public void UnlockAllRemovesEverySlotFilePowerShell() => UnlockAllRemovesEverySlotFile("windows");

    private void UnlockAllRemovesEverySlotFile(string shell)
    {
        WriteSlot(1, Lines("live-job", Now + 3600));
        WriteSlot(2, "job-from-an-older-lanq");
        File.WriteAllText(Slot(3) + ".stale.4242", "x");
        File.WriteAllText(Path.Combine(_root, "locks", "job-abc.pid"), "123"); // not a slot: untouched

        var output = RunScript(shell, RemoteScripts.Unlock(Worker(shell), all: true));

        Assert.Equal(["job-abc.pid"], LockFiles());
        Assert.Equal(3, Cli.ParseUnlock(output).Removed.Count);
    }
}
