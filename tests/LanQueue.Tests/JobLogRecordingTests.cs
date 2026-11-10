using System.Security.Cryptography;
using System.Text;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// What each finished job records in jobs.jsonl from 0.7.0: the command's key and dir (<see cref="JobHistory"/>), a --shards run's shared run
/// id and the shard's index and count, and how long the job's own command ran.
/// </summary>
public sealed class JobLogRecordingTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private static readonly string Yaml = ShardTests.PoolYaml.Replace("allowed_directories: [\".\"]", "allowed_directories: [\".\", \"tests\"]", StringComparison.Ordinal);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-record-" + Guid.NewGuid().ToString("N"));

    public JobLogRecordingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobLog Log => new(Path.Combine(_dir, "state"));

    private JobRunner Runner(ISshClient ssh, TimeProvider? clock = null) => new(
        ConfigLoader.Parse(Yaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"), _ => { },
        delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), clock: clock, bases: new BaseStore(null));

    private JobRequest Jest(string dir = ".") => new(["node"], ["npx", "jest", "--shard={shard}/{shards}"], dir, _dir, TimeSpan.Zero);

    [Fact]
    public void KeyKeepsShardPlaceholdersAndTokenBoundaries()
    {
        Assert.Equal("npx jest '--shard={shard}/{shards}'", JobHistory.Key(["npx", "jest", "--shard={shard}/{shards}"]));
        Assert.Equal("dotnet test --filter Category=Unit", JobHistory.Key(["dotnet", "test", "--filter", "Category=Unit"]));

        // One token with a space and two tokens are different commands.
        Assert.Equal("dotnet test 'a b'", JobHistory.Key(["dotnet", "test", "a b"]));
        Assert.Equal("dotnet test a b", JobHistory.Key(["dotnet", "test", "a", "b"]));

        // POSIX quoting: a single quote is closed, escaped and reopened; an empty token stays visible.
        Assert.Equal("echo 'it'\\''s' ''", JobHistory.Key(["echo", "it's", ""]));
        Assert.Equal("pytest '$HOME' '*.py'", JobHistory.Key(["pytest", "$HOME", "*.py"]));
    }

    [Theory]
    [InlineData(".", ".")]
    [InlineData("./", ".")]
    [InlineData("", ".")]
    [InlineData("tests", "tests")]
    [InlineData("./tests/", "tests")]
    [InlineData("././tests", "tests")]
    [InlineData("tests\\unit\\", "tests/unit")]
    [InlineData(".\\tests", "tests")]
    [InlineData("tests//", "tests")]
    [InlineData("Tests", "Tests")]
    public void KeyNormalisesDir(string dir, string expected) => Assert.Equal(expected, JobHistory.Dir(dir));

    [Fact]
    public void LongKeysAreHashed()
    {
        var exact = new string('a', JobHistory.MaxKeyChars);
        Assert.Equal(exact, JobHistory.Key([exact])); // at the limit: kept whole

        string[] argv = ["dotnet", "test", "--filter", new string('b', 2000)];
        var whole = "dotnet test --filter " + new string('b', 2000);
        var key = JobHistory.Key(argv);
        Assert.Equal(960 + 1 + 16, key.Length);
        Assert.Equal(whole[..960] + "#" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(whole)))[..16], key);
        Assert.Equal(key, JobHistory.Key(argv));

        // The same first 960 characters with a different end is a different key.
        Assert.NotEqual(key, JobHistory.Key(["dotnet", "test", "--filter", new string('b', 1999) + "c"]));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ASingleRunRecordsKeyDirAndRunSeconds()
    {
        var r = await Runner(new PoolSsh()).RunAsync(new JobRequest(["node"], ["npx", "jest", "--ci"], "./tests/", _dir, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.NotNull(r.RunSeconds);

        var rec = Assert.Single(Log.ReadAll());
        Assert.Equal((r.JobId, "npx jest --ci", "Completed"), (rec.Id, rec.Command, rec.Status));
        Assert.Equal(2, rec.V);
        Assert.Equal("npx jest --ci", rec.Key);
        Assert.Equal("tests", rec.Dir);
        Assert.Equal(r.RunSeconds, rec.RunSeconds);
        Assert.True(rec.RunSeconds >= 0 && rec.RunSeconds <= rec.WallSeconds);

        // A single run is no shard and has no run id: those keys are left out of the line.
        Assert.Null(rec.Shard);
        Assert.Null(rec.Shards);
        Assert.Null(rec.RunId);
        var line = Assert.Single(File.ReadAllLines(Log.FilePath));
        Assert.DoesNotContain("\"shard", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\"runId\"", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EveryShardRecordsTheTemplateKeyRunIdAndIndex()
    {
        var runner = Runner(new PoolSsh());
        var r = await runner.RunShardsAsync(Jest("tests"), new ShardOptions(4), CancellationToken.None);
        Assert.Equal("Completed", r.Status);

        var records = Log.ReadAll();
        Assert.Equal(4, records.Count);
        Assert.All(records, rec =>
        {
            Assert.Equal(2, rec.V);
            Assert.Equal("npx jest '--shard={shard}/{shards}'", rec.Key); // the template, not the substituted argv
            Assert.Equal("tests", rec.Dir);
            Assert.Equal(4, rec.Shards);
            Assert.Equal($"npx jest --shard={rec.Shard}/4", rec.Command); // the command still records what ran
            Assert.NotNull(rec.RunSeconds);
        });
        Assert.Equal([1, 2, 3, 4], records.Select(rec => rec.Shard!.Value).Order());
        var runId = Assert.Single(records.Select(rec => rec.RunId).Distinct(StringComparer.Ordinal));
        Assert.Matches("^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$", runId);
        Assert.DoesNotContain(runId, records.Select(rec => rec.Id)); // the run id is no shard's job id

        // The next run of the same command has its own run id and the same key.
        await runner.RunShardsAsync(Jest("tests"), new ShardOptions(2), CancellationToken.None);
        var next = Log.ReadAll().Skip(4).ToList();
        Assert.Equal(2, next.Count);
        Assert.All(next, rec => Assert.Equal(("npx jest '--shard={shard}/{shards}'", 2), (rec.Key, rec.Shards)));
        Assert.NotEqual(runId, Assert.Single(next.Select(rec => rec.RunId).Distinct(StringComparer.Ordinal)));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ARetriedShardRecordsBothAttemptsWithOneRunId()
    {
        var lost = 0;
        var ssh = new PoolSsh
        {
            OnExecute = (e, _) => Task.FromResult(
                e.Shard == 2 && Interlocked.Increment(ref lost) == 1 ? new SshResult(255, "Connection to 10.20.30.12 closed by remote host.") : e.Exit(0)),
        };
        var r = await Runner(ssh).RunShardsAsync(Jest(), new ShardOptions(3, Retries: 1), CancellationToken.None);
        Assert.Equal("Completed", r.Status);

        var records = Log.ReadAll();
        Assert.Equal(4, records.Count);
        Assert.Single(records.Select(rec => rec.RunId).Distinct(StringComparer.Ordinal));
        var second = records.Where(rec => rec.Shard == 2).ToList();
        Assert.Equal(2, second.Count);
        Assert.Equal(["Completed", "InfraError"], second.Select(rec => rec.Status).Order(StringComparer.Ordinal));
        Assert.Equal(2, second.Select(rec => rec.Id).Distinct(StringComparer.Ordinal).Count()); // each attempt is its own job
        Assert.All(second, rec => Assert.Equal((3, "npx jest '--shard={shard}/{shards}'"), (rec.Shards!.Value, rec.Key)));
    }

    /// <summary>runSeconds is the job's own command only: upload, prepare and fetch (here 100 s, 50 s and 30 s on a fake clock) are not in it.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RunSecondsExcludesUploadPrepareAndFetch()
    {
        var clock = new ManualClock();
        var ssh = new ClockedSsh(new PoolSsh(), clock);
        var fetch = new FetchRequest(["out/*.txt"], Path.Combine(_dir, "fetched"), new FetchLimits(10, 1000, 1000));
        var r = await Runner(ssh, clock).RunAsync(new JobRequest(["node"], ["npx", "jest"], ".", _dir, TimeSpan.Zero, Fetch: fetch), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.True(ssh.Uploads > 0 && ssh.Prepares > 0 && ssh.Fetches > 0, "upload, prepare and fetch all ran");
        Assert.Equal(7, r.RunSeconds);
        Assert.Equal(7, Assert.Single(Log.ReadAll()).RunSeconds);
    }

    /// <summary>A job that never started its command (prepare failed) records no runSeconds.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RunSecondsIsLeftOutWhenTheCommandNeverStarted()
    {
        var ssh = new PoolSsh { OnPrepare = _ => new SshResult(1, "fatal: bundle is broken") };
        var r = await Runner(ssh).RunAsync(new JobRequest(["node"], ["npx", "jest"], ".", _dir, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Null(r.RunSeconds);
        var rec = Assert.Single(Log.ReadAll());
        Assert.Null(rec.RunSeconds);
        Assert.Equal("npx jest", rec.Key);
        Assert.DoesNotContain("runSeconds", Assert.Single(File.ReadAllLines(Log.FilePath)), StringComparison.Ordinal);
    }

    /// <summary>
    /// 0.6 bug: a jobs.jsonl held open by another process made the append throw after the job had finished, and the run ended InfraError.
    /// Now the job keeps its result (here Completed, exit 0) and the stats line is skipped with a warning.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ALockedJobLogNeverFailsAFinishedJob()
    {
        var warnings = new List<string>();
        var state = Path.Combine(_dir, "state");
        Directory.CreateDirectory(state);
        var log = new JobLog(state, warnings.Add);
        var ssh = new PoolSsh();
        var runner = new JobRunner(
            ConfigLoader.Parse(Yaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), log, Path.Combine(_dir, "tmp"), _ => { },
            bases: new BaseStore(null));

        JobResult r;
        using (new FileStream(log.FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            r = await runner.RunAsync(new JobRequest(["node"], ["npx", "jest"], ".", _dir, TimeSpan.Zero), CancellationToken.None);
        }

        Assert.Equal((JobStatus.Completed, 0), (r.Status, r.CliExitCode));
        Assert.Contains(warnings, w => w.StartsWith("[lanq] warning: could not record the job in jobs.jsonl", StringComparison.Ordinal));
        Assert.Empty(log.ReadAll());
    }

    /// <summary>A clock that moves only when told: the timestamps JobRunner measures with.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(GetTimestamp());
    }

    /// <summary>Moves the clock as each step "takes" its time: upload 100 s, prepare 50 s, the job 7 s, the fetch 30 s.</summary>
    private sealed class ClockedSsh(PoolSsh inner, ManualClock clock) : ISshClient
    {
        public int Uploads { get; private set; }

        public int Prepares { get; private set; }

        public int Fetches { get; private set; }

        public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            if (remoteCommand.Contains("LANQ_EXIT=", StringComparison.Ordinal))
            {
                clock.Advance(TimeSpan.FromSeconds(7));
            }
            else if (remoteCommand.Contains("bundle verify", StringComparison.Ordinal))
            {
                Prepares++;
                clock.Advance(TimeSpan.FromSeconds(50));
            }

            return inner.RunAsync(machine, remoteCommand, onLine, timeout, ct);
        }

        public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct)
        {
            Uploads++;
            clock.Advance(TimeSpan.FromSeconds(100));
            return inner.UploadAsync(machine, localPath, remotePath, timeout, ct);
        }

        public Task<SshResult> RunSplitAsync(
            Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct)
        {
            Fetches++;
            clock.Advance(TimeSpan.FromSeconds(30));
            return inner.RunSplitAsync(machine, remoteCommand, input, onStdout, onStderr, timeout, ct);
        }
    }
}
