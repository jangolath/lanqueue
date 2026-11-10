using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Pruning the state folder's logs/ after a run (policy.keep_logs_days, default 30, plus a 5,000-file cap): only lanq's own job logs, never
/// a link, never a log touched in the last hour, at most once a day, and never at the cost of a run's result.
/// </summary>
public sealed class LogPruneTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-prune-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _warnings = [];

    public LogPruneTests() => Directory.CreateDirectory(Logs);

    public void Dispose() => TestTree.Delete(_dir);

    private string State => Path.Combine(_dir, "state");

    private string Logs => Path.Combine(State, "logs");

    private string Marker => Path.Combine(Logs, LogPrune.MarkerName);

    private static int _seq;

    /// <summary>A job id as JobRunner makes them: yyyyMMdd-HHmmss-6 hex digits.</summary>
    private static string NewJobId() => "20261001-120000-" + Interlocked.Increment(ref _seq).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes a file in logs/ last modified <paramref name="age"/> before <see cref="Now"/>.</summary>
    private string Put(string name, TimeSpan age, string folder = "")
    {
        var path = Path.Combine(folder.Length == 0 ? Logs : folder, name);
        File.WriteAllText(path, "log of " + name);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    private string PutLog(TimeSpan age) => Put(NewJobId() + ".log", age);

    private LogPruneResult Prune(int keepDays = LogPrune.DefaultKeepDays, int maxFiles = LogPrune.MaxFiles, TimeSpan? budget = null) =>
        LogPrune.Run(Logs, keepDays, Now, _warnings.Add, maxFiles, budget);

    [Fact]
    public void PrunesOnlyOldLanqLogs()
    {
        var old = PutLog(TimeSpan.FromDays(31));
        var older = PutLog(TimeSpan.FromDays(400));
        var young = PutLog(TimeSpan.FromDays(29));
        var day = PutLog(TimeSpan.FromDays(1));

        var r = Prune();

        Assert.Equal(new LogPruneResult(true, 2, true), r);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(day));
        Assert.Empty(_warnings);

        // keep_logs_days 7 on another day keeps a week.
        File.SetLastWriteTimeUtc(Marker, Now - TimeSpan.FromDays(2));
        Assert.Equal(1, LogPrune.Run(Logs, 7, Now, _warnings.Add).Deleted);
        Assert.False(File.Exists(young));
        Assert.True(File.Exists(day));
    }

    /// <summary>Beyond 5,000 lanq logs the oldest go, even when all are younger than keep_logs_days.</summary>
    [Fact]
    public void KeepsTheNewestFiveThousand()
    {
        const int Extra = 7;
        var paths = new List<string>();
        for (var i = 0; i < LogPrune.MaxFiles + Extra; i++)
        {
            paths.Add(PutLog(TimeSpan.FromDays(20) - TimeSpan.FromMinutes(i))); // paths[0] is the oldest
        }

        var r = Prune(budget: TimeSpan.FromMinutes(1)); // a slow test machine must not cut it short

        Assert.Equal(new LogPruneResult(true, Extra, true), r);
        Assert.All(paths.Take(Extra), p => Assert.False(File.Exists(p)));
        Assert.All(paths.Skip(Extra), p => Assert.True(File.Exists(p)));
        Assert.Equal(LogPrune.MaxFiles, Directory.GetFiles(Logs, "*.log").Length);
    }

    /// <summary>
    /// Only regular files directly in logs/ whose whole name is a lanq job id plus .log: other names, other cases, subfolders, other files in the
    /// state folder and links (to an old log, or a logs/ that is itself a link) are left alone, and so is what a link points at.
    /// </summary>
    [Fact]
    public void NeverTouchesOtherFilesOrLinks()
    {
        var old = TimeSpan.FromDays(90);
        var lanq = PutLog(old);
        string[] others =
        [
            Put("notes.txt", old),
            Put("20261001-120000-abcdef.log.bak", old),
            Put("20261001-120000-ABCDEF.log", old), // job ids are lowercase hex
            Put("20261001-120000-abcdefa.log", old),
            Put("2026101-120000-abcdef.log", old),
            Put("x20261001-120000-abcdef.log", old),
            Put("20261001-120000-abcdeg.log", old),
            Put("20261001-120000-abcdef.LOG", old),
            Put("build.log", old),
        ];
        Directory.CreateDirectory(Path.Combine(Logs, "sub"));
        var nested = Put("20261001-120000-aaaaaa.log", old, Path.Combine(Logs, "sub"));
        var jobs = Put("jobs.jsonl", old, State);
        var stateLog = Put("20261001-120000-bbbbbb.log", old, State);

        // A link in logs/ with a lanq log's name, pointing at an old lanq-named file elsewhere.
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        var target = Put("20261001-120000-cccccc.log", old, outside);
        var link = Path.Combine(Logs, "20261001-120000-dddddd.log");
        var linked = TryLink(() => File.CreateSymbolicLink(link, target));

        Assert.Equal(1, Prune().Deleted);
        Assert.False(File.Exists(lanq));
        Assert.All(others, p => Assert.True(File.Exists(p), p));
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(jobs));
        Assert.True(File.Exists(stateLog));
        Assert.True(File.Exists(target));
        if (linked)
        {
            Assert.NotNull(new FileInfo(link).LinkTarget);
        }

        // A logs/ folder that is itself a link is not pruned at all.
        var realLogs = Path.Combine(_dir, "real-logs");
        Directory.CreateDirectory(realLogs);
        var behind = Put("20261001-120000-eeeeee.log", old, realLogs);
        var linkedLogs = Path.Combine(_dir, "linked-state", "logs");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedLogs)!);
        if (TryLink(() => Directory.CreateSymbolicLink(linkedLogs, realLogs)))
        {
            Assert.False(LogPrune.Run(linkedLogs, 30, Now, _warnings.Add).Ran);
            Assert.True(File.Exists(behind));
        }

        // A marker that is a link is never written through.
        File.Delete(Marker);
        var victim = Put("victim.txt", TimeSpan.FromDays(400), outside);
        if (TryLink(() => File.CreateSymbolicLink(Marker, victim)))
        {
            var another = PutLog(old);
            Assert.False(Prune().Ran);
            Assert.True(File.Exists(another));
            Assert.Equal("log of victim.txt", File.ReadAllText(victim));
            Assert.Equal(Now - TimeSpan.FromDays(400), File.GetLastWriteTimeUtc(victim));
            Assert.Contains(_warnings, w => w.Contains("is a link", StringComparison.Ordinal));
        }
    }

    /// <summary>Making a link needs a privilege some Windows test machines lack: the link parts of a test run only where it can be made.</summary>
    private static bool TryLink(Action make)
    {
        try
        {
            make();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A log modified within the last hour stays, even past the count cap and even with a clock that puts it in the future.</summary>
    [Fact]
    public void NeverDeletesAFileModifiedInTheLastHour()
    {
        var recent = PutLog(TimeSpan.FromMinutes(59));
        var future = PutLog(TimeSpan.FromDays(-3));
        var older = PutLog(TimeSpan.FromMinutes(61));

        var r = Prune(keepDays: 1, maxFiles: 1);

        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(future));
        Assert.False(File.Exists(older)); // over the cap of 1 and older than an hour
        Assert.Equal(1, r.Deleted);
    }

    [Fact]
    public void RunsAtMostOncePerDay()
    {
        PutLog(TimeSpan.FromDays(40));
        Assert.True(Prune().Ran);
        Assert.Equal(Now, File.GetLastWriteTimeUtc(Marker));

        var later = PutLog(TimeSpan.FromDays(40));
        Assert.False(Prune().Ran); // the same moment
        Assert.False(LogPrune.Run(Logs, 30, Now + TimeSpan.FromHours(23), _warnings.Add).Ran);
        Assert.True(File.Exists(later));

        var next = LogPrune.Run(Logs, 30, Now + TimeSpan.FromHours(25), _warnings.Add);
        Assert.Equal((true, 1), (next.Ran, next.Deleted));
        Assert.False(File.Exists(later));

        // A marker in the future (a clock that was wrong) does not stop pruning for ever.
        File.SetLastWriteTimeUtc(Marker, Now + TimeSpan.FromDays(300));
        Assert.True(LogPrune.Run(Logs, 30, Now + TimeSpan.FromDays(2), _warnings.Add).Ran);
    }

    /// <summary>A prune cut short by its time budget hands the day back, so the next run carries on.</summary>
    [Fact]
    public void AnInterruptedPruneCarriesOnNextTime()
    {
        var log = PutLog(TimeSpan.FromDays(40));
        var r = LogPrune.Run(Logs, 30, Now, _warnings.Add, budget: TimeSpan.Zero - TimeSpan.FromTicks(1));
        Assert.Equal((true, false, 0), (r.Ran, r.Finished, r.Deleted));
        Assert.True(File.Exists(log));

        Assert.Equal(new LogPruneResult(true, 1, true), Prune());
        Assert.False(File.Exists(log));
    }

    [Fact]
    public void KeepLogsDaysZeroNeverPrunes()
    {
        var ancient = PutLog(TimeSpan.FromDays(3000));
        for (var i = 0; i < 3; i++)
        {
            PutLog(TimeSpan.FromDays(100));
        }

        Assert.False(Prune(keepDays: 0, maxFiles: 1).Ran);
        Assert.True(File.Exists(ancient));
        Assert.Equal(4, Directory.GetFiles(Logs, "*.log").Length);
        Assert.False(File.Exists(Marker));

        // ... and through the config.
        var config = ConfigLoader.Parse(T.Yaml.Replace("policy:\n", "policy:\n  keep_logs_days: 0\n", StringComparison.Ordinal));
        Assert.Equal(0, config.Policy.KeepLogsDays);
        Assert.Equal(LogPrune.DefaultKeepDays, ConfigLoader.Parse(T.Yaml).Policy.KeepLogsDays);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("100000")]
    public void ConfigRejectsBadKeepLogsDays(string value)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("policy:\n", "policy:\n  keep_logs_days: " + value + "\n", StringComparison.Ordinal)));
        Assert.Contains("policy.keep_logs_days must be from 0 to 3650", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3650")]
    public void ConfigAcceptsKeepLogsDaysAtTheLimits(string value) =>
        Assert.Equal(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), ConfigLoader.Parse(T.Yaml.Replace("policy:\n", "policy:\n  keep_logs_days: " + value + "\n", StringComparison.Ordinal)).Policy.KeepLogsDays);

    /// <summary>
    /// A prune that fails (here the marker's name is taken by a folder) is one warning line before the Next and status lines; the run's exit
    /// code, its JSON object and the last stderr line are exactly what they would have been.
    /// </summary>
    [Fact]
    public async Task PruneFailureNeverChangesTheRunResult()
    {
        Directory.CreateDirectory(Marker);
        var old = PutLog(TimeSpan.FromDays(90));
        var ssh = new PoolSsh();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var p = ParsedArgs.Parse(["run", "--json", "--needs", "node", "--", "npx", "jest"]);
        var code = await Cli.Run(
            p, ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(State), _dir, _dir,
            stdout, stderr, CancellationToken.None);

        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(("Completed", 0), (json.RootElement.GetProperty("status").GetString(), json.RootElement.GetProperty("exitCode").GetInt32()));
        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var warning = lines.FindIndex(l => l.StartsWith("[lanq] warning: could not prune old job logs: ", StringComparison.Ordinal));
        Assert.True(warning >= 0, stderr.ToString());
        Assert.StartsWith("[lanq] Completed", lines[^1], StringComparison.Ordinal);
        Assert.True(warning < lines.Count - 1);
        Assert.True(File.Exists(old)); // nothing was pruned
        Assert.True(Directory.Exists(Marker));

        // The same with --shards.
        stdout = new StringWriter();
        stderr = new StringWriter();
        p = ParsedArgs.Parse(["run", "--needs", "node", "--shards", "2", "--", "npx", "jest", "--shard={shard}/{shards}"]);
        code = await Cli.Run(
            p, ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(State), _dir, _dir,
            stdout, stderr, CancellationToken.None);
        Assert.Equal(0, code);
        lines = [.. stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
        Assert.Contains(lines, l => l.StartsWith("[lanq] warning: could not prune old job logs: ", StringComparison.Ordinal));
        Assert.StartsWith("[lanq] Completed", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>After a normal run the old logs are gone and today's job log is kept.</summary>
    [Fact]
    public async Task ARunPrunesAfterItFinishes()
    {
        var old = PutLog(TimeSpan.FromDays(90));
        var ssh = new PoolSsh();
        var stderr = new StringWriter();
        var log = new JobLog(State);
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--needs", "node", "--", "npx", "jest"]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), log, _dir, _dir, new StringWriter(), stderr, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Marker));
        var kept = Assert.Single(Directory.GetFiles(Logs, "*.log"));
        Assert.Matches(LogPrune.LogName(), Path.GetFileName(kept)); // the run's own log, a name the prune would match
        Assert.DoesNotContain("warning", stderr.ToString(), StringComparison.Ordinal);
    }
}
