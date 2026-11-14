using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Fetch;
using LanQueue.Output;
using LanQueue.Results;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq run --results (0.11.0): the TRX and JUnit reader on real-shaped files (Fixtures/results), merging shards, missing files, the results
/// JSON and the "no tests ran" step (Results/TestResultsParser.cs, Cli.Results.cs).
/// </summary>
public sealed class TestResultsParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-results-" + Guid.NewGuid().ToString("N"));

    public TestResultsParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "results", name));

    private static readonly ResultsSpec Trx = new(ResultsFormat.Trx, "TestResults/*.trx");

    private static readonly ResultsSpec Junit = new(ResultsFormat.Junit, "reports/*.xml");

    /// <summary>A fetch report of files written below a fresh out/jobid folder (paths relative, with forward slashes).</summary>
    internal static FetchReport Report(string root, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(root, "out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fetched = new List<FetchedFile>();
        foreach (var (rel, content) in files)
        {
            var local = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, content, new UTF8Encoding(false));
            fetched.Add(new FetchedFile(rel, new FileInfo(local).Length, ""));
        }

        return new FetchReport(dir, fetched, [], null);
    }

    private TestResults Read(ResultsSpec spec, params (string Path, string Content)[] files) => TestResultsParser.Parse(spec, Report(_dir, files), ignoreCase: false);

    [Fact]
    public void TestResultsXunitTrx()
    {
        var r = Read(Trx, ("TestResults/xunit.trx", Fixture("xunit.trx")));

        Assert.Equal((1, 1, 3, 2, 1, 1, 1), (r.Files, r.FilesRead, r.Total, r.Executed, r.Passed, r.Failed, r.Skipped));
        var failed = Assert.Single(r.FailedTests);
        Assert.Equal("Sample.MathTests.Divides(by: 0)", failed.Name); // TestMethod name already starts with className.
        Assert.Equal(0.25, failed.DurationSeconds);
        Assert.Null(failed.Shard);
        Assert.Equal(0.2512, r.DurationSeconds!.Value, 4);
        Assert.Empty(r.Warnings);
        Assert.False(r.NoTests);
        Assert.Null(r.NoTestsReason);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void TestResultsNunitNestedClassKeepsThePlus()
    {
        var r = Read(Trx, ("TestResults/nunit.trx", Fixture("nunit.trx"))); // TestDefinitions before Results

        Assert.Equal((2, 1, 1), (r.Total, r.Passed, r.Failed));
        Assert.Equal("Sample.Outer+Inner.Fails", Assert.Single(r.FailedTests).Name);
        Assert.Equal(1.5, r.FailedTests[0].DurationSeconds);
    }

    /// <summary>InnerResults count only when their parent has no outcome; className is cut at its first ','.</summary>
    [Fact]
    public void TestResultsMstestDataRows()
    {
        var r = Read(Trx, ("TestResults/mstest.trx", Fixture("mstest.trx")));

        Assert.Equal((3, 2, 1, 0), (r.Total, r.Passed, r.Failed, r.Skipped));
        Assert.Equal("Sample.DataTests.Rows", Assert.Single(r.FailedTests).Name);
        Assert.Empty(r.Warnings); // Counters total="3" agrees
    }

    [Fact]
    public void TestResultsTrxCountersThatDisagreeAreAWarning()
    {
        var r = Read(Trx, ("TestResults/counters.trx", Fixture("counters.trx")));

        Assert.Equal((2, 2), (r.Total, r.Passed));
        Assert.Equal("TRX counters disagree with the results in 1 file(s); the counts are lanq's own", Assert.Single(r.Warnings));
    }

    [Fact]
    public void TestResultsPytestJunit()
    {
        var r = Read(Junit, ("reports/pytest.xml", Fixture("pytest.xml")));

        Assert.Equal((3, 2, 1, 1, 1), (r.Total, r.Executed, r.Passed, r.Failed, r.Skipped));
        Assert.Equal("tests.test_math.test_fails", Assert.Single(r.FailedTests).Name);
        Assert.Equal(0.002, r.FailedTests[0].DurationSeconds);
    }

    /// <summary>jest-junit writes the classname as the start of (or the whole) name: the name alone then.</summary>
    [Fact]
    public void TestResultsJestJunit()
    {
        var r = Read(Junit, ("reports/jest.xml", Fixture("jest.xml")));

        Assert.Equal((3, 2, 1), (r.Total, r.Passed, r.Failed));
        Assert.Equal("math divides by zero", Assert.Single(r.FailedTests).Name);
    }

    /// <summary>Surefire: nested testsuites, an error child counts as failed, time with a thousands separator.</summary>
    [Fact]
    public void TestResultsSurefireJunit()
    {
        var r = Read(Junit, ("reports/surefire.xml", Fixture("surefire.xml")));

        Assert.Equal((3, 1, 1, 1), (r.Total, r.Passed, r.Failed, r.Skipped));
        Assert.Equal("com.example.AppTest.broken", Assert.Single(r.FailedTests).Name);
        Assert.Equal(1235.0, r.DurationSeconds);
    }

    /// <summary>Only fetched files matching the results path count (in path order); a Windows worker's files match ignoring case.</summary>
    [Fact]
    public void TestResultsOnlyMatchingFilesCount()
    {
        var report = Report(
            _dir,
            ("TestResults/b.trx", Fixture("nunit.trx")),
            ("TestResults/a.trx", Fixture("xunit.trx")),
            ("TestResults/notes.txt", "not xml"),
            ("other/c.trx", Fixture("xunit.trx")));

        var r = TestResultsParser.Parse(Trx, report, ignoreCase: false);
        Assert.Equal((2, 2, 5, 2), (r.Files, r.FilesRead, r.Total, r.Failed));
        Assert.Equal(["Sample.MathTests.Divides(by: 0)", "Sample.Outer+Inner.Fails"], r.FailedTests.Select(f => f.Name)); // a.trx first

        var upper = new ResultsSpec(ResultsFormat.Trx, "testresults/*.TRX");
        Assert.Equal(2, TestResultsParser.Parse(upper, report, ignoreCase: true).Files);
        var exact = TestResultsParser.Parse(upper, report, ignoreCase: false);
        Assert.Equal((0, "no_results_files"), (exact.Files, exact.NoTestsReason));
    }

    [Fact]
    public void TestResultsNoFileIsNoResultsFiles()
    {
        foreach (var report in new[] { Report(_dir), null })
        {
            var r = TestResultsParser.Parse(Trx, report, ignoreCase: false);

            Assert.True(r.NoTests);
            Assert.Equal("no_results_files", r.NoTestsReason);
            Assert.Equal((0, 0, 0), (r.Files, r.FilesRead, r.Total));
            Assert.Equal("no results file matched results path TestResults/*.trx; check that the tests write it there (dir:) and fetchSkipped", Assert.Single(r.Warnings));
        }
    }

    [Fact]
    public void TestResultsZeroExecuted()
    {
        var r = Read(Junit, ("reports/empty.xml", "<testsuites><testsuite name=\"none\" tests=\"1\"><testcase classname=\"a\" name=\"b\"><skipped/></testcase></testsuite></testsuites>"));

        Assert.True(r.NoTests);
        Assert.Equal("zero_executed", r.NoTestsReason);
        Assert.Equal((1, 1, 1, 0), (r.Files, r.FilesRead, r.Skipped, r.Executed));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void TestResultsFailedNamesAreKeptUpToTheCap()
    {
        var sb = new StringBuilder("<testsuites><testsuite name=\"many\">");
        for (var i = 0; i < TestResultsParser.MaxFailedKept + 5; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<testcase classname=\"c\" name=\"t{i}\"><failure/></testcase>");
        }

        sb.Append("<testcase classname=\"c\" name=\"t0\"><failure/></testcase></testsuite></testsuites>"); // a repeat: counted, listed once
        var r = Read(Junit, ("reports/many.xml", sb.ToString()));

        Assert.Equal(TestResultsParser.MaxFailedKept + 6, r.Failed); // counts stay complete
        Assert.Equal(TestResultsParser.MaxFailedKept, r.FailedTests.Count);
        Assert.Equal(TestResultsParser.MaxFailedKept + 5, r.FailedKnown);
        Assert.True(r.Truncated);
        Assert.Contains("more than 10000 failed tests; names after that are not kept (counts are complete)", r.Warnings);
    }

    // ---- merging shards ----

    private static TestResults Of(int passed, int failed, params string[] names) => new(
        ResultsFormat.Trx, "TestResults/*.trx", 1, 1, passed + failed, passed + failed, passed, failed, 0, 1.5, [.. names.Select(n => new FailedTest(n, 0.5, null))], names.Length, false,
        [], []);

    [Fact]
    public void TestResultsMergeListsAFailureOnceWithTheFirstShard()
    {
        var merged = TestResultsParser.Merge([(1, Of(3, 1, "A.Fails")), (2, Of(0, 0) with { Warnings = ["1 results file(s) over 64 MB were not read"] }), (3, Of(2, 2, "A.Fails", "B.Fails"))])!;

        Assert.Equal((3, 3, 8, 8, 5, 3), (merged.Files, merged.FilesRead, merged.Total, merged.Executed, merged.Passed, merged.Failed));
        Assert.Equal(4.5, merged.DurationSeconds);
        Assert.Equal([("A.Fails", 1), ("B.Fails", 3)], merged.FailedTests.Select(f => (f.Name, f.Shard!.Value)));
        Assert.Equal(2, merged.FailedKnown);
        Assert.Equal([2], merged.ShardsWithNoTests); // information only, never a warning
        Assert.Equal(["shard 2/3: 1 results file(s) over 64 MB were not read"], merged.Warnings);
        Assert.False(merged.NoTests);
    }

    [Fact]
    public void TestResultsMergeOfNothing()
    {
        Assert.Null(TestResultsParser.Merge([(1, null), (2, null)]));
        Assert.Null(TestResultsParser.Merge([]));

        // A shard that never ran is not an empty shard.
        var merged = TestResultsParser.Merge([(1, null), (2, Of(0, 0))])!;
        Assert.Equal([2], merged.ShardsWithNoTests);
        Assert.True(merged.NoTests);
        Assert.Equal("zero_executed", merged.NoTestsReason);
    }

    [Fact]
    public void TestResultsMergeKeepsAtMostTwentyWarnings()
    {
        var noisy = Of(1, 0) with { Warnings = [.. Enumerable.Range(1, 15).Select(i => $"w{i}")] };

        var merged = TestResultsParser.Merge([(1, noisy), (2, noisy)])!;

        Assert.Equal(TestResultsParser.MaxMergedWarnings, merged.Warnings.Count);
        Assert.Equal("shard 2/2: w5", merged.Warnings[^1]);
    }

    // ---- JSON and next steps ----

    [Fact]
    public void TestResultsJsonListsFiftyAndCountsTheRest()
    {
        var names = Enumerable.Range(1, 60).Select(i => "T" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var json = Cli.ResultsJsonOf(Of(1, 60, names))!;

        Assert.Equal(50, json.FailedTests.Count);
        Assert.Equal(10, json.FailedTestsMore);
        Assert.Equal("trx", json.Format);

        var text = JsonSerializer.Serialize(json, LanqJson.Default.TestResultsJson);
        Assert.StartsWith(
            "{\"format\":\"trx\",\"path\":\"TestResults/*.trx\",\"files\":1,\"filesRead\":1,\"total\":61,\"executed\":61,\"passed\":1,\"failed\":60,\"skipped\":0,\"noTests\":false,\"warnings\":[],\"failedTests\":[{\"name\":\"T1\",\"durationSeconds\":0.5},",
            text,
            StringComparison.Ordinal);
        Assert.EndsWith("],\"durationSeconds\":1.5,\"failedTestsMore\":10}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("noTestsReason", text, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shardsWithNoTests", text, StringComparison.Ordinal);
        Assert.Null(Cli.ResultsJsonOf(null));
    }

    [Fact]
    public void TestResultsJsonOfNoTests()
    {
        var r = TestResultsParser.Parse(Trx, null, ignoreCase: false);

        var text = JsonSerializer.Serialize(Cli.ResultsJsonOf(r)!, LanqJson.Default.TestResultsJson);

        Assert.Equal(
            "{\"format\":\"trx\",\"path\":\"TestResults/*.trx\",\"files\":0,\"filesRead\":0,\"total\":0,\"executed\":0,\"passed\":0,\"failed\":0,\"skipped\":0,\"noTests\":true,"
            + "\"warnings\":[\"no results file matched results path TestResults/*.trx; check that the tests write it there (dir:) and fetchSkipped\"],\"failedTests\":[],\"noTestsReason\":\"no_results_files\"}",
            text);
    }

    [Fact]
    public void TestResultsNoTestsStepIsAskDeveloperWithoutACommand()
    {
        var alone = NextSteps.WithNoTests(NextSteps.Nothing("the job completed"), "the job");
        var step = Assert.Single(alone); // replaces the lone none
        Assert.Equal("ask_developer", step.Action);
        Assert.Equal(
            "no tests ran in the job: results show 0 tests executed; check the test filter, the test project and results.path; making this fail is the developer's choice (docs/PLANS.md#make-no-tests-ran-fail)",
            step.Reason);
        Assert.Null(step.Argv);
        Assert.Null(step.Command);
        Assert.False(step.SafeToRunUnattended);

        var after = NextSteps.WithNoTests([NextSteps.Step(NextAction.FixCode, "job 'lint' exited 1")], "job unit");
        Assert.Equal(["fix_code", "ask_developer"], after.Select(s => s.Action));
        Assert.StartsWith("no tests ran in job unit: ", after[1].Reason, StringComparison.Ordinal);
        Assert.Equal(["Next: " + after[1].Reason], NextSteps.Lines([after[1]]));
    }

    // ---- --results on the command line ----

    [Theory]
    [InlineData("trx:TestResults/*.trx", ResultsFormat.Trx, "TestResults/*.trx")]
    [InlineData("junit:reports/**/junit.xml", ResultsFormat.Junit, "reports/**/junit.xml")]
    public void TestResultsSpecParses(string value, ResultsFormat format, string path)
    {
        var spec = ResultsSpec.Parse(value);

        Assert.Equal((format, path, value), (spec.Format, spec.Path, spec.Text));
    }

    [Theory]
    [InlineData("TestResults/*.trx", "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).")]
    [InlineData("xml:a.xml", "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).")]
    [InlineData("TRX:a.trx", "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).")]
    [InlineData("trx:", "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).")]
    [InlineData(":a.trx", "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).")]
    [InlineData("trx:../a.trx", "--results pattern '../a.trx' has an empty, '.' or '..' segment.")]
    [InlineData("trx:C:/a.trx", "--results pattern 'C:/a.trx' must be a relative path with forward slashes (no leading '/', drive letter, ':' or backslash).")]
    [InlineData("junit:-x.xml", "--results pattern '-x.xml' starts with '-'.")]
    public void TestResultsSpecRefusals(string value, string message)
    {
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ResultsSpec.Parse(value)).Message);
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--results", value, "--", "dotnet", "test"])).Message);
    }

    [Fact]
    public void TestResultsCommandLineMergesThePathIntoTheFetch()
    {
        var p = ParsedArgs.Parse(["run", "--results", "trx:TestResults/*.trx", "--", "dotnet", "test"]);
        Assert.Equal(["TestResults/*.trx"], p.Fetch);
        Assert.Equal(ResultsFormat.Trx, p.Results!.Format);

        // A --fetch naming the same pattern is not repeated; another one is kept first.
        Assert.Equal(["TestResults/*.trx"], ParsedArgs.Parse(["run", "--fetch", "TestResults/*.trx", "--results", "trx:TestResults/*.trx", "--", "dotnet", "test"]).Fetch);
        Assert.Equal(["logs/*.log", "TestResults/*.trx"], ParsedArgs.Parse(["run", "--results", "trx:TestResults/*.trx", "--fetch", "logs/*.log", "--", "dotnet", "test"]).Fetch);

        Assert.Equal("--results can be given once.", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--results", "trx:a.trx", "--results", "trx:b.trx", "--", "x"])).Message);

        // --fetch-dir and the limits are accepted with --results alone.
        var dir = ParsedArgs.Parse(["run", "--results", "junit:r.xml", "--fetch-dir", "out", "--fetch-max", "10M", "--", "npm", "test"]);
        Assert.Equal(("out", 10L * 1024 * 1024), (dir.FetchDir, dir.FetchMaxBytes!.Value));
        Assert.Equal(
            "--fetch-dir, --fetch-max and --fetch-max-files need at least one --fetch pattern or --results.",
            Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fetch-dir", "out", "--", "npm", "test"])).Message);

        // With --plan it comes from lanq.yml.
        Assert.StartsWith(
            "with --plan each job's --results comes from lanq.yml; leave --results out",
            Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--results", "trx:a.trx"])).Message,
            StringComparison.Ordinal);
        Assert.Equal("lanq health does not take --results.", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--results", "trx:a.trx"])).Message);
        Assert.True(CommandLine.TakesValue("run", "--results"));
    }

    [Fact]
    public void TestResultsCountTowardSixteenPatterns()
    {
        var sixteen = Enumerable.Range(1, 16).SelectMany(i => new[] { "--fetch", "f" + i.ToString(CultureInfo.InvariantCulture) + ".log" }).ToArray();

        var ex = Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", .. sixteen, "--results", "trx:a.trx", "--", "dotnet", "test"]));
        Assert.Equal("--fetch can be given at most 16 times (--results adds its path unless a --fetch names it).", ex.Message);

        // When one of the sixteen names it, nothing is added.
        Assert.Equal(16, ParsedArgs.Parse(["run", .. sixteen, "--results", "trx:f16.log", "--", "dotnet", "test"]).Fetch.Count);
        Assert.Equal(["a", "b"], FetchPatterns.Validate(["a", "b"], "b"));
        Assert.Equal(["a", "c"], FetchPatterns.Validate(["a"], "c"));
        Assert.Equal("--fetch can be given at most 16 times.", Assert.Throws<ArgumentException>(() => FetchPatterns.Validate([.. Enumerable.Range(1, 17).Select(i => "p" + i.ToString(CultureInfo.InvariantCulture))])).Message);
    }
}
