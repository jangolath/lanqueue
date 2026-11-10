using System.Globalization;
using System.Text.Json;
using LanQueue.Jobs;
using LanQueue.Scheduling;
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

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private const string Skipped1 = "[lanq] stats: 1 unreadable line(s) in jobs.jsonl skipped\n";

    private JobLog Log => new(_home);

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

    /// <summary>The end of the 90-day timing window in these tests.</summary>
    private static readonly DateTimeOffset Now = T0.AddDays(8);

    /// <summary>lanq stats (text, or --json) on the temporary state folder, times in UTC; stdout and stderr with "\n" line ends.</summary>
    private async Task<(int Code, string Out, string Err)> Stats(bool json = false, int keepDays = 30, CancellationToken ct = default, params string[] options)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var p = ParsedArgs.Parse([.. json ? ["stats", "--json"] : new[] { "stats" }, .. options]);
        var code = await Cli.Stats(p, Log, keepDays, o, e, ct, TimeZoneInfo.Utc, Now);
        return (code, o.ToString().ReplaceLineEndings("\n"), e.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>stdout of lanq stats --json: exactly one object (parsing fails on anything else) with every member, and no suggested command.</summary>
    private static JsonElement OneObject(string stdout, string status, int exitCode)
    {
        using var doc = JsonDocument.Parse(stdout);
        var r = doc.RootElement.Clone();
        Assert.Equal((status, exitCode), (r.GetProperty("status").GetString(), r.GetProperty("exitCode").GetInt32()));
        Assert.Equal(
            ["status", "exitCode", "message", "files", "jobs", "wallMinutes", "skippedLines", "oldestUtc", "newestUtc", "logs", "machines", "commands", "nextSteps"],
            r.EnumerateObject().Select(p => p.Name).Where(n => n != "commandsMore"));
        Assert.Equal(["files", "megabytes", "keepDays"], r.GetProperty("logs").EnumerateObject().Select(p => p.Name));
        Assert.NotEqual(0, r.GetProperty("nextSteps").GetArrayLength());
        Assert.All(r.GetProperty("nextSteps").EnumerateArray(), s => Assert.False(s.TryGetProperty("argv", out _)));
        return r;
    }

    /// <summary>One finished job as 0.7 records it: a key and dir, and with <paramref name="run"/> one shard of a --shards run.</summary>
    private static JobRecord Rec(
        string machine, double wall, string status = "Completed", int minutes = 0, string key = "npx jest", string dir = ".", string? run = null, int? shard = null,
        int? shards = null) =>
        new("20261101-100000-" + Guid.NewGuid().ToString("N")[..6], machine, key, T0.AddMinutes(minutes), wall, status == "Completed" ? 0 : 1, status)
        {
            V = 2,
            Key = key,
            Dir = dir,
            RunId = run,
            Shard = shard,
            Shards = shards,
            RunSeconds = wall - 5,
        };

    private void Append(params JobRecord[] records)
    {
        var log = Log;
        foreach (var r in records)
        {
            log.Append(r);
        }
    }

    [Fact]
    public async Task StatsTextKeepsTheOldTotalsLinesFirst()
    {
        WriteGoldenLog();
        var (code, text, err) = await Stats();
        Assert.Equal(0, code);
        Assert.StartsWith(OldLines + "\nMachines (median and p90 of completed jobs):\n", text, StringComparison.Ordinal);
        Assert.Equal(Skipped1, err);
    }

    /// <summary>The whole text of the golden log: the old lines, the machine table, the commands newest first, the logs line.</summary>
    [Fact]
    public async Task StatsTextShowsMachinesCommandsAndLogs()
    {
        WriteGoldenLog();
        var (_, text, _) = await Stats();
        Assert.Equal(
            OldLines
            + "\n"
            + "Machines (median and p90 of completed jobs):\n"
            + "  machine   jobs  pass  fail  other   median       p90  last run          speed\n"
            + "  -            1     0     0      1        -         -  2026-11-03 11:00  -\n"
            + "  build-01     3     2     1      0  1.5 min  10.3 min  2026-11-02 10:00  -\n"
            + "  micro-01     1     1     0      0    1 min     1 min  2026-11-03 10:00  - (1 job; 5 needed)\n"
            + "\n"
            + "Commands (newest first; each shard is one job):\n"
            + "  npx jest (recorded before 0.7.0)\n"
            + "    1 run, 1 job (0 passed, 0 failed, 1 other); median -, p90 -; last run 2026-11-03 11:00\n"
            + "    timing: none (no key)\n"
            + "  npx jest --shard={shard}/{shards}\n"
            + "    1 run, 1 job (1 passed, 0 failed, 0 other); median 1 min, p90 1 min; last run 2026-11-03 10:00 (2 shards)\n"
            + "    timing: about 1.7 min of work + 11.3 s per shard (from 1 run)\n"
            + "  dotnet test (recorded before 0.7.0)\n"
            + "    2 runs, 2 jobs (1 passed, 1 failed, 0 other); median 1.5 min, p90 1.5 min; last run 2026-11-02 10:00\n"
            + "    timing: none (no key)\n"
            + "  dotnet build (recorded before 0.7.0)\n"
            + "    1 run, 1 job (1 passed, 0 failed, 0 other); median 10.3 min, p90 10.3 min; last run 2026-10-20 10:00\n"
            + "    timing: none (no key)\n"
            + "\n"
            + "logs: 0 files, 0.0 MB (kept 30 days)\n",
            text);
    }

    [Fact]
    public async Task StatsJsonIsOneObjectWithMachinesAndCommands()
    {
        WriteGoldenLog();
        var (code, json, err) = await Stats(json: true);
        Assert.Equal(0, code);
        Assert.Equal(Skipped1, err);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal("5 jobs", r.GetProperty("message").GetString());
        Assert.Equal([Path.Combine(_home, "jobs.1.jsonl"), Path.Combine(_home, "jobs.jsonl")], r.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal((5, 13.3, 1), (r.GetProperty("jobs").GetInt32(), r.GetProperty("wallMinutes").GetDouble(), r.GetProperty("skippedLines").GetInt32()));
        Assert.Equal(("2026-10-20T10:00:00Z", "2026-11-03T11:00:00Z"), (r.GetProperty("oldestUtc").GetString(), r.GetProperty("newestUtc").GetString()));
        Assert.Equal((0, 0.0, 30), (r.GetProperty("logs").GetProperty("files").GetInt32(), r.GetProperty("logs").GetProperty("megabytes").GetDouble(),
            r.GetProperty("logs").GetProperty("keepDays").GetInt32()));
        Assert.False(r.TryGetProperty("commandsMore", out _));
        Assert.Equal("none", Assert.Single(r.GetProperty("nextSteps").EnumerateArray()).GetProperty("action").GetString());

        var machines = r.GetProperty("machines").EnumerateArray().ToList();
        Assert.Equal(["-", "build-01", "micro-01"], machines.Select(m => m.GetProperty("machine").GetString()));
        Assert.Equal(
            ["machine", "jobs", "completed", "failed", "other", "wallMinutes", "medianSeconds", "p90Seconds", "lastRunUtc", "speedFactor", "speedSamples"],
            machines[1].EnumerateObject().Select(p => p.Name));
        var build = machines[1];
        Assert.Equal(
            (3, 2, 1, 0, 12.3),
            (build.GetProperty("jobs").GetInt32(), build.GetProperty("completed").GetInt32(), build.GetProperty("failed").GetInt32(), build.GetProperty("other").GetInt32(),
                build.GetProperty("wallMinutes").GetDouble()));
        Assert.Equal((90.5, 615.0), (build.GetProperty("medianSeconds").GetDouble(), build.GetProperty("p90Seconds").GetDouble()));
        Assert.Equal("2026-11-02T10:00:00Z", build.GetProperty("lastRunUtc").GetString());
        Assert.Equal(JsonValueKind.Null, build.GetProperty("speedFactor").ValueKind); // no keyed job: no sample
        Assert.Equal(0, build.GetProperty("speedSamples").GetInt32());
        Assert.Equal((JsonValueKind.Null, 1), (machines[2].GetProperty("speedFactor").ValueKind, machines[2].GetProperty("speedSamples").GetInt32())); // 1 of 5
        Assert.Equal(JsonValueKind.Null, machines[0].GetProperty("medianSeconds").ValueKind); // no completed job

        var commands = r.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(["npx jest", "npx jest --shard={shard}/{shards}", "dotnet test", "dotnet build"], commands.Select(c => c.GetProperty("command").GetString()));
        Assert.Equal(
            ["command", "dir", "runs", "jobs", "completed", "failed", "other", "medianSeconds", "p90Seconds", "lastRunUtc", "lastShards", "timing"],
            commands[0].EnumerateObject().Select(p => p.Name));
        var sharded = commands[1];
        Assert.Equal(
            (".", 1, 1, 2),
            (sharded.GetProperty("dir").GetString(), sharded.GetProperty("runs").GetInt32(), sharded.GetProperty("jobs").GetInt32(), sharded.GetProperty("lastShards").GetInt32()));
        // One shard of 2 completed (50 s of its own run, 61.25 s wall-clock): half the shards, so the run counts: W = 2 x 50 s, O = 11.25 s.
        var timing = sharded.GetProperty("timing");
        Assert.Equal(["runsUsed", "workMinutes", "overheadSeconds", "suggestedShards"], timing.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 1.7, 11.3), (timing.GetProperty("runsUsed").GetInt32(), timing.GetProperty("workMinutes").GetDouble(), timing.GetProperty("overheadSeconds").GetDouble()));
        Assert.Equal(JsonValueKind.Null, timing.GetProperty("suggestedShards").ValueKind); // only with --target-minutes
        Assert.Equal(JsonValueKind.Null, commands[2].GetProperty("timing").ValueKind); // no key
    }

    [Fact]
    public async Task MedianAndP90UseNearestRankOnCompletedJobs()
    {
        // Ten completed jobs of 10..100 s, plus a failed and a timed-out one that would move both if they counted.
        Append([.. Enumerable.Range(1, 10).Select(i => Rec("build-01", i * 10, minutes: i)), Rec("build-01", 5000, "Failed", 11), Rec("build-01", 9000, "TimedOut", 12)]);
        var (_, json, _) = await Stats(json: true);
        var r = OneObject(json, "Ok", 0);
        var m = Assert.Single(r.GetProperty("machines").EnumerateArray());
        Assert.Equal((10, 1, 1), (m.GetProperty("completed").GetInt32(), m.GetProperty("failed").GetInt32(), m.GetProperty("other").GetInt32()));

        // Nearest rank: sorted[ceil(p * n) - 1] -> median the 5th (50 s), p90 the 9th (90 s); never interpolated.
        Assert.Equal((50.0, 90.0), (m.GetProperty("medianSeconds").GetDouble(), m.GetProperty("p90Seconds").GetDouble()));
        var c = Assert.Single(r.GetProperty("commands").EnumerateArray());
        Assert.Equal((50.0, 90.0), (c.GetProperty("medianSeconds").GetDouble(), c.GetProperty("p90Seconds").GetDouble()));

        Assert.Null(JobStats.Percentile([], 50));
        Assert.Equal(7.0, JobStats.Percentile([7], 90));
        Assert.Equal(2.0, JobStats.Percentile([4, 1, 3, 2], 50)); // an even count: the lower middle value
        Assert.Equal(4.0, JobStats.Percentile([4, 1, 3, 2], 90));
        Assert.Equal(1.2, JobStats.Percentile([1.234], 50)); // one decimal
    }

    [Fact]
    public async Task OldLinesCountInTotalsAndGroupByTheirCommandWithNullDir()
    {
        File.WriteAllText(Path.Combine(_home, "jobs.jsonl"),
            "{\"id\":\"a\",\"machine\":\"build-01\",\"command\":\"npx jest\",\"startedUtc\":\"2026-11-01T10:00:00+00:00\",\"wallSeconds\":60,\"exitCode\":0,\"status\":\"Completed\"}\n"
            + "{\"id\":\"b\",\"machine\":\"build-01\",\"command\":\"npx jest\",\"startedUtc\":\"2026-11-01T11:00:00+00:00\",\"wallSeconds\":120,\"exitCode\":0,\"status\":\"Completed\"}\n");
        Append(Rec("build-01", 30, minutes: 180)); // the same command, recorded by 0.7 (key "npx jest", dir ".")

        var (_, json, _) = await Stats(json: true);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal(3, r.GetProperty("jobs").GetInt32());
        Assert.Equal(3, r.GetProperty("machines")[0].GetProperty("jobs").GetInt32());
        var commands = r.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(2, commands.Count);
        Assert.Equal(("npx jest", "."), (commands[0].GetProperty("command").GetString(), commands[0].GetProperty("dir").GetString()));
        var old = commands[1];
        Assert.Equal("npx jest", old.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("dir").ValueKind);
        Assert.Equal(JsonValueKind.Null, old.GetProperty("lastShards").ValueKind);
        Assert.Equal(JsonValueKind.Null, old.GetProperty("timing").ValueKind);
        Assert.Equal(
            (2, 2, 60.0, 120.0),
            (old.GetProperty("runs").GetInt32(), old.GetProperty("jobs").GetInt32(), old.GetProperty("medianSeconds").GetDouble(), old.GetProperty("p90Seconds").GetDouble()));

        var (_, text, _) = await Stats();
        Assert.Contains("  npx jest (recorded before 0.7.0)\n    2 runs, 2 jobs (2 passed, 0 failed, 0 other)", text, StringComparison.Ordinal);
        Assert.Contains("    timing: none (no key)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShardsOfOneRunCountAsOneRunAndOneJobEach()
    {
        const string Key = "npx jest --shard={shard}/{shards}";
        Append(
            Rec("build-01", 100, key: Key, run: "run1", shard: 1, shards: 3),
            Rec("micro-01", 120, "Failed", key: Key, run: "run1", shard: 2, shards: 3),
            Rec("micro-01", 110, key: Key, minutes: 2, run: "run1", shard: 2, shards: 3), // shard 2 retried
            Rec("build-01", 90, key: Key, run: "run1", shard: 3, shards: 3),
            Rec("build-01", 200, key: Key, minutes: 60, run: "run2", shard: 1, shards: 2),
            Rec("build-01", 210, key: Key, minutes: 60, run: "run2", shard: 2, shards: 2),
            Rec("build-01", 400, key: Key, minutes: 120), // a single run of the same command
            Rec("build-01", 50, key: Key, dir: "web", minutes: 30, run: "run3", shard: 1, shards: 4)); // another --dir: another command

        var (_, json, _) = await Stats(json: true);
        var commands = OneObject(json, "Ok", 0).GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal([".", "web"], commands.Select(c => c.GetProperty("dir").GetString()));
        var c = commands[0];
        Assert.Equal(
            (3, 7, 6, 1, 0),
            (c.GetProperty("runs").GetInt32(), c.GetProperty("jobs").GetInt32(), c.GetProperty("completed").GetInt32(), c.GetProperty("failed").GetInt32(),
                c.GetProperty("other").GetInt32()));
        Assert.Equal(1, c.GetProperty("lastShards").GetInt32()); // the newest job was a single run
        Assert.Equal("2026-11-01T12:00:00Z", c.GetProperty("lastRunUtc").GetString());
        Assert.Equal(4, commands[1].GetProperty("lastShards").GetInt32());

        var (_, text, _) = await Stats();
        Assert.Contains("  npx jest --shard={shard}/{shards}\n    3 runs, 7 jobs (6 passed, 1 failed, 0 other)", text, StringComparison.Ordinal);
        Assert.Contains(
            "  npx jest --shard={shard}/{shards} (--dir web)\n    1 run, 1 job (1 passed, 0 failed, 0 other); median 50 s, p90 50 s; last run 2026-11-01 10:30 (4 shards)\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedLineIsSkippedAndCounted()
    {
        Append(Rec("build-01", 10));
        File.AppendAllText(Path.Combine(_home, "jobs.jsonl"), "{\"id\":\"torn\n{}\nnot json\n");
        Append(Rec("build-01", 20, minutes: 1));

        var (code, json, err) = await Stats(json: true);
        Assert.Equal(0, code);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal((2, 3), (r.GetProperty("jobs").GetInt32(), r.GetProperty("skippedLines").GetInt32()));
        Assert.Equal("[lanq] stats: 3 unreadable line(s) in jobs.jsonl skipped\n", err); // one warning, not one per line

        (code, var text, err) = await Stats();
        Assert.Equal(0, code);
        Assert.StartsWith("2 jobs, 0.5 min total wall-clock\n", text, StringComparison.Ordinal);
        Assert.Equal("[lanq] stats: 3 unreadable line(s) in jobs.jsonl skipped\n", err);
    }

    [Fact]
    public async Task AnUnreadableLogIs70WithAnObject()
    {
        Append(Rec("build-01", 10));
        int code;
        string json, err, text, textErr;
        using (new FileStream(Path.Combine(_home, "jobs.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (code, json, err) = await Stats(json: true);
            Assert.Equal(70, code);
            (code, text, textErr) = await Stats();
            Assert.Equal(70, code);
        }

        var r = OneObject(json, "InfraError", 70);
        Assert.StartsWith("the job log could not be read: ", r.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal((0, 0, 0, 0), (r.GetProperty("jobs").GetInt32(), r.GetProperty("files").GetArrayLength(), r.GetProperty("machines").GetArrayLength(),
            r.GetProperty("commands").GetArrayLength()));
        Assert.Equal("ask_developer", Assert.Single(r.GetProperty("nextSteps").EnumerateArray()).GetProperty("action").GetString());
        Assert.StartsWith("[lanq] stats: the job log could not be read: ", err, StringComparison.Ordinal);
        Assert.Contains("\nNext: the job log could not be read: ", err, StringComparison.Ordinal);

        Assert.Equal("", text); // text: nothing on stdout, the same lines on stderr
        Assert.Equal(err, textErr);
    }

    [Fact]
    public async Task StatsJsonCtrlCIs130WithAnObject()
    {
        Append(Rec("build-01", 10));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (code, json, err) = await Stats(json: true, ct: cts.Token);
        Assert.Equal(130, code);
        var r = OneObject(json, "Cancelled", 130);
        Assert.Equal("none", Assert.Single(r.GetProperty("nextSteps").EnumerateArray()).GetProperty("action").GetString());
        Assert.Equal("", err);

        // Text: Ctrl+C ends lanq as for every command (Program.cs prints nothing and exits 130).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stats(ct: cts.Token));
    }

    [Fact]
    public async Task StatsReadsTheRotatedFileToo()
    {
        Append(Rec("build-01", 60));
        File.Move(Log.FilePath, Log.RotatedPath);
        Append(Rec("micro-01", 120, minutes: 5));

        var (_, json, _) = await Stats(json: true);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal(2, r.GetProperty("files").GetArrayLength());
        Assert.Equal(["build-01", "micro-01"], r.GetProperty("machines").EnumerateArray().Select(m => m.GetProperty("machine").GetString()));
        Assert.Equal((2, 3.0), (r.GetProperty("jobs").GetInt32(), r.GetProperty("wallMinutes").GetDouble()));
        var c = Assert.Single(r.GetProperty("commands").EnumerateArray());
        Assert.Equal((2, 2), (c.GetProperty("runs").GetInt32(), c.GetProperty("jobs").GetInt32()));
    }

    [Fact]
    public async Task NoJobLogIsZeroJobsExit0()
    {
        var (code, text, err) = await Stats();
        Assert.Equal(0, code);
        Assert.Equal("0 jobs, 0 min total wall-clock\nlogs: 0 files, 0.0 MB (kept 30 days)\n", text);
        Assert.Equal("", err);

        (code, var json, err) = await Stats(json: true, keepDays: 0);
        Assert.Equal(0, code);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal(("0 jobs", 0, 0), (r.GetProperty("message").GetString(), r.GetProperty("jobs").GetInt32(), r.GetProperty("files").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("oldestUtc").ValueKind);
        Assert.Equal(0, r.GetProperty("logs").GetProperty("keepDays").GetInt32());
        Assert.Equal("", err);
    }

    [Theory]
    [InlineData("stats", "--json", "--bogus")]
    [InlineData("stats", "--json", "--needs", "node")]
    [InlineData("stats", "--json", "--target-minutes", "0")]
    [InlineData("stats", "--json", "--target-minutes", "1441")]
    [InlineData("stats", "--json", "--shards", "auto")]
    [InlineData("stats", "--json", "--", "x")]
    public async Task StatsBadArgumentPrintsOneObject(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(args, o, e, CancellationToken.None));
        var r = OneObject(o.ToString(), "Rejected", 2);
        Assert.Equal((0, 0), (r.GetProperty("jobs").GetInt32(), r.GetProperty("machines").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("logs").GetProperty("keepDays").ValueKind); // the config was never read
        Assert.Equal("fix_command", Assert.Single(r.GetProperty("nextSteps").EnumerateArray()).GetProperty("action").GetString());
        Assert.Contains("Next: fix the command line: ", e.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Section L: lanq stats reports how many job logs logs/ holds and their size; only lanq's own logs count.</summary>
    [Fact]
    public async Task StatsReportsLogsSize()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_home, "logs")).FullName;
        File.WriteAllBytes(Path.Combine(logs, "20261101-100000-00000a.log"), new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(logs, "20261101-100000-00000b.log"), new byte[512 * 1024]);
        File.WriteAllText(Path.Combine(logs, "notes.log"), "not a job log");
        File.WriteAllText(Path.Combine(logs, LogPrune.MarkerName), "");

        var (_, text, _) = await Stats();
        Assert.EndsWith("logs: 2 files, 1.5 MB (kept 30 days)\n", text, StringComparison.Ordinal);
        (_, text, _) = await Stats(keepDays: 0);
        Assert.EndsWith("logs: 2 files, 1.5 MB (kept for ever: policy.keep_logs_days is 0)\n", text, StringComparison.Ordinal);

        var (_, json, _) = await Stats(json: true, keepDays: 7);
        var l = OneObject(json, "Ok", 0).GetProperty("logs");
        Assert.Equal((2, 1.5, 7), (l.GetProperty("files").GetInt32(), l.GetProperty("megabytes").GetDouble(), l.GetProperty("keepDays").GetInt32()));
    }

    [Fact]
    public async Task TextListsTheNewestTwentyCommandsAndJsonListsThemAll()
    {
        Append([.. Enumerable.Range(1, 23).Select(i => Rec("build-01", 10, minutes: i, key: "npm test --suite=s" + i.ToString("00", CultureInfo.InvariantCulture)))]);
        var (_, text, _) = await Stats();
        Assert.Contains("  npm test --suite=s23\n", text, StringComparison.Ordinal);
        Assert.Contains("  npm test --suite=s04\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--suite=s03", text, StringComparison.Ordinal);
        Assert.Contains("  and 3 more (lanq stats --json lists up to 500)\n", text, StringComparison.Ordinal);

        var (_, json, _) = await Stats(json: true);
        var r = OneObject(json, "Ok", 0);
        Assert.Equal(23, r.GetProperty("commands").GetArrayLength());
        Assert.False(r.TryGetProperty("commandsMore", out _));
    }

    [Fact]
    public async Task NamesAndCommandsFromTheLogAreSanitised()
    {
        Append(Rec("evil\u001b[2J", 10, key: "echo '\u001b]0;pwned\u0007'"));
        var (_, text, _) = await Stats();
        var (_, json, _) = await Stats(json: true);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain("\\u001b", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0007", json, StringComparison.Ordinal);
    }

    /// <summary>stats' timing block is the estimate --target-minutes uses: the same runs, work, overhead and (on a free fleet) wanted N.</summary>
    [Fact]
    public async Task StatsTimingMatchesShardTimingEstimate()
    {
        const string Key = ShardTimingTests.Key;
        var records = new List<JobRecord>();
        var id = 0;
        JobRecord Job(string machine, string key, double run, double wall, double daysAgo, string? runId = null, int? shard = null, int? shards = null) =>
            new($"20261101-100000-{++id:x6}", machine, key, Now.AddDays(-daysAgo), wall, 0, "Completed")
            {
                V = 2,
                Key = key,
                Dir = ".",
                RunId = runId,
                Shard = shard,
                Shards = shards,
                RunSeconds = run,
            };

        // Two 4-shard runs and one 2-shard run of the jest template; micro-01 is measured twice as slow on another command.
        for (var i = 1; i <= 4; i++)
        {
            records.Add(Job(i % 2 == 0 ? "micro-01" : "build-01", Key, i % 2 == 0 ? 300 : 150, i % 2 == 0 ? 340 : 190, 1, "a", i, 4));
            records.Add(Job("build-01", Key, 160, 200, 2, "b", i, 4));
        }

        records.Add(Job("build-01", Key, 320, 350, 3, "c", 1, 2));
        records.Add(Job("build-01", Key, 300, 330, 3, "c", 2, 2));
        for (var i = 0; i < 7; i++)
        {
            records.Add(Job("build-01", "npm test", 100, 110, 4 + i));
        }

        for (var i = 0; i < 6; i++)
        {
            records.Add(Job("micro-01", "npm test", 200, 210, 4 + i));
        }

        Append([.. records]);
        var (_, json, _) = await Stats(json: true, options: ["--target-minutes", "5"]);
        var t = OneObject(json, "Ok", 0).GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("command").GetString() == Key).GetProperty("timing");

        // stats probes nothing, so the pick's fleet reports no memory (no size class): each factor is the measured one or 1, as in stats.
        var history = JobHistory.From(records, Key, ".", Now);
        var one = ShardTimingTests.Fleet(("build-01", 32, 0.1, null, null));
        var pick = ShardTiming.Pick(history, 5, one.Free, one.Assessed, ShardInfo.MaxShards);
        Assert.Equal(
            (pick.RunsUsed, pick.WorkMinutes, pick.OverheadSeconds, pick.Wanted),
            (t.GetProperty("runsUsed").GetInt32(), (double?)t.GetProperty("workMinutes").GetDouble(), (double?)t.GetProperty("overheadSeconds").GetDouble(),
                (int?)t.GetProperty("suggestedShards").GetInt32()));
        Assert.Equal(3, pick.RunsUsed);
        Assert.Equal(2.0, history.Speeds["micro-01"].Factor);

        // The work and overhead do not depend on the fleet the pick sees; only N follows the slowest dealt machine.
        var two = ShardTimingTests.Fleet(("build-01", 32, 0.1, null, null), ("micro-01", 32, 0.2, null, null));
        var both = ShardTiming.Pick(history, 5, two.Free, two.Assessed, ShardInfo.MaxShards);
        Assert.Equal((pick.WorkMinutes, pick.OverheadSeconds), (both.WorkMinutes, both.OverheadSeconds));

        // The text says the same, with the machine's speed in the table.
        var (_, text, _) = await Stats(options: ["--target-minutes", "5"]);
        Assert.Contains(
            $"    timing: about {JobStats.Duration(pick.WorkMinutes!.Value * 60)} of work + {JobStats.Duration(pick.OverheadSeconds!.Value)} per shard (from 3 runs); "
            + $"--target-minutes 5: {pick.Wanted} shards on a typical machine\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("  x2.00 (8 jobs)\n", text, StringComparison.Ordinal);
    }
}
