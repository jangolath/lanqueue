using System.Collections.Concurrent;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// A payload builder whose builds report how many run at once. The first build of a kind waits until a second one starts, so builds that
/// are allowed to overlap always do (the wait ends early when one starts, and after a bounded time when none can, which is the passing
/// case; the outcome never depends on how long it waited).
/// </summary>
internal sealed class OverlapPayloads : IPayloadBuilder
{
    private readonly RunnerTests.FakePayloads _inner = new("");
    private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _builds;

    public int MaxActive { get; private set; }

    /// <summary>Throws from the first bundle build (a failed git bundle create), then builds normally.</summary>
    public bool FailFirstBundle { get; set; }

    public ConcurrentQueue<string?> BundleBases { get; } = new();

    public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) =>
        _inner.BuildAsync(jobId, repoDir, outputDir, kinds, ct);

    public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct)
    {
        BundleBases.Enqueue(baseSha);
        return Overlapping(() => _inner.BuildBundleAsync(jobId, repoDir, outputDir, sha, baseSha, ct), bundle: true);
    }

    public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) =>
        Overlapping(() => _inner.BuildArchiveAsync(jobId, repoDir, outputDir, sha, ct), bundle: false);

    private async Task<T> Overlapping<T>(Func<Task<T>> build, bool bundle)
    {
        var now = Interlocked.Increment(ref _active);
        lock (_second)
        {
            MaxActive = Math.Max(MaxActive, now);
        }

        try
        {
            if (Interlocked.Increment(ref _builds) == 1)
            {
                try
                {
                    await _second.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    // no second build could start meanwhile: they are serialised
                }

                if (bundle && FailFirstBundle)
                {
                    throw new InvalidOperationException("git bundle create failed: cannot lock ref 'refs/lanq/x'");
                }
            }
            else
            {
                _second.TrySetResult();
            }

            return await build();
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

/// <summary>Fixes from the shard-0.5.1 review: overlapping builds, --wait, --mem per shard, safe retries, stray exceptions, Ctrl+C after the end.</summary>
public sealed class ShardReviewTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private const string OneBox = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npx jest"]
          allowed_directories: ["."]
        machines:
          - name: solo-01
            os: linux
            ssh: user@10.20.30.31
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 1
        """;

    private const string Archives = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npx jest"]
          allowed_directories: ["."]
        machines:
          - name: arc-01
            os: linux
            ssh: user@10.20.30.41
            work_root: /srv/lanq
            transfer: archive
            capabilities: [node]
            max_jobs: 2
          - name: arc-02
            os: linux
            ssh: user@10.20.30.42
            work_root: /srv/lanq
            transfer: archive
            capabilities: [node]
            max_jobs: 2
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-shardfix-" + Guid.NewGuid().ToString("N"));

    private readonly ConcurrentQueue<string> _output = new();

    public ShardReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string PoolYaml = ShardTests.PoolYaml;

    private JobRunner Runner(PoolSsh ssh, IPayloadBuilder payloads, string? yaml = null, BaseStore? bases = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(ConfigLoader.Parse(yaml ?? PoolYaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"),
            _output.Enqueue, delay: delay ?? ((_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)), bases: bases ?? new BaseStore(null));

    private JobRequest Jest(string needs = "node", TimeSpan? wait = null) => new([needs], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, wait ?? TimeSpan.Zero);

    /// <summary>
    /// A full bundle for one machine and an incremental one for another are needed at the same moment: they are built one after the other,
    /// never at once (each build creates and deletes the same local ref).
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task BundleBuildsOfOneRunNeverOverlap()
    {
        var bases = new BaseStore(null);
        bases.Set("big-01", T.RepoId, new string('1', 40));
        var ssh = new PoolSsh();
        var payloads = new OverlapPayloads();

        var r = await Runner(ssh, payloads, bases: bases).RunShardsAsync(Jest(), new ShardOptions(2), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(2, payloads.BundleBases.Distinct().Count()); // a full one (tiny-01) and an incremental one (big-01)
        Assert.Equal(1, payloads.MaxActive);
    }

    /// <summary>A bundle build that failed is not remembered for ever: the next shard that needs it builds it again (once).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AFailedBundleBuildIsTriedOnceMore()
    {
        var ssh = new PoolSsh();
        var payloads = new OverlapPayloads { FailFirstBundle = true };

        var r = await Runner(ssh, payloads, OneBox).RunShardsAsync(Jest(), new ShardOptions(2), CancellationToken.None);

        Assert.Equal(["InfraError", "Completed"], r.Shards.Select(s => s.Status));
        Assert.Contains("Could not build the bundle", r.Shards[0].Message, StringComparison.Ordinal);
        Assert.Equal(2, payloads.BundleBases.Count);
    }

    /// <summary>Four shards on two archive machines at once: one archive, built once.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TheArchiveIsBuiltOnceForConcurrentShards()
    {
        var ssh = new PoolSsh();
        var started = new Countdown(4);
        ssh.OnExecute = async (e, ct) =>
        {
            started.Signal();
            await started.Reached.WaitAsync(ct);
            return e.Exit(0);
        };
        var payloads = new RunnerTests.FakePayloads("");

        var r = await Runner(ssh, payloads, Archives).RunShardsAsync(Jest(), new ShardOptions(4), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(1, payloads.ArchivesBuilt);
        Assert.Equal(2, ssh.Peak["arc-01"]);
        Assert.Equal(2, ssh.Peak["arc-02"]);
    }

    /// <summary>Two shards on one machine: their uploads and prepares run one at a time there (the per-machine cache gate).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task UploadAndPrepareRunOneAtATimePerMachine()
    {
        var ssh = new PoolSsh();
        var active = 0;
        var maxActive = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        ssh.OnUpload = async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            if (Interlocked.Increment(ref uploads) == 1)
            {
                try
                {
                    await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    // the second upload could not start: serialised
                }
            }
            else
            {
                second.TrySetResult();
            }

            Interlocked.Decrement(ref active);
            return new SshResult(0, "");
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest("docker"), new ShardOptions(2), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.All(r.Shards, s => Assert.Equal("big-01", s.Machine));
        Assert.Equal(1, maxActive);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheJobLogTakesConcurrentAppends()
    {
        var log = new JobLog(Path.Combine(_dir, "log"));
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            log.Append(new JobRecord("j" + i, "m", "npx jest", DateTimeOffset.UnixEpoch, 1, 0, "Completed")))));
        Assert.Equal(64, log.ReadAll().Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every slot claim fails although the probe says a slot is free: the run still ends NoCapacity once --wait has passed without any shard
    /// holding a slot (before the fix, each failed attempt restarted the wait and the run never ended).
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task SlotClaimsThatAlwaysFailEndNoCapacityAfterWait()
    {
        var ssh = new PoolSsh { RefuseSlot = _ => true };
        var runner = Runner(ssh, new RunnerTests.FakePayloads(""), delay: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(10), ct));

        var r = await runner.RunShardsAsync(Jest(wait: TimeSpan.FromMilliseconds(300)), new ShardOptions(2), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75), (r.Status, r.ExitCode));
        Assert.All(r.Shards, s => Assert.Equal("NoCapacity", s.Status));
        Assert.Empty(ssh.Executes);
    }

    /// <summary>A slot claim that fails once sends the shard back to wait (the backoff), then it runs; it was never counted as an attempt.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AFailedSlotClaimBacksOffAndRetries()
    {
        var refused = 0;
        var ssh = new PoolSsh { RefuseSlot = _ => Interlocked.Increment(ref refused) == 1 };
        var delays = 0;
        var runner = Runner(ssh, new RunnerTests.FakePayloads(""), OneBox, delay: async (_, _) =>
        {
            Interlocked.Increment(ref delays);
            await Task.Yield();
        });

        var r = await runner.RunShardsAsync(Jest(wait: TimeSpan.FromMinutes(5)), new ShardOptions(1), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(1, r.Shards[0].Attempts);
        Assert.True(delays >= 1);
        Assert.Single(ssh.Executes);
    }

    /// <summary>--mem is per shard: a 4-slot box with 5 GB free and --mem 4 runs one shard at a time, and auto counts it as one.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task MemIsCheckedPerShardNotPerMachine()
    {
        var ssh = new PoolSsh { ProbeExtra = m => m.Name == "big-01" ? "mem_free_gb=5" : "mem_free_gb=3" };
        var runner = Runner(ssh, new RunnerTests.FakePayloads(""));

        var r = await runner.RunShardsAsync(Jest() with { MinMemGb = 4 }, new ShardOptions(3), CancellationToken.None);
        Assert.Equal("Completed", r.Status);
        Assert.All(r.Shards, s => Assert.Equal("big-01", s.Machine));
        Assert.Equal(1, ssh.Peak["big-01"]);

        var auto = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest() with { MinMemGb = 4 }, new ShardOptions(null), CancellationToken.None);
        Assert.Equal(1, auto.Count);
        Assert.Contains("[lanq] --shards auto: 1 shard(s) (free slots: big-01 1)", _output);

        ssh.ProbeExtra = m => m.Name == "big-01" ? "mem_free_gb=12.5" : "mem_free_gb=3";
        Assert.Equal(3, (await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest() with { MinMemGb = 4 }, new ShardOptions(null), CancellationToken.None)).Count);
    }

    /// <summary>A machine that stops answering during the upload or the prepare step: the job never started, so the shard is re-run.</summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData("upload")]
    [InlineData("prepare")]
    public async Task ALostConnectionBeforeTheJobStartedIsRetried(string step)
    {
        var lost = 0;
        var ssh = new PoolSsh();
        var gone = new SshResult(255, "ssh: connect to host 10.20.30.31 port 22: Connection timed out");
        if (step == "upload")
        {
            ssh.OnUpload = (_, _) => Task.FromResult(Interlocked.Increment(ref lost) == 1 ? gone : new SshResult(0, ""));
        }
        else
        {
            ssh.OnPrepare = _ => Interlocked.Increment(ref lost) == 1 ? gone : null;
        }

        var r = await Runner(ssh, new RunnerTests.FakePayloads(""), OneBox).RunShardsAsync(Jest(), new ShardOptions(1, Retries: 1), CancellationToken.None);

        Assert.Equal(("Completed", 0), (r.Status, r.ExitCode));
        Assert.Equal(2, r.Shards[0].Attempts);
        Assert.Single(ssh.Executes);
    }

    /// <summary>
    /// The connection dropped while the job ran and its cleanup did not finish: the job may still run there, so the re-run goes to another
    /// machine, never the same one.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AJobThatMayStillRunIsRetriedOnAnotherMachine()
    {
        var ssh = new PoolSsh
        {
            OnExecute = (e, _) => Task.FromResult(e.Machine.Name == "big-01" ? new SshResult(255, "Connection to 10.20.30.12 closed by remote host.") : e.Exit(0)),
            OnCleanup = m => m.Name == "big-01" ? new SshResult(255, "ssh: connect to host 10.20.30.12 port 22: No route to host") : new SshResult(0, ""),
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest(), new ShardOptions(1, Retries: 2), CancellationToken.None);

        Assert.Equal(("Completed", 0), (r.Status, r.ExitCode));
        Assert.Equal("tiny-01", r.Shards[0].Machine);
        Assert.Equal(["big-01", "tiny-01"], ssh.Executes.Select(e => e.Machine.Name));
        Assert.Contains(_output, l => l.Contains("may still be running on big-01", StringComparison.Ordinal));
    }

    /// <summary>The same, with no other machine able to take the shard: it is not re-run, and the result says why.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AJobThatMayStillRunIsNotRetriedWhenNoOtherMachineCanTakeIt()
    {
        var ssh = new PoolSsh
        {
            OnExecute = (_, _) => Task.FromResult(new SshResult(255, "Connection to 10.20.30.12 closed by remote host.")),
            OnCleanup = _ => new SshResult(255, "ssh: connect to host 10.20.30.12 port 22: No route to host"),
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest("docker"), new ShardOptions(1, Retries: 3), CancellationToken.None);

        Assert.Equal(("InfraError", 70), (r.Status, r.ExitCode));
        Assert.Single(ssh.Executes);
        Assert.Contains("may still be running on big-01", r.Shards[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shard that must avoid a machine (its job may still run there) and finds no other slot ends NoCapacity, and the message says why
    /// each machine was passed over: the avoided one by name, the other with lanq run's own per-machine reason.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task NoCapacityAfterARetrySaysWhichMachineWasAvoided()
    {
        var ssh = new PoolSsh
        {
            OnExecute = (e, _) => Task.FromResult(e.Machine.Name == "big-01" ? new SshResult(255, "Connection to 10.20.30.12 closed by remote host.") : e.Exit(0)),
            OnCleanup = m => m.Name == "big-01" ? new SshResult(255, "ssh: connect to host 10.20.30.12 port 22: No route to host") : new SshResult(0, ""),
            RefuseSlot = m => m.Name == "tiny-01",
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest(), new ShardOptions(1, Retries: 2), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75), (r.Status, r.ExitCode));
        var message = r.Shards[0].Message;
        Assert.Contains("big-01: avoided", message, StringComparison.Ordinal);
        Assert.Contains("tiny-01: no free slot when the job tried to start", message, StringComparison.Ordinal);
        Assert.DoesNotContain("no match / full", message, StringComparison.Ordinal);
    }

    /// <summary>A sharded run's NoCapacity message names the capability each machine lacks, as a single job's does.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardNoCapacitySaysWhatEachMachineLacks()
    {
        var ssh = new PoolSsh();
        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest("gpu"), new ShardOptions(2), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75), (r.Status, r.ExitCode));
        Assert.All(r.Shards, s => Assert.Contains("big-01: lacks gpu", s.Message, StringComparison.Ordinal));
        Assert.All(r.Shards, s => Assert.DoesNotContain("no match / full", s.Message, StringComparison.Ordinal));
    }

    /// <summary>No machine has the capability: every shard ends NoCapacity at once (no probe, no wait), and the run is not retryable.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardNoCapacityIsNotRetryableWhenNoMachineHasTheCapability()
    {
        var ssh = new PoolSsh();
        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest("gpu", wait: TimeSpan.FromHours(1)), new ShardOptions(3), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75, 3), (r.Status, r.ExitCode, r.Count));
        Assert.False(r.Retryable);
        Assert.Equal(["tiny-01", "big-01"], r.Excluded!.Select(x => x.Machine));
        Assert.All(r.Excluded!, x => Assert.Equal(("missing_capability", true), (x.Reason, x.Permanent)));
        Assert.All(r.Shards, s => Assert.Equal(("NoCapacity", 75), (s.Status, s.ExitCode)));
        Assert.All(r.Shards, s => Assert.Contains("no machine can ever take this job as configured", s.Message, StringComparison.Ordinal));
        Assert.Empty(ssh.Calls);
    }

    /// <summary>--shards auto with a capability no machine has ends the same way, before the auto probe.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardsAutoWithNoCapableMachineEndsBeforeProbing()
    {
        var ssh = new PoolSsh();
        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest("gpu", wait: TimeSpan.FromHours(1)), new ShardOptions(null), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75, 1), (r.Status, r.ExitCode, r.Count));
        Assert.False(r.Retryable);
        Assert.Empty(ssh.Calls);
    }

    /// <summary>--mem above every machine's total memory: the waiting shards end after one probe (the delay here never ends), not after --wait.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardsEndAfterOneProbeWhenNoMachineHasEnoughMemoryForGood()
    {
        var ssh = new PoolSsh { ProbeExtra = _ => "mem_free_gb=3\nmem_total_gb=7.6" };
        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest(wait: TimeSpan.FromHours(1)) with { MinMemGb = 16 }, new ShardOptions(2), CancellationToken.None);

        Assert.Equal(("NoCapacity", 75), (r.Status, r.ExitCode));
        Assert.False(r.Retryable);
        Assert.All(r.Excluded!, x => Assert.Equal("mem_exceeds_total", x.Reason));
        Assert.Empty(ssh.Executes);
        Assert.Equal(2, ssh.Calls.Count); // one probe of each machine
    }

    /// <summary>An exception nobody expected (here an OperationCanceledException that no cancel caused) ends that shard only.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AStrayExceptionEndsOnlyItsOwnShard()
    {
        var ssh = new PoolSsh { OnExecute = (e, _) => e.Shard == 2 ? throw new OperationCanceledException("stray") : Task.FromResult(e.Exit(0)) };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest(), new ShardOptions(3), CancellationToken.None);

        Assert.Equal(("InfraError", 70), (r.Status, r.ExitCode));
        Assert.Equal(["Completed", "InfraError", "Completed"], r.Shards.Select(s => s.Status));
        Assert.Contains("OperationCanceledException", r.Shards[1].Message, StringComparison.Ordinal);
        Assert.Equal(0, ssh.Used("tiny-01") + ssh.Used("big-01")); // every slot was cleaned up
    }

    /// <summary>Ctrl+C that arrives after every shard has finished does not turn the real result into Cancelled.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCAfterEveryShardFinishedKeepsTheRealResult()
    {
        using var cts = new CancellationTokenSource();
        var ssh = new PoolSsh
        {
            OnExecute = async (e, _) =>
            {
                await cts.CancelAsync();
                return e.Exit(0);
            },
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads(""), OneBox).RunShardsAsync(Jest(), new ShardOptions(1), cts.Token);

        Assert.Equal(("Completed", 0), (r.Status, r.ExitCode));
    }

    /// <summary>Without getconf and nproc the Linux probe counts /proc/cpuinfo; when nothing can tell, it reports no core count at all (never 1).</summary>
    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheLinuxProbeFallsBackForCoresAndNeverGuesses()
    {
        var root = Path.Combine(_dir, "worker");
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        Directory.CreateDirectory(bin);
        foreach (var tool in new[] { "getconf", "nproc" })
        {
            File.WriteAllText(Path.Combine(bin, tool), "#!/bin/sh\nexit 1\n");
            File.SetUnixFileMode(Path.Combine(bin, tool), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var m = T.Linux();
        m.WorkRoot = root;
        var path = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        var withCpuinfo = HealthProbe.Parse(Shells.Exec("sh", ["-c", RemoteScripts.Probe(m)], new Dictionary<string, string> { ["PATH"] = path }).Output);
        var processors = File.ReadLines("/proc/cpuinfo").Count(l => l.StartsWith("processor", StringComparison.Ordinal));
        Assert.Equal(processors.ToString(System.Globalization.CultureInfo.InvariantCulture), withCpuinfo["cores"]);

        File.WriteAllText(Path.Combine(bin, "grep"), "#!/bin/sh\nexit 2\n");
        File.SetUnixFileMode(Path.Combine(bin, "grep"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var nothing = HealthProbe.Parse(Shells.Exec("sh", ["-c", RemoteScripts.Probe(m)], new Dictionary<string, string> { ["PATH"] = path }).Output);
        Assert.False(nothing.ContainsKey("cores"));
        Assert.True(nothing.ContainsKey("load"));
    }

    private const string ArchiveAndGit = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npx jest"]
          allowed_directories: ["."]
        machines:
          - name: arc-01
            os: linux
            ssh: user@10.20.30.41
            work_root: /srv/lanq
            transfer: archive
            capabilities: [node]
            max_jobs: 1
          - name: git-01
            os: linux
            ssh: user@10.20.30.51
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 1
        """;

    /// <summary>The first archive build fails, the next one works: a later NoCapacity message carries no stale "Archive build failed" note.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ARetriedArchiveBuildThatWorkedLeavesNoStaleFailureNote()
    {
        var ssh = new PoolSsh();
        ssh.RefuseSlot = m => m.Name == "git-01" || (m.Name == "arc-01" && ssh.Peak.ContainsKey("arc-01"));
        var payloads = new FailOnceArchive();
        var runner = Runner(ssh, payloads, ArchiveAndGit, delay: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(10), ct));

        // The failed build, the retry and the run must all fit inside --wait; 300 ms was too tight on a slow Windows CI runner.
        var r = await runner.RunShardsAsync(Jest(wait: TimeSpan.FromSeconds(3)), new ShardOptions(2), CancellationToken.None);

        Assert.Equal(2, payloads.ArchiveBuilds);
        Assert.Contains(r.Shards, s => s.Status == "Completed" && s.Machine == "arc-01");
        var waiting = Assert.Single(r.Shards, s => s.Status == "NoCapacity");
        Assert.DoesNotContain("Archive build failed", waiting.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// --mem 4 on a box with 9 GB free: two shards placed there are still uploading (no memory used yet) when the next placement round looks
    /// (the other machine has finished its shard and is now short of memory). That round must count them, so the box never holds a third.
    /// The uploads are released when a round places nothing (the wait timer) or, on a scheduler that over-places, at the third slot claim.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task MemCountsPlacedShardsThatAreNotRunningYet()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tinyRan = false;
        var uploadingToBig = 0;
        var ssh = new PoolSsh
        {
            OnUpload = async (m, ct) =>
            {
                if (m.Name == "big-01")
                {
                    Interlocked.Increment(ref uploadingToBig);
                    try
                    {
                        await release.Task.WaitAsync(ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref uploadingToBig);
                    }
                }

                return new SshResult(0, "");
            },
        };

        // big-01 has 9 GB free less 4 GB for each slot taken there whose upload is done: a shard still uploading uses no memory yet (so the
        // probe cannot see it, and only counting it as placed keeps a third shard off), one past its upload uses its 4 GB until its slot is
        // given back. A constant 9 GB would let a correct scheduler place a third shard once the first two run, and the peak would read 3.
        ssh.ProbeExtra = m => m.Name == "big-01"
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"mem_free_gb={9 - (4 * (ssh.Used("big-01") - Volatile.Read(ref uploadingToBig)))}")
            : Volatile.Read(ref tinyRan) ? "mem_free_gb=0.5" : "mem_free_gb=4.5";
        ssh.RefuseSlot = m =>
        {
            if (m.Name == "big-01" && ssh.Peak.TryGetValue("big-01", out var peak) && peak >= 2)
            {
                release.TrySetResult(); // a third claim on big-01 while two are still uploading: over-placed
            }

            return false;
        };
        ssh.OnExecute = (e, _) =>
        {
            if (e.Machine.Name == "tiny-01")
            {
                Volatile.Write(ref tinyRan, true);
            }

            return Task.FromResult(e.Exit(0));
        };
        // Release the uploads only once both big-01 shards hold their slot: releasing at the first empty round could let shard 1 finish
        // before shard 3 has claimed its slot (a fast tiny-01 makes that round early), and the peak would read 1 on a correct scheduler.
        // The cap ends the wait on a scheduler that under-places, so it fails on the assertion below instead of hanging.
        var emptyRounds = 0;
        var runner = Runner(ssh, new RunnerTests.FakePayloads(""), delay: (_, ct) =>
        {
            if ((ssh.Peak.TryGetValue("big-01", out var placed) && placed >= 2) || Interlocked.Increment(ref emptyRounds) > 500)
            {
                release.TrySetResult();
            }

            return Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        });

        var r = await runner.RunShardsAsync(Jest(wait: TimeSpan.FromMinutes(5)) with { MinMemGb = 4 }, new ShardOptions(4), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.Equal(2, ssh.Peak["big-01"]);
    }

    /// <summary>
    /// Two shards on big-01 start while a probe is out: the machine answered before they ran, so its free memory (9 GB) does not show them.
    /// They still count as placed but not running for that look, so no third shard goes there.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardsThatStartDuringAProbeStillCountAsNotRunning()
    {
        var uploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothRunning = new Countdown(2);
        var tinyRan = false;
        var runningOnBig = 0;
        var bigProbes = 0;
        var stale = false;
        var ssh = new PoolSsh
        {
            // big-01's free memory drops by 4 GB for each job running there.
            ProbeExtra = m => m.Name == "big-01"
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mem_free_gb={9 - (4 * Volatile.Read(ref runningOnBig))}")
                : Volatile.Read(ref tinyRan) ? "mem_free_gb=0.5" : "mem_free_gb=4.5",
            OnUpload = async (m, ct) =>
            {
                if (m.Name == "big-01")
                {
                    await uploads.Task.WaitAsync(ct);
                }

                return new SshResult(0, "");
            },
            OnExecute = async (e, ct) =>
            {
                if (e.Machine.Name == "tiny-01")
                {
                    Volatile.Write(ref tinyRan, true);
                    return e.Exit(0);
                }

                Interlocked.Increment(ref runningOnBig);
                bothRunning.Signal();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref runningOnBig);
                return e.Exit(0);
            },
            AfterProbe = async (m, r, ct) =>
            {
                if (m.Name == "big-01" && Interlocked.Increment(ref bigProbes) == 2)
                {
                    // The look after tiny-01's shard ended was taken while both big-01 shards were still uploading: answer it only once both run.
                    uploads.TrySetResult();
                    await bothRunning.Reached.WaitAsync(ct);
                    Volatile.Write(ref stale, true);
                }

                return r;
            },
        };
        ssh.RefuseSlot = m =>
        {
            if (m.Name == "big-01" && ssh.Used("big-01") >= 2)
            {
                release.TrySetResult(); // a third claim on big-01 while two run: over-placed (end the wait so the assertion below reports it)
            }

            return false;
        };
        var runner = Runner(ssh, new RunnerTests.FakePayloads(""), delay: (_, ct) =>
        {
            if (Volatile.Read(ref stale))
            {
                release.TrySetResult(); // the stale look placed nothing on big-01: let its two shards finish
            }

            return Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        });

        var r = await runner.RunShardsAsync(Jest(wait: TimeSpan.FromMinutes(5)) with { MinMemGb = 4 }, new ShardOptions(4), CancellationToken.None);

        Assert.Equal("Completed", r.Status);
        Assert.True(Volatile.Read(ref stale));
        Assert.Equal(2, ssh.Peak["big-01"]);
    }

    /// <summary>
    /// The other machine has the capabilities but is held (paused): placement could not use it, so a shard that may still run on its first
    /// machine is not re-run, and says so.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ARetryOnlyGoesToAMachinePlacementCouldUse()
    {
        var ssh = new PoolSsh
        {
            ProbeExtra = m => m.Name == "tiny-01" ? "mem_free_gb=3\npaused=1" : "mem_free_gb=3",
            OnExecute = (_, _) => Task.FromResult(new SshResult(255, "Connection to 10.20.30.12 closed by remote host.")),
            OnCleanup = _ => new SshResult(255, "ssh: connect to host 10.20.30.12 port 22: No route to host"),
        };

        var r = await Runner(ssh, new RunnerTests.FakePayloads("")).RunShardsAsync(Jest(), new ShardOptions(1, Retries: 2), CancellationToken.None);

        Assert.Equal(("InfraError", 70), (r.Status, r.ExitCode));
        Assert.Contains("Not re-run", r.Shards[0].Message, StringComparison.Ordinal);
        Assert.Single(ssh.Executes);
    }

    /// <summary>getconf answering something that is not a positive whole number ("undefined") counts as failed: nproc is asked next.</summary>
    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AnUndefinedGetconfFallsThroughToNproc()
    {
        var root = Path.Combine(_dir, "worker2");
        var bin = Path.Combine(_dir, "bin2");
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        Directory.CreateDirectory(bin);
        foreach (var (tool, body) in new[] { ("getconf", "echo undefined"), ("nproc", "echo 3") })
        {
            File.WriteAllText(Path.Combine(bin, tool), "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(Path.Combine(bin, tool), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var m = T.Linux();
        m.WorkRoot = root;
        var values = HealthProbe.Parse(Shells.Exec("sh", ["-c", RemoteScripts.Probe(m)], new Dictionary<string, string> { ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH") }).Output);
        Assert.Equal("3", values["cores"]);
    }
}

/// <summary>The first archive build fails (a disk error), every later one works.</summary>
internal sealed class FailOnceArchive : IPayloadBuilder
{
    private readonly RunnerTests.FakePayloads _inner = new("");
    private int _archives;

    public int ArchiveBuilds => _archives;

    public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) =>
        _inner.BuildAsync(jobId, repoDir, outputDir, kinds, ct);

    public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
        _inner.BuildBundleAsync(jobId, repoDir, outputDir, sha, baseSha, ct);

    public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) =>
        Interlocked.Increment(ref _archives) == 1
            ? throw new IOException("disk said no")
            : _inner.BuildArchiveAsync(jobId, repoDir, outputDir, sha, ct);
}
