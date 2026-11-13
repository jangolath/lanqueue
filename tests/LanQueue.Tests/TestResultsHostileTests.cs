using System.Diagnostics;
using System.Text;
using LanQueue.Fetch;
using LanQueue.Results;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq run --results (0.11.0): results files come from the worker and are hostile input. No DTD, no resolver, caps on size, files, depth,
/// elements, attribute length and read time; names sanitised; a link is never read. Every problem is a counted warning, never an exception.
/// </summary>
public sealed class TestResultsHostileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-results-hostile-" + Guid.NewGuid().ToString("N"));

    public TestResultsHostileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestTree.Delete(_dir);

    private static readonly ResultsSpec Junit = new(ResultsFormat.Junit, "r/*.xml");

    private static readonly ResultsSpec Trx = new(ResultsFormat.Trx, "r/*.trx");

    private TestResults Read(ResultsSpec spec, string name, string content) =>
        TestResultsParser.Parse(spec, TestResultsParserTests.Report(_dir, ("r/" + name, content)), ignoreCase: false);

    private static void Refused(TestResults r, string warning)
    {
        Assert.Equal((1, 0, 0), (r.Files, r.FilesRead, r.Total));
        Assert.Equal(warning, Assert.Single(r.Warnings));
        Assert.True(r.NoTests);
        Assert.Equal("zero_executed", r.NoTestsReason);
    }

    private const string NotValidJunit = "1 results file(s) were not valid JUnit XML and were not counted";

    private const string NotValidTrx = "1 results file(s) were not valid TRX XML and were not counted";

    private const string Capped = "1 results file(s) hit the depth or element cap and were not counted";

    [Fact]
    public void TestResultsBillionLaughsIsRefusedFast()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?>\n<!DOCTYPE testsuites [\n  <!ENTITY a \"lol\">\n");
        for (var i = 1; i < 10; i++)
        {
            sb.Append("  <!ENTITY ").Append((char)('a' + i)).Append(" \"").Append(string.Concat(Enumerable.Repeat("&" + (char)('a' + i - 1) + ";", 10))).Append("\">\n");
        }

        sb.Append("]>\n<testsuites><testsuite><testcase classname=\"c\" name=\"&j;\"/></testsuite></testsuites>");
        var watch = Stopwatch.StartNew();

        var r = Read(Junit, "laughs.xml", sb.ToString());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Refused(r, NotValidJunit);
    }

    /// <summary>An external entity is never resolved: the DOCTYPE alone refuses the file, and the file it names is never read into a name.</summary>
    [Fact]
    public void TestResultsExternalEntityIsNeverRead()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-VALUE");
        var uri = new Uri(secret).AbsoluteUri;

        var r = Read(Junit, "xxe.xml", $"<?xml version=\"1.0\"?><!DOCTYPE testsuites [<!ENTITY x SYSTEM \"{uri}\">]><testsuites><testcase classname=\"c\" name=\"&x;\"><failure/></testcase></testsuites>");

        Refused(r, NotValidJunit);
        Assert.Empty(r.FailedTests);
    }

    /// <summary>The predefined entities are not DTD entities: 10,000 of them read fine.</summary>
    [Fact]
    public void TestResultsTenThousandAmpersandsParse()
    {
        var text = string.Concat(Enumerable.Repeat("&amp;", 10_000));

        var r = Read(Junit, "amp.xml", $"<testsuites><testcase classname=\"c\" name=\"t\"><failure>{text}</failure></testcase></testsuites>");

        Assert.Equal((1, 1, 1), (r.FilesRead, r.Total, r.Failed));
        Assert.Empty(r.Warnings);
    }

    private static string Nested(int depth)
    {
        // testsuites, then (depth - 2) testsuite levels, then a testcase: depth elements deep.
        var sb = new StringBuilder("<testsuites>");
        sb.Append(string.Concat(Enumerable.Repeat("<testsuite>", depth - 2)));
        sb.Append("<testcase classname=\"c\" name=\"deep\"/>");
        sb.Append(string.Concat(Enumerable.Repeat("</testsuite>", depth - 2)));
        return sb.Append("</testsuites>").ToString();
    }

    [Fact]
    public void TestResultsDepthCap()
    {
        Assert.Equal(1, Read(Junit, "d32.xml", Nested(TestResultsParser.MaxDepth)).Passed);
        Refused(Read(Junit, "d33.xml", Nested(TestResultsParser.MaxDepth + 1)), Capped);
    }

    [Fact]
    public void TestResultsElementCap()
    {
        // testsuites plus 2M empty elements: 2M + 1 elements.
        var sb = new StringBuilder("<testsuites>", (TestResultsParser.MaxElements * 4) + 32);
        for (var i = 0; i < TestResultsParser.MaxElements; i++)
        {
            sb.Append("<x/>");
        }

        Refused(Read(Junit, "wide.xml", sb.Append("</testsuites>").ToString()), Capped);
    }

    [Fact]
    public void TestResultsFileOverSixtyFourMegabytesIsNotOpened()
    {
        var report = TestResultsParserTests.Report(_dir, ("r/big.xml", ""));
        using (var f = new FileStream(Path.Combine(report.Directory, "r", "big.xml"), FileMode.Open, FileAccess.Write))
        {
            f.SetLength(TestResultsParser.MaxFileBytes + 1); // sparse where the file system allows it: never read
        }

        Refused(TestResultsParser.Parse(Junit, report, ignoreCase: false), "1 results file(s) over 64 MB were not read");
    }

    [Fact]
    public void TestResultsFilesBeyondSixtyFourAreNotRead()
    {
        var files = Enumerable.Range(0, TestResultsParser.MaxFiles + 2)
            .Select(i => ($"r/f{i:D3}.xml", "<testsuites><testcase classname=\"c\" name=\"t\"/></testsuites>")).ToArray();

        var r = TestResultsParser.Parse(Junit, TestResultsParserTests.Report(_dir, files), ignoreCase: false);

        Assert.Equal((66, 64, 64), (r.Files, r.FilesRead, r.Passed));
        Assert.Equal("2 results file(s) beyond the first 64 were not read", Assert.Single(r.Warnings));
    }

    [Theory]
    [InlineData("truncated", "<testsuites><testsuite><testcase classname=\"c\" name=\"t\">")]
    [InlineData("text", "this is not xml at all")]
    [InlineData("empty", "")]
    [InlineData("other root", "<html><body/></html>")]
    [InlineData("namespaced root", "<testsuites xmlns=\"urn:x\"><testcase classname=\"c\" name=\"t\"/></testsuites>")]
    [InlineData("raw escape", "<testsuites><testcase classname=\"c\" name=\"a\u001b[2Jb\"/></testsuites>")]
    public void TestResultsBrokenJunitIsNotCounted(string what, string content)
    {
        Assert.NotEmpty(what);
        Refused(Read(Junit, "broken.xml", content), NotValidJunit);
    }

    [Fact]
    public void TestResultsBinaryFileIsNotCounted()
    {
        var report = TestResultsParserTests.Report(_dir, ("r/bin.xml", ""));
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(report.Directory, "r", "bin.xml"), bytes);

        Refused(TestResultsParser.Parse(Junit, report, ignoreCase: false), NotValidJunit);
    }

    /// <summary>A UTF-16 file with its byte order mark is valid XML and is read like any other.</summary>
    [Fact]
    public void TestResultsUtf16WithBomIsRead()
    {
        var report = TestResultsParserTests.Report(_dir, ("r/u16.xml", ""));
        File.WriteAllText(
            Path.Combine(report.Directory, "r", "u16.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-16\"?><testsuites><testcase classname=\"c\" name=\"t\"><failure/></testcase></testsuites>",
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var r = TestResultsParser.Parse(Junit, report, ignoreCase: false);

        Assert.Equal((1, 1, "c.t"), (r.FilesRead, r.Failed, r.FailedTests[0].Name));
    }

    [Fact]
    public void TestResultsTrxWithAnotherNamespaceIsNotCounted()
    {
        var other = TestResultsParserTests.Fixture("xunit.trx").Replace(TestResultsParser.TrxNamespace, "http://example.com/not-trx", StringComparison.Ordinal);

        Refused(Read(Trx, "other.trx", other), NotValidTrx);

        // JUnit given as TRX (and the other way round) is not counted either.
        Refused(Read(Trx, "junit.trx", TestResultsParserTests.Fixture("pytest.xml")), NotValidTrx);
        Refused(Read(Junit, "trx.xml", TestResultsParserTests.Fixture("xunit.trx")), NotValidJunit);
    }

    [Fact]
    public void TestResultsTrxWithoutANamespaceIsRead()
    {
        var bare = TestResultsParserTests.Fixture("xunit.trx").Replace(" xmlns=\"" + TestResultsParser.TrxNamespace + "\"", "", StringComparison.Ordinal);

        Assert.Equal((3, 1), (Read(Trx, "bare.trx", bare).Total, Read(Trx, "bare2.trx", bare).Failed));
    }

    [Fact]
    public void TestResultsLongAttributeIsCutAndMarked()
    {
        var name = new string('n', 5000);

        var r = Read(Junit, "long.xml", $"<testsuites><testcase classname=\"c\" name=\"{name}\"><failure/></testcase></testsuites>");

        Assert.True(r.Truncated);
        Assert.Equal(TestResultsParser.MaxNameChars, r.FailedTests[0].Name.Length);
        Assert.StartsWith("c.nnnn", r.FailedTests[0].Name, StringComparison.Ordinal);
        Assert.Empty(r.Warnings);
    }

    /// <summary>The read-time cap, checked every 4,096 nodes, on an injected clock that moves 11 seconds per look.</summary>
    [Fact]
    public void TestResultsSlowReadIsStopped()
    {
        var sb = new StringBuilder("<testsuites>");
        for (var i = 0; i < 5000; i++)
        {
            sb.Append("<testcase classname=\"c\" name=\"t\"/>");
        }

        var report = TestResultsParserTests.Report(_dir, ("r/slow.xml", sb.Append("</testsuites>").ToString()));

        Refused(TestResultsParser.Parse(Junit, report, ignoreCase: false, new SteppingTimestamps()), "1 results file(s) took over 10 s to read and were not counted");
        Assert.Equal(5000, TestResultsParser.Parse(Junit, report, ignoreCase: false).Passed); // the real clock: read in full
    }

    private sealed class SteppingTimestamps : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => _now += 11;
    }

    /// <summary>C1 controls, bidi overrides and zero-width characters in names become '?'; a raw ESC is not even valid XML.</summary>
    [Fact]
    public void TestResultsNamesAreSanitised()
    {
        var r = Read(Junit, "names.xml", "<testsuites><testcase classname=\"c\" name=\"a&#x9b;2Jb&#x202e;evil&#x200b;\"><failure/></testcase></testsuites>");

        Assert.Equal("c.a?2Jb?evil?", Assert.Single(r.FailedTests).Name);
        Assert.Contains(r.FailedTests[0].Name, Cli.ResultsJsonOf(r)!.FailedTests.Select(f => f.Name));
    }

    /// <summary>A link (or a folder named like a results file) is never followed: the fetch never writes one, and the reader checks again.</summary>
    [Fact]
    public void TestResultsLinksAreNotRead()
    {
        var report = TestResultsParserTests.Report(_dir, ("r/real.xml", "<testsuites><testcase classname=\"c\" name=\"t\"/></testsuites>"));
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        FetchReceiverTests.MakeDirLink(Path.Combine(report.Directory, "r", "dir.xml"), target);
        var fetched = new List<FetchedFile>(report.Fetched) { new("r/dir.xml", 0, "") };
        var outside = Path.Combine(_dir, "outside.xml");
        File.WriteAllText(outside, "<testsuites><testcase classname=\"c\" name=\"outside\"><failure/></testcase></testsuites>");
        try
        {
            File.CreateSymbolicLink(Path.Combine(report.Directory, "r", "file.xml"), outside);
            fetched.Add(new FetchedFile("r/file.xml", 0, ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege: the junction above still covers a link.
        }

        var r = TestResultsParser.Parse(Junit, report with { Fetched = fetched }, ignoreCase: false);

        Assert.Equal((fetched.Count, 1, 1, 0), (r.Files, r.FilesRead, r.Passed, r.Failed));
        Assert.Equal($"{fetched.Count - 1} results file(s) were not regular files (a link or a folder) and were not read", Assert.Single(r.Warnings));
    }

    /// <summary>No file name ever appears in a warning (TRX names carry the worker's user and host), only counts.</summary>
    [Fact]
    public void TestResultsWarningsNeverNameAFile()
    {
        var r = TestResultsParser.Parse(
            Trx,
            TestResultsParserTests.Report(_dir, ("r/worker@build-01 2026-11-13.trx", "broken"), ("r/ok.trx", TestResultsParserTests.Fixture("counters.trx"))),
            ignoreCase: false);

        Assert.Equal(2, r.Warnings.Count);
        Assert.All(r.Warnings, w => Assert.DoesNotContain("build-01", w, StringComparison.Ordinal));
        Assert.All(r.Warnings, w => Assert.DoesNotContain(".trx", w, StringComparison.Ordinal));
    }
}
