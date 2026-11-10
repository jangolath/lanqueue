using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run: logPath in the JSON, --log-tail N, JobLog.Tail and the "[lanq] log: path" line before the status line.</summary>
public sealed class LogOutputTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-log-" + Guid.NewGuid().ToString("N"));

    public LogOutputTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobLog Log => new(Path.Combine(_dir, "state"));

    private async Task<(int Code, string Stdout, string Stderr)> Run(PoolSsh ssh, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", .. args]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System),
            Log, _dir, _dir, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    private static JsonElement Json(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout); // exactly one JSON value
        return doc.RootElement.Clone();
    }

    private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    /// <summary>A job that prints "line 1" to "line 10", then the wrapper's exit marker.</summary>
    private static PoolSsh TenLines() => new()
    {
        OnExecute = (e, _) => Task.FromResult(new SshResult(0, string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + $"\nLANQ_EXIT={e.JobId}:0")),
    };

    [Fact(Timeout = TestTimeout)]
    public async Task RunJsonHasTheJobsLogPath()
    {
        var (code, stdout, _) = await Run(new PoolSsh(), "--json", "--needs", "node", "--", "npx", "jest");

        Assert.Equal(0, code);
        var r = Json(stdout);
        var jobId = r.GetProperty("jobId").GetString()!;
        var path = r.GetProperty("logPath").GetString()!;
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(Path.GetFullPath(Log.LogPathFor(jobId)), path);
        Assert.Contains("output of " + jobId, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(r.TryGetProperty("logTail", out _)); // only with --log-tail
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LogPathIsNullWhenNoJobStarted()
    {
        var refused = Json((await Run(new PoolSsh(), "--json", "--needs", "node", "--", "rm", "-rf", "/")).Stdout);
        Assert.Equal("Rejected", refused.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("logPath").ValueKind);

        var none = Json((await Run(new PoolSsh(), "--json", "--needs", "gpu", "--", "npx", "jest")).Stdout);
        Assert.Equal("NoCapacity", none.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("logPath").ValueKind);

        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--json", "--bogus", "--", "npx", "jest"], o, new StringWriter(), CancellationToken.None));
        Assert.Equal(JsonValueKind.Null, Json(o.ToString()).GetProperty("logPath").ValueKind);
        Assert.False(Directory.Exists(Path.Combine(_dir, "state", "logs")));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ACancelledRunStillReportsTheLogOfItsStartedJob()
    {
        // Ctrl+C while the job runs: the log exists, so logPath (and --log-tail) point at it, as a sharded run's cancelled shards do.
        using var cts = new CancellationTokenSource();
        var ssh = new PoolSsh
        {
            OnExecute = async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new SshResult(0, "");
            },
        };
        var o = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--log-tail", "5", "--needs", "node", "--", "npx", "jest"]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), Log, _dir, _dir, o, new StringWriter(), cts.Token);

        Assert.Equal(130, code);
        var r = Json(o.ToString());
        Assert.Equal("Cancelled", r.GetProperty("status").GetString());
        var jobId = Assert.Single(ssh.Executes).JobId;
        Assert.Equal(Path.GetFullPath(Log.LogPathFor(jobId)), r.GetProperty("logPath").GetString());
        Assert.True(File.Exists(r.GetProperty("logPath").GetString()));
        Assert.Equal(JsonValueKind.Array, r.GetProperty("logTail").ValueKind);
        Assert.Equal("none", r.GetProperty("nextSteps")[0].GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("--log-tail", "0", "--json")]
    [InlineData("--log-tail", "201", "--json")]
    [InlineData("--log-tail", "abc", "--json")]
    [InlineData("--log-tail", "-5", "--json")]
    [InlineData("--log-tail", "5")]
    public void LogTailNeedsJsonAndARange(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", .. args, "--", "dotnet", "test"]));
        Assert.Equal(1, ParsedArgs.Parse(["run", "--json", "--log-tail", "1", "--", "dotnet", "test"]).LogTail);
        Assert.Equal(200, ParsedArgs.Parse(["run", "--log-tail", "200", "--json", "--", "dotnet", "test"]).LogTail);
        Assert.Null(ParsedArgs.Parse(["run", "--json", "--", "dotnet", "test"]).LogTail);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["health", "--json", "--log-tail", "5"]));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LogTailIsTheLastNLines()
    {
        var (code, stdout, _) = await Run(TenLines(), "--json", "--log-tail", "3", "--needs", "node", "--", "npx", "jest");

        Assert.Equal(0, code);
        var r = Json(stdout);
        var tail = r.GetProperty("logTail").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Equal(["line 8", "line 9", "line 10"], tail);
        Assert.Equal(File.ReadAllLines(r.GetProperty("logPath").GetString()!)[^3..], tail);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LogTailIsPresentAndEmptyWhenNothingRan()
    {
        var refused = Json((await Run(new PoolSsh(), "--json", "--log-tail", "5", "--needs", "node", "--", "rm", "-rf", "/")).Stdout);
        Assert.Equal(0, refused.GetProperty("logTail").GetArrayLength());

        var shardsRefused = Json((await Run(new PoolSsh(), "--json", "--log-tail", "5", "--shards", "2", "--needs", "gpu", "--", "npx", "jest")).Stdout);
        Assert.All(shardsRefused.GetProperty("shards").EnumerateArray(), s =>
        {
            Assert.Equal(JsonValueKind.Null, s.GetProperty("logPath").ValueKind);
            Assert.Equal(0, s.GetProperty("logTail").GetArrayLength());
        });

        // A refused command line still prints one object, with the empty tail it asked for.
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--json", "--log-tail", "5", "--bogus", "--", "npx", "jest"], o, new StringWriter(), CancellationToken.None));
        Assert.Equal(0, Json(o.ToString()).GetProperty("logTail").GetArrayLength());
    }

    [Fact]
    public void TailReadsOnlyTheEndOfALargeLog()
    {
        // 10 MB of 3000-character lines: 200 of them would be 600 KB, more than the 256 KB Tail ever reads.
        var path = Path.Combine(_dir, "big.log");
        const int Count = 3500;
        static string Line(int i) => i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 2994);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < Count; i++)
            {
                w.Write(Line(i) + "\n");
            }
        }

        Assert.True(new FileInfo(path).Length >= 10_000_000);
        Assert.Equal(Enumerable.Range(Count - 5, 5).Select(Line), JobLog.Tail(path, 5));

        var most = JobLog.Tail(path, 200);
        Assert.InRange(most.Count, 1, (JobLog.TailMaxBytes / 3001) + 1); // bounded by the bytes read, never a partial first line
        Assert.Equal(Enumerable.Range(Count - most.Count, most.Count).Select(Line), most);
    }

    [Fact]
    public void TailLinesAreSanitisedAndCapped()
    {
        var path = Path.Combine(_dir, "job.log");
        File.WriteAllText(path, "first\r\nok\r\n\u001b[2Jbad‮name\u0007\r\n" + new string('y', 10_000) + "\r\nlast", new UTF8Encoding(false));

        var tail = JobLog.Tail(path, 4);

        Assert.Equal(["ok", "?[2Jbad?name?", new string('y', JobLog.TailMaxLineChars), "last"], tail);
        Assert.Empty(JobLog.Tail(Path.Combine(_dir, "missing.log"), 5));
        File.WriteAllText(Path.Combine(_dir, "empty.log"), "");
        Assert.Empty(JobLog.Tail(Path.Combine(_dir, "empty.log"), 5));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EachShardHasItsOwnLogPathAndTail()
    {
        var (code, stdout, _) = await Run(new PoolSsh(), "--json", "--log-tail", "2", "--shards", "3", "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");

        Assert.Equal(0, code);
        var r = Json(stdout);
        Assert.False(r.TryGetProperty("logPath", out _)); // per shard only
        var shards = r.GetProperty("shards").EnumerateArray().ToList();
        Assert.Equal(3, shards.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in shards)
        {
            var jobId = s.GetProperty("jobId").GetString()!;
            var path = s.GetProperty("logPath").GetString()!;
            Assert.Equal(Path.GetFullPath(Log.LogPathFor(jobId)), path);
            Assert.True(paths.Add(path));
            Assert.Contains("output of " + jobId, s.GetProperty("logTail").EnumerateArray().Select(l => l.GetString()));
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TextRunPrintsTheLogPathBeforeTheStatusLine()
    {
        var (code, stdout, stderr) = await Run(TenLines(), "--needs", "node", "--", "npx", "jest");

        Assert.Equal(0, code);
        Assert.Contains("line 10", stdout, StringComparison.Ordinal);
        var lines = Lines(stderr);
        Assert.StartsWith("[lanq] Completed: ", lines[^1], StringComparison.Ordinal); // the status line stays last
        Assert.StartsWith("[lanq] log: ", lines[^2], StringComparison.Ordinal);
        var path = lines[^2]["[lanq] log: ".Length..];
        Assert.Contains("line 10", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("[lanq] log: ", stdout, StringComparison.Ordinal);

        // --json prints no such line (logPath carries it).
        var json = await Run(TenLines(), "--json", "--needs", "node", "--", "npx", "jest");
        Assert.DoesNotContain("[lanq] log: ", json.Stderr, StringComparison.Ordinal);

        // --shards: only the shards that did not complete, still before the status line.
        var failing = new PoolSsh { OnExecute = (e, _) => Task.FromResult(e.Exit(e.Shard == 2 ? 1 : 0)) };
        var (shardCode, _, shardErr) = await Run(failing, "--shards", "2", "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");
        Assert.Equal(1, shardCode);
        var shardLines = Lines(shardErr);
        Assert.StartsWith("[lanq] Failed: ", shardLines[^1], StringComparison.Ordinal);
        var logLine = Assert.Single(shardLines, l => l.StartsWith("[lanq] log: ", StringComparison.Ordinal));
        Assert.Equal(shardLines[^2], logLine);
        Assert.EndsWith(" (shard 2/2)", logLine, StringComparison.Ordinal);
        Assert.Single(failing.Executes, e => e.Shard == 2 && logLine.Contains(e.JobId + ".log", StringComparison.Ordinal));
    }
}
