using System.Globalization;
using System.Text.Json;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq stats by plan job (0.10.0): jobs.jsonl planFile, and planJobs grouped by (planFile, planJob) (Jobs/JobStats.cs).</summary>
public sealed class PlanStatsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private const string RepoA = "/home/dev/src/app/lanq.yml";

    private const string RepoB = "/home/dev/clones/app/lanq.yml";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "lanq-planstats-" + Guid.NewGuid().ToString("N"));

    public PlanStatsTests() => Directory.CreateDirectory(_home);

    public void Dispose() => TestTree.Delete(_home);

    private JobLog Log => new(_home);

    private static int _id;

    /// <summary>One record of a plan job: started <paramref name="minute"/> minutes after T0, <paramref name="wall"/> seconds long.</summary>
    private static JobRecord R(
        string? planFile, string job, string planRun, string status, double minute = 0, double wall = 60, int? shard = null, int? shards = null, string? baselineOf = null) =>
        new(string.Create(CultureInfo.InvariantCulture, $"j{Interlocked.Increment(ref _id)}"), "build-01", "npm run " + job, T0.AddMinutes(minute), wall, status == "Completed" ? 0 : 1, status)
        {
            V = 2,
            Key = "npm run " + job,
            Dir = ".",
            PlanJob = job,
            PlanRun = planRun,
            PlanFile = planFile,
            Shard = shard,
            Shards = shards,
            BaselineOf = baselineOf,
        };

    private async Task<JsonElement> StatsJson()
    {
        var o = new StringWriter();
        var code = await Cli.Stats(ParsedArgs.Parse(["stats", "--json"]), Log, 30, o, new StringWriter(), CancellationToken.None, TimeZoneInfo.Utc, T0.AddDays(1));
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o.ToString());
        return doc.RootElement.Clone();
    }

    [Fact]
    public void GroupsByPlanFileAndJob()
    {
        var stats = JobStats.PlanJobsOf(
        [
            R(RepoA, "unit", "p1", "Completed", 0),
            R(RepoA, "lint", "p1", "Completed", 0),
            R(RepoA, "unit", "p2", "Failed", 10),
            R(RepoB, "unit", "p3", "Completed", 20), // a clone in another folder: its own row
            R(null, "unit", "p0", "Completed", -10), // the plan file was not recorded (before 0.10.0)
            new JobRecord("plain", "build-01", "npm test", T0, 5, 0, "Completed"), // not a plan job
        ]);

        Assert.Equal([(RepoB, "unit"), (RepoA, "unit"), (RepoA, "lint"), (null, "unit")], stats.Select(s => (s.PlanFile, s.Job)));
        var unit = stats.Single(s => s.PlanFile == RepoA && s.Job == "unit");
        Assert.Equal((2, 1, 1, 0), (unit.Runs, unit.Completed, unit.Failed, unit.Other));
        Assert.Equal(T0.AddMinutes(10), unit.LastRunUtc);
    }

    [Fact]
    public void ShardedPlanJobIsOneRunPerPlanRun()
    {
        var stats = JobStats.PlanJobsOf(
        [
            // p1: shard 2 lost its machine and its retry completed; every shard's last attempt completed.
            R(RepoA, "unit", "p1", "Completed", 0, shard: 1, shards: 2),
            R(RepoA, "unit", "p1", "InfraError", 0, shard: 2, shards: 2),
            R(RepoA, "unit", "p1", "Completed", 1, shard: 2, shards: 2),

            // p2: one shard failed.
            R(RepoA, "unit", "p2", "Completed", 10, shard: 1, shards: 3),
            R(RepoA, "unit", "p2", "Failed", 10, shard: 2, shards: 3),
            R(RepoA, "unit", "p2", "Completed", 10, shard: 3, shards: 3),

            // p3: one shard timed out, none failed.
            R(RepoA, "unit", "p3", "Completed", 20, shard: 1, shards: 2),
            R(RepoA, "unit", "p3", "TimedOut", 20, shard: 2, shards: 2),
        ]);

        var unit = Assert.Single(stats);
        Assert.Equal((3, 1, 1, 1), (unit.Runs, unit.Completed, unit.Failed, unit.Other));
        Assert.Equal(2, unit.LastShards);
    }

    [Fact]
    public void WallIsFirstStartToLastEnd()
    {
        var stats = JobStats.PlanJobsOf(
        [
            R(RepoA, "unit", "p1", "Completed", 0, wall: 60, shard: 1, shards: 2),
            R(RepoA, "unit", "p1", "Completed", 10.0 / 60, wall: 100, shard: 2, shards: 2), // starts 10 s later, ends at 110 s
            R(RepoA, "unit", "p2", "Completed", 30, wall: 50, shard: 1, shards: 2),
            R(RepoA, "unit", "p2", "Completed", 30, wall: 30, shard: 2, shards: 2),
            R(RepoA, "unit", "p3", "Failed", 60, wall: 999, shard: 1, shards: 1), // failed runs are counted, never timed
        ]);

        var unit = Assert.Single(stats);
        Assert.Equal(50, unit.MedianSeconds); // nearest rank of [50, 110]: the lower middle
        Assert.Equal(110, unit.P90Seconds);
    }

    [Fact(Timeout = 30_000)]
    public async Task OldRecordsGroupUnderNull()
    {
        Log.Append(R(null, "unit", "p1", "Completed"));
        Log.Append(R(RepoA, "unit", "p2", "Completed", 5));

        var json = await StatsJson();

        var rows = json.GetProperty("planJobs").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(RepoA, rows[0].GetProperty("planFile").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("planFile").ValueKind);
        Assert.Equal(
            ["planFile", "job", "runs", "completed", "failed", "other", "medianSeconds", "p90Seconds", "lastRunUtc", "lastShards"],
            rows[0].EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("planFile", File.ReadAllLines(Log.FilePath)[0], StringComparison.Ordinal); // left out of the line when null
    }

    [Fact(Timeout = 30_000)]
    public async Task BaselineRecordsExcluded()
    {
        Log.Append(R(RepoA, "unit", "p1", "Failed", 0));
        Log.Append(R(RepoA, "unit", "p1", "Completed", 2, baselineOf: "j-failed")); // the rerun at the base commit

        var json = await StatsJson();
        var unit = Assert.Single(json.GetProperty("planJobs").EnumerateArray());

        Assert.Equal((1, 0, 1), (unit.GetProperty("runs").GetInt32(), unit.GetProperty("completed").GetInt32(), unit.GetProperty("failed").GetInt32()));
        Assert.Equal(1, Assert.Single(JobStats.PlanJobsOf(Log.ReadAll())).Runs); // left out even from the raw records
    }

    [Fact(Timeout = 30_000)]
    public async Task CappedWithPlanJobsMore()
    {
        for (var i = 0; i < Cli.MaxStatsPlanJobs + 3; i++)
        {
            Log.Append(R(string.Create(CultureInfo.InvariantCulture, $"/repos/r{i}/lanq.yml"), "unit", "p" + i, "Completed", i));
        }

        var json = await StatsJson();

        Assert.Equal(Cli.MaxStatsPlanJobs, json.GetProperty("planJobs").GetArrayLength());
        Assert.Equal(3, json.GetProperty("planJobsMore").GetInt32());
        Assert.Equal("/repos/r502/lanq.yml", json.GetProperty("planJobs")[0].GetProperty("planFile").GetString()); // newest first

        // The text lists the newest 20 and says how many more.
        var o = new StringWriter();
        await Cli.Stats(ParsedArgs.Parse(["stats"]), Log, 30, o, new StringWriter(), CancellationToken.None, TimeZoneInfo.Utc, T0.AddDays(1));
        var text = o.ToString().ReplaceLineEndings("\n");
        Assert.Contains("Plan jobs (newest first; one run per plan run;", text, StringComparison.Ordinal);
        Assert.Contains("/repos/r502/lanq.yml", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/repos/r482/lanq.yml", text, StringComparison.Ordinal);
        Assert.Contains("  and 483 more (lanq stats --json lists up to 500)", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task JsonAlwaysHasPlanJobs()
    {
        var empty = await StatsJson(); // no job log at all
        Log.Append(new JobRecord("plain", "build-01", "npm test", T0, 5, 0, "Completed") { V = 2, Key = "npm test", Dir = "." });
        var noPlan = await StatsJson();

        Assert.Equal(0, empty.GetProperty("planJobs").GetArrayLength());
        Assert.Equal(0, noPlan.GetProperty("planJobs").GetArrayLength());
        Assert.False(noPlan.TryGetProperty("planJobsMore", out _));
        var error = JsonSerializer.Serialize(Cli.StatsError("InfraError", 70, "x", 30, []), Output.LanqJson.Default.StatsJson);
        Assert.Contains("\"planJobs\":[]", error, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task TextTableShowsPlanJobs()
    {
        Log.Append(R(RepoA, "unit", "p1", "Completed", 0, wall: 90, shard: 1, shards: 2));
        Log.Append(R(RepoA, "unit", "p1", "Completed", 0, wall: 60, shard: 2, shards: 2));
        Log.Append(R(null, "lint", "p0", "Failed", -5));

        var o = new StringWriter();
        await Cli.Stats(ParsedArgs.Parse(["stats"]), Log, 30, o, new StringWriter(), CancellationToken.None, TimeZoneInfo.Utc, T0.AddDays(1));
        var lines = o.ToString().ReplaceLineEndings("\n").Split('\n');

        var header = Array.FindIndex(lines, l => l.StartsWith("Plan jobs", StringComparison.Ordinal));
        Assert.True(header > 0);
        static string[] Cells(string line) => line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["job", "runs", "pass", "fail", "other", "median", "p90", "last run", "shards", "plan file"], Cells(lines[header + 1]));
        Assert.Equal(["unit", "1", "1", "0", "0", "1.5 min", "1.5 min", "2026-11-01 10:00", "2", RepoA], Cells(lines[header + 2]));
        Assert.Equal(["lint", "1", "0", "1", "0", "-", "-", "2026-11-01 09:55", "1", "(recorded before 0.10.0)"], Cells(lines[header + 3]));
    }
}

/// <summary>A plan run records its plan file (0.10.0).</summary>
public sealed partial class PlanFileRunTests
{
    [Fact(Timeout = TestTimeout)]
    public async Task PlanRecordsCarryThePlanFile()
    {
        Plan(Three);

        var (code, _, _) = await Run(new PoolSsh(), "--plan");

        Assert.Equal(0, code);
        var records = Log.ReadAll();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(Path.Combine(Repo, "lanq.yml"), r.PlanFile));
        Assert.Single(records.Select(r => r.PlanRun).Distinct());
        Assert.Equal(["build", "lint", "unit"], JobStats.PlanJobsOf(records).Select(s => s.Job).Order(StringComparer.Ordinal));
    }
}
