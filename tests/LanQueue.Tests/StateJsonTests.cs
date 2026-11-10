using System.Diagnostics;
using LanQueue.Jobs;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The local state files written by 0.4 (reflection JSON) still read, and still write the same shape, with source-generated JSON; the 0.7
/// job log: new optional fields, a tolerant read, an append that never throws, and rotation to jobs.1.jsonl.
/// </summary>
public sealed class StateJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void AJobLogWrittenBy04StillReads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "jobs.jsonl"),
            "{\"id\":\"20260101-000000-000001\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\","
            + "\"wallSeconds\":12.5,\"exitCode\":1,\"status\":\"Failed\"}\n\n"
            + "{\"Id\":\"20260101-000000-000002\",\"Machine\":null,\"Command\":\"dotnet build\",\"StartedUtc\":\"2026-01-01T01:00:00Z\","
            + "\"WallSeconds\":\"3\",\"ExitCode\":0,\"Status\":\"Completed\"}\n");

        var all = new JobLog(_dir).ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(new JobRecord("20260101-000000-000001", "build-01", "dotnet test", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 12.5, 1, "Failed"), all[0]);
        Assert.Null(all[1].Machine);
        Assert.Equal(3, all[1].WallSeconds); // web defaults: case-insensitive names, numbers in strings
    }

    [Fact]
    public void AJobLogLineIsCamelCase()
    {
        var log = new JobLog(_dir);
        log.Append(new JobRecord("j1", null, "dotnet test", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1.5, 0, "Completed"));
        Assert.Equal(
            "{\"id\":\"j1\",\"machine\":null,\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\",\"wallSeconds\":1.5,\"exitCode\":0,\"status\":\"Completed\"}\n",
            File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void BasesWrittenBy04StillRead()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bases.json");
        var sha = new string('a', 40);
        File.WriteAllText(path, "{\"build-01\":{\"0123456789abcdef\":\"" + sha + "\"}}");
        var store = new BaseStore(path);
        Assert.Equal(sha, store.Get("build-01", "0123456789abcdef"));

        store.Set("Build-02", "R", new string('b', 40));
        Assert.Equal("{\"build-01\":{\"0123456789abcdef\":\"" + sha + "\"},\"Build-02\":{\"R\":\"" + new string('b', 40) + "\"}}", File.ReadAllText(path));
    }

    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Line04 =
        "{\"id\":\"20260101-000000-000001\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\","
        + "\"wallSeconds\":12.5,\"exitCode\":1,\"status\":\"Failed\"}";

    private const string Line07 =
        "{\"id\":\"20260101-000000-000002\",\"machine\":\"build-01\",\"command\":\"npx jest --shard=2/4\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\","
        + "\"wallSeconds\":40,\"exitCode\":0,\"status\":\"Completed\",\"v\":2,\"key\":\"npx jest \\u0027--shard={shard}/{shards}\\u0027\",\"dir\":\"tests\","
        + "\"shard\":2,\"shards\":4,\"runId\":\"20260101-000000-aaaaaa\",\"runSeconds\":31.25}";

    private (JobLog Log, List<string> Warnings) Make()
    {
        var warnings = new List<string>();
        return (new JobLog(_dir, warnings.Add), warnings);
    }

    private static JobRecord Rec(string id, string command = "dotnet test") => new(id, "build-01", command, Jan1, 1.5, 0, "Completed");

    /// <summary>A file holding 0.4-0.6 lines and 0.7 lines reads every one: old lines get null for the new fields, new lines carry them.</summary>
    [Fact]
    public void A04LineAndA07LineReadSideBySide()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "jobs.jsonl"), Line04 + "\n" + Line07 + "\n");

        var read = new JobLog(_dir).Read(CancellationToken.None);
        Assert.Equal(0, read.Skipped);
        Assert.Equal([Path.Combine(_dir, "jobs.jsonl")], read.Files);
        Assert.Equal(2, read.Records.Count);

        var old = read.Records[0];
        Assert.Equal(("20260101-000000-000001", "Failed"), (old.Id, old.Status));
        Assert.Null(old.V);
        Assert.Null(old.Key);
        Assert.Null(old.Dir);
        Assert.Null(old.Shard);
        Assert.Null(old.Shards);
        Assert.Null(old.RunId);
        Assert.Null(old.RunSeconds);

        var now = read.Records[1];
        Assert.Equal(2, now.V);
        Assert.Equal("npx jest '--shard={shard}/{shards}'", now.Key);
        Assert.Equal("tests", now.Dir);
        Assert.Equal((2, 4), (now.Shard, now.Shards));
        Assert.Equal("20260101-000000-aaaaaa", now.RunId);
        Assert.Equal(31.25, now.RunSeconds);

        // Written back, a 0.7 record gives the same line (the fields in the same order; the default encoder writes ' as ').
        Assert.Equal(Line07, System.Text.Json.JsonSerializer.Serialize(now, StateJson.Default.JobRecord));
    }

    /// <summary>Every new field is left out when null, so an old-shape record writes exactly the old line; a set field is written.</summary>
    [Fact]
    public void NewFieldsAreLeftOutWhenNull()
    {
        var (log, warnings) = Make();
        log.Append(Rec("j1"));
        log.Append(Rec("j2") with { V = 2, Key = "dotnet test", Dir = "." });

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(
            "{\"id\":\"j1\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\",\"wallSeconds\":1.5,\"exitCode\":0,\"status\":\"Completed\"}",
            lines[0]);
        Assert.Equal(
            "{\"id\":\"j2\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\",\"wallSeconds\":1.5,\"exitCode\":0,\"status\":\"Completed\","
            + "\"v\":2,\"key\":\"dotnet test\",\"dir\":\".\"}",
            lines[1]);
        foreach (var absent in new[] { "shard", "runId", "runSeconds" })
        {
            Assert.DoesNotContain("\"" + absent + "\"", lines[1], StringComparison.Ordinal);
        }

        Assert.Empty(warnings);
    }

    /// <summary>
    /// 0.6 bug: one torn line (a crash in the middle of a write) made every read throw, so lanq stats crashed. Now it is skipped and counted,
    /// like any line that is not a job record, and every other line still reads.
    /// </summary>
    [Fact]
    public void AMalformedLineIsSkipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "jobs.jsonl"),
            Line04 + "\n" + Line04[..40] + "\n" + "not json\n" + "{}\n" + "null\n" + "[1,2]\n" + "\n" + Line07 + "\n" + Line07[..60]);

        var log = new JobLog(_dir);
        var read = log.Read(CancellationToken.None);
        Assert.Equal(["20260101-000000-000001", "20260101-000000-000002"], read.Records.Select(r => r.Id));
        Assert.Equal(6, read.Skipped); // the blank line is not counted
        Assert.Equal(read.Records, log.ReadAll());
    }

    /// <summary>
    /// 0.6 bug: an append to a jobs.jsonl another process held open threw, and the finished job became an InfraError. Now the append is
    /// retried briefly and then given up with a warning; it never throws. Once the file is free again, appends work.
    /// </summary>
    [Fact]
    public void AppendNeverThrowsWhenTheFileIsLocked()
    {
        var (log, warnings) = Make();
        log.Append(Rec("before"));
        var clock = Stopwatch.StartNew();
        using (new FileStream(log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            log.Append(Rec("locked"));
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the retries are bounded");
        var warning = Assert.Single(warnings);
        Assert.StartsWith("[lanq] warning: could not record the job in jobs.jsonl: ", warning, StringComparison.Ordinal);

        log.Append(Rec("after"));
        Assert.Equal(["before", "after"], log.ReadAll().Select(r => r.Id));
        Assert.Single(warnings);
    }

    /// <summary>At 4 MiB jobs.jsonl moves to jobs.1.jsonl before the next append; a read returns both files' records, the older file first.</summary>
    [Fact]
    public void RotatesAtFourMiBAndReadsBothFiles()
    {
        var (log, warnings) = Make();
        var big = new string('x', 4000);
        var n = 0;
        while (new FileInfo(log.FilePath) is var f && (!f.Exists || f.Length < JobLog.RotateBytes))
        {
            log.Append(Rec("old-" + n++, big));
        }

        Assert.False(File.Exists(log.RotatedPath)); // only an append that finds the file full rotates it
        var full = File.ReadAllBytes(log.FilePath);

        log.Append(Rec("new-0"));
        Assert.Equal(full, File.ReadAllBytes(log.RotatedPath));
        Assert.StartsWith("{\"id\":\"new-0\",", Assert.Single(File.ReadAllLines(log.FilePath)), StringComparison.Ordinal);

        log.Append(Rec("new-1"));
        var read = log.Read(CancellationToken.None);
        Assert.Equal([log.RotatedPath, log.FilePath], read.Files);
        Assert.Equal(n + 2, read.Records.Count);
        Assert.Equal("old-0", read.Records[0].Id);
        Assert.Equal(["new-0", "new-1"], read.Records.TakeLast(2).Select(r => r.Id));
        Assert.Equal(0, read.Skipped);
        Assert.Empty(warnings);
    }

    /// <summary>When the full file cannot be moved (here jobs.1.jsonl is a folder), rotation is skipped and the job is still recorded.</summary>
    [Fact]
    public void RotationThatCannotRenameIsSkipped()
    {
        var (log, warnings) = Make();
        Directory.CreateDirectory(log.RotatedPath);
        File.WriteAllText(log.FilePath, string.Concat(Enumerable.Repeat(Line04 + "\n", (int)(JobLog.RotateBytes / Line04.Length) + 1)));
        var lines = File.ReadAllLines(log.FilePath).Length;

        log.Append(Rec("still-recorded"));

        Assert.True(Directory.Exists(log.RotatedPath));
        Assert.Empty(warnings);
        var read = log.Read(CancellationToken.None);
        Assert.Equal([log.FilePath], read.Files); // a folder named jobs.1.jsonl is not read
        Assert.Equal(lines + 1, read.Records.Count);
        Assert.Equal("still-recorded", read.Records[^1].Id);
    }

    /// <summary>
    /// Two lanq processes appending at the same moment (two logs on one folder with no in-process lock between them, as two processes have
    /// none) keep every line whole: none is lost, torn or written over.
    /// </summary>
    [Fact]
    public async Task TwoProcessesStyleConcurrentAppendsKeepEveryLine()
    {
        const int PerProcess = 100;
        var (a, warningsA) = Make();
        var (b, warningsB) = Make();
        using var start = new ManualResetEventSlim();
        Task Process(JobLog log, string name) => Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var i = 0; i < PerProcess; i++)
                {
                    log.Append(Rec(name + "-" + i, "npx jest " + new string('y', 200)), processGate: false);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var both = Task.WhenAll(Process(a, "a"), Process(b, "b"));
        start.Set();
        await both;

        Assert.Empty(warningsA);
        Assert.Empty(warningsB);
        var read = a.Read(CancellationToken.None);
        Assert.Equal(0, read.Skipped);
        Assert.Equal(2 * PerProcess, read.Records.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
