using System.Globalization;
using System.Xml;
using LanQueue.Fetch;
using LanQueue.Jobs;

namespace LanQueue.Results;

/// <summary>One failed test: its name (sanitised, at most 512 characters), its duration when the file gives one, and the shard that reported it.</summary>
public sealed record FailedTest(string Name, double? DurationSeconds, int? Shard);

/// <summary>
/// What lanq counted in a job's results files (0.11.0). Information only: never changes a status or exit code. Counts are lanq's own (a TRX
/// file's Counters are only compared); the names are data the worker wrote, sanitised, and are never run.
/// </summary>
/// <param name="Path">The results pattern (results.path / --results).</param>
/// <param name="Files">Fetched files that matched the pattern.</param>
/// <param name="FilesRead">Of those, the files that were read and counted.</param>
/// <param name="Total">Every test counted (passed, failed and skipped).</param>
/// <param name="Executed">Passed plus failed.</param>
/// <param name="DurationSeconds">The sum of the test durations the files give; null when none gives one.</param>
/// <param name="FailedTests">The failed tests' names, without repeats, at most <see cref="TestResultsParser.MaxFailedKept"/>.</param>
/// <param name="FailedKnown">Failed names known: the kept ones plus those past the cap (counted, not kept).</param>
/// <param name="Truncated">Names past the cap were not kept, or an attribute was cut at 4,096 characters.</param>
/// <param name="Warnings">Counts only, never file names (TRX file names hold the worker's user and host).</param>
/// <param name="ShardsWithNoTests">Merged shards only: the shards that executed no test (information, never a warning).</param>
public sealed record TestResults(
    ResultsFormat Format, string Path, int Files, int FilesRead, int Total, int Executed, int Passed, int Failed, int Skipped, double? DurationSeconds,
    IReadOnlyList<FailedTest> FailedTests, int FailedKnown, bool Truncated, IReadOnlyList<string> Warnings, IReadOnlyList<int> ShardsWithNoTests)
{
    /// <summary>
    /// Some results may be missing: a matching file was not read (a cap, not valid XML, a link), the fetch had an error, or the fetch
    /// skipped a file the results path matches (or more files than it lists). With no test counted, lanq cannot tell whether tests ran.
    /// </summary>
    public bool Unreadable { get; init; }

    /// <summary>No test was counted as executed (none passed or failed).</summary>
    public bool NoTests => Executed == 0;

    /// <summary>
    /// With <see cref="NoTests"/>: results_unreadable (<see cref="Unreadable"/>: whether tests ran is unknown), no_results_files (the fetch
    /// completed, skipped no results file, and no file matched) or zero_executed (every matched file was read and no test ran); null when
    /// tests ran.
    /// </summary>
    public string? NoTestsReason => !NoTests ? null : Unreadable ? TestResultsParser.ResultsUnreadable : Files == 0 ? "no_results_files" : "zero_executed";
}

/// <summary>
/// Counts tests in the fetched TRX or JUnit files of one job (0.11.0). The files are hostile input (a compromised worker writes them), so the
/// reader takes no DTD and no resolver, every file and the job have caps (size, count, depth, elements, attribute length, read time), and
/// names are sanitised and cut. <see cref="Parse(ResultsSpec, FetchReport?, bool)"/> never throws: every problem is a warning, counted.
/// </summary>
public static class TestResultsParser
{
    internal const long MaxFileBytes = 64L * 1024 * 1024;
    internal const int MaxFiles = 64;
    internal const long MaxTotalBytes = 256L * 1024 * 1024;
    internal const int MaxDepth = 32;
    internal const int MaxElements = 2_000_000;
    internal const int MaxAttributeChars = 4096;
    internal const int MaxFailedKept = 10_000;
    internal const int MaxNameChars = 512;
    internal const int CheckEvery = 4096;
    internal const int MaxMergedWarnings = 20;
    internal static readonly TimeSpan MaxReadTime = TimeSpan.FromSeconds(10);

    /// <summary>One test's duration above this (or not a finite number) is dropped.</summary>
    internal const double MaxCaseSeconds = 1e7;

    /// <summary>A sum of durations above this (or not a finite number) is left out (durationSeconds absent) with a warning.</summary>
    internal const double MaxSumSeconds = 1e12;

    /// <summary>The noTestsReason when results may be missing (<see cref="TestResults.Unreadable"/>).</summary>
    internal const string ResultsUnreadable = "results_unreadable";

    internal const string DurationWarning = "the test durations do not add up to a usable number; durationSeconds is left out";

    /// <summary>The namespace of a VSTest TRX file (a TRX file without a namespace is read too).</summary>
    internal const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 64L * 1024 * 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = true,
    };

    /// <summary>Counts the fetched files of <paramref name="report"/> that match the results pattern (in path order). Never throws.</summary>
    /// <param name="ignoreCase">The pattern matches ignoring case (a Windows worker, as the fetch itself).</param>
    public static TestResults Parse(ResultsSpec spec, FetchReport? report, bool ignoreCase) => Parse(spec, report, ignoreCase, TimeProvider.System);

    /// <param name="clock">The read-time cap's clock (tests).</param>
    internal static TestResults Parse(ResultsSpec spec, FetchReport? report, bool ignoreCase, TimeProvider clock)
    {
        try
        {
            return ParseFiles(spec, report, ignoreCase, clock);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Empty(spec, $"the results could not be counted ({ex.GetType().Name})");
        }
    }

    private static TestResults Empty(ResultsSpec spec, string warning) =>
        new(spec.Format, spec.Path, 0, 0, 0, 0, 0, 0, 0, null, [], 0, false, [warning], []) { Unreadable = true };

    /// <summary>A count that cannot overflow: summed as long, then held at int.MaxValue.</summary>
    internal static int Saturate(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    /// <summary>The saturating sum of counts.</summary>
    internal static int Sum(IEnumerable<int> values) => Saturate(values.Sum(v => (long)v));

    /// <summary>a + b for durations; a sum that is not finite or above <see cref="MaxSumSeconds"/> sets <paramref name="bad"/>.</summary>
    private static double? AddSeconds(double? a, double? b, ref bool bad)
    {
        if (b is not { } add)
        {
            return a;
        }

        var sum = (a ?? 0) + add;
        if (!double.IsFinite(sum) || sum < 0 || sum > MaxSumSeconds)
        {
            bad = true;
        }

        return sum;
    }

    private static TestResults ParseFiles(ResultsSpec spec, FetchReport? report, bool ignoreCase, TimeProvider clock)
    {
        var matcher = FetchPatterns.Matcher([spec.Path], ignoreCase);
        var files = report is null
            ? []
            : report.Fetched.Where(f => matcher.IsMatch(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var job = new Totals();
        int read = 0, invalid = 0, oversize = 0, beyond = 0, overTotal = 0, capped = 0, slow = 0, mismatched = 0, notRegular = 0;
        long bytes = 0;
        var badDuration = false;
        for (var i = 0; i < files.Count; i++)
        {
            if (i >= MaxFiles)
            {
                beyond++;
                continue;
            }

            var local = System.IO.Path.Combine(report!.Directory, files[i].Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            FileInfo info;
            try
            {
                info = new FileInfo(local);
                if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
                {
                    notRegular++;
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                invalid++;
                continue;
            }

            if (info.Length > MaxFileBytes)
            {
                oversize++;
                continue;
            }

            if (bytes + info.Length > MaxTotalBytes)
            {
                overTotal++;
                continue;
            }

            try
            {
                using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

                // Again once open: the file may have grown, or been swapped for a link, since it was checked.
                if (stream.Length > MaxFileBytes)
                {
                    oversize++;
                    continue;
                }

                if (bytes + stream.Length > MaxTotalBytes)
                {
                    overTotal++;
                    continue;
                }

                var again = new FileInfo(local);
                if (again.LinkTarget is not null || (again.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
                {
                    notRegular++;
                    continue;
                }

                bytes += stream.Length;
                var one = ReadFile(spec.Format, stream, clock);
                badDuration |= job.Add(one);
                read++;
                mismatched += one.CountersDisagree ? 1 : 0;
            }
            catch (CapException ex)
            {
                if (ex.Slow)
                {
                    slow++;
                }
                else
                {
                    capped++;
                }
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                or NotSupportedException)
            {
                invalid++;
            }
        }

        var warnings = new List<string>();
        if (files.Count == 0)
        {
            warnings.Add($"no results file matched results path {spec.Path}; check that the tests write it there (dir:) and fetchSkipped");
        }

        void Warn(int n, string text)
        {
            if (n > 0)
            {
                warnings.Add(n.ToString(CultureInfo.InvariantCulture) + " " + text);
            }
        }

        Warn(invalid, $"results file(s) were not valid {(spec.Format == ResultsFormat.Trx ? "TRX" : "JUnit")} XML and were not counted");
        Warn(notRegular, "results file(s) were not regular files (a link or a folder) and were not read");
        Warn(oversize, "results file(s) over 64 MB were not read");
        Warn(beyond, $"results file(s) beyond the first {MaxFiles} were not read");
        Warn(overTotal, "results file(s) past 256 MB in all were not read");
        Warn(capped, "results file(s) hit the depth or element cap and were not counted");
        Warn(slow, "results file(s) took over 10 s to read and were not counted");
        if (mismatched > 0)
        {
            warnings.Add($"TRX counters disagree with the results in {mismatched.ToString(CultureInfo.InvariantCulture)} file(s); the counts are lanq's own");
        }

        if (job.NamesPastCap > 0)
        {
            warnings.Add($"more than {MaxFailedKept} failed tests; names after that are not kept (counts are complete)");
        }

        if (badDuration)
        {
            warnings.Add(DurationWarning);
        }

        // Results may be missing when the fetch did not finish, or skipped a file the results path matches (or more than it lists).
        var fetchSkips = report?.Skipped.Count(f => matcher.IsMatch(f.Path)) ?? 0;
        if (fetchSkips > 0)
        {
            warnings.Add($"{fetchSkips.ToString(CultureInfo.InvariantCulture)} results file(s) were skipped by the fetch and not read (see fetchSkipped)");
        }

        if (report is { Error: not null })
        {
            warnings.Add("the fetch did not complete (see fetchError); results files may be missing");
        }

        var unreadable = read < files.Count || fetchSkips > 0 || report is { Error: not null } || report is { MoreSkipped: > 0 };
        return new TestResults(
            spec.Format, spec.Path, files.Count, read, Saturate((long)job.Passed + job.Failed + job.Skipped), Saturate((long)job.Passed + job.Failed), job.Passed, job.Failed,
            job.Skipped, badDuration ? null : job.Duration, job.Names, Saturate((long)job.Names.Count + job.NamesPastCap), job.NamesPastCap > 0 || job.Cut, warnings, [])
        {
            Unreadable = unreadable,
        };
    }

    /// <summary>
    /// Merges the results of a sharded job's shards (in shard order): counts, file counts and durations summed, failed names without repeats
    /// (the first shard to report one keeps it), warnings prefixed "shard i/N: " (at most 20), and the shards that executed no test. Null when
    /// no shard has results.
    /// </summary>
    public static TestResults? Merge(IReadOnlyList<(int Shard, TestResults? R)> shards)
    {
        var present = shards.Where(s => s.R is not null).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        var first = present[0].R!;
        var names = new List<FailedTest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pastCap = 0;
        var warnings = new List<string>();
        double? duration = null;
        var badDuration = false;
        var count = shards.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var (shard, r) in present)
        {
            foreach (var f in r!.FailedTests)
            {
                if (!seen.Add(f.Name))
                {
                    continue;
                }

                if (names.Count < MaxFailedKept)
                {
                    names.Add(f with { Shard = shard });
                }
                else
                {
                    pastCap++;
                }
            }

            pastCap = Saturate((long)pastCap + Math.Max(0, r.FailedKnown - r.FailedTests.Count));
            foreach (var w in r.Warnings)
            {
                if (warnings.Count < MaxMergedWarnings)
                {
                    warnings.Add($"shard {shard.ToString(CultureInfo.InvariantCulture)}/{count}: {w}");
                }
            }

            duration = AddSeconds(duration, r.DurationSeconds, ref badDuration);
        }

        if (badDuration)
        {
            warnings.Add(DurationWarning);
        }

        return new TestResults(
            first.Format,
            first.Path,
            Sum(present.Select(s => s.R!.Files)),
            Sum(present.Select(s => s.R!.FilesRead)),
            Sum(present.Select(s => s.R!.Total)),
            Sum(present.Select(s => s.R!.Executed)),
            Sum(present.Select(s => s.R!.Passed)),
            Sum(present.Select(s => s.R!.Failed)),
            Sum(present.Select(s => s.R!.Skipped)),
            badDuration ? null : duration,
            names,
            Saturate((long)names.Count + pastCap),
            pastCap > 0 || present.Any(s => s.R!.Truncated),
            warnings,
            [.. present.Where(s => s.R!.Executed == 0).Select(s => s.Shard)])
        {
            Unreadable = present.Any(s => s.R!.Unreadable),
        };
    }

    /// <summary>One results file, read in full under the caps; throws on anything that is not a complete, valid file of the format.</summary>
    private static FileCounts ReadFile(ResultsFormat format, Stream stream, TimeProvider clock)
    {
        using var reader = XmlReader.Create(stream, Settings);
        var guard = new Guard(clock);
        return format == ResultsFormat.Trx ? ReadTrx(reader, guard) : ReadJunit(reader, guard);
    }

    private static FileCounts ReadTrx(XmlReader r, Guard g)
    {
        var c = new FileCounts();
        string? ns = null;
        var countedDepth = -1;
        var resultsSeen = false;
        var failed = new List<(string? Id, string? TestName, double? Duration)>();
        var failedIds = new HashSet<string>(StringComparer.Ordinal);
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        int? countersTotal = null;
        string? unitTestId = null;
        var unitTestDepth = -1;
        while (g.Read(r))
        {
            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.Depth == countedDepth && r.LocalName == "UnitTestResult")
                {
                    countedDepth = -1;
                }
                else if (r.Depth == unitTestDepth && r.LocalName == "UnitTest")
                {
                    (unitTestId, unitTestDepth) = (null, -1);
                }

                continue;
            }

            if (r.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (ns is null)
            {
                if (r.LocalName != "TestRun" || (r.NamespaceURI != TrxNamespace && r.NamespaceURI.Length != 0))
                {
                    throw new XmlException("not a TRX file");
                }

                ns = r.NamespaceURI;
                continue;
            }

            if (r.NamespaceURI != ns)
            {
                continue;
            }

            switch (r.LocalName)
            {
                case "Results":
                    resultsSeen = true;
                    break;
                case "UnitTestResult":
                    // Inside a counted result its InnerResults are not counted again; a result without an outcome (the parent of data rows)
                    // is not counted, its InnerResults are.
                    if (countedDepth >= 0 || g.Attr(r, "outcome") is not { } outcome)
                    {
                        break;
                    }

                    var duration = TrxSeconds(g.Attr(r, "duration"));
                    var kind = TrxOutcome(outcome);
                    c.Count(kind, duration);
                    if (kind == Outcome.Failed)
                    {
                        var id = g.Attr(r, "testId");
                        failed.Add((id, g.Attr(r, "testName"), duration));
                        if (id is not null)
                        {
                            failedIds.Add(id);
                        }
                    }

                    if (!r.IsEmptyElement)
                    {
                        countedDepth = r.Depth;
                    }

                    break;
                case "UnitTest":
                    if (!r.IsEmptyElement)
                    {
                        (unitTestId, unitTestDepth) = (g.Attr(r, "id"), r.Depth);
                    }

                    break;
                case "TestMethod" when unitTestId is not null && r.Depth == unitTestDepth + 1:
                    // Before the results are seen every definition is kept (the join needs it); after, only the failed ones.
                    if ((!resultsSeen || failedIds.Contains(unitTestId)) && g.Attr(r, "name") is { } method)
                    {
                        var className = g.Attr(r, "className") is { } cn ? cn.Split(',')[0].Trim() : "";
                        defs.TryAdd(unitTestId, className.Length == 0 || method.StartsWith(className + ".", StringComparison.Ordinal) ? method : className + "." + method);
                    }

                    break;
                case "Counters":
                    countersTotal = int.TryParse(g.Attr(r, "total"), NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : null;
                    break;
            }
        }

        if (ns is null)
        {
            throw new XmlException("no TestRun element");
        }

        foreach (var (id, testName, duration) in failed)
        {
            var name = id is not null && defs.TryGetValue(id, out var full) ? full : testName ?? id ?? "(unnamed)";
            c.Fail(name, duration);
        }

        c.CountersDisagree = countersTotal is { } t && t != c.Total;
        c.Cut = g.Cut;
        return c;
    }

    private static FileCounts ReadJunit(XmlReader r, Guard g)
    {
        var c = new FileCounts();
        var root = false;
        var caseDepth = -1;
        string? caseName = null;
        double? caseTime = null;
        var caseOutcome = Outcome.Passed;
        while (g.Read(r))
        {
            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.Depth == caseDepth && r.LocalName == "testcase")
                {
                    End();
                }

                continue;
            }

            if (r.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!root)
            {
                if (r.LocalName is not ("testsuites" or "testsuite") || r.NamespaceURI.Length != 0)
                {
                    throw new XmlException("not a JUnit file");
                }

                root = true;
                continue;
            }

            if (caseDepth >= 0)
            {
                if (r.Depth == caseDepth + 1)
                {
                    caseOutcome = r.LocalName switch
                    {
                        "failure" or "error" => Outcome.Failed,
                        "skipped" when caseOutcome != Outcome.Failed => Outcome.Skipped,
                        _ => caseOutcome,
                    };
                }

                continue;
            }

            if (r.LocalName == "testcase")
            {
                var name = g.Attr(r, "name") ?? "(unnamed)";
                var className = g.Attr(r, "classname") ?? "";
                caseName = className.Length == 0 || name.StartsWith(className, StringComparison.Ordinal) ? name : className + "." + name;
                caseTime = double.TryParse(g.Attr(r, "time"), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var s)
                    && double.IsFinite(s) && s is >= 0 and <= MaxCaseSeconds
                    ? s
                    : null;
                caseOutcome = Outcome.Passed;
                if (r.IsEmptyElement)
                {
                    End();
                }
                else
                {
                    caseDepth = r.Depth;
                }
            }
        }

        if (!root)
        {
            throw new XmlException("no testsuites or testsuite element");
        }

        c.Cut = g.Cut;
        return c;

        void End()
        {
            c.Count(caseOutcome, caseTime);
            if (caseOutcome == Outcome.Failed)
            {
                c.Fail(caseName!, caseTime);
            }

            caseDepth = -1;
        }
    }

    private enum Outcome
    {
        Passed,
        Failed,
        Skipped,
    }

    /// <summary>TRX outcomes: Passed, PassedButRunAborted, Completed and Warning passed; Failed, Error, Timeout and Aborted failed; anything else skipped.</summary>
    private static Outcome TrxOutcome(string outcome) => outcome switch
    {
        "Passed" or "PassedButRunAborted" or "Completed" or "Warning" => Outcome.Passed,
        "Failed" or "Error" or "Timeout" or "Aborted" => Outcome.Failed,
        _ => Outcome.Skipped,
    };

    private static double? TrxSeconds(string? text) =>
        text is not null && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero && span.TotalSeconds <= MaxCaseSeconds
            ? span.TotalSeconds
            : null;

    /// <summary>A test name for output: sanitised (no control, bidi or zero-width characters), then cut to 512 characters.</summary>
    internal static string SafeName(string name)
    {
        var safe = JobRunner.SafeLine(name);
        return safe.Length > MaxNameChars ? safe[..MaxNameChars] : safe;
    }

    /// <summary>A file hit the depth or element cap, or (<see cref="Slow"/>) took longer than the read-time cap.</summary>
    private sealed class CapException(bool slow) : Exception
    {
        public bool Slow { get; } = slow;
    }

    /// <summary>The per-file caps, checked as the reader goes: depth, elements, read time (every 4,096 nodes) and attribute length.</summary>
    private sealed class Guard(TimeProvider clock)
    {
        private readonly long _start = clock.GetTimestamp();
        private long _nodes;
        private int _elements;

        /// <summary>An attribute value was cut at <see cref="MaxAttributeChars"/>.</summary>
        public bool Cut { get; private set; }

        public bool Read(XmlReader r)
        {
            if (!r.Read())
            {
                return false;
            }

            if (++_nodes % CheckEvery == 0 && clock.GetElapsedTime(_start) > MaxReadTime)
            {
                throw new CapException(slow: true);
            }

            if (r.NodeType == XmlNodeType.Element && (++_elements > MaxElements || r.Depth >= MaxDepth))
            {
                throw new CapException(slow: false);
            }

            return true;
        }

        public string? Attr(XmlReader r, string name)
        {
            var value = r.GetAttribute(name);
            if (value is { Length: > MaxAttributeChars })
            {
                Cut = true;
                return value[..MaxAttributeChars];
            }

            return value;
        }
    }

    /// <summary>One file's counts, added to the job's only when the whole file was read.</summary>
    private sealed class FileCounts
    {
        public int Passed;
        public int Failed;
        public int Skipped;
        public double? Duration;
        public bool CountersDisagree;
        public bool Cut;
        public readonly List<(string Name, double? Duration)> FailedNames = [];

        public int Total => Passed + Failed + Skipped;

        public void Count(Outcome outcome, double? duration)
        {
            switch (outcome)
            {
                case Outcome.Passed:
                    Passed++;
                    break;
                case Outcome.Failed:
                    Failed++;
                    break;
                default:
                    Skipped++;
                    break;
            }

            if (duration is { } d)
            {
                Duration = (Duration ?? 0) + d;
            }
        }

        public void Fail(string name, double? duration) => FailedNames.Add((name, duration));
    }

    /// <summary>A job's counts over its files, with its failed names (sanitised, without repeats, at most <see cref="MaxFailedKept"/>).</summary>
    private sealed class Totals
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Passed;
        public int Failed;
        public int Skipped;
        public double? Duration;
        public bool Cut;
        public int NamesPastCap;
        public readonly List<FailedTest> Names = [];

        /// <summary>Adds one file's counts; true when the durations no longer add up to a usable number (see <see cref="AddSeconds"/>).</summary>
        public bool Add(FileCounts c)
        {
            Passed = Saturate((long)Passed + c.Passed);
            Failed = Saturate((long)Failed + c.Failed);
            Skipped = Saturate((long)Skipped + c.Skipped);
            Cut |= c.Cut;
            var bad = false;
            Duration = AddSeconds(Duration, c.Duration, ref bad);

            foreach (var (name, duration) in c.FailedNames)
            {
                var safe = SafeName(name);
                if (!_seen.Add(safe))
                {
                    continue;
                }

                if (Names.Count < MaxFailedKept)
                {
                    Names.Add(new FailedTest(safe, duration is { } s ? Math.Round(s, 3) : null, null));
                }
                else
                {
                    NamesPastCap++;
                }
            }

            return bad;
        }
    }
}
