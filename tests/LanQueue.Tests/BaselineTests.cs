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
/// A payload builder for baseline tests: the snapshot and its bundles come from <see cref="RunnerTests.FakePayloads"/>; the base commit is
/// <see cref="Resolution"/> (or the real <see cref="BaseRef"/> on the repository when null), and its payload is recorded by id.
/// </summary>
internal sealed class BaselinePayloads(RunnerTests.FakePayloads inner) : IPayloadBuilder
{
    public static readonly string BaseSha = new('b', 40);

    public RunnerTests.FakePayloads Inner => inner;

    public BaseResolution? Resolution { get; set; } = new(new BaseCommit("origin/main", BaseSha, "merge-base with origin/main"), null);

    public int Resolved { get; private set; }

    /// <summary>The payload id of every base payload built (the bundle carries it as refs/lanq/&lt;id&gt;).</summary>
    public List<string> CommitIds { get; } = [];

    public List<string> BaseFiles { get; } = ["src/app.cs"];

    public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) => inner.BuildAsync(jobId, repoDir, outputDir, kinds, ct);

    public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => inner.BuildArchiveAsync(jobId, repoDir, outputDir, sha, ct);

    public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
        inner.BuildBundleAsync(jobId, repoDir, outputDir, sha, baseSha, ct);

    public async Task<BaseResolution> ResolveBaseAsync(string repoDir, string? named, CancellationToken ct)
    {
        Resolved++;
        return Resolution ?? await BaseRef.ResolveAsync(repoDir, named, ct);
    }

    /// <summary>When set, base payloads come from this (real) builder.</summary>
    public IPayloadBuilder? Real { get; set; }

    /// <summary>The local ancestry answer (default: none is an ancestor, as the interface's default).</summary>
    public Func<string, string, bool> Ancestor { get; set; } = (_, _) => false;

    public Task<bool> IsAncestorAsync(string repoDir, string ancestor, string descendant, CancellationToken ct) => Task.FromResult(Ancestor(ancestor, descendant));

    public Task<JobPayload> BuildCommitAsync(string jobId, string repoDir, string outputDir, string sha, PayloadKinds kinds, CancellationToken ct)
    {
        lock (CommitIds)
        {
            CommitIds.Add(jobId);
        }

        if (Real is not null)
        {
            return Real.BuildCommitAsync(jobId, repoDir, outputDir, sha, kinds, ct);
        }

        return Task.FromResult(new JobPayload(sha, [], "", RepoId: kinds.HasFlag(PayloadKinds.Bundle) ? T.RepoId : null, Head: sha, CommittedFiles: BaseFiles, CommittedConfigs: new Dictionary<string, string?>()));
    }

    /// <summary>Whether an execute call runs on a base payload (its job was prepared from a bundle carrying a base payload id).</summary>
    public bool IsBase(PoolSsh ssh, PoolSsh.Exec e)
    {
        string[] ids;
        lock (CommitIds)
        {
            ids = [.. CommitIds];
        }

        return ssh.Calls.Any(c => c.Command.Contains("bundle verify", StringComparison.Ordinal) && c.Command.Contains(e.JobId, StringComparison.Ordinal)
            && ids.Any(id => c.Command.Contains(id, StringComparison.Ordinal)));
    }
}

/// <summary>lanq run --baseline (0.9.0): the base commit, the rerun after a failure, the verdict, and where it shows (Item 3 of the design).</summary>
public sealed class BaselineTests : IDisposable
{
    private const int TestTimeout = 60_000;

    internal const string Fleet = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npm test", "npm run", "npx jest"]
          allowed_directories: ["."]
        machines:
          - name: big-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 2
          - name: build-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 2
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-baseline-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _output = [];

    private readonly List<IDisposable> _repos = [];

    public BaselineTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
    }

    public void Dispose()
    {
        foreach (var r in _repos)
        {
            r.Dispose();
        }

        TestTree.Delete(_dir);
    }

    private string Repo => Path.Combine(_dir, "repo");

    private JobLog Log => new(Path.Combine(_dir, "state"));

    private (JobRunner Runner, PoolSsh Ssh, BaselinePayloads Payloads) Pool(string yaml = Fleet)
    {
        var ssh = new PoolSsh();
        var payloads = new BaselinePayloads(new RunnerTests.FakePayloads(""));
        var runner = new JobRunner(
            ConfigLoader.Parse(yaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"),
            line =>
            {
                lock (_output)
                {
                    _output.Add(line);
                }
            },
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), bases: new BaseStore(null));
        return (runner, ssh, payloads);
    }

    private JobRequest Npm(bool baseline = true) => new(["node"], ["npm", "test"], ".", Repo, TimeSpan.Zero) { Baseline = baseline };

    /// <summary>The head job exits <paramref name="head"/>, the base rerun as <paramref name="base"/> says.</summary>
    private static void Exits(PoolSsh ssh, BaselinePayloads payloads, Func<PoolSsh.Exec, SshResult> head, Func<PoolSsh.Exec, SshResult> @base) =>
        ssh.OnExecute = (e, _) => Task.FromResult(payloads.IsBase(ssh, e) ? @base(e) : head(e));

    public enum HeadEnd
    {
        Completed,
        Failed,
        TimedOut,
        InfraError,
        NoCapacity,
    }

    /// <summary>Only the job's own failure is rerun at the base: never after Completed, TimedOut, InfraError or NoCapacity (nor Rejected, below).</summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData(HeadEnd.Completed)]
    [InlineData(HeadEnd.Failed)]
    [InlineData(HeadEnd.TimedOut)]
    [InlineData(HeadEnd.InfraError)]
    [InlineData(HeadEnd.NoCapacity)]
    public async Task BaselineOnlyAfterFailed(HeadEnd end)
    {
        var (runner, ssh, payloads) = Pool();
        ssh.RefuseSlot = _ => end == HeadEnd.NoCapacity;
        Exits(ssh, payloads, e => end switch
        {
            HeadEnd.Completed => e.Exit(0),
            HeadEnd.Failed => e.Exit(3),
            HeadEnd.TimedOut => new SshResult(124, "", TimedOut: true),
            _ => new SshResult(255, "connection lost"),
        }, e => e.Exit(0));

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(end.ToString(), r.Status.ToString());
        if (end == HeadEnd.Failed)
        {
            Assert.NotNull(r.Baseline);
            Assert.Single(payloads.CommitIds);
            Assert.Equal(2, ssh.Executes.Count);
        }
        else
        {
            Assert.Null(r.Baseline);
            Assert.Empty(payloads.CommitIds); // built lazily: no base payload unless a baseline runs
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RejectedRunsNoBaseline()
    {
        var (runner, ssh, payloads) = Pool();
        var r = await runner.RunAsync(Npm() with { Argv = ["rm", "-rf", "/"] }, CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Null(r.Baseline);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Resolved);
    }

    /// <summary>The verdict follows the base rerun's status; the run's own status and exit code never change.</summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData(0, BaselineVerdict.New, "Completed")]
    [InlineData(1, BaselineVerdict.Preexisting, "Failed")]
    [InlineData(-1, BaselineVerdict.Inconclusive, "TimedOut")]
    [InlineData(-2, BaselineVerdict.Inconclusive, "InfraError")]
    public async Task VerdictNewPreexistingInconclusiveAndStatusAndExitUnchanged(int baseExit, string verdict, string baseStatus)
    {
        var (runner, ssh, payloads) = Pool();
        Exits(ssh, payloads, e => e.Exit(3), e => baseExit switch
        {
            -1 => new SshResult(124, "", TimedOut: true),
            -2 => new SshResult(255, "connection lost"),
            _ => e.Exit(baseExit),
        });

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(3, r.CliExitCode);
        var b = Assert.IsType<BaselineResult>(r.Baseline);
        Assert.Equal(verdict, b.Verdict);
        Assert.Equal(baseStatus, b.Status);
        Assert.Equal(BaselinePayloads.BaseSha, b.Sha);
        Assert.Equal("origin/main", b.Ref);
        Assert.StartsWith("at bbbbbbb (merge-base with origin/main)", b.Message, StringComparison.Ordinal);
        Assert.NotNull(b.JobId);
        Assert.NotEqual(r.JobId, b.JobId);
        if (verdict == BaselineVerdict.New)
        {
            Assert.EndsWith("this failure is new", b.Message, StringComparison.Ordinal);
            Assert.Equal(0, b.ExitCode);
        }

        if (verdict == BaselineVerdict.Preexisting)
        {
            Assert.Contains("Failed (exit 1)", b.Message, StringComparison.Ordinal);
            Assert.EndsWith("this failure is pre-existing", b.Message, StringComparison.Ordinal);
            Assert.NotNull(b.LogPath);
        }
    }

    /// <summary>The rerun keeps the request (argv, dir, needs, env, timeout) and records baselineOf and commit; timing history leaves it out.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RerunIsTheSameRequestAndItsRecordIsMarked()
    {
        var yaml = Fleet.Replace("  allowed_directories:", "  allowed_env: [CONFIGURATION]\n  allowed_directories:", StringComparison.Ordinal);
        var (runner, ssh, payloads) = Pool(yaml);
        Exits(ssh, payloads, e => e.Exit(3), e => e.Exit(0));
        var req = Npm() with { Env = [new Policy.JobEnvVar("CONFIGURATION", "Release")], Timeout = TimeSpan.FromMinutes(7) };

        var r = await runner.RunAsync(req, CancellationToken.None);

        Assert.Equal(2, ssh.Executes.Count);
        var (head, rerun) = (ssh.Executes.First(), ssh.Executes.Last());
        Assert.Contains("CONFIGURATION=Release", rerun.Command, StringComparison.Ordinal);
        Assert.Contains("npm", rerun.Command, StringComparison.Ordinal);
        var records = Log.ReadAll();
        Assert.Equal(2, records.Count);
        var marked = Assert.Single(records, x => x.BaselineOf is not null);
        Assert.Equal(r.JobId, marked.BaselineOf);
        Assert.Equal("bbbbbbb", marked.Commit);
        Assert.Null(Assert.Single(records, x => x.BaselineOf is null).Commit);
        Assert.Single(Log.ReadHistory(CancellationToken.None).Records);
        Assert.Contains(_output, l => l.StartsWith("[baseline] [lanq] the job failed; running the same command at bbbbbbb", StringComparison.Ordinal));
    }

    /// <summary>The rerun prefers the machine the job failed on when it can take it, even when another machine ranks first.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task SameMachinePreferred()
    {
        var (runner, ssh, payloads) = Pool();
        var headDone = false;
        ssh.RefuseSlot = m => !headDone && m.Name == "big-01"; // big-01 ranks first (by name); the head goes to build-01
        ssh.OnExecute = (e, _) =>
        {
            var isBase = payloads.IsBase(ssh, e);
            headDone = true;
            return Task.FromResult(e.Exit(isBase ? 0 : 3));
        };

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal("build-01", r.Machine);
        Assert.Equal("build-01", r.Baseline!.Machine);
        Assert.Equal("build-01", ssh.Executes.Last().Machine.Name);
    }

    /// <summary>The snapshot is the base commit (on it, nothing uncommitted): nothing to compare, nothing runs.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task OnTheBaseCleanSkips()
    {
        var (runner, ssh, payloads) = Pool();
        payloads.Resolution = new BaseResolution(new BaseCommit("origin/main", payloads.Inner.Sha, "origin/main"), null);
        Exits(ssh, payloads, e => e.Exit(3), e => e.Exit(0));

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(BaselineVerdict.Skipped, r.Baseline!.Verdict);
        Assert.StartsWith("skipped: the snapshot is the base commit", r.Baseline.Message, StringComparison.Ordinal);
        Assert.Null(r.Baseline.Status);
        Assert.Single(ssh.Executes);
        Assert.Empty(payloads.CommitIds);
    }

    /// <summary>A base found nowhere (a shallow clone) is inconclusive, and the job itself still ran.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task UnknownBaseIsInconclusive()
    {
        var (runner, ssh, payloads) = Pool();
        payloads.Resolution = new BaseResolution(new BaseCommit("origin/main", null, "merge-base with origin/main", BaseRef.ShallowProblem), null);
        Exits(ssh, payloads, e => e.Exit(3), e => e.Exit(0));

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(BaselineVerdict.Inconclusive, r.Baseline!.Verdict);
        Assert.Contains("shallow clone?", r.Baseline.Message, StringComparison.Ordinal);
        Assert.Single(ssh.Executes);
    }

    /// <summary>The base's committed file names get the same secret scan as a snapshot's: a refused base is not sent, and is inconclusive.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task BaseWithASecretFileIsNotSent()
    {
        var (runner, ssh, payloads) = Pool();
        payloads.BaseFiles.Add(".env");
        Exits(ssh, payloads, e => e.Exit(3), e => e.Exit(0));

        var r = await runner.RunAsync(Npm(), CancellationToken.None);

        Assert.Equal(BaselineVerdict.Inconclusive, r.Baseline!.Verdict);
        Assert.Contains("not sent", r.Baseline.Message, StringComparison.Ordinal);
        Assert.Single(ssh.Executes);
        Assert.Single(ssh.Uploads);
    }

    /// <summary>A bad --baseline-ref is refused (Rejected 2, Arguments) before any snapshot or network.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task BadRefIsRejectedBeforeAnything()
    {
        var repo = NewRepo();
        var (runner, ssh, payloads) = Pool();
        payloads.Resolution = null; // the real resolution, on a real repository

        var r = await runner.RunAsync(Npm() with { RepoDir = repo.Dir, BaselineRef = "no-such-branch" }, CancellationToken.None);

        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Equal(RejectKind.Arguments, r.RejectKind);
        Assert.Equal(2, r.CliExitCode);
        Assert.Contains("--baseline-ref no-such-branch is not a branch, tag or commit", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Inner.Built);

        // The shards and the dry run refuse it the same way.
        var shards = await runner.RunShardsAsync(Npm() with { RepoDir = repo.Dir, BaselineRef = "no-such-branch" }, new ShardOptions(2), CancellationToken.None);
        Assert.Equal("Rejected", shards.Status);
        Assert.Equal(2, shards.ExitCode);
        var plan = await runner.PlanAsync(Npm() with { RepoDir = repo.Dir, BaselineRef = "no-such-branch" }, null, CancellationToken.None);
        Assert.Equal("Rejected", plan.Status);
        Assert.Empty(ssh.Calls);
    }

    /// <summary>--shards: only the failed shards rerun, each with its own LANQ_SHARD and the run's LANQ_SHARDS; the others get no baseline.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task OnlyFailedShardsRerunWithSameShardVars()
    {
        var (runner, ssh, payloads) = Pool();
        Exits(ssh, payloads, e => e.Exit(e.Shard is 2 or 3 ? 1 : 0), e => e.Exit(e.Shard == 2 ? 1 : 0));
        var req = new JobRequest(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", Repo, TimeSpan.Zero) { Baseline = true };

        var r = await runner.RunShardsAsync(req, new ShardOptions(3), CancellationToken.None);

        Assert.Equal("Failed", r.Status);
        Assert.Equal(1, r.ExitCode);
        var reruns = ssh.Executes.Where(e => payloads.IsBase(ssh, e)).OrderBy(e => e.Shard).ToList();
        Assert.Equal([2, 3], reruns.Select(e => e.Shard));
        Assert.All(reruns, e => Assert.Contains("LANQ_SHARDS=3", e.Command, StringComparison.Ordinal));
        Assert.Contains("--shard=2/3", reruns[0].Command, StringComparison.Ordinal);
        Assert.Single(payloads.CommitIds); // one base payload for every rerun
        Assert.Null(r.Shards[0].Baseline);
        Assert.Equal(BaselineVerdict.Preexisting, r.Shards[1].Baseline!.Verdict);
        Assert.Equal(BaselineVerdict.New, r.Shards[2].Baseline!.Verdict);
        Assert.Equal(3, Log.ReadHistory(CancellationToken.None).Records.Count);
        Assert.Equal(5, Log.ReadAll().Count);
    }

    /// <summary>Ctrl+C during the rerun cancels and cleans it up; the result is still the job's own Failed, the baseline inconclusive.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCDuringBaseline()
    {
        var (runner, ssh, payloads) = Pool();
        using var cts = new CancellationTokenSource();
        ssh.OnExecute = async (e, ct) =>
        {
            if (!payloads.IsBase(ssh, e))
            {
                return e.Exit(3);
            }

            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return e.Exit(0);
        };

        var r = await runner.RunAsync(Npm(), cts.Token);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(3, r.CliExitCode);
        Assert.Equal(BaselineVerdict.Inconclusive, r.Baseline!.Verdict);
        Assert.Equal("Cancelled", r.Baseline.Status);
        Assert.Contains("cancelled (Ctrl+C); its cleanup ran", r.Baseline.Message, StringComparison.Ordinal);
        Assert.Equal(0, ssh.Used("big-01") + ssh.Used("build-01"));
    }

    /// <summary>The JSON baseline object (every member written) on a single run, the text line before the status line, and the fix_code reason.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task JsonShapeTextLineAndNextStepsReasonCarriesVerdict()
    {
        var (_, ssh, payloads) = Pool();
        Exits(ssh, payloads, e => e.Exit(3), e => e.Exit(0));
        var (code, stdout, stderr) = await Cli(ssh, payloads, "run", "--json", "--baseline", "--needs", "node", "--", "npm", "test");

        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(stdout);
        var json = doc.RootElement;
        Assert.Equal("Failed", json.GetProperty("status").GetString());
        var b = json.GetProperty("baseline");
        string[] keys = ["ref", "sha", "status", "exitCode", "machine", "jobId", "logPath", "durationSeconds", "verdict", "message", "sameMachine"];
        Assert.Equal(keys, b.EnumerateObject().Select(p => p.Name));
        Assert.Equal("new", b.GetProperty("verdict").GetString());
        Assert.Equal("Completed", b.GetProperty("status").GetString());
        var reason = json.GetProperty("nextSteps")[0].GetProperty("reason").GetString()!;
        Assert.Equal("fix_code", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.EndsWith("; new: the base commit passes", reason, StringComparison.Ordinal);

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("[lanq] Failed: ", lines[^1], StringComparison.Ordinal);
        var at = lines.FindIndex(l => l.StartsWith("[lanq] baseline: at bbbbbbb (merge-base with origin/main) the same command Completed on ", StringComparison.Ordinal));
        Assert.True(at >= 0 && at < lines.Count - 1, stderr);

        // Without --baseline the key is left out.
        var (_, plain, _) = await Cli(ssh, payloads, "run", "--json", "--needs", "node", "--", "npm", "test");
        using var plainDoc = JsonDocument.Parse(plain);
        Assert.False(plainDoc.RootElement.TryGetProperty("baseline", out _));
    }

    /// <summary>--shards --json: the baseline object sits on each failed shard; the fix_code reason names each shard's verdict.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ShardJsonCarriesBaselinePerShard()
    {
        var (_, ssh, payloads) = Pool();
        Exits(ssh, payloads, e => e.Exit(e.Shard == 2 ? 1 : 0), e => e.Exit(1));
        var (code, stdout, stderr) = await Cli(ssh, payloads, "run", "--json", "--baseline", "--shards", "2", "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(stdout);
        var shards = doc.RootElement.GetProperty("shards");
        Assert.False(shards[0].TryGetProperty("baseline", out _));
        Assert.Equal("preexisting", shards[1].GetProperty("baseline").GetProperty("verdict").GetString());
        Assert.Contains("pre-existing: it fails at the base commit too (log: ", doc.RootElement.GetProperty("nextSteps")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("[lanq] baseline: shard 2/2 at bbbbbbb", stderr, StringComparison.Ordinal);
    }

    /// <summary>A plan reruns only its failed jobs that asked for it (not a passing one, not one without baseline:, not one skipped after).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task PlanRerunsOnlyFailedJobs()
    {
        File.WriteAllText(Path.Combine(Repo, "lanq.yml"), """
            version: 1
            defaults:
              needs: [node]
              baseline: true
            jobs:
              build:
                command: npm run build
              unit:
                command: npm run unit
              lint:
                command: npm run lint
                baseline: false
              pack:
                command: npm run pack
                after: [build]
            """);
        var (_, ssh, payloads) = Pool();
        Exits(ssh, payloads, e => e.Exit(PlanFileRunTests.JobOf(e) is "build" or "lint" ? 2 : 0), e => e.Exit(0));

        var o = new StringWriter();
        var err = new StringWriter();
        var code = await LanQueue.Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--json"]), ConfigLoader.Parse(Fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, _dir, Repo, o, err,
            CancellationToken.None, (_, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));

        Assert.Equal(2, code);
        var reruns = ssh.Executes.Where(e => payloads.IsBase(ssh, e)).ToList();
        Assert.Equal(["build"], reruns.Select(PlanFileRunTests.JobOf));
        using var doc = JsonDocument.Parse(o.ToString());
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j);
        Assert.Equal("new", jobs["build"].GetProperty("baseline").GetProperty("verdict").GetString());
        Assert.False(jobs["lint"].TryGetProperty("baseline", out _));
        Assert.False(jobs["unit"].TryGetProperty("baseline", out _));
        Assert.Equal("Skipped", jobs["pack"].GetProperty("status").GetString());
        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("[lanq] baseline: job build at bbbbbbb", err.ToString(), StringComparison.Ordinal);
    }

    /// <summary>--dry-run says which base a failure would be rerun at, and runs nothing.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task DryRunNamesTheBase()
    {
        var (_, ssh, payloads) = Pool();
        var (code, stdout, _) = await Cli(ssh, payloads, "run", "--dry-run", "--baseline", "--needs", "node", "--", "npm", "test");

        Assert.Equal(0, code);
        Assert.Contains("Baseline: on failure, rerun at bbbbbbb (merge-base with origin/main)", stdout, StringComparison.Ordinal);
        Assert.Empty(ssh.Executes);
        Assert.Empty(payloads.CommitIds);
    }

    private async Task<(int Code, string Stdout, string Stderr)> Cli(PoolSsh ssh, BaselinePayloads payloads, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await LanQueue.Cli.Run(
            ParsedArgs.Parse(args), ConfigLoader.Parse(Fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, _dir, Repo, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData(BaselineVerdict.New, null, "; new: the base commit passes")]
    [InlineData(BaselineVerdict.Preexisting, "/l/x.log", "; pre-existing: it fails at the base commit too (log: /l/x.log)")]
    [InlineData(BaselineVerdict.Skipped, null, "; baseline skipped: the snapshot is the base commit")]
    public void NextStepsReasonCarriesVerdict(string verdict, string? logPath, string sentence)
    {
        var b = new BaselineResult("origin/main", BaselinePayloads.BaseSha, verdict, "m") { LogPath = logPath };
        var steps = NextSteps.ForRun(ParsedArgs.Parse(["run", "--baseline", "--", "npm", "test"]), new NextSteps.RunOutcome("Failed", 1, "exit 1", null, null, false, "build-01", null, null, "/l/h.log") { Baseline = b });
        Assert.EndsWith(sentence, Assert.Single(steps).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TimingHistoryIgnoresBaselineRuns()
    {
        var log = Log;
        var now = DateTimeOffset.UtcNow;
        log.Append(new JobRecord("j1", "build-01", "npm test", now.AddMinutes(-5), 60, 0, "Completed") { V = 2, Key = "npm test", Dir = ".", RunSeconds = 55 });
        log.Append(new JobRecord("j2", "build-01", "npm test", now.AddMinutes(-3), 600, 0, "Completed") { V = 2, Key = "npm test", Dir = ".", RunSeconds = 590, BaselineOf = "j1", Commit = "bbbbbbb" });
        Assert.Equal(2, log.ReadAll().Count);
        Assert.Equal(["j1"], log.ReadHistory(CancellationToken.None).Records.Select(r => r.Id));
        var history = JobHistory.For(log, "npm test", ".", now);
        var all = JobHistory.From(log.ReadAll(), "npm test", ".", now);
        Assert.Equal(2, all.Runs.Count);
        Assert.Single(history.Runs);
        Assert.Contains("\"baselineOf\":\"j1\"", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
        Assert.DoesNotContain("baselineOf", File.ReadAllLines(log.FilePath)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineForms()
    {
        var plain = ParsedArgs.Parse(["run", "--baseline", "--", "npm", "test"]);
        Assert.True(plain.Baseline);
        Assert.Null(plain.BaselineRef);
        var named = ParsedArgs.Parse(["run", "--baseline-ref", "origin/release", "--", "npm", "test"]);
        Assert.True(named.Baseline);
        Assert.Equal("origin/release", named.BaselineRef);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--baseline-ref", "--json", "--", "npm", "test"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--baseline-ref", "a..b", "--", "npm", "test"]));
        Assert.Contains("--baseline", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--baseline"])).Message, StringComparison.Ordinal);
        Assert.Contains("--baseline-ref", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--baseline-ref", "main"])).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--baseline"]));

        // A suggested re-run keeps both, the ref as its value.
        var rerun = SuggestedCommand.Rerun(ParsedArgs.Parse(["run", "--baseline-ref", "v1.2.0", "--wait", "0", "--", "npm", "test"]), waitSeconds: 120)!;
        Assert.Equal(["lanq", "run", "--baseline-ref", "v1.2.0", "--wait", "120", "--no-wake", "--", "npm", "test"], rerun.Argv);
    }

    /// <summary>Parity: lanq.yml baseline: and baseline_ref: expand to --baseline and --baseline-ref; a job's baseline: false turns both off.</summary>
    [Fact]
    public void PlanKeysExpandToTheFlags()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults:
              baseline_ref: origin/release
            jobs:
              a:
                command: npm test
              b:
                command: npm test
                baseline: false
              c:
                command: npm test
                baseline: true
                baseline_ref: v1.2.0
            """);
        var jobs = plan.Jobs.ToDictionary(j => j.Name);
        Assert.Equal(["run", "--baseline-ref", "origin/release", "--", "npm", "test"], jobs["a"].RunArgs);
        Assert.True(jobs["a"].Args.Baseline);
        Assert.False(jobs["b"].Args.Baseline);
        Assert.Equal("v1.2.0", jobs["c"].Args.BaselineRef);

        var on = PlanFileLoader.Parse("""
            version: 1
            jobs:
              a:
                command: npm test
                baseline: true
            """).Jobs[0];
        Assert.Equal(["run", "--baseline", "--", "npm", "test"], on.RunArgs);
        Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse("version: 1\njobs:\n  a:\n    command: npm test\n    baseline: maybe\n"));
    }

    // ---- The base commit, on real repositories ----

    private TempRepo NewRepo()
    {
        var r = new TempRepo();
        _repos.Add(r);
        return r;
    }

    private static string Commit(TempRepo r, string file) => r.Commit(file, file + Guid.NewGuid().ToString("N"), file);

    /// <summary>A bare origin for <paramref name="r"/>: its branches pushed, fetched back as origin/*, origin/HEAD set to <paramref name="head"/>.</summary>
    private TempRepo Origin(TempRepo r, string head)
    {
        var origin = NewRepo();
        var bare = Path.Combine(origin.Dir, "origin.git");
        r.Git("init", "-q", "--bare", bare);
        r.Git("remote", "add", "origin", bare);
        r.Git("push", "-q", "origin", "--all");
        r.Git("fetch", "-q", "origin");
        r.Git("remote", "set-head", "origin", head);
        return origin;
    }

    [Fact]
    public async Task MergeBaseWithOriginHead()
    {
        var r = NewRepo();
        var root = r.Git("rev-parse", "HEAD").Trim();
        Commit(r, "m2.txt"); // main moves on
        r.Git("checkout", "-q", "-b", "trunk", root);
        var t1 = Commit(r, "t1.txt");
        r.Git("checkout", "-q", "-b", "feature");
        Commit(r, "f1.txt");
        Origin(r, "trunk"); // origin/HEAD -> origin/trunk, although origin/main exists too

        var res = await BaseRef.ResolveAsync(r.Dir, null, CancellationToken.None);

        Assert.Null(res.Error);
        Assert.Equal("origin/trunk", res.Base!.Ref);
        Assert.Equal(t1, res.Base.Sha);
        Assert.Equal("origin/trunk", res.Base.Label); // the merge-base is the branch's tip

        // origin/main: the merge-base with HEAD, not main's tip.
        var main = await BaseRef.ResolveAsync(r.Dir, "origin/main", CancellationToken.None);
        Assert.Equal(root, main.Base!.Sha);
        Assert.Equal("merge-base with origin/main", main.Base.Label);
    }

    [Fact]
    public async Task FallsBackToMainThenMaster()
    {
        var r = NewRepo();
        var root = r.Git("rev-parse", "HEAD").Trim();
        r.Git("checkout", "-q", "-b", "feature");
        Commit(r, "f1.txt");
        var onMain = await BaseRef.ResolveAsync(r.Dir, null, CancellationToken.None);
        Assert.Equal(("main", root), (onMain.Base!.Ref, onMain.Base.Sha));

        r.Git("branch", "-m", "main", "master");
        var onMaster = await BaseRef.ResolveAsync(r.Dir, null, CancellationToken.None);
        Assert.Equal(("master", root), (onMaster.Base!.Ref, onMaster.Base.Sha));

        r.Git("branch", "-m", "master", "dev");
        var none = await BaseRef.ResolveAsync(r.Dir, null, CancellationToken.None);
        Assert.Null(none.Base);
        Assert.Contains("no default branch found", none.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefWithNoCommonHistoryIsRefused()
    {
        var r = NewRepo();
        r.Git("checkout", "-q", "--orphan", "other");
        Commit(r, "o.txt");
        var res = await BaseRef.ResolveAsync(r.Dir, "main", CancellationToken.None);
        Assert.Null(res.Base);
        Assert.Equal("--baseline-ref main is not an ancestor of HEAD (no common history).", res.Error);
    }

    /// <summary>On the base commit with uncommitted changes, the base is HEAD itself, a clean tree; the snapshot differs, so the rerun runs.</summary>
    [Fact]
    public async Task OnTheBaseWithChangesUsesHeadClean()
    {
        var r = NewRepo();
        var head = r.Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(r.Dir, "wip.txt"), "uncommitted");
        var res = await BaseRef.ResolveAsync(r.Dir, null, CancellationToken.None);
        Assert.Equal(head, res.Base!.Sha);
        Assert.Equal("main", res.Base.Label);

        var builder = new GitPayloadBuilder();
        var snapshot = await builder.BuildAsync("s1", r.Dir, r.Out, PayloadKinds.None, CancellationToken.None);
        Assert.NotEqual(head, snapshot.Sha);
        Assert.Equal("", JobRunner.PreviewBaseline(res.Base, snapshot.Sha).Verdict); // would run

        File.Delete(Path.Combine(r.Dir, "wip.txt"));
        var clean = await builder.BuildAsync("s2", r.Dir, r.Out, PayloadKinds.None, CancellationToken.None);
        Assert.Equal(BaselineVerdict.Skipped, JobRunner.PreviewBaseline(res.Base, clean.Sha).Verdict);
    }

    /// <summary>The base payload is the base commit's own tree: none of the uncommitted overlay, none of the later commits; incremental on HEAD's base.</summary>
    [Fact]
    public async Task PayloadIsTheBaseTreeNotTheOverlay()
    {
        var r = NewRepo();
        var c1 = Commit(r, "base.txt");
        Commit(r, "later.txt");
        var head = r.Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(r.Dir, "wip.txt"), "uncommitted");
        File.WriteAllText(Path.Combine(r.Dir, "a.txt"), "changed");

        var builder = new GitPayloadBuilder();
        var p = await builder.BuildCommitAsync("base1", r.Dir, r.Out, c1, PayloadKinds.Bundle, CancellationToken.None);

        Assert.Equal(c1, p.Sha);
        Assert.Equal(c1, p.Head);
        Assert.Empty(p.ChangedFiles);
        Assert.Equal("", p.OverlayDiff);
        Assert.Equal([".gitignore", "a.txt", "base.txt"], p.CommittedFiles!.Order(StringComparer.Ordinal));
        Assert.NotNull(p.RepoId);

        // The worker holds the newer HEAD: the base's bundle is incremental, on the base commit's parent, and carries the base commit.
        var bundle = await builder.BuildBundleAsync("base1", r.Dir, r.Out, c1, head, CancellationToken.None);
        Assert.Equal(head, bundle.Base);
        Assert.Contains(c1 + " refs/lanq/base1", r.Git("bundle", "list-heads", bundle.Path), StringComparison.Ordinal);
        Assert.Contains("-" + r.Git("rev-parse", c1 + "^").Trim(), File.ReadAllText(bundle.Path), StringComparison.Ordinal);
        Assert.Empty(r.Git("for-each-ref", "refs/lanq"));
    }

    /// <summary>A shallow clone without the merge-base: not refused (the job can run); the base is unknown and the baseline inconclusive.</summary>
    [Fact]
    public async Task ShallowCloneWithoutTheMergeBaseIsInconclusive()
    {
        var src = NewRepo();
        Commit(src, "m2.txt");
        src.Git("checkout", "-q", "-b", "feature", "HEAD~1");
        Commit(src, "f1.txt");
        src.Git("checkout", "-q", "main");
        var clone = NewRepo();
        var dir = Path.Combine(clone.Dir, "shallow");
        clone.Git("clone", "-q", "--depth", "1", "--no-single-branch", "--branch", "feature", new Uri(src.Dir).AbsoluteUri, dir);

        var res = await BaseRef.ResolveAsync(dir, null, CancellationToken.None);

        Assert.Null(res.Error);
        Assert.Null(res.Base!.Sha);
        Assert.Equal(BaseRef.ShallowProblem, res.Base.Problem);
    }
}
