using System.Text;
using System.Text.Json;
using LanQueue.Fetch;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Plan;
using LanQueue.Results;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.11.0 review fixes: durations that do not add up to a finite number (JSON must never throw), "no tests ran" only when the results were
/// read (results_unreadable otherwise), saturating counts, the per-job byte cap, the element cap's edge, lanq.yml empty values and the
/// smoke's version probe.
/// </summary>
public sealed class TestResultsReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-results-review-" + Guid.NewGuid().ToString("N"));

    public TestResultsReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    private static readonly ResultsSpec Junit = new(ResultsFormat.Junit, "r/*.xml");

    private TestResults Read(params (string Path, string Content)[] files) => TestResultsParser.Parse(Junit, TestResultsParserTests.Report(_dir, files), ignoreCase: false);

    private const string Valid = "<testsuites><testcase classname=\"c\" name=\"t\"/></testsuites>";

    private const string NoneRan = "<testsuites><testcase classname=\"c\" name=\"t\"><skipped/></testcase></testsuites>";

    // ---- durations (HIGH 1) ----

    [Theory]
    [InlineData("1e308")]
    [InlineData("1.7976931348623157e308")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("10000001")]
    public void TestResultsCaseDurationOutOfRangeIsDropped(string time)
    {
        var r = Read(("r/t.xml", $"<testsuites><testcase classname=\"c\" name=\"a\" time=\"{time}\"><failure/></testcase><testcase classname=\"c\" name=\"b\" time=\"{time}\"/></testsuites>"));

        Assert.Equal((2, 1), (r.Total, r.Failed));
        Assert.Null(r.DurationSeconds);
        Assert.Null(r.FailedTests[0].DurationSeconds);
        Assert.Empty(r.Warnings);
        _ = JsonSerializer.Serialize(Cli.ResultsJsonOf(r)!, LanqJson.Default.TestResultsJson);
    }

    [Fact]
    public void TestResultsTrxDurationOutOfRangeIsDropped()
    {
        var trx = TestResultsParserTests.Fixture("nunit.trx").Replace("00:00:01.5000000", "10675199.02:48:05.4775807", StringComparison.Ordinal);

        var r = TestResultsParser.Parse(new ResultsSpec(ResultsFormat.Trx, "r/*.trx"), TestResultsParserTests.Report(_dir, ("r/x.trx", trx)), ignoreCase: false);

        Assert.Null(r.FailedTests[0].DurationSeconds);
        Assert.Equal(0.5, r.DurationSeconds);
    }

    /// <summary>A sum past 10^12 s (here 100,001 tests of 10^7 s each) is left out with a warning.</summary>
    [Fact]
    public void TestResultsDurationSumOutOfRangeIsLeftOut()
    {
        var sb = new StringBuilder("<testsuites>");
        for (var i = 0; i <= 100_000; i++)
        {
            sb.Append("<testcase classname=\"c\" name=\"t\" time=\"10000000\"/>");
        }

        var r = Read(("r/long.xml", sb.Append("</testsuites>").ToString()));

        Assert.Equal(100_001, r.Passed);
        Assert.Null(r.DurationSeconds);
        Assert.Equal(TestResultsParser.DurationWarning, Assert.Single(r.Warnings));
    }

    private static TestResults Of(int total, double? duration, bool unreadable = false) => new(
        ResultsFormat.Junit, "r/*.xml", 1, unreadable ? 0 : 1, total, total, total, 0, 0, duration, [new FailedTest("x", duration, null)], 1, false, [], []) { Unreadable = unreadable };

    [Fact]
    public void TestResultsMergedDurationThatOverflowsIsLeftOut()
    {
        var merged = TestResultsParser.Merge([(1, Of(1, double.MaxValue)), (2, Of(1, double.MaxValue))])!;
        Assert.Null(merged.DurationSeconds);
        Assert.Equal(TestResultsParser.DurationWarning, Assert.Single(merged.Warnings));

        var nan = TestResultsParser.Merge([(1, Of(1, double.NaN)), (2, Of(1, 1))])!;
        Assert.Null(nan.DurationSeconds);

        // Whatever comes in, the JSON writer never meets a non-finite number.
        var text = JsonSerializer.Serialize(Cli.ResultsJsonOf(Of(1, double.PositiveInfinity) with { FailedTests = [new FailedTest("y", double.NaN, 1)] })!, LanqJson.Default.TestResultsJson);
        Assert.DoesNotContain("durationSeconds", text, StringComparison.Ordinal);
        Assert.Null(Cli.Finite(double.NegativeInfinity));
        Assert.Equal(1.235, Cli.Finite(1.23456));
    }

    // ---- counts never overflow (LOW 1) ----

    [Fact]
    public void TestResultsCountsSaturate()
    {
        var merged = TestResultsParser.Merge([(1, Of(int.MaxValue, 1)), (2, Of(int.MaxValue, 1)), (3, Of(5, 1))])!;
        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), (merged.Total, merged.Passed, merged.Executed));
        Assert.Equal(3, merged.Files);

        var job = new JobResult("j", JobStatus.Completed, 0, "m", "exit 0") { Results = Of(int.MaxValue, 1) };
        var plan = new PlanRunResult("Completed", 0, "done", [
            new PlanJobOutcome("a", "Completed", 0, "", []) { Single = job },
            new PlanJobOutcome("b", "Completed", 0, "", []) { Single = job },
        ]);
        var total = Cli.ResultsTotalOf(plan)!;
        Assert.Equal((2, int.MaxValue, int.MaxValue), (total.Jobs, total.Total, total.Passed));
        Assert.Equal(int.MaxValue, TestResultsParser.Saturate(long.MaxValue));
    }

    // ---- results_unreadable (MEDIUM 1) ----

    [Fact]
    public void TestResultsZeroExecutedOnlyWhenEveryFileWasRead()
    {
        Assert.Equal("zero_executed", Read(("r/a.xml", NoneRan)).NoTestsReason);

        // One file not valid XML: the other's zero proves nothing.
        var mixed = Read(("r/a.xml", NoneRan), ("r/b.xml", "broken"));
        Assert.Equal((2, 1), (mixed.Files, mixed.FilesRead));
        Assert.Equal("results_unreadable", mixed.NoTestsReason);
        Assert.True(mixed.NoTests);

        // Tests counted: no reason, even with a file left out.
        var some = Read(("r/a.xml", Valid), ("r/b.xml", "broken"));
        Assert.Null(some.NoTestsReason);
        Assert.True(some.Unreadable);
    }

    [Fact]
    public void TestResultsFetchProblemsMakeThemUnreadable()
    {
        var clean = TestResultsParserTests.Report(_dir);

        // The fetch skipped a results file (over --fetch-max, a link...): unreadable, with a warning.
        var skipped = TestResultsParser.Parse(Junit, clean with { Skipped = [new FetchSkip("r/big.xml", "over the size limit")] }, ignoreCase: false);
        Assert.Equal("results_unreadable", skipped.NoTestsReason);
        Assert.Contains("1 results file(s) were skipped by the fetch and not read (see fetchSkipped)", skipped.Warnings);

        // A skipped file the results path does not match changes nothing.
        Assert.Equal("no_results_files", TestResultsParser.Parse(Junit, clean with { Skipped = [new FetchSkip("logs/x.log", "link")] }, ignoreCase: false).NoTestsReason);

        // More skipped than listed: any could be a results file.
        Assert.Equal("results_unreadable", TestResultsParser.Parse(Junit, clean with { MoreSkipped = 3 }, ignoreCase: false).NoTestsReason);

        // A fetch error (timed out, cut short, the connection lost, the hours ended).
        var error = TestResultsParser.Parse(Junit, clean with { Error = "fetch stopped after 10 min (incomplete)" }, ignoreCase: false);
        Assert.Equal("results_unreadable", error.NoTestsReason);
        Assert.Contains("the fetch did not complete (see fetchError); results files may be missing", error.Warnings);

        // A merge is unreadable when any shard is.
        Assert.Equal("results_unreadable", TestResultsParser.Merge([(1, Of(0, null)), (2, Of(0, null, unreadable: true))])!.NoTestsReason);
        Assert.Equal("zero_executed", TestResultsParser.Merge([(1, Of(0, null)), (2, Of(0, null))])!.NoTestsReason);
    }

    [Fact]
    public void TestResultsUnreadableStepsDoNotBlameTheTests()
    {
        var unreadable = Of(0, null, unreadable: true);
        var steps = Cli.WithNoTestsSteps(NextSteps.Nothing("the job completed"), [(null, unreadable)]);

        var step = Assert.Single(steps);
        Assert.Equal("ask_developer", step.Action);
        Assert.StartsWith("the results of the job could not be read", step.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("filter", step.Reason, StringComparison.Ordinal);
        Assert.Null(step.Argv);
        Assert.Equal(
            "[lanq] warning: results could not be read in job unit: 0 tests counted, 0 of 1 results file(s) read (results path r/*.xml); see the results warnings, fetchSkipped and fetchError",
            Cli.NoTestsLine("unit", unreadable));

        // A plan with both kinds: one step each.
        var both = Cli.WithNoTestsSteps(NextSteps.Nothing("done"), [("a", Of(0, null)), ("b", unreadable), ("c", unreadable)]);
        Assert.Equal(["ask_developer", "ask_developer"], both.Select(s => s.Action));
        Assert.StartsWith("no tests ran in job a: ", both[0].Reason, StringComparison.Ordinal);
        Assert.StartsWith("the results of jobs b, c could not be read", both[1].Reason, StringComparison.Ordinal);
    }

    // ---- caps (LOW 3, LOW 7) ----

    /// <summary>Past 256 MB in all, later files are not read (four 63 MB files fit; the fifth does not). Sparse files: never written.</summary>
    [Fact]
    public void TestResultsPerJobByteCap()
    {
        var files = Enumerable.Range(1, 5).Select(i => ($"r/f{i}.xml", "")).ToArray();
        var report = TestResultsParserTests.Report(_dir, files);
        foreach (var (rel, _) in files)
        {
            using var f = new FileStream(Path.Combine(report.Directory, rel.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Write);
            f.SetLength(63L * 1024 * 1024);
        }

        var r = TestResultsParser.Parse(Junit, report, ignoreCase: false);

        Assert.Equal((5, 0), (r.Files, r.FilesRead));
        Assert.Contains("4 results file(s) were not valid JUnit XML and were not counted", r.Warnings);
        Assert.Contains("1 results file(s) past 256 MB in all were not read", r.Warnings);
        Assert.Equal("results_unreadable", r.NoTestsReason);
    }

    [Fact]
    public void TestResultsExactlyTwoMillionElementsAreRead()
    {
        // testsuites, one testcase, then empty elements: 2,000,000 elements in all.
        var sb = new StringBuilder("<testsuites><testcase classname=\"c\" name=\"t\"/>", (TestResultsParser.MaxElements * 4) + 64);
        for (var i = 0; i < TestResultsParser.MaxElements - 2; i++)
        {
            sb.Append("<x/>");
        }

        var r = Read(("r/wide.xml", sb.Append("</testsuites>").ToString()));

        Assert.Equal((1, 1), (r.FilesRead, r.Passed));
        Assert.Empty(r.Warnings);
    }

    // ---- lanq.yml empty values (LOW 6) ----

    [Theory]
    [InlineData("results: { format: trx, path: \"\" }", "results: path: is required: the fetch pattern of the results files, for example path: TestResults/*.trx (line 5).")]
    [InlineData("results: { format: \"\", path: a.trx }", "results: format: is required: trx or junit (line 5).")]
    public void TestResultsEmptyValuesInLanqYml(string keys, string message)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse($"version: 1\njobs:\n  unit:\n    command: [dotnet, test]\n    {keys}\n"));

        Assert.Equal("lanq.yml: job 'unit' (line 3): " + message, ex.Message);
    }

    // ---- the smoke's version probe (MEDIUM 2) ----

    /// <summary>Scenario 10 runs only when the probe's JSON says "--results comes from lanq.yml", which only 0.11.0 and later write.</summary>
    [Fact]
    public async Task TestResultsSmokeProbeMatchesWhatLanqSays()
    {
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--plan", "--dry-run", "--json", "--no-wake", "--results", "junit:TestResults/*.xml"], o, new StringWriter(), CancellationToken.None, () => T.Config()));
        Assert.Contains("--results comes from lanq.yml", o.ToString(), StringComparison.Ordinal);

        var script = File.ReadAllText(Path.Combine(T.RepoRoot, "scripts", "smoke-workers.ps1"));
        Assert.Contains("$probe10 = Lanq @('run', '--plan', '--dry-run', '--json', '--no-wake', '--results', 'junit:TestResults/*.xml')", script, StringComparison.Ordinal);
        Assert.Contains("if (-not ($probe10.Stdout -match '--results comes from lanq\\.yml'))", script, StringComparison.Ordinal);
        Assert.DoesNotContain("does not take --results", script, StringComparison.Ordinal);
    }
}
