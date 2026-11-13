using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Plan;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq run --baseline review fixes (0.9.0): the rerun waits for the machine that failed, says when it ran elsewhere, the wait wording, Ctrl+C
/// before a rerun, the recorded incremental base, the plan keys, and the remaining gaps (exclusive, timeout, NoCapacity, package configs,
/// fail_fast, Ctrl+C during shard and plan reruns).
/// </summary>
public sealed class BaselineReviewTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private const string Solo = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npm test", "npx jest"]
          allowed_directories: ["."]
        machines:
          - name: micro-01
            os: linux
            ssh: user@10.20.30.21
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 1
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-baseline-review-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _output = [];

    private readonly List<IDisposable> _owned = [];

    /// <summary>Every pause the runner asked for (with waitAtOnce).</summary>
    private readonly List<TimeSpan> _delays = [];

    public BaselineReviewTests() => Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));

    public void Dispose()
    {
        foreach (var o in _owned)
        {
            o.Dispose();
        }

        TestTree.Delete(_dir);
    }

    private string Repo => Path.Combine(_dir, "repo");

    private JobLog Log => new(Path.Combine(_dir, "state"));

    /// <summary>Called with every runner line as it is printed.</summary>
    private Action<string> OnLine { get; set; } = _ => { };

    private List<string> Output()
    {
        lock (_output)
        {
            return [.. _output];
        }
    }

    private (JobRunner Runner, PoolSsh Ssh, BaselinePayloads Payloads, BaseStore Bases) Pool(string yaml = BaselineTests.Fleet, bool waitAtOnce = false)
    {
        var ssh = new PoolSsh();
        var payloads = new BaselinePayloads(new RunnerTests.FakePayloads(""));
        var bases = new BaseStore(null);
        var runner = new JobRunner(
            ConfigLoader.Parse(yaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"),
            line =>
            {
                lock (_output)
                {
                    _output.Add(line);
                }

                OnLine(line);
            },
            delay: waitAtOnce ? RecordPause : (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            bases: bases);
        return (runner, ssh, payloads, bases);
    }

    /// <summary>A pause that ends at once and is recorded in <see cref="_delays"/>.</summary>
    private Task RecordPause(TimeSpan pause, CancellationToken ct)
    {
        lock (_delays)
        {
            _delays.Add(pause);
        }

        return Task.CompletedTask;
    }

    private JobRequest Npm() => new(["node"], ["npm", "test"], ".", Repo, TimeSpan.Zero) { Baseline = true };

    private JobRequest Jest() => new(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", Repo, TimeSpan.Zero) { Baseline = true };

    private static string Json(BaselineResult b) => JsonSerializer.Serialize(LanQueue.Cli.BaselineJsonOf(b)!, LanqJson.Default.BaselineJson);

    // ---- M1: the machine that failed ----

    /// <summary>The head fails on build-01. Its rerun waits for build-01 (busy at the first look) instead of taking big-01, which ranks first.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RerunWaitsForTheMachineThatFailed()
    {
        var (runner, ssh, payloads, _) = Pool(waitAtOnce: true);
        var headDone = false;
        var refusals = 1;
        ssh.RefuseSlot = m => !headDone ? m.Name == "big-01" : m.Name == "build-01" && Interlocked.Decrement(ref refusals) >= 0;
        ssh.OnExecute = (e, _) =>
        {
            var isBase = payloads.IsBase(ssh, e);
            headDone = true;
            return Task.FromResult(e.Exit(isBase ? 0 : 3));
        };

        var r = await runner.RunAsync(Npm() with { Wait = TimeSpan.FromMinutes(1) }, CancellationToken.None);

        Assert.Equal("build-01", r.Machine);
        var b = r.Baseline!;
        Assert.Equal("build-01", b.Machine);
        Assert.True(b.SameMachine);
        Assert.DoesNotContain("may reflect the machine", b.Message, StringComparison.Ordinal);
        Assert.Contains("\"sameMachine\":true", Json(b), StringComparison.Ordinal);
        Assert.Contains(Output(), l => l.Contains("waiting for a slot on build-01, where the job failed, before taking another machine (look 1 of 2)", StringComparison.Ordinal));
        Assert.DoesNotContain(ssh.Executes, e => e.Machine.Name == "big-01");
    }

    /// <summary>build-01 stays full: after two looks the rerun takes big-01, and the message, JSON and fix_code reason say it ran elsewhere.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RerunElsewhereSaysTheResultMayReflectTheMachine()
    {
        var (runner, ssh, payloads, _) = Pool(waitAtOnce: true);
        var headDone = false;
        ssh.RefuseSlot = m => !headDone ? m.Name == "big-01" : m.Name == "build-01";
        ssh.OnExecute = (e, _) =>
        {
            var isBase = payloads.IsBase(ssh, e);
            headDone = true;
            return Task.FromResult(e.Exit(isBase ? 0 : 3));
        };

        var r = await runner.RunAsync(Npm() with { Wait = TimeSpan.FromMinutes(1) }, CancellationToken.None);

        var b = r.Baseline!;
        Assert.Equal(BaselineVerdict.New, b.Verdict);
        Assert.Equal("big-01", b.Machine);
        Assert.False(b.SameMachine);
        Assert.EndsWith(
            "the same command Completed on big-01 in 0.0s: this failure is new (on big-01, not build-01 where it failed: the result may reflect the machine)",
            b.Message,
            StringComparison.Ordinal);
        Assert.Contains("\"sameMachine\":false", Json(b), StringComparison.Ordinal);
        Assert.Contains(Output(), l => l.Contains("(look 2 of 2)", StringComparison.Ordinal));
        var steps = NextSteps.ForRun(ParsedArgs.Parse(["run", "--baseline", "--", "npm", "test"]), NextSteps.Outcome(r));
        Assert.EndsWith("; new: the base commit passes (on big-01, not build-01 where it failed: the result may reflect the machine)", Assert.Single(steps).Reason, StringComparison.Ordinal);
    }

    /// <summary>--wait 0: no wait for the machine that failed; the rerun takes another machine that can take it now.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task WithWaitZeroTheRerunDoesNotWaitForTheFailedMachine()
    {
        var (runner, ssh, payloads, _) = Pool(waitAtOnce: true);
        var headDone = false;
        ssh.RefuseSlot = m => !headDone ? m.Name == "big-01" : m.Name == "build-01";
        ssh.OnExecute = (e, _) =>
        {
            var isBase = payloads.IsBase(ssh, e);
            headDone = true;
            return Task.FromResult(e.Exit(isBase ? 0 : 3));
        };

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal("big-01", r.Baseline!.Machine);
        Assert.False(r.Baseline.SameMachine);
        Assert.DoesNotContain(Output(), l => l.Contains("waiting for a slot on", StringComparison.Ordinal));
        Assert.Empty(_delays);
    }

    /// <summary>The wait for the machine that failed never goes past what is left of --wait: each pause is at most that.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TheWaitForTheFailedMachineIsCappedByWait()
    {
        var (runner, ssh, payloads, _) = Pool(waitAtOnce: true);
        var headDone = false;
        ssh.RefuseSlot = m => !headDone ? m.Name == "big-01" : m.Name == "build-01";
        ssh.OnExecute = (e, _) =>
        {
            var isBase = payloads.IsBase(ssh, e);
            headDone = true;
            return Task.FromResult(e.Exit(isBase ? 0 : 3));
        };

        var r = await runner.RunAsync(Npm() with { Wait = TimeSpan.FromSeconds(5) }, CancellationToken.None);

        Assert.Equal("big-01", r.Baseline!.Machine);
        Assert.Equal(JobRunner.PreferRounds, _delays.Count);
        Assert.All(_delays, d => Assert.True(d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(5), d.ToString()));
    }

    [Fact]
    public void SameMachineIsAbsentWhenTheRerunRanNowhere()
    {
        var b = new BaselineResult("origin/main", BaselinePayloads.BaseSha, BaselineVerdict.Skipped, "skipped") { FailedOn = "build-01" };
        Assert.Null(b.SameMachine);
        Assert.DoesNotContain("sameMachine", Json(b), StringComparison.Ordinal);
    }

    // ---- L2: the waiting line ----

    /// <summary>Two failed shards rerun on a one-slot fleet: the second waits behind the first and says who holds the slot.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TwoShardRerunsOnOneSlotSayWhoHoldsIt()
    {
        var (runner, ssh, payloads, _) = Pool(Solo);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnLine = l =>
        {
            if (l.Contains("(the other baseline reruns hold some)", StringComparison.Ordinal))
            {
                waiting.TrySetResult();
            }
        };
        ssh.OnExecute = async (e, ct) =>
        {
            if (!payloads.IsBase(ssh, e))
            {
                return e.Exit(1);
            }

            await waiting.Task.WaitAsync(ct); // the first rerun holds the slot until the second has said it waits
            return e.Exit(0);
        };

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(2), CancellationToken.None);

        Assert.All(r.Shards, s => Assert.Equal(BaselineVerdict.New, s.Baseline!.Verdict));
        Assert.Contains(Output(), l => l.Contains("waiting for a slot (the other baseline reruns hold some)", StringComparison.Ordinal));
        Assert.Equal(1, ssh.Peak["micro-01"]);
    }

    [Fact]
    public void PlanProgressKeepsItsWording() => Assert.Equal("this plan's other jobs", new PlanProgress().Holders);

    // ---- L3: Ctrl+C before a rerun starts ----

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCBeforeTheRerunSingle()
    {
        var (runner, ssh, payloads, _) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = async (e, _) =>
        {
            await cts.CancelAsync();
            return e.Exit(3);
        };

        var r = await runner.RunAsync(Npm(), cts.Token);

        AssertCancelledBeforeItStarted(r.Baseline, payloads);
        Assert.Equal(JobStatus.Failed, r.Status);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCBeforeTheRerunShards()
    {
        var (runner, ssh, payloads, _) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = async (e, _) =>
        {
            await cts.CancelAsync();
            return e.Exit(1);
        };

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(1), cts.Token);

        AssertCancelledBeforeItStarted(Assert.Single(r.Shards).Baseline, payloads);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCBeforeTheRerunPlan()
    {
        var (runner, ssh, payloads, _) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = async (e, _) =>
        {
            await cts.CancelAsync();
            return e.Exit(2);
        };

        var r = await runner.RunPlanAsync([new PlanJobSpec("build", Npm(), null, [])], false, cts.Token);

        AssertCancelledBeforeItStarted(Assert.Single(r.Jobs).Baseline, payloads);
    }

    private static void AssertCancelledBeforeItStarted(BaselineResult? b, BaselinePayloads payloads)
    {
        Assert.NotNull(b);
        Assert.Equal(BaselineVerdict.Inconclusive, b.Verdict);
        Assert.Equal("at bbbbbbb (merge-base with origin/main): inconclusive: cancelled (Ctrl+C) before it started", b.Message);
        Assert.Null(b.Status);
        Assert.Empty(payloads.CommitIds);
    }

    // ---- L4: the recorded incremental base ----

    /// <summary>bases.json keeps the newer HEAD when the rerun delivered its ancestor; the next run's bundle is incremental on that HEAD.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task StoredBaseDoesNotMoveBackAfterARerun()
    {
        var (runner, ssh, payloads, bases) = Pool();
        var head = payloads.Inner.Head;
        payloads.Ancestor = (a, d) => a == BaselinePayloads.BaseSha && d == head;
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(payloads.IsBase(ssh, e) ? 0 : 3));

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(r.Machine, r.Baseline!.Machine);
        Assert.Equal(head, bases.Get(r.Machine!, T.RepoId));
        payloads.Inner.BundleBases.Clear();
        await runner.RunAsync(Npm() with { Baseline = false }, CancellationToken.None);
        Assert.Equal(head, payloads.Inner.BundleBases.Last());
    }

    [Fact]
    public async Task IsAncestorUsesTheLocalRepository()
    {
        var repo = new TempRepo();
        _owned.Add(repo);
        var c1 = repo.Git("rev-parse", "HEAD").Trim();
        var c2 = repo.Commit("b.txt", "b", "two");
        var git = new GitPayloadBuilder();
        Assert.True(await git.IsAncestorAsync(repo.Dir, c1, c2, CancellationToken.None));
        Assert.True(await git.IsAncestorAsync(repo.Dir, c2, c2, CancellationToken.None));
        Assert.False(await git.IsAncestorAsync(repo.Dir, c2, c1, CancellationToken.None));
        Assert.False(await git.IsAncestorAsync(repo.Dir, "nope", c1, CancellationToken.None));
    }

    /// <summary>
    /// The real prepare script: a rerun's older commit leaves the worker's refs/lanq/base on the newer snapshot, and the next run's bundle on
    /// that base passes bundle verify and moves the base forward.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task WorkerKeepsItsNewerBaseAfterARerun()
    {
        var repo = new TempRepo();
        var worker = new LocalWorker();
        _owned.Add(repo);
        _owned.Add(worker);
        var c1 = repo.Commit("c1.txt", "one", "c1");
        var c2 = repo.Commit("c2.txt", "two", "c2");
        var git = new GitPayloadBuilder();
        var ct = CancellationToken.None;

        string CacheBase(string repoId) => repo.Git("-C", Path.Combine(worker.Root, "repos", repoId + ".git"), "rev-parse", "refs/lanq/base").Trim();
        (int Code, string Output) Prepare(string job, string payloadId, string repoId, BundleBuild b)
        {
            File.Copy(b.Path, Path.Combine(worker.Root, "incoming", job + ".bundle"), overwrite: true);
            return worker.Run(RemoteScripts.Prepare(worker.Machine, job, repoId, payloadId));
        }

        var p1 = await git.BuildAsync("j1", repo.Dir, repo.Out, PayloadKinds.Bundle, ct);
        Assert.Equal(c2, p1.Sha);
        var r1 = Prepare("j1", "j1", p1.RepoId!, await git.BuildBundleAsync("j1", repo.Dir, repo.Out, p1.Sha, null, ct));
        Assert.True(r1.Code == 0, r1.Output);
        Assert.Equal(c2, CacheBase(p1.RepoId!));

        // The rerun at c1 (an ancestor of the worker's base): incremental on c2, prepared, and the base stays c2.
        var basePayload = await git.BuildCommitAsync("b1", repo.Dir, repo.Out, c1, PayloadKinds.Bundle, ct);
        var rerunBundle = await git.BuildBundleAsync("b1", repo.Dir, repo.Out, c1, c2, ct);
        Assert.Equal(c2, rerunBundle.Base);
        var r2 = Prepare("j2", "b1", basePayload.RepoId!, rerunBundle);
        Assert.True(r2.Code == 0, r2.Output);
        Assert.Equal(c2, CacheBase(p1.RepoId!));
        Assert.Equal(c1, repo.Git("-C", Path.Combine(worker.Root, "jobs", "j2"), "rev-parse", "HEAD").Trim());

        // The next run: incremental on c2, verified by the cache, and the base moves forward.
        var c3 = repo.Commit("c3.txt", "three", "c3");
        var p3 = await git.BuildAsync("j3", repo.Dir, repo.Out, PayloadKinds.Bundle, ct);
        var next = await git.BuildBundleAsync("j3", repo.Dir, repo.Out, p3.Sha, c2, ct);
        Assert.Equal(c2, next.Base);
        var r3 = Prepare("j3", "j3", p3.RepoId!, next);
        Assert.True(r3.Code == 0, r3.Output);
        Assert.Equal(c3, CacheBase(p1.RepoId!));
    }

    // ---- L5: baseline: false with baseline_ref: on one job ----

    [Fact]
    public void BaselineFalseWithBaselineRefOnTheSameJobIsRefused()
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("""
            version: 1
            jobs:
              x:
                command: npm test
                baseline: false
                baseline_ref: main
            """));
        Assert.Contains("job 'x'", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("baseline_ref has no effect with baseline: false; drop one.", ex.Message, StringComparison.Ordinal);

        // Inherited from defaults: turned off quietly.
        var job = PlanFileLoader.Parse("""
            version: 1
            defaults:
              baseline_ref: main
            jobs:
              x:
                command: npm test
                baseline: false
            """).Jobs[0];
        Assert.False(job.Args.Baseline);
        Assert.Equal(["run", "--", "npm", "test"], job.RunArgs);
    }

    /// <summary>A job's own keys decide before the defaults' (baseline_ref implies baseline: true).</summary>
    [Theory]
    [InlineData("baseline: false", "baseline_ref: rel", "run --baseline-ref rel")] // the job's own ref wins over an inherited false
    [InlineData("baseline_ref: rel", "baseline: false", "run")] // the job's own false turns an inherited ref off, quietly
    [InlineData("baseline_ref: rel", "baseline: true", "run --baseline-ref rel")] // the job's own true keeps the inherited ref
    [InlineData("baseline: true", "", "run --baseline")]
    [InlineData("baseline_ref: rel", "", "run --baseline-ref rel")]
    [InlineData("baseline: false", "", "run")]
    [InlineData("baseline_ref: rel", "baseline_ref: mine", "run --baseline-ref mine")]
    [InlineData("baseline: true", "baseline: false", "run")]
    [InlineData("needs: [node]", "baseline_ref: mine", "run --needs node --baseline-ref mine")]
    public void JobKeysDecideBeforeTheDefaults(string defaults, string job, string expected)
    {
        var yaml = "version: 1\ndefaults:\n  " + defaults + "\njobs:\n  x:\n    command: npm test\n" + (job.Length > 0 ? "    " + job + "\n" : "");
        var parsed = PlanFileLoader.Parse(yaml).Jobs[0];
        Assert.Equal(expected + " -- npm test", string.Join(' ', parsed.RunArgs));
        Assert.Equal(expected.Contains("--baseline", StringComparison.Ordinal), parsed.Args.Baseline);
    }

    // ---- lanq.yml baseline_ref: a branch or tag, never a commit ----

    [Fact]
    public async Task OnlyBranchAndTagNamesCount()
    {
        var repo = new TempRepo();
        _owned.Add(repo);
        var c1 = repo.Git("rev-parse", "HEAD").Trim();
        repo.Commit("b.txt", "b", "two");
        repo.Git("tag", "v1.0", c1);
        repo.Git("branch", "release", c1);
        var bare = Path.Combine(repo.Dir, ".out", "origin.git");
        repo.Git("init", "-q", "--bare", bare);
        repo.Git("remote", "add", "origin", bare);
        repo.Git("push", "-q", "origin", "main");
        repo.Git("fetch", "-q", "origin");
        var ct = CancellationToken.None;

        foreach (var name in new[] { "main", "release", "v1.0", "origin/main", "refs/heads/main" })
        {
            Assert.NotNull((await BaseRef.BranchOrTagAsync(repo.Dir, name, ct)).FullRef);
        }

        foreach (var name in new[] { c1, c1[..7], "HEAD", "HEAD~1", "main~1", "main^", "v1.0^{}", "main@{1}", "-main", "no-such", "main..release", ":/two" })
        {
            var refused = await BaseRef.BranchOrTagAsync(repo.Dir, name, ct);
            Assert.Null(refused.FullRef);
            Assert.Equal(BaseRef.PlanRefMessage, refused.Error);
        }

        // The command line still takes a commit.
        var bySha = await BaseRef.ResolveAsync(repo.Dir, c1, ct);
        Assert.Equal(c1, bySha.Base!.Sha);
    }

    /// <summary>
    /// The check returns the exact full ref, and that is what is resolved: a tag whose name is 40 hex digits (here the id of another commit)
    /// is the tag, a name that is both a branch and a tag is refused with the two full refs, and a full ref is taken as written.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TheCheckedFullRefIsWhatIsResolved()
    {
        var repo = new TempRepo();
        _owned.Add(repo);
        var c1 = repo.Git("rev-parse", "HEAD").Trim();
        var c2 = repo.Commit("b.txt", "b", "two");
        repo.Git("tag", c2, c1); // a tag named like c2's id, pointing at c1
        repo.Git("branch", "rel", c1);
        repo.Git("tag", "rel", c1);
        var ct = CancellationToken.None;

        Assert.Equal("refs/tags/" + c2, (await BaseRef.BranchOrTagAsync(repo.Dir, c2, ct)).FullRef);
        var both = await BaseRef.BranchOrTagAsync(repo.Dir, "rel", ct);
        Assert.Null(both.FullRef);
        Assert.Equal("baseline_ref rel is both a branch and a tag; write refs/heads/rel or refs/tags/rel", both.Error);
        Assert.Equal("refs/heads/rel", (await BaseRef.BranchOrTagAsync(repo.Dir, "refs/heads/rel", ct)).FullRef);
        Assert.Equal("refs/tags/rel", (await BaseRef.BranchOrTagAsync(repo.Dir, "refs/tags/rel", ct)).FullRef);
        Assert.Null((await BaseRef.BranchOrTagAsync(repo.Dir, "refs/heads/nope", ct)).FullRef);
        Assert.Null((await BaseRef.BranchOrTagAsync(repo.Dir, "refs/lanq/x", ct)).FullRef);

        // Through a plan: the 40-hex tag resolves to the tag's commit (c1), never to the object c2; it is shown as written.
        var (runner, ssh, payloads, _) = Pool();
        payloads.Resolution = null; // the real resolution
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(payloads.IsBase(ssh, e) ? 0 : 2));
        var r = await runner.RunPlanAsync([new PlanJobSpec("x", Npm() with { RepoDir = repo.Dir, BaselineRef = c2 }, null, [])], false, ct);
        var b = Assert.Single(r.Jobs).Baseline!;
        Assert.Equal(c1, b.Sha);
        Assert.Equal(c2, b.Ref);
        Assert.Contains($"({c2})", b.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/tags/", b.Message, StringComparison.Ordinal);

        // The ambiguous name refuses the plan before anything is sent, with that message.
        var refused = await runner.RunPlanAsync([new PlanJobSpec("x", Npm() with { RepoDir = repo.Dir, BaselineRef = "rel" }, null, []) { Where = "lanq.yml: job 'x' (line 3)" }], false, ct);
        Assert.Equal("Rejected", refused.Status);
        Assert.Equal("lanq.yml: job 'x' (line 3): baseline_ref rel is both a branch and a tag; write refs/heads/rel or refs/tags/rel", refused.Message);
        Assert.Equal(RejectKind.PlanFile, refused.RejectKind);
    }

    /// <summary>lanq.yml's baseline_ref naming a commit refuses the plan before anything is sent; a branch name runs.</summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanBaselineRefMustBeABranchOrTag(bool commit)
    {
        var repo = new TempRepo();
        _owned.Add(repo);
        var c1 = repo.Git("rev-parse", "HEAD").Trim();
        repo.Commit("b.txt", "b", "two");
        File.WriteAllText(Path.Combine(repo.Dir, "lanq.yml"), "version: 1\njobs:\n  x:\n    command: npm test\n    needs: [node]\n    baseline_ref: " + (commit ? c1 : "main") + "\n");
        var (_, ssh, payloads, _) = Pool();
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(0));
        var o = new StringWriter();
        var err = new StringWriter();

        var code = await LanQueue.Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--json"]), ConfigLoader.Parse(BaselineTests.Fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, _dir,
            repo.Dir, o, err, CancellationToken.None, (_, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));

        using var doc = JsonDocument.Parse(o.ToString());
        if (commit)
        {
            Assert.Equal(2, code);
            Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "lanq.yml: job 'x' (line 3): baseline_ref must name a branch or tag (a commit can be given on the command line with --baseline-ref)",
                doc.RootElement.GetProperty("message").GetString());
            Assert.StartsWith("fix lanq.yml: ", doc.RootElement.GetProperty("nextSteps")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.Empty(ssh.Calls);
            Assert.Equal(0, payloads.Resolved);
        }
        else
        {
            Assert.Equal(0, code);
            Assert.Equal(1, payloads.Resolved);
        }
    }

    // ---- L6: the remaining gaps ----

    /// <summary>The rerun keeps --exclusive (every slot claimed) and the job's timeout (the worker's own limit is the same).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RerunCarriesExclusiveAndTheTimeout()
    {
        var (runner, ssh, payloads, _) = Pool();
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(payloads.IsBase(ssh, e) ? 0 : 3));

        var r = await runner.RunAsync(Npm() with { Exclusive = true, Timeout = TimeSpan.FromMinutes(7) }, CancellationToken.None);

        Assert.Equal(BaselineVerdict.New, r.Baseline!.Verdict);
        var rerun = ssh.Executes.Single(e => payloads.IsBase(ssh, e));
        Assert.Contains("timeout -k 30 480", rerun.Command, StringComparison.Ordinal); // 7 min + the worker's 1 min margin
        Assert.Contains("timeout -k 30 480", ssh.Executes.First().Command, StringComparison.Ordinal);
        Assert.Equal(2, ssh.Calls.Count(c => c.Command.Contains("SLOTS=", StringComparison.Ordinal))); // the head's and the rerun's claim of every slot
        Assert.Equal(2, ssh.Peak[r.Machine!]); // every slot of the machine
    }

    /// <summary>No slot for the rerun within its own wait: NoCapacity, inconclusive; the job's result is unchanged.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RerunEndingNoCapacityIsInconclusive()
    {
        var (runner, ssh, payloads, _) = Pool(waitAtOnce: true);
        var headDone = false;
        ssh.RefuseSlot = _ => headDone;
        ssh.OnExecute = (e, _) =>
        {
            headDone = true;
            return Task.FromResult(e.Exit(3));
        };

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(3, r.CliExitCode);
        var b = r.Baseline!;
        Assert.Equal(BaselineVerdict.Inconclusive, b.Verdict);
        Assert.Equal("NoCapacity", b.Status);
        Assert.Equal(75, b.ExitCode);
        Assert.Contains("the same command ended NoCapacity: inconclusive (No eligible machine.", b.Message, StringComparison.Ordinal);
        Assert.Null(b.SameMachine);
        Assert.Single(ssh.Executes);
    }

    /// <summary>
    /// A real repository whose base commit holds a credential in .npmrc that HEAD removed: the base payload is refused by the package-config
    /// scan, nothing of it is sent, the baseline is inconclusive and never echoes the value.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task BaseWithACredentialInAPackageConfigIsNotSent()
    {
        var repo = new TempRepo();
        _owned.Add(repo);
        var withToken = repo.Commit(".npmrc", "//registry.npmjs.org/:_authToken=npm_SuperSecretValue123\n", "add npmrc");
        repo.Git("rm", "-q", ".npmrc");
        repo.Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "drop npmrc");
        var (runner, ssh, payloads, _) = Pool();
        payloads.Real = new GitPayloadBuilder();
        payloads.Resolution = new BaseResolution(new BaseCommit("origin/main", withToken, "merge-base with origin/main"), null);
        ssh.OnExecute = (e, _) => Task.FromResult(e.Exit(3));

        var r = await runner.RunAsync(Npm() with { RepoDir = repo.Dir }, CancellationToken.None);

        var b = r.Baseline!;
        Assert.Equal(BaselineVerdict.Inconclusive, b.Verdict);
        Assert.Contains("the base commit was not sent: Refused: possible secrets in payload", b.Message, StringComparison.Ordinal);
        Assert.Contains(".npmrc", b.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretValue123", b.Message, StringComparison.Ordinal);
        Assert.Single(ssh.Uploads);
        Assert.Single(ssh.Executes);
    }

    /// <summary>With fail_fast the failed job still gets its rerun after the plan (the cancelled sibling does not).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task PlanRerunWithFailFast()
    {
        var (runner, ssh, payloads, _) = Pool();
        ssh.OnExecute = async (e, ct) =>
        {
            if (payloads.IsBase(ssh, e))
            {
                return e.Exit(0);
            }

            if (PlanFileRunTests.JobOf(e) == "unit")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return e.Exit(2);
        };
        PlanJobSpec[] specs = [new("unit", Npm(), null, []) { FileOrder = 0 }, new("build", Npm(), null, []) { FileOrder = 1 }];

        var r = await runner.RunPlanAsync(specs, failFast: true, CancellationToken.None);

        Assert.Equal("Failed", r.Status);
        var jobs = r.Jobs.ToDictionary(j => j.Name);
        Assert.Equal("Cancelled", jobs["unit"].Status);
        Assert.Null(jobs["unit"].Baseline);
        Assert.Equal(BaselineVerdict.New, jobs["build"].Baseline!.Verdict);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCDuringAShardRerun()
    {
        var (runner, ssh, payloads, _) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = CancelInTheRerun(ssh, payloads, cts);

        var r = await runner.RunShardsAsync(Jest(), new ShardOptions(2), cts.Token);

        Assert.Equal("Failed", r.Status);
        Assert.All(r.Shards, s => AssertCancelledRerun(s.Baseline));
        Assert.Equal(0, ssh.Used("big-01") + ssh.Used("build-01"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCDuringAPlanRerun()
    {
        var (runner, ssh, payloads, _) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = CancelInTheRerun(ssh, payloads, cts);

        var r = await runner.RunPlanAsync([new PlanJobSpec("build", Npm(), null, [])], false, cts.Token);

        Assert.Equal("Failed", r.Status);
        AssertCancelledRerun(Assert.Single(r.Jobs).Baseline);
        Assert.Equal(0, ssh.Used("big-01") + ssh.Used("build-01"));
    }

    /// <summary>The head fails; a rerun presses Ctrl+C once it runs and then hangs until it is stopped.</summary>
    private static Func<PoolSsh.Exec, CancellationToken, Task<SshResult>> CancelInTheRerun(PoolSsh ssh, BaselinePayloads payloads, CancellationTokenSource cts) =>
        async (e, ct) =>
        {
            if (!payloads.IsBase(ssh, e))
            {
                return e.Exit(1);
            }

            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return e.Exit(0);
        };

    private static void AssertCancelledRerun(BaselineResult? b)
    {
        Assert.NotNull(b);
        Assert.Equal(BaselineVerdict.Inconclusive, b.Verdict);
        Assert.True(b.Status is "Cancelled" or null, b.Status); // null: cancelled before it began to look for a machine
        Assert.Matches("cancelled \\(Ctrl\\+C\\)(; its cleanup ran| before it started)$", b.Message);
    }
}
