using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq stats: a summary of the local job log (jobs.1.jsonl then jobs.jsonl) in a temporary state folder.</summary>
public sealed class StatsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "lanq-stats-" + Guid.NewGuid().ToString("N"));

    public StatsTests() => Directory.CreateDirectory(_home);

    public void Dispose() => TestTree.Delete(_home);

    /// <summary>
    /// The lines lanq stats printed before 0.7.0 (written and committed against the 0.6 code): the totals line, then one line per machine
    /// ("-" for a job without one) in ordinal order. They stay first and byte for byte the same.
    /// </summary>
    private const string OldLines =
        "5 jobs, 13.3 min total wall-clock\n"
        + "  -: 1 jobs, 0 min\n"
        + "  build-01: 3 jobs, 12.3 min\n"
        + "  micro-01: 1 jobs, 1 min\n";

    /// <summary>The 0.6 job log of the golden test: an old rotated file, old lines, one 0.7 line with every new field, a torn line.</summary>
    private void WriteGoldenLog()
    {
        File.WriteAllText(Path.Combine(_home, "jobs.1.jsonl"),
            "{\"id\":\"20261020-100000-000001\",\"machine\":\"build-01\",\"command\":\"dotnet build\",\"startedUtc\":\"2026-10-20T10:00:00+00:00\","
            + "\"wallSeconds\":615,\"exitCode\":0,\"status\":\"Completed\"}\n");
        File.WriteAllText(Path.Combine(_home, "jobs.jsonl"),
            "{\"id\":\"20261101-100000-000002\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-11-01T10:00:00+00:00\","
            + "\"wallSeconds\":90.5,\"exitCode\":0,\"status\":\"Completed\"}\n"
            + "{\"id\":\"20261102-100000-000003\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-11-02T10:00:00+00:00\","
            + "\"wallSeconds\":30,\"exitCode\":1,\"status\":\"Failed\"}\n"
            + "{\"id\":\"20261103-100000-000004\",\"machine\":\"micro-01\",\"command\":\"npx jest --shard=1/2\",\"startedUtc\":\"2026-11-03T10:00:00+00:00\","
            + "\"wallSeconds\":61.25,\"exitCode\":0,\"status\":\"Completed\",\"v\":2,\"key\":\"npx jest --shard={shard}/{shards}\",\"dir\":\".\","
            + "\"shard\":1,\"shards\":2,\"runId\":\"r1\",\"runSeconds\":50}\n"
            + "{\"id\":\"20261103-110000-000005\",\"machine\":null,\"command\":\"npx jest\",\"startedUtc\":\"2026-11-03T11:00:00+00:00\","
            + "\"wallSeconds\":0.4,\"exitCode\":2,\"status\":\"Rejected\"}\n"
            + "{\"id\":\"20261103-120000-0000\n");
    }

    private (int Code, string Out) Stats()
    {
        var o = new StringWriter();
        var code = Cli.Stats(new JobLog(_home), o);
        return (code, o.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void StatsTextKeepsTheOldTotalsLinesFirst()
    {
        WriteGoldenLog();
        var (code, text) = Stats();
        Assert.Equal(0, code);
        Assert.StartsWith(OldLines, text, StringComparison.Ordinal);
    }
}
