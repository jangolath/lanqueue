using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run --plan: lanq.yml jobs run through Cli.RunPlanFile on the fake pool (Jobs/JobRunner.PlanFile.cs, Cli.PlanFile.cs).</summary>
public sealed partial class PlanFileRunTests : IDisposable
{
    private const int TestTimeout = 60_000;

    internal const string Fleet = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npm run", "npx jest"]
          allowed_directories: ["."]
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 2
          - name: big-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 2
        """;

    /// <summary>One machine with one slot: a second job of the plan must wait for the first.</summary>
    internal const string Solo = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npm run"]
          allowed_directories: ["."]
        machines:
          - name: solo-01
            os: linux
            ssh: user@10.20.30.21
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 1
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-planrun-" + Guid.NewGuid().ToString("N"));

    public PlanFileRunTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Home);
    }

    public void Dispose() => TestTree.Delete(_root);

    private string Repo => Path.Combine(_root, "repo");

    private string Home => Path.Combine(_root, "home");

    private JobLog Log => new(Path.Combine(Home, "state"));

    private void Plan(string yaml, string name = "lanq.yml") => File.WriteAllText(Path.Combine(Repo, name), yaml);

    /// <summary>build, unit and lint: three independent jobs.</summary>
    private const string Three = """
        version: 1
        defaults:
          needs: [node]
        jobs:
          build:
            command: npm run build
          unit:
            command: npm run unit
          lint:
            command: npm run lint
        """;

    [GeneratedRegex("LANQ_PLAN_JOB=([A-Za-z0-9_-]+)", RegexOptions.None, 5000)]
    private static partial Regex PlanJobRegex();

    /// <summary>The plan job an execute call runs for (from its LANQ_PLAN_JOB).</summary>
    internal static string JobOf(PoolSsh.Exec e) => PlanJobRegex().Match(e.Command).Groups[1].Value;

    private async Task<(int Code, string Stdout, string Stderr)> Run(
        PoolSsh ssh, RunnerTests.FakePayloads payloads, string[] args, CancellationToken ct = default, string? repoDir = null, string fleet = Fleet)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", .. args]), ConfigLoader.Parse(fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Home, repoDir ?? Repo, o, e, ct,
            (_, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));
        return (code, o.ToString(), e.ToString());
    }

    private Task<(int Code, string Stdout, string Stderr)> Run(PoolSsh ssh, params string[] args) => Run(ssh, new RunnerTests.FakePayloads(""), args);

    private static JsonElement Json(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout); // exactly one JSON value
        return doc.RootElement.Clone();
    }

    private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    private static Dictionary<string, JsonElement> JobsOf(JsonElement json) =>
        json.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j, StringComparer.Ordinal);

    [Fact(Timeout = TestTimeout)]
    public async Task EveryJobIsCheckedBeforeAnythingIsSent()
    {
        Plan("""
            version: 1
            jobs:
              build:
                command: npm run build
              deploy:
                command: [rm, -rf, dist]
            """);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, stderr) = await Run(ssh, payloads, ["--plan", "--json"]);

        Assert.Equal(2, code);
        var json = Json(stdout);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.StartsWith("job 'deploy': ", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("allowed_commands", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(json.GetProperty("jobs").EnumerateArray());
        Assert.Equal("ask_developer", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.Empty(ssh.Calls); // not even a probe
        Assert.Equal(0, payloads.Built); // no snapshot
        Assert.StartsWith("[lanq] Rejected: job 'deploy': ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShardCommandLinesAreCheckedToo()
    {
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");
        // A token-shaped argument once {shard} is substituted (built here so this file itself holds no token).
        var token = "gh" + "p_abcdefghijklmnopqrstuvwxyz012345678";
        Plan($$"""
            version: 1
            jobs:
              unit:
                command: [npx, jest, "--x={{token}}{shard}"]
                shards: 2
            """);

        var (code, stdout, _) = await Run(ssh, payloads, ["--plan", "--json"]);

        Assert.Equal(2, code);
        Assert.Contains("job 'unit': shard 1", Json(stdout).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AJobThatCanNeverRunEndsThePlanBeforeTheSnapshot()
    {
        Plan("""
            version: 1
            jobs:
              build:
                command: npm run build
              gpu:
                command: npm run train
                needs: [cuda]
            """);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, stderr) = await Run(ssh, payloads, ["--plan", "--json"]);

        Assert.Equal(75, code);
        var json = Json(stdout);
        Assert.Equal(("NoCapacity", false), (json.GetProperty("status").GetString(), json.GetProperty("retryable").GetBoolean()));
        var jobs = JobsOf(json);
        Assert.Equal("NoCapacity", jobs["gpu"].GetProperty("status").GetString());
        Assert.Equal("missing_capability", jobs["gpu"].GetProperty("excluded")[0].GetProperty("reason").GetString());
        Assert.Equal("Skipped", jobs["build"].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, jobs["build"].GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, jobs["build"].GetProperty("startedSeconds").ValueKind);
        Assert.Equal(["ask_developer", "run_health"], json.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetProperty("action").GetString()));
        Assert.Equal(["lanq", "health", "--needs", "cuda", "--json"], json.GetProperty("nextSteps")[1].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
        Assert.StartsWith("[lanq] NoCapacity: ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OneSnapshotForTheWholePlan()
    {
        Plan(Three);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, _) = await Run(ssh, payloads, ["--plan", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(1, payloads.Built);
        Assert.Equal(3, ssh.Executes.Count);
        Assert.All(JobsOf(Json(stdout)).Values, j => Assert.Equal("Completed", j.GetProperty("status").GetString()));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task IndependentJobsRunInParallel()
    {
        Plan(Three);
        var ssh = new PoolSsh();
        var all = new Countdown(3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.OnExecute = async (e, ct) =>
        {
            all.Signal();
            await release.Task.WaitAsync(ct);
            return e.Exit(0);
        };

        var run = Run(ssh, "--plan");
        await all.Reached; // every job is running at once before any may finish
        release.SetResult();
        var (code, _, _) = await run;

        Assert.Equal(0, code);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AfterWaitsForCompleted()
    {
        Plan("""
            version: 1
            jobs:
              integration:
                command: npm run integration
              serial:
                command: npm run serial
                after: [integration]
            """);
        var ssh = new PoolSsh();
        var events = new List<string>();
        ssh.OnExecute = (e, _) =>
        {
            lock (events)
            {
                events.Add("start " + JobOf(e));
                events.Add("end " + JobOf(e));
            }

            return Task.FromResult(e.Exit(0));
        };

        var (code, stdout, _) = await Run(ssh, "--plan", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["start integration", "end integration", "start serial", "end serial"], events);
        var json = Json(stdout);
        Assert.Equal(["integration", "serial"], json.GetProperty("order").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["integration"], JobsOf(json)["serial"].GetProperty("after").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AfterAFailedJobIsSkipped()
    {
        Plan("""
            version: 1
            jobs:
              integration:
                command: npm run integration
              serial:
                command: npm run serial
                after: [integration]
            """);
        var ssh = new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(JobOf(e) == "integration" ? 3 : 0)) };

        var (code, stdout, stderr) = await Run(ssh, "--plan");

        Assert.Equal(3, code); // the failed job's own exit code
        Assert.Single(ssh.Executes);
        Assert.Contains("[lanq] serial", stdout, StringComparison.Ordinal);
        Assert.Contains("Skipped", stdout, StringComparison.Ordinal);
        Assert.Contains("(not started: integration ended Failed)", stdout, StringComparison.Ordinal);
        Assert.StartsWith("[lanq] Failed: 0 of 2 job(s) completed; integration: Failed: exit 3", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OthersKeepRunningWhenOneFails()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(JobOf(e) == "build" ? 1 : 0)) };

        var (code, stdout, _) = await Run(ssh, "--plan", "--json");

        Assert.Equal(1, code);
        var jobs = JobsOf(Json(stdout));
        Assert.Equal(("Failed", "Completed", "Completed"), (jobs["build"].GetProperty("status").GetString(), jobs["unit"].GetProperty("status").GetString(), jobs["lint"].GetProperty("status").GetString()));
        var fix = Json(stdout).GetProperty("nextSteps")[0];
        Assert.Equal("fix_code", fix.GetProperty("action").GetString());
        Assert.Contains("build exited 1 (log: ", fix.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FailFastCancelsRunningAndSkipsRest()
    {
        Plan("""
            version: 1
            jobs:
              long:
                command: npm run long
              broken:
                command: npm run broken
              tail:
                command: npm run tail
                after: [long]
            """);
        var ssh = new PoolSsh();
        ssh.OnExecute = async (e, ct) =>
        {
            if (JobOf(e) == "broken")
            {
                return e.Exit(4);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // long runs until cancelled
            return e.Exit(0);
        };

        var (code, stdout, stderr) = await Run(ssh, "--plan", "--fail-fast", "--json");

        Assert.Equal(4, code);
        var json = Json(stdout);
        var jobs = JobsOf(json);
        Assert.Equal("Failed", json.GetProperty("status").GetString());
        Assert.Equal(("Cancelled", 130), (jobs["long"].GetProperty("status").GetString(), jobs["long"].GetProperty("exitCode").GetInt32()));
        Assert.Contains("--fail-fast", jobs["long"].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, jobs["long"].GetProperty("logPath").ValueKind); // it had started: its log is there
        Assert.Equal("Skipped", jobs["tail"].GetProperty("status").GetString());
        Assert.Contains("--fail-fast", jobs["tail"].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal((0, 0), (ssh.Used("build-01"), ssh.Used("big-01"))); // every started job was cleaned up
        Assert.Contains("[lanq] --fail-fast: job broken Failed", stderr, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WaitCountsOnlyWhileNoPlanJobHoldsASlot()
    {
        Plan("""
            version: 1
            jobs:
              first:
                command: npm run first
              second:
                command: npm run second
            """);
        var ssh = new PoolSsh();
        var firstRuns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.OnExecute = async (e, ct) =>
        {
            if (JobOf(e) == "first")
            {
                firstRuns.SetResult();
                await release.Task.WaitAsync(ct);
            }

            return e.Exit(0);
        };

        // --wait 0: with one slot, the second job waits behind the first (a plain run would end NoCapacity at once), then runs.
        var run = Run(ssh, new RunnerTests.FakePayloads(""), ["--plan", "--json", "--wait", "0"], fleet: Solo);
        await firstRuns.Task;
        release.SetResult();
        var (code, stdout, stderr) = await run;

        Assert.Equal(0, code);
        Assert.Equal(2, ssh.Executes.Count);
        Assert.Equal(1, ssh.Peak["solo-01"]);
        Assert.Contains("[second] [lanq] no eligible machine; waiting for a slot (this plan's other jobs hold some)", stderr, StringComparison.Ordinal);
        Assert.Equal("Completed", Json(stdout).GetProperty("status").GetString());
    }

    /// <summary>
    /// --wait 0: the second job's look at the machines sees the one slot taken, and its answer comes back only after the first job has given
    /// the slot back. The second job must look again (the slot is free now), not end NoCapacity because nothing of the plan holds a slot
    /// any more. The same for a job with shards.
    /// </summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData("")]
    [InlineData("    shards: 1")]
    public async Task AJobThatSawTheSlotTakenJustBeforeASiblingGaveItBackLooksAgain(string secondExtra)
    {
        Plan("""
            version: 1
            jobs:
              first:
                command: npm run first
              second:
                command: npm run second
            """ + "\n" + secondExtra + "\n");
        var ssh = new PoolSsh();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = 0;
        ssh.OnExecute = async (e, ct) =>
        {
            if (JobOf(e) == "first")
            {
                await release.Task.WaitAsync(ct);
            }

            return e.Exit(0);
        };
        ssh.AfterProbe = async (m, r, ct) =>
        {
            if (r.Output.Contains("slots_used=1", StringComparison.Ordinal) && Interlocked.Exchange(ref gated, 1) == 0)
            {
                release.SetResult(); // the first job ends while this answer is on its way back
                while (ssh.Used(m.Name) != 0)
                {
                    await Task.Delay(10, ct);
                }

                await Task.Delay(500, ct); // the first job gives its slot back to the plan right after its cleanup
            }

            return r;
        };

        var (code, stdout, stderr) = await Run(ssh, new RunnerTests.FakePayloads(""), ["--plan", "--json", "--wait", "0"], fleet: Solo);

        Assert.Equal(1, gated);
        Assert.True(code == 0, stderr);
        Assert.Equal(2, ssh.Executes.Count);
        Assert.Equal("Completed", Json(stdout).GetProperty("status").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WithNothingOfThePlanRunningWaitZeroEndsAtOnce()
    {
        Plan("""
            version: 1
            jobs:
              only:
                command: npm run only
            """);
        var ssh = new PoolSsh { RefuseSlot = _ => true };

        var (code, stdout, _) = await Run(ssh, new RunnerTests.FakePayloads(""), ["--plan", "--json"], fleet: Solo);

        Assert.Equal(75, code);
        var json = Json(stdout);
        Assert.True(json.GetProperty("retryable").GetBoolean());
        Assert.Equal("wait", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.Equal(["lanq", "run", "--plan", "--json", "--only", "only", "--wait", "120", "--no-wake"], json.GetProperty("nextSteps")[0].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void StatusAndExitFollowTheWorstJob()
    {
        static PlanJobOutcome J(string name, string status, int exit) => new(name, status, exit, "m", []);

        var failed = JobRunner.AggregatePlan([J("a", "Completed", 0), J("b", "NoCapacity", 75), J("c", "Failed", 3), J("d", "Failed", 5), J("e", "Skipped", 0)], false);
        Assert.Equal(("Failed", 3, "c"), (failed.Status, failed.ExitCode, failed.Decided));
        Assert.StartsWith("1 of 5 job(s) completed; c: Failed: m", failed.Message, StringComparison.Ordinal);

        Assert.Equal(("Rejected", 2), Pick(J("a", "TimedOut", 124), J("b", "Rejected", 2)));
        Assert.Equal(("TimedOut", 124), Pick(J("a", "InfraError", 70), J("b", "TimedOut", 124), J("c", "WindowClosed", 124)));
        Assert.Equal(("InfraError", 70), Pick(J("a", "NoCapacity", 75), J("b", "InfraError", 70)));
        Assert.Equal(("NoCapacity", 75), Pick(J("a", "Cancelled", 130), J("b", "NoCapacity", 75), J("c", "Skipped", 0)));
        Assert.Equal(("Completed", 0), Pick(J("a", "Completed", 0)));
        var cancelled = JobRunner.AggregatePlan([J("a", "Failed", 1), J("b", "Cancelled", 130)], cancelled: true);
        Assert.Equal(("Cancelled", 130), (cancelled.Status, cancelled.ExitCode));

        static (string, int) Pick(params PlanJobOutcome[] jobs) => JobRunner.AggregatePlan(jobs, false) is var r ? (r.Status, r.ExitCode) : default;
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LinesArePrefixedWithJobAndShard()
    {
        Plan("""
            version: 1
            defaults:
              needs: [node]
            jobs:
              build:
                command: npm run build
              unit:
                command: ["npx", "jest", "--shard={shard}/{shards}"]
                shards: 2
            """);
        var ssh = new PoolSsh();

        var (code, stdout, stderr) = await Run(ssh, "--plan");

        Assert.Equal(0, code);
        Assert.Matches(@"(?m)^\[build (big|build)-01\] output of ", stdout);
        Assert.Matches(@"(?m)^\[unit 1/2 (big|build)-01\] output of ", stdout);
        Assert.Matches(@"(?m)^\[unit 2/2 (big|build)-01\] output of ", stdout);
        Assert.Contains("[lanq] plan lanq.yml: 2 job(s); order: build, unit", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", stderr, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StaticWarningsGoToStderrAndJson()
    {
        Plan("""
            version: 1
            jobs:
              unit:
                command: npx jest
                shards: 2
            """);

        var (code, stdout, stderr) = await Run(new PoolSsh(), "--plan", "--json");

        Assert.Equal(0, code);
        var warning = Assert.Single(Json(stdout).GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Contains("job 'unit' has shards:", warning, StringComparison.Ordinal);
        Assert.Contains("[lanq] lanq.yml: warning: " + warning, stderr, StringComparison.Ordinal);

    }

    [Fact(Timeout = TestTimeout)]
    public async Task SummaryTableThenNextThenStatusLineLast()
    {
        Plan("""
            version: 1
            jobs:
              integration:
                command: npm run integration
              serial:
                command: npm run serial
                after: [integration]
            """);
        var ssh = new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(1)) };

        var (code, stdout, stderr) = await Run(ssh, "--plan", "--json");

        Assert.Equal(1, code);
        var lines = Lines(stderr);
        var table = Array.FindIndex(lines, l => l.StartsWith("[lanq] job ", StringComparison.Ordinal));
        var next = Array.FindIndex(lines, l => l.StartsWith("[lanq] Next: ", StringComparison.Ordinal));
        Assert.InRange(table, 0, next - 1);
        Assert.Matches(@"^\[lanq\] integration +- +(big|build)-01 +Failed +1 +[0-9.]+s$", lines[table + 1]);
        Assert.Matches(@"^\[lanq\] serial +- +- +Skipped +- +[0-9.]+s  \(not started: integration ended Failed\)$", lines[table + 2]);
        Assert.StartsWith("[lanq] Failed: 0 of 2 job(s) completed; integration: Failed: exit 1", lines[^1], StringComparison.Ordinal);
        Assert.Equal("Failed", Json(stdout).GetProperty("status").GetString());

        // Without --json the logs of the jobs that did not complete come after Next, before the status line.
        var text = await Run(new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(1)) }, "--plan");
        var t = Lines(text.Stderr);
        Assert.Matches(@"^\[lanq\] log: .+ \(integration\)$", t[^2]);
        Assert.StartsWith("[lanq] Next: ", t[^3], StringComparison.Ordinal);
    }

    public static TheoryData<string[], string?> JsonPaths => new()
    {
        { ["run", "--plan", "--json", "--needs", "node"], null }, // bad arguments
        { ["run", "--plan", "--dry-run", "--json", "--shards", "2"], null }, // bad arguments, dry run
        { ["run", "--plan", "--json"], "config" }, // the config does not load
        { ["run", "--plan", "--dry-run", "--json"], "config" },
    };

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(JsonPaths))]
    public async Task PlanJsonIsOneObjectOnEveryPath(string[] args, string? failing)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunAsync(args, o, e, CancellationToken.None, () => failing is null ? ConfigLoader.Parse(Fleet) : throw new ConfigException("Config file not found: fleet.yml."));

        Assert.Equal(2, code);
        var json = Json(o.ToString());
        Assert.Single(Lines(o.ToString()));
        Assert.Equal(("Rejected", 2), (json.GetProperty("status").GetString(), json.GetProperty("exitCode").GetInt32()));
        Assert.Empty(json.GetProperty("jobs").EnumerateArray());
        Assert.NotEmpty(json.GetProperty("nextSteps").EnumerateArray());
        Assert.Equal(args.Contains("--dry-run"), json.TryGetProperty("dryRun", out _));
        Assert.True(json.TryGetProperty("order", out _));
        Assert.StartsWith("Next: ", Lines(e.ToString())[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanJsonIsOneObjectOnTheRunPathsToo()
    {
        // Completed, refused (no file, bad file, --only unknown) and Cancelled before anything started.
        Plan(Three);
        Assert.Equal("Completed", Json((await Run(new PoolSsh(), "--plan", "--json")).Stdout).GetProperty("status").GetString());
        foreach (var (args, action) in new[] { (new[] { "--plan", "--json", "--only", "nope" }, "fix_command"), (["--plan-file", "missing.yml", "--json"], "fix_code") })
        {
            var (code, stdout, stderr) = await Run(new PoolSsh(), args);
            Assert.Equal(2, code);
            var json = Json(stdout);
            Assert.Equal("Rejected", json.GetProperty("status").GetString());
            Assert.Equal(action, json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
            Assert.StartsWith("[lanq] Rejected: ", Lines(stderr)[^1], StringComparison.Ordinal);
        }

        Plan("version: 1\njobs:\n  build:\n    command: npm run build\n    hosts: [x]\n");
        var bad = Json((await Run(new PoolSsh(), "--plan", "--json")).Stdout);
        Assert.Equal("fix_code", bad.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.StartsWith("fix lanq.yml: lanq.yml", bad.GetProperty("nextSteps")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("lanq.yml", bad.GetProperty("plan").GetString(), StringComparison.Ordinal);

        Plan(Three);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (c, o, e) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--json"], cts.Token);
        Assert.Equal(130, c);
        Assert.Equal("Cancelled", Json(o).GetProperty("status").GetString());
        Assert.StartsWith("[lanq] Cancelled: ", Lines(e)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCCleansUpEveryStartedJob()
    {
        Plan(Three);
        var ssh = new PoolSsh();
        var two = new Countdown(2);
        ssh.OnExecute = async (e, ct) =>
        {
            if (JobOf(e) == "lint")
            {
                return e.Exit(0);
            }

            two.Signal();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return e.Exit(0);
        };
        using var cts = new CancellationTokenSource();

        var run = Run(ssh, new RunnerTests.FakePayloads(""), ["--plan", "--json"], cts.Token);
        await two.Reached;
        await cts.CancelAsync();
        var (code, stdout, stderr) = await run;

        Assert.Equal(130, code);
        var json = Json(stdout);
        Assert.Equal("Cancelled", json.GetProperty("status").GetString());
        var jobs = JobsOf(json);
        Assert.Equal(3, jobs.Count); // every job so far, each with its status
        Assert.Equal("Cancelled", jobs["build"].GetProperty("status").GetString());
        Assert.Equal("Cancelled", jobs["unit"].GetProperty("status").GetString());
        Assert.Equal((0, 0), (ssh.Used("build-01"), ssh.Used("big-01"))); // both cancelled jobs gave their slots back (cleanup ran)
        Assert.NotEqual(JsonValueKind.Null, jobs["build"].GetProperty("logPath").ValueKind);
        Assert.StartsWith("[lanq] Cancelled: ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task JobsJsonlRecordsPlanJobAndPlanRun()
    {
        Plan("""
            version: 1
            jobs:
              build:
                command: npm run build
              unit:
                command: ["npx", "jest", "--shard={shard}/{shards}"]
                shards: 2
            """);

        var (code, _, _) = await Run(new PoolSsh(), "--plan");

        Assert.Equal(0, code);
        var records = Log.Read(CancellationToken.None).Records;
        Assert.Equal(3, records.Count);
        Assert.Equal(["build", "unit", "unit"], records.Select(r => r.PlanJob!).Order(StringComparer.Ordinal));
        Assert.Single(records.Select(r => r.PlanRun).Distinct());
        Assert.NotNull(records[0].PlanRun);
        var shards = records.Where(r => r.PlanJob == "unit").ToList();
        Assert.Single(shards.Select(r => r.RunId).Distinct());
        Assert.NotEqual(shards[0].RunId, shards[0].PlanRun); // the sharded job is its own run within the plan
        Assert.All(records, r => Assert.Equal(2, r.V));
        Assert.Contains("\"planJob\":\"build\"", File.ReadAllText(Log.FilePath), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LanqPlanJobIsInTheEnvironmentAndWindowsStaysUnderTheGuard()
    {
        Plan(Three);
        var ssh = new PoolSsh();

        await Run(ssh, "--plan");

        Assert.Equal(["build", "lint", "unit"], ssh.Executes.Select(JobOf).Order(StringComparer.Ordinal));
        Assert.All(ssh.Executes, e => Assert.Contains("'LANQ_PLAN_JOB=" + JobOf(e) + "'", e.Command, StringComparison.Ordinal));

        var windows = RemoteScripts.Execute(T.Windows(), "j1", ".", ["dotnet", "test"], JobRequest.DefaultTimeout, null, "build");
        Assert.Contains("$env:LANQ_PLAN_JOB = 'build'", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("LANQ_PLAN_JOB", RemoteScripts.Execute(T.Windows(), "j1", ".", ["dotnet", "test"]), StringComparison.Ordinal);
        var longest = RemoteScripts.Execute(T.Windows(), "20260101-000000-000000", ".", [], JobRequest.DefaultTimeout, new ShardInfo(64, 64), new string('x', 32));
        Assert.True(RemoteScripts.WindowsCommand(longest).Length <= RemoteScripts.MaxWindowsCommandLength);
        foreach (var bad in new[] { "", "-x", "a b", "a'b", new string('x', 33) })
        {
            Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], null, null, bad));
        }
    }

    [Theory]
    [InlineData("--needs", "node")]
    [InlineData("--dir", "src")]
    [InlineData("--timeout", "10")]
    [InlineData("--on", "big-01")]
    [InlineData("--min-size", "heavy")]
    [InlineData("--shards", "2")]
    [InlineData("--fetch", "x")]
    public void RunOnlyOptionsAreRefusedWithPlan(string option, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", option, value]));
        Assert.StartsWith($"with --plan each job's {option} comes from lanq.yml", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--", "npm", "run", "build"]));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanFileFoundFromASubfolder()
    {
        Plan(Three);
        var sub = Path.Combine(Repo, "src", "app");
        Directory.CreateDirectory(sub);

        var (code, stdout, _) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--json"], repoDir: sub);

        Assert.Equal(0, code);
        Assert.Equal(Path.Combine(Repo, "lanq.yml"), Json(stdout).GetProperty("plan").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanFileOptionOverridesLocation()
    {
        Plan(Three);
        Directory.CreateDirectory(Path.Combine(Repo, "ci"));
        Plan("version: 1\njobs:\n  other:\n    command: npm run other\n", Path.Combine("ci", "other.yml"));
        var ssh = new PoolSsh();

        var (code, stdout, _) = await Run(ssh, new RunnerTests.FakePayloads(""), ["--plan-file", Path.Combine(Repo, "ci", "other.yml"), "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(["other"], Json(stdout).GetProperty("order").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["other"], ssh.Executes.Select(JobOf));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MissingPlanFileSaysHowToWriteOne()
    {
        var (code, stdout, stderr) = await Run(new PoolSsh(), "--plan", "--json");

        Assert.Equal(2, code);
        var json = Json(stdout);
        Assert.Contains("No lanq.yml in ", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("docs/PLANS.md", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("plan").ValueKind);
        Assert.Equal("fix_code", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.StartsWith("[lanq] Rejected: No lanq.yml in ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnlyRunsTheSelectedJobs()
    {
        Plan("""
            version: 1
            jobs:
              integration:
                command: npm run integration
              serial:
                command: npm run serial
                after: [integration]
              lint:
                command: npm run lint
            """);
        var ssh = new PoolSsh();

        var (code, stdout, stderr) = await Run(ssh, "--plan", "--only", "serial", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["serial"], ssh.Executes.Select(JobOf));
        Assert.Equal(["serial"], Json(stdout).GetProperty("order").EnumerateArray().Select(o => o.GetString()));
        Assert.Empty(JobsOf(Json(stdout))["serial"].GetProperty("after").EnumerateArray());
        Assert.Contains("[lanq] --only: serial starts without waiting for integration (not selected)", stderr, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RetrySuggestionsAddOnlyTheJobsThatDidNotComplete()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = (e, _) => Task.FromResult(JobOf(e) == "lint" ? new SshResult(255, "ssh: connection reset") : e.Exit(0)) };

        var (code, stdout, _) = await Run(ssh, "--plan", "--json", "--wait", "30");

        Assert.Equal(70, code);
        var step = Json(stdout).GetProperty("nextSteps")[0];
        Assert.Equal("retry", step.GetProperty("action").GetString());
        Assert.Equal(["lanq", "run", "--plan", "--json", "--wait", "30", "--only", "lint"], step.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.True(step.GetProperty("safeToRunUnattended").GetBoolean());
    }
}
