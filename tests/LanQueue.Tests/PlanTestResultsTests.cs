using LanQueue.Config;
using LanQueue.Health;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq.yml results: (0.11.0) in a plan run: each job's results (a sharded job's merged), resultsTotal, and "no tests ran" per job.</summary>
public sealed partial class PlanFileRunTests
{
    /// <summary>unit fetches no results file (no tests ran), e2e has two shards (one empty), lint drops the defaults' results.</summary>
    private const string WithResults = """
        version: 1
        defaults:
          needs: [node]
          results: { format: junit, path: reports/*.xml }
        jobs:
          unit:
            command: npm run unit
          e2e:
            command: npm run e2e
            shards: 2
          lint:
            command: npm run lint
            results: false
        """;

    private async Task<(int Code, string Stdout, string Stderr)> RunWithResults(ResultsSsh ssh, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", .. args]), ConfigLoader.Parse(Fleet), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Home, Repo, o, e,
            CancellationToken.None, (_, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));
        return (code, o.ToString(), e.ToString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsPlanJobsAndTotal()
    {
        Plan(WithResults);
        var ssh = new ResultsSsh(new PoolSsh(), e => (JobOf(e), e.Shard) switch
        {
            ("e2e", 1) => FetchRunnerTests.Stream(("reports/e2e.xml", ResultsSsh.Junit("logs in", "pays!"))),
            ("e2e", _) => FetchRunnerTests.Stream(("reports/e2e.xml", ResultsSsh.Junit())),
            _ => FetchRunnerTests.Stream(),
        });

        var (code, stdout, stderr) = await RunWithResults(ssh, "--plan", "--json", "--fetch-dir", Path.Combine(_root, "fetched"));

        Assert.Equal(0, code); // information only: the plan still completes
        var json = Json(stdout);
        Assert.Equal("Completed", json.GetProperty("status").GetString());
        Assert.Equal("resultsTotal", json.EnumerateObject().Last().Name);
        var jobs = JobsOf(json);

        var unit = jobs["unit"];
        Assert.Equal("results", unit.EnumerateObject().Last().Name);
        Assert.True(unit.GetProperty("results").GetProperty("noTests").GetBoolean());
        Assert.Equal("no_results_files", unit.GetProperty("results").GetProperty("noTestsReason").GetString());
        Assert.Equal(0, unit.GetProperty("fetched").GetArrayLength()); // a results job shows the fetch fields

        var e2e = jobs["e2e"].GetProperty("results");
        Assert.Equal((2, 1, 1), (e2e.GetProperty("total").GetInt32(), e2e.GetProperty("passed").GetInt32(), e2e.GetProperty("failed").GetInt32()));
        Assert.Equal([2], e2e.GetProperty("shardsWithNoTests").EnumerateArray().Select(s => s.GetInt32()));
        Assert.Equal(("S.pays", 1), (e2e.GetProperty("failedTests")[0].GetProperty("name").GetString(), e2e.GetProperty("failedTests")[0].GetProperty("shard").GetInt32()));
        Assert.Equal([2, 0], jobs["e2e"].GetProperty("shards").EnumerateArray().Select(s => s.GetProperty("results").GetProperty("total").GetInt32()));

        Assert.False(jobs["lint"].TryGetProperty("results", out _));
        Assert.False(jobs["lint"].TryGetProperty("fetched", out _));

        var total = json.GetProperty("resultsTotal");
        Assert.Equal(
            ["jobs", "total", "executed", "passed", "failed", "skipped", "noTestsJobs"],
            total.EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 2, 2, 1, 1, 0), (total.GetProperty("jobs").GetInt32(), total.GetProperty("total").GetInt32(), total.GetProperty("executed").GetInt32(),
            total.GetProperty("passed").GetInt32(), total.GetProperty("failed").GetInt32(), total.GetProperty("skipped").GetInt32()));
        Assert.Equal(["unit"], total.GetProperty("noTestsJobs").EnumerateArray().Select(j => j.GetString()));

        var steps = json.GetProperty("nextSteps").EnumerateArray().ToList();
        var ask = Assert.Single(steps);
        Assert.Equal("ask_developer", ask.GetProperty("action").GetString());
        Assert.StartsWith("no tests ran in job unit: results show 0 tests executed", ask.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var lines = Lines(stderr);
        var warning = Array.IndexOf(lines, "[lanq] warning: no tests ran in job unit: 0 executed in 0 results file(s) (results path reports/*.xml)");
        var next = Array.FindIndex(lines, l => l.StartsWith("[lanq] Next: no tests ran in job unit", StringComparison.Ordinal));
        Assert.True(warning >= 0 && next > warning, stderr);
        Assert.StartsWith("[lanq] Completed", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>Durations of 1e308 s in every job and shard: dropped, so the plan's one JSON object is still written and the status line is last.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsPlanHugeDurations()
    {
        Plan(WithResults);
        const string Huge = "<testsuites><testcase classname=\"c\" name=\"a\" time=\"1e308\"/><testcase classname=\"c\" name=\"b\" time=\"1e308\"/></testsuites>";
        var ssh = new ResultsSsh(new PoolSsh(), _ => FetchRunnerTests.Stream(("reports/x.xml", Huge)));

        var (code, stdout, stderr) = await RunWithResults(ssh, "--plan", "--json");

        Assert.Equal(0, code);
        var json = Json(stdout);
        Assert.Equal(6, json.GetProperty("resultsTotal").GetProperty("total").GetInt32());
        Assert.All(JobsOf(json).Values.Where(j => j.TryGetProperty("results", out _)), j => Assert.False(j.GetProperty("results").TryGetProperty("durationSeconds", out _)));
        Assert.StartsWith("[lanq] Completed", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    /// <summary>Without results: in lanq.yml, no plan object gains results or resultsTotal.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsPlanWithoutResultsIsUnchanged()
    {
        Plan(Three);

        var (code, stdout, _) = await Run(new PoolSsh(), "--plan", "--json");

        Assert.Equal(0, code);
        var json = Json(stdout);
        Assert.False(json.TryGetProperty("resultsTotal", out _));
        Assert.All(JobsOf(json).Values, j => Assert.False(j.TryGetProperty("results", out _)));
        Assert.All(JobsOf(json).Values, j => Assert.False(j.TryGetProperty("fetched", out _)));
    }

    /// <summary>--plan --fetch-dir (and the limits) are taken when only results: jobs fetch; refused when no selected job fetches either way.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsPlanFetchDirForAResultsOnlyPlan()
    {
        Plan(WithResults);
        var outDir = Path.Combine(_root, "fetched");

        var (code, stdout, _) = await Run(new PoolSsh(), "--plan", "--dry-run", "--fetch-dir", outDir, "--fetch-max", "10M", "--json");

        Assert.Equal(0, code);
        var argv = Json(stdout).GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j.GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()!).ToList());
        Assert.Equal(["lanq", "run", "--needs", "node", "--shards", "2", "--results", "junit:reports/*.xml", "--fetch-dir", outDir, "--fetch-max", "10M", "--", "npm", "run", "e2e"], argv["e2e"]);
        Assert.DoesNotContain("--fetch-dir", argv["lint"]);

        var (refused, refusedOut, _) = await Run(new PoolSsh(), "--plan", "--only", "lint", "--fetch-dir", outDir, "--json");
        Assert.Equal(2, refused);
        Assert.Equal(LanQueue.Plan.PlanFetch.NoFetchingJob, Json(refusedOut).GetProperty("message").GetString());
    }
}
