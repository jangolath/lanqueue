using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Plan;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// A pool whose fetch answers with files chosen per job (from the job's execute call: its shard, its plan job); everything else is
/// <see cref="PoolSsh"/>'s.
/// </summary>
internal sealed class ResultsSsh(PoolSsh pool, Func<PoolSsh.Exec, string> fetchOutput) : ISshClient
{
    private int _fetches;

    public PoolSsh Pool => pool;

    public int Fetches => _fetches;

    public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
        pool.RunAsync(machine, remoteCommand, onLine, timeout, ct);

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
        pool.UploadAsync(machine, localPath, remotePath, timeout, ct);

    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct)
    {
        if (!remoteCommand.Contains("LQF1", StringComparison.Ordinal))
        {
            return pool.RunSplitAsync(machine, remoteCommand, input, onStdout, onStderr, timeout, ct);
        }

        Interlocked.Increment(ref _fetches);
        var exec = pool.Executes.LastOrDefault(e => remoteCommand.Contains("/jobs/" + e.JobId, StringComparison.Ordinal));
        var output = exec is null ? FetchRunnerTests.Stream() : fetchOutput(exec);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onStdout(line);
        }

        return Task.FromResult(new SshResult(0, output));
    }

    /// <summary>A JUnit file: each case passes, or fails when its name ends with "!" (the "!" dropped).</summary>
    public static string Junit(params string[] cases) =>
        "<testsuites><testsuite name=\"s\">"
        + string.Concat(cases.Select(c => c.EndsWith('!') ? $"<testcase classname=\"S\" name=\"{c[..^1]}\" time=\"0.5\"><failure/></testcase>" : $"<testcase classname=\"S\" name=\"{c}\" time=\"0.5\"/>"))
        + "</testsuite></testsuites>";
}

/// <summary>
/// lanq run --results and lanq.yml results: (0.11.0) end to end: the run, --shards and baseline paths, the JSON and stderr contract, and the
/// lanq.yml key (Plan/PlanFileLoader.cs). The plan run is in PlanFileRunTests (TestResultsPlan*).
/// </summary>
public sealed class TestResultsRunTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-results-run-" + Guid.NewGuid().ToString("N"));

    public TestResultsRunTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    private string OutRoot => Path.Combine(_dir, "artifacts");

    private static string Decode(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    private static bool IsExecute(string text) => text.Contains("exec 3<&0", StringComparison.Ordinal) || text.Contains("Set-Content -LiteralPath $pf", StringComparison.Ordinal);

    /// <summary>One machine (build-01, Linux) takes the job; it exits <paramref name="jobExit"/> (TimedOut with -1) and the fetch answers <paramref name="fetchOutput"/>.</summary>
    private FakeSsh Ssh(int jobExit, string fetchOutput)
    {
        var ssh = new FakeSsh();
        ssh.Handler = (m, cmd) =>
        {
            var text = Decode(m, cmd);
            if (text.Contains("disk_free_gb", StringComparison.Ordinal))
            {
                return new SshResult(0, (m.Name == "build-01" ? "load=0.1" : "load=0.99") + "\ndisk_free_gb=50\nslots_used=0\ntool=ok\nlocal_time=2026-01-02T23:00:00");
            }

            if (text.Contains("SLOT=", StringComparison.Ordinal))
            {
                return new SshResult(0, "SLOT=1");
            }

            if (IsExecute(text))
            {
                return jobExit < 0 ? new SshResult(124, "", TimedOut: true) : new SshResult(jobExit, "job output");
            }

            return text.Contains("LQF1", StringComparison.Ordinal) ? new SshResult(0, fetchOutput) : new SshResult(0, "");
        };
        return ssh;
    }

    private async Task<(int Code, JsonElement Json, string[] Stderr)> RunCli(int jobExit, string fetchOutput, params string[] args)
    {
        var ssh = Ssh(jobExit, fetchOutput);
        var o = new StringWriter();
        var e = new StringWriter();
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var code = await Cli.Run(
            ParsedArgs.Parse(args), config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")),
            _dir, _dir, o, e, CancellationToken.None);
        Assert.Single(o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(o.ToString());
        return (code, doc.RootElement.Clone(), [.. e.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))]);
    }

    private string[] Args(params string[] more) =>
        ["run", "--json", "--needs", "dotnet", "--results", "trx:TestResults/*.trx", "--fetch-dir", OutRoot, .. more, "--", "dotnet", "test"];

    private static int IndexOf(string[] lines, string start) => Array.FindIndex(lines, l => l.StartsWith(start, StringComparison.Ordinal));

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsRunJson()
    {
        var (code, json, stderr) = await RunCli(0, FetchRunnerTests.Stream(("TestResults/x.trx", TestResultsParserTests.Fixture("xunit.trx"))), Args());

        Assert.Equal(0, code);
        Assert.Equal("Completed", json.GetProperty("status").GetString());
        Assert.Equal("results", json.EnumerateObject().Last().Name); // added last
        var r = json.GetProperty("results");
        Assert.Equal(
            ["format", "path", "files", "filesRead", "total", "executed", "passed", "failed", "skipped", "noTests", "warnings", "failedTests", "durationSeconds"],
            r.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("trx", "TestResults/*.trx", 3, 2, 1, 1, 1, false), (
            r.GetProperty("format").GetString(), r.GetProperty("path").GetString(), r.GetProperty("total").GetInt32(), r.GetProperty("executed").GetInt32(),
            r.GetProperty("passed").GetInt32(), r.GetProperty("failed").GetInt32(), r.GetProperty("skipped").GetInt32(), r.GetProperty("noTests").GetBoolean()));
        var failed = Assert.Single(r.GetProperty("failedTests").EnumerateArray().ToList());
        Assert.Equal("Sample.MathTests.Divides(by: 0)", failed.GetProperty("name").GetString());
        Assert.False(failed.TryGetProperty("shard", out _));

        // The results path is fetched too, so the fetch fields are there.
        Assert.Equal("TestResults/x.trx", Assert.Single(json.GetProperty("fetched").EnumerateArray().ToList()).GetProperty("path").GetString());
        Assert.Equal("none", Assert.Single(json.GetProperty("nextSteps").EnumerateArray().ToList()).GetProperty("action").GetString());

        // After the fetch summary: the counts and the failed names; the status line stays last.
        var fetch = IndexOf(stderr, "[lanq] fetch (the job completed)");
        var counts = Array.IndexOf(stderr, "[lanq] results: 3 tests, 1 passed, 1 failed, 1 skipped");
        Assert.True(fetch >= 0 && counts > fetch, string.Join("\n", stderr));
        Assert.Equal("[lanq]   failed Sample.MathTests.Divides(by: 0)", stderr[counts + 1]);
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
        Assert.Equal(-1, IndexOf(stderr, "[lanq] warning: no tests ran"));
    }

    /// <summary>Without results the run object is exactly 0.10.0's: no results key (and no fetch keys without --fetch).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsAbsentLeavesTheObjectUnchanged()
    {
        var (code, json, _) = await RunCli(0, FetchRunnerTests.Stream(), "run", "--json", "--needs", "dotnet", "--", "dotnet", "test");

        Assert.Equal(0, code);
        Assert.Equal(["status", "exitCode", "machine", "jobId", "durationSeconds", "message", "logPath", "nextSteps"], json.EnumerateObject().Select(p => p.Name));

        var (_, fetched, _) = await RunCli(0, FetchRunnerTests.Stream(("TestResults/x.trx", "<x/>")), "run", "--json", "--needs", "dotnet", "--fetch", "TestResults/*.trx", "--fetch-dir", OutRoot, "--", "dotnet", "test");
        Assert.Equal(
            ["status", "exitCode", "machine", "jobId", "durationSeconds", "message", "fetched", "fetchSkipped", "fetchDir", "logPath", "nextSteps"],
            fetched.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Completed with no test executed: a warning before the Next lines, an ask_developer step, the status and exit code unchanged.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsNoTestsRanWarnsAndAsksTheDeveloper()
    {
        var (code, json, stderr) = await RunCli(0, FetchRunnerTests.Stream(), Args());

        Assert.Equal(0, code);
        Assert.Equal("Completed", json.GetProperty("status").GetString());
        var r = json.GetProperty("results");
        Assert.True(r.GetProperty("noTests").GetBoolean());
        Assert.Equal("no_results_files", r.GetProperty("noTestsReason").GetString());
        var step = Assert.Single(json.GetProperty("nextSteps").EnumerateArray().ToList());
        Assert.Equal("ask_developer", step.GetProperty("action").GetString());
        Assert.False(step.GetProperty("safeToRunUnattended").GetBoolean());
        Assert.False(step.TryGetProperty("argv", out _));

        var warning = Array.IndexOf(stderr, "[lanq] warning: no tests ran: 0 executed in 0 results file(s) (results path TestResults/*.trx)");
        var next = IndexOf(stderr, "[lanq] Next: no tests ran in the job: ");
        Assert.True(warning >= 0 && next > warning, string.Join("\n", stderr));
        Assert.Contains("[lanq] results warning: no results file matched results path TestResults/*.trx; check that the tests write it there (dir:) and fetchSkipped", stderr);
        Assert.Equal("[lanq] results: 0 tests, 0 passed, 0 failed, 0 skipped", stderr[Array.IndexOf(stderr, "[lanq] results warning: no results file matched results path TestResults/*.trx; check that the tests write it there (dir:) and fetchSkipped") + 1]);
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
    }

    /// <summary>Only a completed job warns: failed or timed out, the results are still there, without the warning or the step.</summary>
    [Theory(Timeout = TestTimeout)]
    [InlineData(1, "Failed", 1)]
    [InlineData(-1, "TimedOut", 124)]
    public async Task TestResultsWarnOnlyWhenCompleted(int jobExit, string status, int exit)
    {
        var (code, json, stderr) = await RunCli(jobExit, FetchRunnerTests.Stream(), Args());

        Assert.Equal(exit, code);
        Assert.Equal(status, json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("results").GetProperty("noTests").GetBoolean());
        Assert.DoesNotContain(json.GetProperty("nextSteps").EnumerateArray(), s => s.GetProperty("reason").GetString()!.StartsWith("no tests ran", StringComparison.Ordinal));
        Assert.Equal(-1, IndexOf(stderr, "[lanq] warning: no tests ran"));
        Assert.StartsWith("[lanq] " + status, stderr[^1], StringComparison.Ordinal);
    }

    /// <summary>A refused run (nothing ran, no fetch report) has no results object, but the fetch keys --results promises.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsRefusedRunHasNoResultsObject()
    {
        var (code, json, _) = await RunCli(0, FetchRunnerTests.Stream(), "run", "--json", "--results", "trx:a.trx", "--", "rm", "-rf", "/");
        Assert.Equal(2, code);
        Assert.False(json.TryGetProperty("results", out _));
        Assert.Equal(0, json.GetProperty("fetched").GetArrayLength());

        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--json", "--results", "xml:a", "--", "dotnet", "test"], o, new StringWriter(), CancellationToken.None, () => T.Config()));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("fetched").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("results", out _));
    }

    /// <summary>The connection dropped and the machine stayed down: a report with a fetch error, so results_unreadable (InfraError: no warning).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsAfterALostConnection()
    {
        var ssh = Ssh(255, FetchRunnerTests.Stream());
        var inner = ssh.Handler;
        ssh.Handler = (m, cmd) => IsExecute(Decode(m, cmd)) || Decode(m, cmd).Contains(RemoteScripts.StopJobMarker, StringComparison.Ordinal) ? new SshResult(255, "Connection reset") : inner(m, cmd);
        var lines = new List<string>();
        var runner = new JobRunner(T.Config(), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"), lines.Add);
        var p = ParsedArgs.Parse(Args());

        var r = await runner.RunAsync(Cli.RequestOf(p, _dir, new FetchRequest(p.Fetch, OutRoot, FetchLimits.Default)), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Equal("results_unreadable", r.Results!.NoTestsReason);
        Assert.Null(Cli.NoTestsRan(r.Status.ToString(), r.Results));
        Assert.Contains("[lanq] results: 0 tests, 0 passed, 0 failed, 0 skipped", lines);
    }

    // ---- --shards ----

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsShardsAreMergedAfterwards()
    {
        var ssh = new ResultsSsh(new PoolSsh(), e => e.Shard switch
        {
            1 => FetchRunnerTests.Stream(("reports/a.xml", ResultsSsh.Junit("one", "fails!"))),
            2 => FetchRunnerTests.Stream(("reports/a.xml", ResultsSsh.Junit())), // an empty shard: listed, never a warning
            _ => FetchRunnerTests.Stream(("reports/a.xml", ResultsSsh.Junit("two", "fails!", "other!"))),
        });
        var (code, json, stderr) = await Shards(ssh);

        Assert.Equal(0, code);
        Assert.Equal("Completed", json.GetProperty("status").GetString());
        var merged = json.GetProperty("results");
        Assert.Equal((5, 2, 3, 3), (merged.GetProperty("total").GetInt32(), merged.GetProperty("passed").GetInt32(), merged.GetProperty("failed").GetInt32(), merged.GetProperty("files").GetInt32()));
        Assert.Equal([("S.fails", 1), ("S.other", 3)], merged.GetProperty("failedTests").EnumerateArray().Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("shard").GetInt32())));
        Assert.Equal([2], merged.GetProperty("shardsWithNoTests").EnumerateArray().Select(s => s.GetInt32()));
        Assert.Empty(merged.GetProperty("warnings").EnumerateArray());
        Assert.False(merged.GetProperty("noTests").GetBoolean());

        // Each shard has its own object (no shard on its names), the top level the merged one.
        var shards = json.GetProperty("shards").EnumerateArray().ToList();
        Assert.Equal([2, 0, 3], shards.Select(s => s.GetProperty("results").GetProperty("total").GetInt32()));
        Assert.True(shards[1].GetProperty("results").GetProperty("noTests").GetBoolean());
        Assert.False(shards[0].GetProperty("results").GetProperty("failedTests")[0].TryGetProperty("shard", out _));
        Assert.Equal(-1, IndexOf(stderr, "[lanq] warning: no tests ran"));
        Assert.Equal("none", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsShardsThatRanNoTestWarnOnce()
    {
        var ssh = new ResultsSsh(new PoolSsh(), _ => FetchRunnerTests.Stream());
        var (code, json, stderr) = await Shards(ssh);

        Assert.Equal(0, code);
        var merged = json.GetProperty("results");
        Assert.True(merged.GetProperty("noTests").GetBoolean());
        Assert.Equal([1, 2, 3], merged.GetProperty("shardsWithNoTests").EnumerateArray().Select(s => s.GetInt32()));
        Assert.Equal(3, merged.GetProperty("warnings").GetArrayLength());
        Assert.StartsWith("shard 1/3: no results file matched", merged.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Single(stderr, l => l.StartsWith("[lanq] warning: no tests ran", StringComparison.Ordinal));
        var warning = Array.IndexOf(stderr, "[lanq] warning: no tests ran: 0 executed in 0 results file(s) (results path reports/*.xml)");
        Assert.True(warning >= 0 && IndexOf(stderr, "[lanq] Next: no tests ran in the job: ") > warning, string.Join("\n", stderr));
        Assert.Equal("ask_developer", Assert.Single(json.GetProperty("nextSteps").EnumerateArray().ToList()).GetProperty("action").GetString());
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
    }

    private async Task<(int Code, JsonElement Json, string[] Stderr)> Shards(ResultsSsh ssh)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--needs", "node", "--shards", "3", "--json", "--results", "junit:reports/*.xml", "--fetch-dir", OutRoot, "--", "npx", "jest", "--shard={shard}/{shards}"]);
        var code = await Cli.Run(
            p, ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")),
            _dir, _dir, o, e, CancellationToken.None);
        using var doc = JsonDocument.Parse(o.ToString());
        return (code, doc.RootElement.Clone(), [.. e.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))]);
    }

    // ---- review fixes: huge durations never break the JSON; unreadable results never claim no tests ran ----

    private const string HugeTimes =
        "<testsuites><testcase classname=\"c\" name=\"a\" time=\"1e308\"/><testcase classname=\"c\" name=\"b\" time=\"1e308\"><failure/></testcase></testsuites>";

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsHugeDurationsKeepTheRunContract()
    {
        var (code, json, stderr) = await RunCli(
            0, FetchRunnerTests.Stream(("reports/x.xml", HugeTimes)), "run", "--json", "--needs", "dotnet", "--results", "junit:reports/*.xml", "--fetch-dir", OutRoot, "--", "dotnet", "test");

        Assert.Equal(0, code);
        var r = json.GetProperty("results");
        Assert.Equal((2, 1), (r.GetProperty("total").GetInt32(), r.GetProperty("failed").GetInt32()));
        Assert.False(r.TryGetProperty("durationSeconds", out _));
        Assert.False(r.GetProperty("failedTests")[0].TryGetProperty("durationSeconds", out _));
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsHugeDurationsKeepTheShardsContract()
    {
        var ssh = new ResultsSsh(new PoolSsh(), _ => FetchRunnerTests.Stream(("reports/a.xml", HugeTimes)));

        var (code, json, stderr) = await Shards(ssh);

        Assert.Equal(0, code);
        Assert.Equal(6, json.GetProperty("results").GetProperty("total").GetInt32());
        Assert.False(json.GetProperty("results").TryGetProperty("durationSeconds", out _));
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
    }

    /// <summary>A completed job whose only results file is not valid XML: results_unreadable, a "could not be read" warning and step, exit 0.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsUnreadableAfterACompletedJob()
    {
        var (code, json, stderr) = await RunCli(0, FetchRunnerTests.Stream(("TestResults/x.trx", "not xml")), Args());

        Assert.Equal(0, code);
        var r = json.GetProperty("results");
        Assert.True(r.GetProperty("noTests").GetBoolean());
        Assert.Equal("results_unreadable", r.GetProperty("noTestsReason").GetString());
        var step = Assert.Single(json.GetProperty("nextSteps").EnumerateArray().ToList());
        Assert.Equal("ask_developer", step.GetProperty("action").GetString());
        Assert.DoesNotContain("filter", step.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(-1, IndexOf(stderr, "[lanq] warning: no tests ran"));
        var warning = IndexOf(stderr, "[lanq] warning: results could not be read: 0 tests counted, 0 of 1 results file(s) read (results path TestResults/*.trx)");
        Assert.True(warning >= 0 && IndexOf(stderr, "[lanq] Next: the results of the job could not be read") > warning, string.Join("\n", stderr));
        Assert.StartsWith("[lanq] Completed", stderr[^1], StringComparison.Ordinal);
    }

    /// <summary>The fetch skipped the results file (here over --fetch-max-files): results_unreadable, never "no tests ran".</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsSkippedByTheFetchAreUnreadable()
    {
        var stream = "LQF1 SKIP count " + Convert.ToBase64String("TestResults/x.trx"u8.ToArray()) + "\n" + FetchRunnerTests.Stream();

        var (code, json, stderr) = await RunCli(0, stream, Args());

        Assert.Equal(0, code);
        Assert.Equal(1, json.GetProperty("fetchSkipped").GetArrayLength());
        Assert.Equal("results_unreadable", json.GetProperty("results").GetProperty("noTestsReason").GetString());
        Assert.Equal(-1, IndexOf(stderr, "[lanq] warning: no tests ran"));
        Assert.True(IndexOf(stderr, "[lanq] warning: results could not be read") >= 0, string.Join("\n", stderr));
    }

    // ---- --baseline ----

    /// <summary>The base rerun counts nothing: it fetches nothing and has no results (only the failed job's are reported).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TestResultsBaselineRerunHasNoResults()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
        var pool = new PoolSsh();
        var payloads = new BaselinePayloads(new RunnerTests.FakePayloads(""));
        pool.OnExecute = (e, _) => Task.FromResult(e.Exit(payloads.IsBase(pool, e) ? 0 : 1));
        var ssh = new ResultsSsh(pool, _ => FetchRunnerTests.Stream(("reports/a.xml", ResultsSsh.Junit("fails!"))));
        var o = new StringWriter();
        var e = new StringWriter();

        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "node", "--baseline", "--results", "junit:reports/*.xml", "--fetch-dir", OutRoot, "--", "npm", "test"]),
            ConfigLoader.Parse(BaselineTests.Fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, Path.Combine(_dir, "repo"), o, e,
            CancellationToken.None);

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(o.ToString());
        var json = doc.RootElement;
        Assert.Equal("new", json.GetProperty("baseline").GetProperty("verdict").GetString());
        Assert.False(json.GetProperty("baseline").TryGetProperty("results", out _));
        Assert.Equal(1, json.GetProperty("results").GetProperty("failed").GetInt32());
        Assert.Equal(2, pool.Executes.Count); // the job and its base rerun
        Assert.Equal(1, ssh.Fetches); // only the job's own files
        Assert.Single(e.ToString().Split('\n'), l => l.StartsWith("[lanq] results:", StringComparison.Ordinal));
    }

    // ---- lanq.yml results: ----

    private static string Job(string keys) => $"version: 1\njobs:\n  unit:\n    command: [dotnet, test]\n    {keys}\n";

    [Theory]
    [InlineData("results: true", "results: must be a mapping with format: and path:, or false to drop the defaults: value (line 5).")]
    [InlineData("results: [trx, a.trx]", "results: must be a mapping with format: and path:, or false to drop the defaults: value (line 5).")]
    [InlineData("results: { format: trx, path: a.trx, extra: x }", "results: unknown key 'extra' (line 5); results: takes format: and path:.")]
    [InlineData("results: { path: a.trx }", "results: format: is required: trx or junit (line 5).")]
    [InlineData("results: { format: xml, path: a.trx }", "results: format: must be trx or junit (line 5).")]
    [InlineData("results:\n      format: trx\n      path: a.trx\n      fetch: x", "results: unknown key 'fetch' (line 8); results: takes format: and path:.")]
    [InlineData("results:\n      path: a.trx\n      format: nunit", "results: format: must be trx or junit (line 7).")]
    [InlineData("results: { format: trx }", "results: path: is required: the fetch pattern of the results files, for example path: TestResults/*.trx (line 5).")]
    [InlineData("results: { format: [trx], path: a.trx }", "results: format: and path: must each be a single value (line 5).")]
    [InlineData("results: { format: trx, path: ../a.trx }", "--results pattern '../a.trx' has an empty, '.' or '..' segment.")]
    [InlineData("results: { format: trx, path: \"C:/a.trx\" }", "--results pattern 'C:/a.trx' must be a relative path with forward slashes (no leading '/', drive letter, ':' or backslash).")]
    public void TestResultsKeyRefusals(string keys, string message)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(Job(keys)));

        Assert.Equal("lanq.yml: job 'unit' (line 3): " + message, ex.Message);
    }

    [Fact]
    public void TestResultsKeyExpandsAndMergesIntoTheFetch()
    {
        var job = PlanFileLoader.Parse(Job("results: { format: trx, path: TestResults/*.trx }")).Jobs[0];
        Assert.Equal(["run", "--results", "trx:TestResults/*.trx", "--", "dotnet", "test"], job.RunArgs);
        Assert.Equal(["TestResults/*.trx"], job.Args.Fetch);
        Assert.Equal(("trx", "TestResults/*.trx"), (job.Args.Results!.FormatName, job.Args.Results.Path));

        // The same pattern in fetch: is fetched once; another one is kept.
        var both = PlanFileLoader.Parse(Job("fetch: [TestResults/*.trx, logs/*.log]\n    results: { format: trx, path: TestResults/*.trx }")).Jobs[0];
        Assert.Equal(["TestResults/*.trx", "logs/*.log"], both.Args.Fetch);

        // 16 fetch patterns plus another results path: one too many.
        var sixteen = "fetch: [" + string.Join(", ", Enumerable.Range(1, 16).Select(i => $"f{i}.log")) + "]";
        Assert.Equal(
            "lanq.yml: job 'unit' (line 3): --fetch can be given at most 16 times (--results adds its path unless a --fetch names it).",
            Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(Job(sixteen + "\n    results: { format: junit, path: r.xml }"))).Message);
        Assert.Equal(16, PlanFileLoader.Parse(Job(sixteen + "\n    results: { format: junit, path: f3.log }")).Jobs[0].Args.Fetch.Count);
    }

    [Fact]
    public void TestResultsKeyInDefaultsAndFalse()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults:
              needs: [dotnet]
              fetch_max: 50M
              results: { format: trx, path: TestResults/*.trx }
            jobs:
              unit:
                command: dotnet test
              e2e:
                command: npm test
                results: { format: junit, path: reports/*.xml }
              lint:
                command: dotnet build
                results: false
            """);

        // The defaults' fetch_max applies to a job that only has results: (it fetches its path).
        Assert.Equal(["run", "--needs", "dotnet", "--results", "trx:TestResults/*.trx", "--fetch-max", "50M", "--", "dotnet", "test"], plan.Job("unit")!.RunArgs);
        Assert.Equal(50L * 1024 * 1024, plan.Job("unit")!.Args.FetchMaxBytes);

        // A job's own results: replaces the defaults' as a whole.
        Assert.Equal(["reports/*.xml"], plan.Job("e2e")!.Args.Fetch);
        Assert.Equal("junit:reports/*.xml", plan.Job("e2e")!.Args.Results!.Text);

        // results: false drops it, and with it the fetch and the defaults' limit.
        Assert.Equal(["run", "--needs", "dotnet", "--", "dotnet", "build"], plan.Job("lint")!.RunArgs);
        Assert.Null(plan.Job("lint")!.Args.Results);
        Assert.True(PlanFetch.AnyFetches([plan.Job("unit")!]));
        Assert.False(PlanFetch.AnyFetches([plan.Job("lint")!]));
        Assert.Contains("results", PlanFileLoader.JobKeys);
        Assert.Contains("results", PlanFileLoader.DefaultKeys);
    }

    /// <summary>The dry run names the results path among the run's options.</summary>
    [Fact]
    public void TestResultsDryRunLine()
    {
        var p = ParsedArgs.Parse(["run", "--results", "junit:reports/*.xml", "--", "npm", "test"]);

        Assert.Contains("--results junit:reports/*.xml (counted from the fetched files)", Cli.OptionsText(p), StringComparison.Ordinal);
    }
}
