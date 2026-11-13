using System.Globalization;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Plan;
using LanQueue.Policy;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq run --exclusive and lanq.yml exclusive: (0.9.0): the job claims every slot of its machine, all or nothing
/// (<see cref="RemoteScripts.AcquireAll"/>), releases only its own slots (<see cref="RemoteScripts.Cleanup"/>), waits like busy while any
/// slot is in use, takes one machine per shard, and is reported in the JSON and jobs.jsonl. The claim scripts run for real (sh and
/// PowerShell) against a temporary work root; a shell this machine does not have is reported as skipped.
/// </summary>
public sealed class ExclusiveTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-excl-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public ExclusiveTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
    }

    public void Dispose() => TestTree.Delete(_root);

    private string Repo => Path.Combine(_root, "repo");

    private JobLog Log => new(Path.Combine(_root, "state"));

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long Stale => Now - RemoteScripts.StaleSlotGraceSeconds - 60;

    private string Slot(int i) => Path.Combine(_root, "locks", "slot-" + i.ToString(CultureInfo.InvariantCulture));

    private void WriteSlot(int i, string content) => File.WriteAllText(Slot(i), content);

    private static string Lines(params object[] parts) => string.Join('\n', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))) + "\n";

    /// <summary>The job id on a slot file's first line, or null when there is no such file.</summary>
    private string? Holder(int i) => File.Exists(Slot(i)) ? File.ReadAllText(Slot(i)).Replace("\r", "", StringComparison.Ordinal).Split('\n')[0] : null;

    private string[] LockFiles() => [.. Directory.GetFiles(Path.Combine(_root, "locks")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private void ClearLocks()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(_root, "locks")))
        {
            File.Delete(f);
        }
    }

    private Machine Worker(string shell, int maxJobs) => new()
    {
        Name = "w",
        Os = shell == "windows" ? MachineOs.Windows : MachineOs.Linux,
        WorkRoot = _root,
        MaxJobs = maxJobs,
    };

    private string ClaimAll(string shell, string jobId, int maxJobs) =>
        Shells.Run(shell, RemoteScripts.AcquireAll(Worker(shell, maxJobs), jobId, TimeSpan.FromMinutes(10))).Output;

    private string ClaimOne(string shell, string jobId, int maxJobs) =>
        Shells.Run(shell, RemoteScripts.AcquireSlot(Worker(shell, maxJobs), jobId, TimeSpan.FromMinutes(10))).Output;

    // ---- the claim, for real ----

    [ShFact]
    public void ExclusiveClaimsEverySlotOrNoneSh() => ExclusiveClaimsEverySlotOrNone("linux");

    [PowerShellFact]
    public void ExclusiveClaimsEverySlotOrNonePowerShell() => ExclusiveClaimsEverySlotOrNone("windows");

    private void ExclusiveClaimsEverySlotOrNone(string shell)
    {
        var before = Now;
        var output = ClaimAll(shell, "ex-job", 3);

        Assert.Contains("SLOTS=1,2,3", output, StringComparison.Ordinal);
        Assert.Equal("1,2,3", JobRunner.ParseSlot(output));
        var lease = (long)(TimeSpan.FromMinutes(10) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        foreach (var i in new[] { 1, 2, 3 })
        {
            var lines = File.ReadAllText(Slot(i)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
            Assert.Equal("ex-job", lines[0]);
            Assert.InRange(long.Parse(lines[1], CultureInfo.InvariantCulture), before + lease - 5, Now + lease + 5);
        }

        // Every slot is taken now: another exclusive claim and a normal one are both told FULL, and nothing changes.
        Assert.Null(JobRunner.ParseSlot(ClaimAll(shell, "ex-2", 3)));
        Assert.Contains("SLOT=FULL", ClaimOne(shell, "normal", 3), StringComparison.Ordinal);
        Assert.Equal(["slot-1", "slot-2", "slot-3"], LockFiles());
        Assert.All(new[] { 1, 2, 3 }, i => Assert.Equal("ex-job", Holder(i)));
    }

    [ShFact]
    public void ABackedOutClaimLeavesNoSlotFileSh() => ABackedOutClaimLeavesNoSlotFile("linux");

    [PowerShellFact]
    public void ABackedOutClaimLeavesNoSlotFilePowerShell() => ABackedOutClaimLeavesNoSlotFile("windows");

    /// <summary>The last slot is someone else's: the two slots this claim created are removed again, the other job's slot is untouched.</summary>
    private void ABackedOutClaimLeavesNoSlotFile(string shell)
    {
        var live = Lines("other-job", Now + 3600);
        WriteSlot(3, live);

        var output = ClaimAll(shell, "ex-job", 3);

        Assert.Contains("SLOT=FULL", output, StringComparison.Ordinal);
        Assert.Null(JobRunner.ParseSlot(output));
        Assert.Equal(["slot-3"], LockFiles());
        Assert.Equal(live, File.ReadAllText(Slot(3)));

        // A middle slot taken: slot-1 was created and backed out, slot-3 never tried.
        ClearLocks();
        WriteSlot(2, live);
        Assert.Null(JobRunner.ParseSlot(ClaimAll(shell, "ex-job", 3)));
        Assert.Equal(["slot-2"], LockFiles());
    }

    [ShFact]
    public void StaleExclusiveSlotsAreReclaimedOneByOneSh() => StaleExclusiveSlotsAreReclaimedOneByOne("linux");

    [PowerShellFact]
    public void StaleExclusiveSlotsAreReclaimedOneByOnePowerShell() => StaleExclusiveSlotsAreReclaimedOneByOne("windows");

    /// <summary>
    /// An exclusive job that crashed left every slot behind with its deadline. Each is an ordinary stale slot: a normal claim reclaims just
    /// the first, and an exclusive claim reclaims them one by one and holds them all.
    /// </summary>
    private void StaleExclusiveSlotsAreReclaimedOneByOne(string shell)
    {
        foreach (var i in new[] { 1, 2, 3 })
        {
            WriteSlot(i, Lines("crashed-ex", Stale));
        }

        Assert.Equal("1", JobRunner.ParseSlot(ClaimOne(shell, "normal", 3)));
        Assert.Equal(["normal", "crashed-ex", "crashed-ex"], new[] { 1, 2, 3 }.Select(Holder));

        ClearLocks();
        foreach (var i in new[] { 1, 2, 3 })
        {
            WriteSlot(i, Lines("crashed-ex", Stale));
        }

        Assert.Equal("1,2,3", JobRunner.ParseSlot(ClaimAll(shell, "new-ex", 3)));
        Assert.Equal(["new-ex", "new-ex", "new-ex"], new[] { 1, 2, 3 }.Select(Holder));
        Assert.Equal(["slot-1", "slot-2", "slot-3"], LockFiles());

        // A stale slot is reclaimed even when the claim then backs out at a live one: the reclaimed slot is gone, the live one stays.
        ClearLocks();
        WriteSlot(1, Lines("crashed-ex", Stale));
        WriteSlot(2, Lines("live-job", Now + 3600));
        Assert.Null(JobRunner.ParseSlot(ClaimAll(shell, "new-ex", 3)));
        Assert.Equal(["slot-2"], LockFiles());
        Assert.Equal("live-job", Holder(2));
    }

    [ShFact]
    public void CleanupRemovesEverySlotOfTheJobOnlySh() => CleanupRemovesEverySlotOfTheJobOnly("linux");

    [PowerShellFact]
    public void CleanupRemovesEverySlotOfTheJobOnlyPowerShell() => CleanupRemovesEverySlotOfTheJobOnly("windows");

    /// <summary>Cleanup takes the slot list back and removes each slot only while it still holds this job's id (git and archive mode alike).</summary>
    private void CleanupRemovesEverySlotOfTheJobOnly(string shell)
    {
        foreach (var archive in new[] { false, true })
        {
            ClearLocks();
            WriteSlot(1, Lines("ex-job", Now + 3600));
            WriteSlot(2, Lines("ex-job", Now + 3600));
            WriteSlot(3, Lines("other-job", Now + 3600)); // reclaimed and claimed by another job meanwhile
            WriteSlot(5, Lines("ex-job", Now + 3600)); // not in the list: never touched
            var worker = Worker(shell, 4);
            worker.Transfer = archive ? TransferMode.Archive : TransferMode.Git;

            var (code, output) = Shells.Run(shell, RemoteScripts.Cleanup(worker, "ex-job", "1,2,3,4"));

            Assert.True(code == 0, output);
            Assert.Equal(["slot-3", "slot-5"], LockFiles());
            Assert.Equal("other-job", Holder(3));
        }
    }

    [ShFact]
    public void UnlockAndTheProbeStillWorkSh() => UnlockAndTheProbeStillWork("linux");

    [PowerShellFact]
    public void UnlockAndTheProbeStillWorkPowerShell() => UnlockAndTheProbeStillWork("windows");

    /// <summary>An exclusive job's slots are ordinary slot files: the probe counts each, and lanq unlock treats each as any other slot.</summary>
    private void UnlockAndTheProbeStillWork(string shell)
    {
        Assert.Equal("1,2,3", JobRunner.ParseSlot(ClaimAll(shell, "ex-job", 3)));
        var worker = Worker(shell, 3);
        Assert.Equal("3", HealthProbe.Parse(Shells.Run(shell, RemoteScripts.Probe(worker)).Output)["slots_used"]);

        var kept = Cli.ParseUnlock(Shells.Run(shell, RemoteScripts.Unlock(worker, all: false)).Output);
        Assert.Empty(kept.Removed);
        Assert.Equal(3, kept.Kept.Count(k => k.Item2 == "live"));
        Assert.Equal(["slot-1", "slot-2", "slot-3"], LockFiles());

        // Past their deadline (a crashed exclusive job): unlock removes every one of them.
        foreach (var i in new[] { 1, 2, 3 })
        {
            WriteSlot(i, Lines("ex-job", Now - 60));
        }

        Assert.Equal(3, Cli.ParseUnlock(Shells.Run(shell, RemoteScripts.Unlock(worker, all: false)).Output).Removed.Count);
        Assert.Empty(LockFiles());

        Assert.Equal("1,2,3", JobRunner.ParseSlot(ClaimAll(shell, "ex-job", 3)));
        Assert.Equal(3, Cli.ParseUnlock(Shells.Run(shell, RemoteScripts.Unlock(worker, all: true)).Output).Removed.Count);
        Assert.Empty(LockFiles());
    }

    // ---- races, for real ----

    /// <summary>
    /// Runs every script at once: each first creates its own ready file and then spins (bounded: 10 s in PowerShell, 2 million loops in sh) until a shared go file exists, which the
    /// test creates only once every script is ready. So the claims really start together, whatever each shell's start-up time. The outputs,
    /// in the order given.
    /// </summary>
    /// <param name="stagger">Per script, a short spin after go (PowerShell: milliseconds; sh: hundreds of loop turns), or null for none.</param>
    private List<string> Race(string shell, IReadOnlyList<string> scripts, IReadOnlyList<int>? stagger = null)
    {
        var gate = (_root + "/gate-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(gate);
        var go = gate + "/go";
        int Spin(int i) => stagger is null ? 0 : stagger[i];
        string Barrier(int i)
        {
            var ready = gate + "/ready-" + i.ToString(CultureInfo.InvariantCulture);
            return shell == "windows"
                ? $"[IO.File]::WriteAllText({ShellQuote.PowerShell(ready)}, '');$sw=[Diagnostics.Stopwatch]::StartNew();while(-not [IO.File]::Exists({ShellQuote.PowerShell(go)}) -and $sw.ElapsedMilliseconds -lt 10000){{}};$sw.Restart();while($sw.Elapsed.TotalMilliseconds -lt {Spin(i)}){{}}"
                : $": > {ShellQuote.Posix(ready)}; w=0; while [ ! -f {ShellQuote.Posix(go)} ] && [ \"$w\" -lt 2000000 ]; do w=$((w+1)); done; w=0; while [ \"$w\" -lt {Spin(i) * 100} ]; do w=$((w+1)); done";
        }

        // The barrier goes just before each script's claim loop, after its set-up (function definitions, the Windows unpacking), so the
        // claims themselves start together rather than the scripts.
        string AtTheLoop(string script, int i)
        {
            var loop = System.Text.RegularExpressions.Regex.Match(script, @"^[ \t]*(for\(\$i=1;|i=1$)", System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(5));
            Assert.True(loop.Success, "no claim loop found");
            return script[..loop.Index] + Barrier(i) + "\n" + script[loop.Index..];
        }

        var runs = scripts.Select((script, i) => Task.Run(() => Shells.Run(shell, AtTheLoop(script, i)).Output)).ToArray();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Directory.GetFiles(gate, "ready-*").Length < scripts.Count && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(5);
        }

        File.WriteAllText(go, "");
        Task.WaitAll(runs);
        Directory.Delete(gate, recursive: true);
        return [.. runs.Select(r => r.Result)];
    }

    private string AllScript(string shell, string jobId, int maxJobs) => RemoteScripts.AcquireAll(Worker(shell, maxJobs), jobId, TimeSpan.FromMinutes(10));

    private string OneScript(string shell, string jobId, int maxJobs) => RemoteScripts.AcquireSlot(Worker(shell, maxJobs), jobId, TimeSpan.FromMinutes(10));

    /// <summary>Two exclusive claims released together on one machine: never both win; the winner holds every slot, a loser none.</summary>
    [LinuxFact]
    public void TwoExclusiveClaimsNeverBothWin() => TwoExclusiveClaims("linux", 30);

    [PowerShellFact]
    public void TwoExclusiveClaimsNeverBothWinPowerShell() => TwoExclusiveClaims("windows", 12);

    private void TwoExclusiveClaims(string shell, int iterations)
    {
        var won = 0;
        for (var n = 0; n < iterations; n++)
        {
            ClearLocks();
            var outputs = Race(shell, [AllScript(shell, "ex-a", 3), AllScript(shell, "ex-b", 3)]);
            var results = new[] { "ex-a", "ex-b" }.Zip(outputs, (job, o) => (Job: job, Slots: JobRunner.ParseSlot(o))).ToList();

            var winners = results.Where(r => r.Slots is not null).ToList();
            Assert.True(winners.Count <= 1, $"iteration {n}: both exclusive claims won");
            if (winners.Count == 1)
            {
                won++;
                Assert.Equal("1,2,3", winners[0].Slots);
                Assert.All(new[] { 1, 2, 3 }, i => Assert.Equal(winners[0].Job, Holder(i)));
            }
            else
            {
                Assert.Empty(LockFiles()); // both backed out: nothing left behind
            }
        }

        Assert.True(won > 0, "no exclusive claim ever won");
    }

    /// <summary>
    /// An exclusive claim that must back out (slot-3 is another job's, live) released together with two normal claims on a max_jobs 3
    /// machine. Every time: the exclusive claim is FULL and its id is in no slot file, the normal claims hold distinct slots, and nothing else
    /// is left. At least once a normal claim must have been refused because the exclusive claim held slot-1 or slot-2 for a moment (both are
    /// free again at the end): proof that the back-out ran while the others were claiming.
    /// </summary>
    [LinuxFact]
    public void AnExclusiveClaimBacksOutUnderContention() => BackOutUnderContention("linux", 20, 120);

    [PowerShellFact]
    public void AnExclusiveClaimBacksOutUnderContentionPowerShell() => BackOutUnderContention("windows", 10, 80);

    /// <param name="iterations">At least this many races; more, up to <paramref name="atMost"/>, until one was contended.</param>
    private void BackOutUnderContention(string shell, int iterations, int atMost)
    {
        var live = Lines("other-job", Now + 3600);
        var contended = 0;
        var normalHeldWhileExclusiveHeldNone = 0;
        var n = 0;
        for (; n < atMost && (n < iterations || contended == 0 || normalHeldWhileExclusiveHeldNone == 0); n++)
        {
            ClearLocks();
            WriteSlot(3, live);
            // The normal claims start a little later, by a different amount each round, to land inside the exclusive claim's short hold.
            var lag = n % 6;
            var outputs = Race(shell, [AllScript(shell, "ex", 3), OneScript(shell, "n1", 3), OneScript(shell, "n2", 3)], [0, lag, lag + 1]);
            var (ex, n1, n2) = (JobRunner.ParseSlot(outputs[0]), JobRunner.ParseSlot(outputs[1]), JobRunner.ParseSlot(outputs[2]));

            Assert.Null(ex);
            Assert.Contains("SLOT=FULL", outputs[0], StringComparison.Ordinal);
            Assert.DoesNotContain("ex", new[] { 1, 2, 3 }.Select(Holder));
            Assert.Equal(live, File.ReadAllText(Slot(3)));
            string[] held = [.. new[] { n1, n2 }.OfType<string>()];
            Assert.Equal(held.Length, held.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal([.. held.Select(h => "slot-" + h).Append("slot-3").Order(StringComparer.Ordinal)], LockFiles());
            if (held.Length > 0)
            {
                normalHeldWhileExclusiveHeldNone++;
            }

            if (held.Length < 2)
            {
                contended++; // a normal claim was refused while slot-1 or slot-2 was briefly the exclusive claim's
            }
        }

        Assert.True(normalHeldWhileExclusiveHeldNone > 0, $"in {n} races no normal claim ever held a slot");
        Assert.True(contended > 0, $"in {n} races the exclusive claim never held a slot while a normal claim looked");
    }

    // ---- the scripts and the parser ----

    [Fact]
    public void ParseSlotReadsAnExclusiveSlotList()
    {
        Assert.Equal("1,2,3,4", JobRunner.ParseSlot("noise\nSLOTS=1,2,3,4\n"));
        Assert.Equal("1", JobRunner.ParseSlot("SLOTS=1"));
        Assert.Equal("2", JobRunner.ParseSlot("SLOT=2"));
        foreach (var bad in new[] { "SLOT=FULL", "SLOTS=", "SLOTS=2,1", "SLOTS=1,1", "SLOTS=1,,2", "SLOTS=0,1", "SLOTS=1;rm", "SLOTS=-1", "SLOTS=1, 2", "" })
        {
            Assert.Null(JobRunner.ParseSlot(bad));
        }
    }

    [Fact]
    public void ASlotListThatIsNotNumbersNeverReachesAScript()
    {
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            foreach (var bad in new[] { "1,x", "1;rm -rf /", "1,", "0,1", "1,$(id)" })
            {
                Assert.Throws<ArgumentException>(() => RemoteScripts.Cleanup(m, "j", bad));
            }
        }
    }

    /// <summary>A normal job's claim is unchanged; its cleanup, like an exclusive job's, removes its slot only while the slot holds its id.</summary>
    [Fact]
    public void ANormalJobsClaimIsUnchangedAndItsCleanupChecksTheHolder()
    {
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            Assert.DoesNotContain("SLOTS=", RemoteScripts.AcquireSlot(m, "j", TimeSpan.FromMinutes(5)), StringComparison.Ordinal);
        }

        var linux = RemoteScripts.Cleanup(T.Linux(), "j", "2", T.RepoId);
        Assert.Contains("rm -f \"$R/incoming/$ID.bundle\"\nfor n in 2; do if [ \"$(sed -n 1p \"$R/locks/slot-$n\" 2>/dev/null)\" = \"$ID\" ]; then rm -f \"$R/locks/slot-$n\"; fi; done", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$R/locks/slot-2\"", linux, StringComparison.Ordinal);
        var windows = RemoteScripts.Cleanup(T.Windows(), "j", "2", T.RepoId);
        Assert.Contains("foreach($n in @(2)){$g=\"$root/locks/slot-$n\";try{if(([IO.File]::ReadAllText($g) -split \"`n\")[0].Trim() -ceq $id){[IO.File]::Delete($g)}}catch{}}", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$root/locks/slot-2\"", windows, StringComparison.Ordinal);
    }

    [ShFact]
    public void ASingleSlotNowHeldByAnotherJobSurvivesCleanupSh() => ASingleSlotNowHeldByAnotherJobSurvivesCleanup("linux");

    [PowerShellFact]
    public void ASingleSlotNowHeldByAnotherJobSurvivesCleanupPowerShell() => ASingleSlotNowHeldByAnotherJobSurvivesCleanup("windows");

    /// <summary>
    /// A normal job's controller slept past its slot's deadline plus the grace; an exclusive job reclaimed the slot meanwhile. The late cleanup
    /// must not delete the exclusive job's slot file (another job could then run beside it). Its own slot it still removes.
    /// </summary>
    private void ASingleSlotNowHeldByAnotherJobSurvivesCleanup(string shell)
    {
        foreach (var archive in new[] { false, true })
        {
            ClearLocks();
            var exclusive = Lines("ex-job", Now + 3600);
            WriteSlot(1, exclusive);
            WriteSlot(2, Lines("sleepy-job", Now + 3600));
            var worker = Worker(shell, 2);
            worker.Transfer = archive ? TransferMode.Archive : TransferMode.Git;

            Assert.Equal(0, Shells.Run(shell, RemoteScripts.Cleanup(worker, "sleepy-job", "1")).Code);
            Assert.Equal(exclusive, File.ReadAllText(Slot(1)));

            Assert.Equal(0, Shells.Run(shell, RemoteScripts.Cleanup(worker, "sleepy-job", "2")).Code);
            Assert.Equal(["slot-1"], LockFiles());
        }
    }

    [ShFact]
    public void ReleaseClaimRemovesOnlyThisJobsSlotsSh() => ReleaseClaimRemovesOnlyThisJobsSlots("linux");

    [PowerShellFact]
    public void ReleaseClaimRemovesOnlyThisJobsSlotsPowerShell() => ReleaseClaimRemovesOnlyThisJobsSlots("windows");

    /// <summary>The release after a claim that did not report back: every slot of this job goes, any other job's slot (and any other file) stays.</summary>
    private void ReleaseClaimRemovesOnlyThisJobsSlots(string shell)
    {
        WriteSlot(1, Lines("lost-job", Now + 3600));
        WriteSlot(2, Lines("lost-job", Now + 3600));
        WriteSlot(3, Lines("other-job", Now + 3600));
        WriteSlot(5, Lines("lost-job", Now + 3600)); // beyond max_jobs 4: not this machine's slot range
        File.WriteAllText(Slot(4) + ".reclaim", "x");

        var (code, output) = Shells.Run(shell, RemoteScripts.ReleaseClaim(Worker(shell, 4), "lost-job"));

        Assert.True(code == 0, output);
        Assert.Equal(["slot-3", "slot-4.reclaim", "slot-5"], LockFiles());
        Assert.Equal("other-job", Holder(3));
    }

    /// <summary>
    /// The claim timed out (it may have created slots before the connection or the clock ran out): the job tries the next look, and first
    /// releases this job's slots on that machine. A clean SLOT=FULL created nothing, so nothing is released.
    /// </summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData(124, "", true)] // ssh timed out
    [InlineData(255, "Connection reset by peer", true)] // the connection dropped
    [InlineData(0, "SLOT=", true)] // unreadable output
    [InlineData(0, "SLOT=FULL", false)]
    public async Task AClaimThatDidNotReportBackIsReleased(int code, string output, bool released)
    {
        foreach (var exclusive in new[] { true, false })
        {
            var ssh = new FakeSsh
            {
                Handler = (_, cmd) => cmd.Contains(RemoteScripts.ReleaseClaimMarker, StringComparison.Ordinal) ? new SshResult(0, "")
                    : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(code, output)
                    : new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok"),
            };
            var config = T.Config();
            var runner = new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_root, "tmp"), _ => { }, (_, _) => Task.CompletedTask);

            var r = await runner.RunAsync(new JobRequest(["dotnet"], ["dotnet", "test"], ".", Repo, TimeSpan.Zero) { On = ["build-01"], Exclusive = exclusive }, CancellationToken.None);

            Assert.Equal(JobStatus.NoCapacity, r.Status);
            var releases = ssh.Calls.Where(c => c.Command.Contains(RemoteScripts.ReleaseClaimMarker, StringComparison.Ordinal)).ToList();
            if (released)
            {
                var release = Assert.Single(releases);
                Assert.Contains("for n in 1 2; do", release.Command, StringComparison.Ordinal); // build-01: max_jobs 2, every slot checked
                Assert.Contains("= \"$ID\" ]", release.Command, StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(releases);
            }
        }
    }

    [Fact]
    public void AReleaseThatFailsNeverFailsTheJob()
    {
        Assert.True(JobRunner.IsCleanFull("noise\nSLOT=FULL\n"));
        Assert.False(JobRunner.IsCleanFull("SLOT=FULL\nSLOTS=1,2"));
        Assert.False(JobRunner.IsCleanFull(""));
        Assert.False(JobRunner.IsCleanFull("SLOT=3"));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Cleanup(T.Linux(), "j", ""));
    }

    /// <summary>
    /// kill_recorded runs inside the slot claim's reclaim and inside unlock, which use f and g themselves: it must never assign either (it once
    /// looped "for f in /proc/...", so a reclaim then renamed the wrong path and unlock reported a removal it had not done).
    /// </summary>
    [Fact]
    public void KillRecordedNeverTouchesTheCallersFOrG()
    {
        var body = RemoteScripts.LinuxKillRecordedFunction;
        Assert.DoesNotMatch(@"(^|[\s;(])(f|g)=", body);
        Assert.DoesNotMatch(@"\bfor\s+(f|g)\s+in\b", body);
        Assert.DoesNotMatch(@"\bread\b[^\n;]*\s(f|g)\b", body);
    }

    /// <summary>The all-slots claim fits the Windows command-line cap on a large machine, and leases its slots as a normal claim does.</summary>
    [Fact]
    public void TheAllSlotsClaimFitsTheWindowsCapAndLeasesLikeANormalClaim()
    {
        var w = T.Windows();
        w.MaxJobs = 64;
        Assert.True(RemoteScripts.Wrap(w, RemoteScripts.AcquireAll(w, "j", TimeSpan.FromDays(7))).Length <= RemoteScripts.MaxWindowsCommandLength);
        var seconds = (long)(TimeSpan.FromMinutes(30) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        Assert.Contains("+ " + seconds.ToString(CultureInfo.InvariantCulture), RemoteScripts.AcquireAll(T.Linux(), "j", TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
        Assert.Contains("$deadline=$now+" + seconds.ToString(CultureInfo.InvariantCulture), RemoteScripts.AcquireAll(T.Windows(), "j", TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
    }

    // ---- scheduling ----

    private static (Machine Machine, MachineHealth Health) Probed(int maxJobs, int used, double? memFree = null) =>
        (new Machine { Name = "big-01", Os = MachineOs.Linux, MaxJobs = maxJobs, Capabilities = ["node"] },
            new MachineHealth("big-01", true, 0.1, 50, used, null, null) { MemFreeGb = memFree, MemTotalGb = 32 });

    [Fact]
    public void ExclusiveWaitsWhileAnySlotIsUsed()
    {
        var needs = new JobNeeds(["node"], null, false) { Exclusive = true };

        var busy = Assert.Single(Scheduler.Assess([Probed(4, 1)], needs));
        Assert.Equal((Exclusion.Busy, "busy", false), (busy.Reason, busy.Code, busy.Permanent));
        Assert.Equal("busy (slots 1/4; --exclusive needs all 4 free)", busy.Why);
        Assert.Equal(0, busy.JobSlots);

        var idle = Assert.Single(Scheduler.Assess([Probed(4, 0)], needs));
        Assert.Equal((true, 1, 4), (idle.Eligible, idle.JobSlots, idle.FreeSlots));

        // A normal job on the same machine still sees three free slots.
        Assert.Equal(3, Assert.Single(Scheduler.Assess([Probed(4, 1)], needs with { Exclusive = false })).JobSlots);

        // --mem still applies; this run's own shard there counts as a used slot.
        var mem = Assert.Single(Scheduler.Assess([Probed(4, 0, memFree: 2)], new JobNeeds(["node"], 4, false) { Exclusive = true }));
        Assert.Equal(Exclusion.MemBelowRequest, mem.Reason);
        var (m, h) = Probed(4, 0, memFree: 8);
        Assert.Equal(1, Scheduler.FreeFor(m, h, 4, 0, 0, exclusive: true));
        Assert.Equal(0, Scheduler.FreeFor(m, h, 4, 1, 1, exclusive: true));
        Assert.Equal(0, Scheduler.FreeFor(m, h, 16, 0, 0, exclusive: true));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AnExclusiveRunOnABusyMachineEndsBusyAndRetryable()
    {
        var ssh = new PoolSsh();
        var big = ConfigLoader.Parse(ShardTests.PoolYaml).Machines.Single(m => m.Name == "big-01");
        await ssh.RunAsync(big, "echo SLOT=", null, TimeSpan.FromSeconds(1), CancellationToken.None); // another job holds one slot

        var (code, json, _) = await Run(ssh, "--exclusive", "--on", "big-01");

        Assert.Equal(75, code);
        Assert.Equal(("NoCapacity", true, true), (json.GetProperty("status").GetString(), json.GetProperty("retryable").GetBoolean(), json.GetProperty("exclusive").GetBoolean()));
        var excluded = Assert.Single(json.GetProperty("excluded").EnumerateArray());
        Assert.Equal(("busy", "busy (slots 1/4; --exclusive needs all 4 free)"), (excluded.GetProperty("reason").GetString(), excluded.GetProperty("why").GetString()));
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOTS=", StringComparison.Ordinal)); // judged busy: never even tried
    }

    // ---- a real run (fake ssh) ----

    private async Task<(int Code, JsonElement Json, string Stderr)> Run(PoolSsh ssh, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", .. args, "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}"]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh,
            new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, e, CancellationToken.None);
        using var doc = JsonDocument.Parse(o.ToString());
        return (code, doc.RootElement.Clone(), e.ToString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AnExclusiveRunHoldsEverySlotAndReleasesThemAll()
    {
        var ssh = new PoolSsh();
        var holding = -1;
        ssh.OnExecute = (x, _) =>
        {
            holding = ssh.Used(x.Machine.Name);
            return Task.FromResult(x.Exit(0));
        };

        var (code, json, stderr) = await Run(ssh, "--exclusive", "--on", "big-01");

        Assert.Equal(0, code);
        Assert.Equal(4, holding);
        Assert.Equal(0, ssh.Used("big-01"));
        Assert.True(json.GetProperty("exclusive").GetBoolean());
        Assert.Contains("(slots 1,2,3,4, exclusive)", stderr, StringComparison.Ordinal);
        Assert.Contains(ssh.Calls, c => c.Command.Contains("for n in 1 2 3 4;", StringComparison.Ordinal)); // the cleanup got the list back
        var record = Assert.Single(Log.ReadAll());
        Assert.True(record.Exclusive);
        Assert.Contains("\"exclusive\":true", File.ReadAllText(Log.FilePath), StringComparison.Ordinal);

        // Without --exclusive: one slot, and neither the JSON nor jobs.jsonl says exclusive.
        var (_, plain, _) = await Run(new PoolSsh(), "--on", "big-01");
        Assert.False(plain.TryGetProperty("exclusive", out _));
        Assert.DoesNotContain("exclusive", File.ReadAllLines(Log.FilePath)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExclusiveShardsOnePerMachine()
    {
        foreach (var shards in new[] { "auto", "2" })
        {
            var ssh = new PoolSsh();
            var (code, json, _) = await Run(ssh, "--exclusive", "--shards", shards);

            Assert.Equal(0, code);
            Assert.Equal(2, json.GetProperty("shardCount").GetInt32());
            Assert.True(json.GetProperty("exclusive").GetBoolean());
            Assert.Equal(["big-01", "tiny-01"], ssh.Executes.Select(x => x.Machine.Name).Order(StringComparer.Ordinal));
            Assert.Equal(4, ssh.Peak["big-01"]); // the big machine's shard held all four slots
            Assert.Equal(0, ssh.Used("big-01") + ssh.Used("tiny-01"));
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExclusiveShardsMoreThanMachinesIsRefused()
    {
        var ssh = new PoolSsh();
        var (code, json, _) = await Run(ssh, "--exclusive", "--shards", "3");

        Assert.Equal(2, code);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.Equal("--exclusive --shards 3: only 2 machines can take this job", json.GetProperty("message").GetString());
        Assert.Empty(ssh.Calls); // before any network

        // --on narrows the machines that count; the dry run refuses the same way.
        var (dryCode, dry, _) = await Run(ssh, "--exclusive", "--shards", "2", "--on", "big-01", "--dry-run");
        Assert.Equal((2, "--exclusive --shards 2: only 1 machine can take this job"), (dryCode, dry.GetProperty("message").GetString()));
        Assert.Empty(ssh.Calls);
    }

    // ---- plans ----

    private async Task<(int Code, Dictionary<string, JsonElement> Jobs)> Plan(string yaml, bool dry)
    {
        File.WriteAllText(Path.Combine(Repo, "lanq.yml"), yaml);
        var ssh = new PoolSsh();
        var o = new StringWriter();
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--json", .. (dry ? new[] { "--dry-run" } : [])]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, new StringWriter(), CancellationToken.None);
        using var doc = JsonDocument.Parse(o.ToString());
        return (code, doc.RootElement.Clone().GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanDryRunMarksTheMachineFull()
    {
        // An exclusive job first: big-01 is full for the plan, so the next job on it would wait.
        var (code, jobs) = await Plan("""
            version: 1
            defaults:
              needs: [node]
              on: [big-01]
            jobs:
              whole:
                command: npx jest
                exclusive: true
              unit:
                command: npx jest --ci
            """, dry: true);
        Assert.Equal(0, code);
        Assert.Equal(("WouldRun", "big-01", true), (jobs["whole"].GetProperty("status").GetString(), jobs["whole"].GetProperty("machine").GetString(), jobs["whole"].GetProperty("exclusive").GetBoolean()));
        Assert.Equal("WouldWait", jobs["unit"].GetProperty("status").GetString());
        Assert.False(jobs["unit"].TryGetProperty("exclusive", out _));

        // A normal job first: one of big-01's four slots is the plan's, so the exclusive job cannot go there now.
        (code, jobs) = await Plan("""
            version: 1
            defaults:
              needs: [node]
              on: [big-01]
            jobs:
              unit:
                command: npx jest --ci
              whole:
                command: npx jest
                exclusive: true
            """, dry: true);
        Assert.Equal(0, code);
        Assert.Equal("WouldRun", jobs["unit"].GetProperty("status").GetString());
        Assert.Equal("WouldWait", jobs["whole"].GetProperty("status").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanRunCarriesExclusiveAndASiblingWaits()
    {
        var (code, jobs) = await Plan("""
            version: 1
            defaults:
              needs: [node]
              on: [big-01]
              exclusive: true
            jobs:
              whole:
                command: npx jest
              unit:
                command: npx jest --ci
                exclusive: false
            """, dry: false);
        Assert.Equal(0, code);
        Assert.Equal(("Completed", true), (jobs["whole"].GetProperty("status").GetString(), jobs["whole"].GetProperty("exclusive").GetBoolean()));
        Assert.Equal("Completed", jobs["unit"].GetProperty("status").GetString());
        Assert.False(jobs["unit"].TryGetProperty("exclusive", out _));
    }

    // ---- the command line and lanq.yml ----

    [Fact]
    public void ExclusiveIsAFlagOfRunShardsAndDryRunOnly()
    {
        Assert.True(ParsedArgs.Parse(["run", "--exclusive", "--", "dotnet", "test"]).Exclusive);
        Assert.False(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).Exclusive);
        var p = ParsedArgs.Parse(["run", "--exclusive", "--shards", "2", "--dry-run", "--", "npx", "jest"]);
        Assert.True(p.Exclusive && p.DryRun && p.Shards is not null);
        Assert.Equal(["dotnet", "test"], ParsedArgs.Parse(["run", "--exclusive", "--", "dotnet", "test"]).Argv); // takes no value
        Assert.Contains("lanq health does not take --exclusive", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--exclusive"])).Message, StringComparison.Ordinal);
        var plan = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--exclusive"]));
        Assert.Contains("with --plan each job's --exclusive comes from lanq.yml", plan.Message, StringComparison.Ordinal);
        Assert.Contains("--exclusive", ParsedArgs.PlanFileOptions);
        Assert.True(Cli.RequestOf(p, ".", null).Exclusive);
    }

    [Fact]
    public void ARerunKeepsExclusive()
    {
        var p = ParsedArgs.Parse(["run", "--exclusive", "--dry-run", "--", "dotnet", "test"]);
        var again = SuggestedCommand.Rerun(p, dropDryRun: true, waitSeconds: 120)!;
        Assert.Contains("--exclusive", again.Argv);
        Assert.DoesNotContain("--dry-run", again.Argv);
        Assert.True(ParsedArgs.Parse([.. again.Argv.Skip(1)]).Exclusive);
    }

    [Fact]
    public void PlanExclusiveExpandsToTheFlagAndAJobsValueReplacesTheDefault()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults:
              exclusive: true
            jobs:
              whole:
                command: dotnet test
              unit:
                command: dotnet build
                exclusive: false
            """);
        Assert.Equal(["run", "--exclusive", "--", "dotnet", "test"], plan.Job("whole")!.RunArgs);
        Assert.True(plan.Job("whole")!.Args.Exclusive);
        Assert.Equal(["run", "--", "dotnet", "build"], plan.Job("unit")!.RunArgs);
        Assert.False(plan.Job("unit")!.Args.Exclusive);

        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("version: 1\njobs:\n  unit:\n    command: dotnet test\n    exclusive: maybe\n"));
        Assert.Contains("exclusive: must be true or false", ex.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheDryRunSaysItNeedsEverySlot()
    {
        var (code, json, stderr) = await Run(new PoolSsh(), "--exclusive", "--dry-run");
        Assert.Equal(0, code);
        Assert.True(json.GetProperty("exclusive").GetBoolean());
        Assert.Contains("--exclusive (needs every slot of the machine", stderr, StringComparison.Ordinal);
        Assert.Contains("--exclusive", json.GetProperty("nextSteps")[0].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    // ---- stale slots on a machine judged busy (0.9.0: the probe reports them, scheduling does not count them) ----

    [ShFact]
    public void TheProbeCountsStaleSlotsByTheReclaimsRulesSh() => TheProbeCountsStaleSlotsByTheReclaimsRules("linux");

    [PowerShellFact]
    public void TheProbeCountsStaleSlotsByTheReclaimsRulesPowerShell() => TheProbeCountsStaleSlotsByTheReclaimsRules("windows");

    /// <summary>
    /// slots_stale counts only what a claim would reclaim now: a deadline of 1 to 18 digits more than the grace in the past. A live slot, one
    /// past its deadline but inside the grace, one without a deadline and a malformed one are not stale; a reclaim's lock file is no slot.
    /// </summary>
    private void TheProbeCountsStaleSlotsByTheReclaimsRules(string shell)
    {
        WriteSlot(1, Lines("live-job", Now + 3600));
        WriteSlot(2, Lines("crashed-job", Stale));
        WriteSlot(3, Lines("slow-job", Now - 600)); // past its deadline, inside the grace: still in use
        WriteSlot(4, "job-from-an-older-lanq"); // no deadline
        WriteSlot(5, Lines("odd-job", "12x4")); // malformed
        WriteSlot(6, Lines("crashed-too", "0" + Stale.ToString(CultureInfo.InvariantCulture))); // a leading zero is still base 10
        WriteSlot(7, Lines("huge-job", new string('9', 30))); // 30 digits: no deadline at all
        File.WriteAllText(Slot(8) + ".reclaim", "x");

        var values = HealthProbe.Parse(Shells.Run(shell, RemoteScripts.Probe(Worker(shell, 4))).Output);

        Assert.Equal(("7", "2"), (values["slots_used"], values["slots_stale"]));
        ClearLocks();
        Assert.Equal("0", HealthProbe.Parse(Shells.Run(shell, RemoteScripts.Probe(Worker(shell, 4))).Output)["slots_stale"]);
    }

    [Fact]
    public void StaleSlotsDoNotCountAsUsedButSlotsInsideTheGraceDo()
    {
        var m = new Machine { Name = "big-01", Os = MachineOs.Linux, MaxJobs = 4, Capabilities = ["node"] };
        MachineHealth H(int used, int stale) => new("big-01", true, 0.1, 50, used, null, null) { SlotsStale = stale, MemTotalGb = 32 };
        var exclusive = new JobNeeds(["node"], null, false) { Exclusive = true };
        var normal = new JobNeeds(["node"], null, false);

        // A dead exclusive job left all four slots: both kinds of job can be placed (the claim reclaims what it passes).
        Assert.Equal((true, 1), Assert.Single(Scheduler.Assess([(m, H(4, 4))], exclusive)) is var e ? (e.Eligible, e.JobSlots) : default);
        Assert.Equal((true, 4, 4), Assert.Single(Scheduler.Assess([(m, H(4, 4))], normal)) is var n ? (n.Eligible, n.JobSlots, n.FreeSlots) : default);
        Assert.Equal(1, Scheduler.AutoCount(Scheduler.Assess([(m, H(4, 4))], exclusive), null, 64, exclusive: true).Count);

        // One dead normal job's slot no longer blocks an exclusive job forever; a live one beside it still does, and says so.
        Assert.True(Assert.Single(Scheduler.Assess([(m, H(1, 1))], exclusive)).Eligible);
        var busy = Assert.Single(Scheduler.Assess([(m, H(2, 1))], exclusive));
        Assert.Equal((Exclusion.Busy, "busy (slots 2/4, 1 stale: reclaimed by the next claim; --exclusive needs all 4 free)"), (busy.Reason, busy.Why));
        Assert.Equal(2, Assert.Single(Scheduler.Assess([(m, H(3, 1))], normal)).JobSlots);

        // Inside the grace a slot is not stale (the probe does not count it): still used.
        Assert.Equal("busy (slots 4/4)", Assert.Single(Scheduler.Assess([(m, H(4, 0))], normal)).Why);
        Assert.Equal(0, H(4, 0).SlotsStale);
    }

    [ShFact]
    public void BothKindsOfJobReclaimADeadExclusiveJobsSlotsSh() => BothKindsOfJobReclaimADeadExclusiveJobsSlots("linux");

    [PowerShellFact]
    public void BothKindsOfJobReclaimADeadExclusiveJobsSlotsPowerShell() => BothKindsOfJobReclaimADeadExclusiveJobsSlots("windows");

    /// <summary>The probe sees four stale slots, the scheduler places the job, and the real claim reclaims them (all of them, or the first).</summary>
    private void BothKindsOfJobReclaimADeadExclusiveJobsSlots(string shell)
    {
        foreach (var exclusive in new[] { true, false })
        {
            ClearLocks();
            foreach (var i in new[] { 1, 2, 3, 4 })
            {
                WriteSlot(i, Lines("dead-ex", Stale));
            }

            var worker = Worker(shell, 4);
            var values = HealthProbe.Parse(Shells.Run(shell, RemoteScripts.Probe(worker)).Output);
            var h = new MachineHealth("w", true, 0.1, 50, int.Parse(values["slots_used"], CultureInfo.InvariantCulture), null, null)
            {
                SlotsStale = int.Parse(values["slots_stale"], CultureInfo.InvariantCulture),
            };
            Assert.True(Assert.Single(Scheduler.Assess([(worker, h)], new JobNeeds([], null, false) { Exclusive = exclusive })).Eligible);

            var output = exclusive ? ClaimAll(shell, "next-job", 4) : ClaimOne(shell, "next-job", 4);
            Assert.Equal(exclusive ? "1,2,3,4" : "1", JobRunner.ParseSlot(output));
            Assert.Equal(exclusive ? ["next-job", "next-job", "next-job", "next-job"] : ["next-job", "dead-ex", "dead-ex", "dead-ex"], new[] { 1, 2, 3, 4 }.Select(Holder));
        }
    }

    /// <summary>End to end with fake ssh: a machine whose every slot is stale takes an exclusive job; health shows the stale slots.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AMachineWithOnlyStaleSlotsTakesAnExclusiveJobAndHealthShowsThem()
    {
        var stale = 4;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => cmd.Contains("LANQ_EXIT=", StringComparison.Ordinal) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0")
                : cmd.Contains("SLOTS=", StringComparison.Ordinal) ? new SshResult(0, "SLOTS=1,2,3,4")
                : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, $"load=0.1\ndisk_free_gb=50\nslots_used={(m.Name == "big-01" ? 4 : 1)}\nslots_stale={(m.Name == "big-01" ? stale : 0)}\ntool=ok")
                : new SshResult(0, ""),
        };
        var config = ConfigLoader.Parse(ShardTests.PoolYaml);
        async Task<JsonElement> RunOnBig()
        {
            var o = new StringWriter();
            await Cli.Run(
                ParsedArgs.Parse(["run", "--json", "--exclusive", "--on", "big-01", "--needs", "node", "--", "npx", "jest"]), config, ssh, new RunnerTests.FakePayloads(""),
                new HealthProbe(ssh, TimeProvider.System), Log, _root, Repo, o, new StringWriter(), CancellationToken.None);
            using var doc = JsonDocument.Parse(o.ToString());
            return doc.RootElement.Clone();
        }

        var placed = await RunOnBig();
        Assert.Equal("Completed", placed.GetProperty("status").GetString());
        Assert.Equal("big-01", placed.GetProperty("machine").GetString());
        Assert.Contains(ssh.Calls, c => c.Machine == "big-01" && c.Command.Contains("SLOTS=", StringComparison.Ordinal)); // the claim that reclaims

        var text = new StringWriter();
        await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), text, new StringWriter(), CancellationToken.None);
        Assert.Contains("slots free 4/4 (4 stale: reclaimed by the next claim)", text.ToString(), StringComparison.Ordinal);
        var json = new StringWriter();
        await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), json, new StringWriter(), CancellationToken.None, json: true);
        using (var doc = JsonDocument.Parse(json.ToString()))
        {
            var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.Clone());
            Assert.Equal((4, 4, 4), (rows["big-01"].GetProperty("slotsUsed").GetInt32(), rows["big-01"].GetProperty("slotsStale").GetInt32(), rows["big-01"].GetProperty("freeSlots").GetInt32()));
            Assert.False(rows["tiny-01"].TryGetProperty("slotsStale", out _));
        }

        // The same machine with the slots inside their grace (not stale): busy, and the job is never even claimed there.
        stale = 0;
        ssh.Calls.Clear();
        var busy = await RunOnBig();
        Assert.Equal("NoCapacity", busy.GetProperty("status").GetString());
        Assert.Equal("busy (slots 4/4; --exclusive needs all 4 free)", busy.GetProperty("excluded")[0].GetProperty("why").GetString());
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOTS=", StringComparison.Ordinal));
    }

    // ---- another lanq with a higher max_jobs for the same work_root ----

    [ShFact]
    public void ALiveSlotAboveMaxJobsMakesTheExclusiveClaimBackOutSh() => ALiveSlotAboveMaxJobsMakesTheExclusiveClaimBackOut("linux");

    [PowerShellFact]
    public void ALiveSlotAboveMaxJobsMakesTheExclusiveClaimBackOutPowerShell() => ALiveSlotAboveMaxJobsMakesTheExclusiveClaimBackOut("windows");

    /// <summary>
    /// Another developer's lanq has max_jobs 6 for this work_root and runs a job in slot-5: "exclusive" with max_jobs 3 must not take the
    /// machine. A stale slot-5 (past its grace) does not block; an in-grace one does; a reclaim's lock is no slot.
    /// </summary>
    private void ALiveSlotAboveMaxJobsMakesTheExclusiveClaimBackOut(string shell)
    {
        foreach (var (content, blocks) in new[] { (Lines("other-dev", Now + 3600), true), (Lines("other-dev", Now - 600), true), (Lines("other-dev", Stale), false), ("no-deadline", true) })
        {
            ClearLocks();
            WriteSlot(5, content);
            File.WriteAllText(Slot(7) + ".reclaim", "x");

            var output = ClaimAll(shell, "ex-job", 3);

            if (blocks)
            {
                Assert.Contains("SLOT=FULL", output, StringComparison.Ordinal);
                Assert.Null(JobRunner.ParseSlot(output));
                Assert.Equal(["slot-5", "slot-7.reclaim"], LockFiles()); // its own three slots backed out
            }
            else
            {
                Assert.Equal("1,2,3", JobRunner.ParseSlot(output));
            }
        }

        // slot-05 is slot 5 too; slot-3 within max_jobs is an ordinary taken slot.
        ClearLocks();
        WriteSlot(5, "x");
        File.Move(Slot(5), Path.Combine(_root, "locks", "slot-05"));
        Assert.Null(JobRunner.ParseSlot(ClaimAll(shell, "ex-job", 3)));
        Assert.Equal(["slot-05"], LockFiles());
    }

    /// <summary>The scheduler side of a higher max_jobs elsewhere: the probe counts slot-5 in slots_used, so an exclusive job sees the machine busy.</summary>
    [Fact]
    public void ASlotAboveMaxJobsMakesTheMachineBusyForAnExclusiveJob()
    {
        var m = new Machine { Name = "big-01", Os = MachineOs.Linux, MaxJobs = 3, Capabilities = ["node"] };
        var h = new MachineHealth("big-01", true, 0.1, 50, 1, null, null);
        var a = Assert.Single(Scheduler.Assess([(m, h)], new JobNeeds(["node"], null, false) { Exclusive = true }));
        Assert.Equal((Exclusion.Busy, "busy (slots 1/3; --exclusive needs all 3 free)"), (a.Reason, a.Why));
    }
}
