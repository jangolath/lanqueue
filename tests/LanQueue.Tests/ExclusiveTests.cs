using System.Globalization;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Plan;
using LanQueue.Scheduling;
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

    /// <summary>Two exclusive claims at once on one machine, 50 times: never both win; the winner holds every slot, a loser none.</summary>
    [LinuxFact]
    public void TwoExclusiveClaimsNeverBothWin() => TwoExclusiveClaims("linux", 50);

    [PowerShellFact]
    public void TwoExclusiveClaimsNeverBothWinPowerShell() => TwoExclusiveClaims("windows", 50);

    private void TwoExclusiveClaims(string shell, int iterations)
    {
        var won = 0;
        for (var n = 0; n < iterations; n++)
        {
            ClearLocks();
            var results = new[] { "ex-a", "ex-b" }.AsParallel().WithDegreeOfParallelism(2)
                .Select(job => (Job: job, Slots: JobRunner.ParseSlot(ClaimAll(shell, job, 3))))
                .ToList();

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

    /// <summary>An exclusive claim against a normal claim on a max_jobs 2 machine: never both hold slots, and nothing is left half-claimed.</summary>
    [LinuxFact]
    public void AnExclusiveAndANormalClaimNeverBothHoldSlots() => MixedRace("linux", 30);

    [PowerShellFact]
    public void AnExclusiveAndANormalClaimNeverBothHoldSlotsPowerShell() => MixedRace("windows", 20);

    private void MixedRace(string shell, int iterations)
    {
        for (var n = 0; n < iterations; n++)
        {
            ClearLocks();
            var results = new[] { "ex", "normal" }.AsParallel().WithDegreeOfParallelism(2)
                .Select(job => (Job: job, Slots: JobRunner.ParseSlot(job == "ex" ? ClaimAll(shell, job, 2) : ClaimOne(shell, job, 2))))
                .ToDictionary(r => r.Job, r => r.Slots);

            Assert.False(results["ex"] is not null && results["normal"] is not null, $"iteration {n}: an exclusive and a normal claim both hold slots");
            if (results["ex"] is not null)
            {
                Assert.Equal(["ex", "ex"], new[] { 1, 2 }.Select(Holder));
            }
            else if (results["normal"] is { } slot)
            {
                Assert.Equal(["slot-" + slot], LockFiles()); // the exclusive claim backed out completely
                Assert.Equal("normal", Holder(int.Parse(slot, CultureInfo.InvariantCulture)));
            }
        }
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

    /// <summary>A normal job's claim and cleanup are what they were before 0.9: the exclusive path adds a script, it changes none.</summary>
    [Fact]
    public void ANormalJobsClaimAndCleanupAreUnchanged()
    {
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            Assert.DoesNotContain("SLOTS=", RemoteScripts.AcquireSlot(m, "j", TimeSpan.FromMinutes(5)), StringComparison.Ordinal);
            var cleanup = RemoteScripts.Cleanup(m, "j", "2", T.RepoId);
            Assert.DoesNotContain("for n in", cleanup, StringComparison.Ordinal);
            Assert.DoesNotContain("foreach($n", cleanup, StringComparison.Ordinal);
        }

        Assert.Contains("rm -f \"$R/incoming/$ID.bundle\" \"$R/locks/slot-2\"", RemoteScripts.Cleanup(T.Linux(), "j", "2", T.RepoId), StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Force -ErrorAction SilentlyContinue \"$root/locks/slot-2\"", RemoteScripts.Cleanup(T.Windows(), "j", "2", T.RepoId), StringComparison.Ordinal);
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
}
