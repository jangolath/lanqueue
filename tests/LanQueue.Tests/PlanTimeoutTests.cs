using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Plan;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run --plan --plan-timeout M / lanq.yml plan_timeout: M (0.10.0): the whole plan's limit (Jobs/JobRunner.PlanFile.cs, PlanDeadline).</summary>
public sealed partial class PlanFileRunTests
{
    /// <summary>
    /// Runs the plan with a plan timer the test fires (<paramref name="fire"/>): the plan's limit is the only wait of <paramref name="minutes"/>
    /// minutes; every other wait (the stagger, the 15 s looks) waits until cancelled, as in the other plan tests.
    /// </summary>
    private async Task<(int Code, string Stdout, string Stderr)> RunTimed(
        PoolSsh ssh, IPayloadBuilder payloads, TaskCompletionSource fire, string[] args, int minutes = 1, string fleet = Fleet, CancellationToken ct = default)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", .. args]), ConfigLoader.Parse(fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Home, Repo, o, e, ct,
            (d, c) => d == TimeSpan.FromMinutes(minutes) ? fire.Task.WaitAsync(c) : Task.Delay(Timeout.InfiniteTimeSpan, c));
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>Every job blocks on its machine until it is cancelled (Ctrl+C, --fail-fast or --plan-timeout).</summary>
    private static async Task<SshResult> Hang(PoolSsh.Exec e, CancellationToken c)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, c);
        return e.Exit(0);
    }

    /// <summary>Waits (a few seconds at most) until <paramref name="done"/> holds.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 1000 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(done());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanTimeoutStopsRunningJobsAsTimedOut()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = Hang };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--json"]);
        await Until(() => ssh.Executes.Count == 3);
        fire.SetResult();
        var (code, stdout, stderr) = await run;

        Assert.Equal(124, code);
        var json = Json(stdout);
        Assert.Equal("TimedOut", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("planTimedOut").GetBoolean());
        Assert.Equal(1, json.GetProperty("planTimeoutMinutes").GetInt32());
        Assert.All(JobsOf(json).Values, j =>
        {
            Assert.Equal("TimedOut", j.GetProperty("status").GetString());
            Assert.Equal(124, j.GetProperty("exitCode").GetInt32());
            Assert.Equal("plan_timeout 1 min reached; cancelled and cleaned up", j.GetProperty("message").GetString());
            Assert.NotNull(j.GetProperty("machine").GetString());
        });
        Assert.Equal(0, ssh.Used("build-01") + ssh.Used("big-01")); // every slot given back: each job was cleaned up
        Assert.Contains("[lanq] plan_timeout: 1 min reached; stopping 3 running job(s)", stderr, StringComparison.Ordinal);
        Assert.Contains("[lanq] plan_timeout: 1 min for the whole plan (--plan-timeout), from after the snapshot", stderr, StringComparison.Ordinal);
        var step = json.GetProperty("nextSteps")[0];
        Assert.Equal("fix_code", step.GetProperty("action").GetString());
        Assert.Contains("raise plan_timeout in lanq.yml or pass --plan-timeout", step.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.False(step.TryGetProperty("argv", out var argv) && argv.ValueKind != System.Text.Json.JsonValueKind.Null); // never a command that raises it
        Assert.StartsWith("[lanq] TimedOut: 0 of 3 job(s) completed; plan_timeout 1 min reached", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task JobsJsonlRecordsTimedOut()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = Hang };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1"]);
        await Until(() => ssh.Executes.Count == 3);
        fire.SetResult();
        await run;

        var records = Log.ReadAll();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal("TimedOut", r.Status));
        Assert.All(records, r => Assert.Equal(124, r.ExitCode));
        Assert.Equal(["build", "lint", "unit"], records.Select(r => r.PlanJob).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Cancelled", File.ReadAllText(Log.FilePath), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SlotWaitingJobsEndTimedOut()
    {
        Plan("""
            version: 1
            jobs:
              first:
                command: npm run first
                needs: [node]
              second:
                command: npm run second
                needs: [node]
            """);
        var ssh = new PoolSsh { OnExecute = Hang };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--json"], fleet: Solo);
        await Until(() => ssh.Executes.Count == 1 && ssh.Calls.Count(c => c.Command.Contains("disk_free_gb", StringComparison.Ordinal)) >= 2);
        fire.SetResult();
        var (code, stdout, _) = await run;

        Assert.Equal(124, code);
        var jobs = JobsOf(Json(stdout));
        var running = jobs.Values.Single(j => j.GetProperty("machine").ValueKind == System.Text.Json.JsonValueKind.String);
        var waiting = jobs.Values.Single(j => j.GetProperty("machine").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.Equal("TimedOut", running.GetProperty("status").GetString());
        Assert.Equal("TimedOut", waiting.GetProperty("status").GetString());
        Assert.Equal(124, waiting.GetProperty("exitCode").GetInt32());
        Assert.Equal(PlanDeadline.WaitingMessage, waiting.GetProperty("message").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, waiting.GetProperty("jobId").ValueKind);
        Assert.Single(ssh.Executes); // the waiting job never ran
        Assert.Equal(0, ssh.Used("solo-01"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NotStartedJobsAreSkipped()
    {
        Plan("""
            version: 1
            defaults:
              needs: [node]
            jobs:
              build:
                command: npm run build
              unit:
                command: npm run unit
                after: [build]
            """);
        var ssh = new PoolSsh { OnExecute = Hang };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--json"]);
        await Until(() => ssh.Executes.Count == 1);
        fire.SetResult();
        var (code, stdout, _) = await run;

        Assert.Equal(124, code);
        var jobs = JobsOf(Json(stdout));
        Assert.Equal("TimedOut", jobs["build"].GetProperty("status").GetString());
        Assert.Equal("Skipped", jobs["unit"].GetProperty("status").GetString());
        Assert.Equal(PlanDeadline.NotStartedMessage, jobs["unit"].GetProperty("message").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, jobs["unit"].GetProperty("exitCode").ValueKind);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExpiryWithNothingRunningIs124()
    {
        // Every job queued, none running: the only machine refuses every claim (another user's jobs fill it), and --wait keeps them waiting.
        Plan(Three);
        var ssh = new PoolSsh { RefuseSlot = _ => true };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--wait", "600", "--json"], fleet: Solo);
        await Until(() => ssh.Calls.Count(c => c.Command.Contains("SLOT=", StringComparison.Ordinal)) >= 3);
        fire.SetResult();
        var (code, stdout, stderr) = await run;

        Assert.Equal(124, code);
        var json = Json(stdout);
        Assert.Equal("TimedOut", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("planTimedOut").GetBoolean());
        Assert.Empty(ssh.Executes);
        Assert.All(JobsOf(json).Values, j => Assert.True(
            j.GetProperty("status").GetString() == "Skipped" || j.GetProperty("message").GetString() == PlanDeadline.WaitingMessage));
        Assert.Contains("stopping 3 running job(s)", stderr, StringComparison.Ordinal);

        // Between after: waves (nothing running, nothing waiting): still TimedOut 124, decided by no job.
        var between = JobRunner.PlanTimedOutResult(
            JobRunner.AggregatePlan(
                [
                    new PlanJobOutcome("build", "Completed", 0, "exit 0", []) { FileOrder = 0 },
                    new PlanJobOutcome("unit", PlanJobOutcome.Skipped, 0, PlanDeadline.NotStartedMessage, ["build"]) { FileOrder = 1 },
                ],
                cancelled: false),
            new PlanDeadline(5));
        Assert.Equal(("TimedOut", 124), (between.Status, between.ExitCode));
        Assert.Null(between.Decided);
        Assert.Equal("1 of 2 job(s) completed; plan_timeout 5 min reached", between.Message);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FailedBeforeTimeoutStillDecides()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = (e, c) => JobOf(e) == "lint" ? Task.FromResult(e.Exit(3)) : Hang(e, c) };
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--json"]);
        await Until(() => ssh.Executes.Count == 3 && Log.ReadAll().Any(r => r.PlanJob == "lint"));
        fire.SetResult();
        var (code, stdout, _) = await run;

        Assert.Equal(3, code);
        var json = Json(stdout);
        Assert.Equal("Failed", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("planTimedOut").GetBoolean());
        var jobs = JobsOf(json);
        Assert.Equal("TimedOut", jobs["build"].GetProperty("status").GetString());
        Assert.Equal("TimedOut", jobs["unit"].GetProperty("status").GetString());
        Assert.Equal("fix_code", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
        Assert.Contains("lint exited 3", json.GetProperty("nextSteps")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedStillDecides()
    {
        var jobs = new List<PlanJobOutcome>
        {
            new("build", "TimedOut", 124, "plan_timeout 5 min reached; cancelled and cleaned up", []) { FileOrder = 0 },
            new("lint", "Rejected", 2, "the Windows wrapper refused it", []) { FileOrder = 1 },
            new("unit", PlanJobOutcome.Skipped, 0, PlanDeadline.NotStartedMessage, []) { FileOrder = 2 },
        };

        var result = JobRunner.PlanTimedOutResult(JobRunner.AggregatePlan(jobs, cancelled: false), new PlanDeadline(5));

        Assert.Equal(("Rejected", 2, "lint"), (result.Status, result.ExitCode, result.Decided));

        // A plan job that ended Cancelled because the limit was reached becomes TimedOut, and a sharded one is aggregated again.
        var deadline = new PlanDeadline(5);
        var waiting = JobRunner.PlanTimedOutJob(new PlanJobOutcome("unit", JobRunner.Cancelled, 130, "cancelled before it started", []), deadline);
        Assert.Equal(("TimedOut", 124, PlanDeadline.WaitingMessage), (waiting.Status, waiting.ExitCode, waiting.Message));
        var sharded = new ShardRunResult(JobRunner.Cancelled, 130, 2, [
            new ShardResult(1, "Completed", 0, "build-01", "j1", TimeSpan.FromSeconds(1), 1, "exit 0"),
            new ShardResult(2, JobRunner.Cancelled, 130, null, null, TimeSpan.Zero, 1, "cancelled before it started"),
        ], "cancelled");
        var again = JobRunner.PlanTimedOutJob(new PlanJobOutcome("unit", JobRunner.Cancelled, 130, "cancelled", []) { Sharded = sharded }, deadline);
        Assert.Equal(("TimedOut", 124), (again.Status, again.ExitCode));
        Assert.Equal(PlanDeadline.WaitingMessage, again.Sharded!.Shards[1].Message);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BaselinesAfterTimeoutAreInconclusive()
    {
        Plan("""
            version: 1
            defaults:
              needs: [node]
            jobs:
              build:
                command: npm run build
                baseline: true
              unit:
                command: npm run unit
            """);
        var ssh = new PoolSsh { OnExecute = (e, c) => JobOf(e) == "build" ? Task.FromResult(e.Exit(2)) : Hang(e, c) };
        var payloads = new BaselinePayloads(new RunnerTests.FakePayloads(""));
        var fire = new TaskCompletionSource();

        var run = RunTimed(ssh, payloads, fire, ["--plan", "--plan-timeout", "1", "--json"]);
        await Until(() => ssh.Executes.Count == 2 && Log.ReadAll().Any(r => r.PlanJob == "build"));
        fire.SetResult();
        var (code, stdout, stderr) = await run;

        Assert.Equal(2, code); // build's own failure decides
        var jobs = JobsOf(Json(stdout));
        var baseline = jobs["build"].GetProperty("baseline");
        Assert.Equal("inconclusive", baseline.GetProperty("verdict").GetString());
        Assert.EndsWith("inconclusive: plan_timeout reached before it started", baseline.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ssh.Executes, e => payloads.IsBase(ssh, e)); // nothing reran at the base
        Assert.Equal("TimedOut", jobs["unit"].GetProperty("status").GetString());
        Assert.Contains("[lanq] baseline: job build ", stderr, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CliReplacesFileValue()
    {
        Plan("version: 1\nplan_timeout: 30\njobs:\n  build:\n    command: npm run build\n    needs: [node]\n");

        var (_, fromFile, fileText) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--dry-run", "--json"]);
        var (_, fromCli, _) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--dry-run", "--plan-timeout", "5", "--json"]);

        Assert.Equal(30, Json(fromFile).GetProperty("planTimeoutMinutes").GetInt32());
        Assert.Equal(5, Json(fromCli).GetProperty("planTimeoutMinutes").GetInt32());
        Assert.Contains("Timeout:  plan_timeout 30 min for the whole plan", fileText, StringComparison.Ordinal);
        Assert.Equal(30, PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml")).PlanTimeoutMinutes);
    }

    [Theory]
    [InlineData("run", "--plan-timeout", "5", "--", "npm", "test")]
    [InlineData("run", "--plan-timeout", "5", "--shards", "2", "--", "npm", "test")]
    [InlineData("health", "--plan-timeout", "5")]
    public void RefusedWithoutPlan(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(args));
        Assert.Contains("--plan-timeout", ex.Message, StringComparison.Ordinal);
        if (args[0] == "run")
        {
            Assert.StartsWith("--plan-timeout goes with --plan", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(60, ParsedArgs.Parse(["run", "--plan", "--plan-timeout", "60"]).PlanTimeoutMinutes);
        Assert.Null(ParsedArgs.Parse(["run", "--plan"]).PlanTimeoutMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10081")]
    [InlineData("soon")]
    [InlineData("1.5")]
    public void KeyParityExpandsToFlag(string value)
    {
        var cli = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--plan-timeout", value]));
        var file = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse($"version: 1\nplan_timeout: {value}\njobs:\n  build:\n    command: dotnet build\n"));

        Assert.Equal($"lanq.yml: plan_timeout (line 2): {cli.Message}", file.Message);
        Assert.Equal("--plan-timeout must be a whole number of minutes from 1 to 10080 (one week).", cli.Message);
        Assert.Equal(10080, PlanFileLoader.Parse("version: 1\nplan_timeout: 10080\njobs:\n  build:\n    command: dotnet build\n").PlanTimeoutMinutes);
        Assert.Null(PlanFileLoader.Parse("version: 1\njobs:\n  build:\n    command: dotnet build\n").PlanTimeoutMinutes);
        Assert.Contains("plan_timeout: must be a single value", Assert.Throws<PlanFileException>(
            () => PlanFileLoader.Parse("version: 1\nplan_timeout: [5]\njobs:\n  build:\n    command: dotnet build\n")).Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunWarns()
    {
        Plan("""
            version: 1
            plan_timeout: 15
            defaults:
              needs: [node]
            jobs:
              build:
                command: npm run build
              unit:
                command: npm run unit
                after: [build]
              lint:
                command: npm run lint
            """);
        foreach (var (job, seconds) in new[] { ("build", 600), ("unit", 600), ("lint", 300) })
        {
            Log.Append(new JobRecord("old-" + job, "build-01", "npm run " + job, DateTimeOffset.UtcNow.AddDays(-1), seconds + 10, 0, "Completed")
            {
                V = 2,
                Key = JobHistory.Key(["npm", "run", job]),
                Dir = ".",
                RunSeconds = seconds,
            });
        }

        var (code, stdout, stderr) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--dry-run", "--json"]);
        var (_, enough, _) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--dry-run", "--plan-timeout", "30", "--json"]);

        Assert.Equal(0, code); // a warning, never a refusal
        const string Warning = "history's expected critical path is about 20 min, over plan_timeout 15 min: the plan would likely time out";
        Assert.Contains(Warning, Json(stdout).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("Warning:  " + Warning, stderr, StringComparison.Ordinal);
        Assert.Empty(Json(enough).GetProperty("warnings").EnumerateArray());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCBeatsTimeout()
    {
        Plan(Three);
        var ssh = new PoolSsh { OnExecute = Hang };
        var fire = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = RunTimed(ssh, new RunnerTests.FakePayloads(""), fire, ["--plan", "--plan-timeout", "1", "--json"], ct: cts.Token);
        await Until(() => ssh.Executes.Count == 3);
        await cts.CancelAsync();
        fire.SetResult(); // the limit comes after Ctrl+C
        var (code, stdout, stderr) = await run;

        Assert.Equal(130, code);
        var json = Json(stdout);
        Assert.Equal("Cancelled", json.GetProperty("status").GetString());
        Assert.False(json.TryGetProperty("planTimedOut", out _));
        Assert.All(JobsOf(json).Values, j => Assert.Equal("Cancelled", j.GetProperty("status").GetString()));
        Assert.DoesNotContain("min reached", stderr, StringComparison.Ordinal);
        Assert.Empty(Log.ReadAll()); // a cancelled job is not recorded, as before
    }

    [Fact(Timeout = TestTimeout)]
    public async Task JsonKeysConditional()
    {
        Plan(Three);

        var (code, plain, _) = await Run(new PoolSsh(), "--plan", "--json");
        var (limitedCode, limited, _) = await RunTimed(new PoolSsh(), new RunnerTests.FakePayloads(""), new TaskCompletionSource(), ["--plan", "--plan-timeout", "90", "--json"], minutes: 90);
        var (_, dry, _) = await Run(new PoolSsh(), "--plan", "--dry-run", "--json");

        Assert.Equal((0, 0), (code, limitedCode));
        Assert.False(Json(plain).TryGetProperty("planTimeoutMinutes", out _));
        Assert.False(Json(plain).TryGetProperty("planTimedOut", out _));
        Assert.Equal(90, Json(limited).GetProperty("planTimeoutMinutes").GetInt32());
        Assert.False(Json(limited).TryGetProperty("planTimedOut", out _)); // not reached
        Assert.False(Json(dry).TryGetProperty("planTimeoutMinutes", out _));
    }
}
