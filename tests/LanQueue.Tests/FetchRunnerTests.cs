using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>When --fetch runs (after the job, before cleanup, only if the job ran), its deadline, and what run and run --json report.</summary>
public sealed class FetchRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-fetchrun-" + Guid.NewGuid().ToString("N"));

    public FetchRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string OutRoot => Path.Combine(_dir, "artifacts");

    private static string Decode(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    private static bool IsFetch(string text) => text.Contains("LQF1", StringComparison.Ordinal);

    private static bool IsExecute(string text) => text.Contains("exec 3<&0", StringComparison.Ordinal) || text.Contains("Set-Content -LiteralPath $pf", StringComparison.Ordinal);

    private static bool IsCleanup(string text) => text.Contains("worktree remove", StringComparison.Ordinal);

    internal static string Stream(params (string Path, string Content)[] files)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            sb.Append($"LQF1 FILE {bytes.Length} {Convert.ToBase64String(Encoding.UTF8.GetBytes(path))}\n");
            if (bytes.Length > 0)
            {
                sb.Append(Convert.ToBase64String(bytes)).Append('\n');
            }

            sb.Append("LQF1 END ").Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }

        return sb.Append("LQF1 DONE\n").ToString();
    }

    /// <summary>
    /// The job goes to <paramref name="target"/> (the other machine is loaded). The job exits with <paramref name="jobExit"/>; the fetch
    /// command answers <paramref name="fetchOutput"/>.
    /// </summary>
    private (JobRunner Runner, FakeSsh Ssh, List<string> Lines) Make(string target, int jobExit, string fetchOutput, string? hours = null, TimeProvider? clock = null, Action? onExecute = null)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = hours is null ? null : [hours];
        var lines = new List<string>();
        var ssh = new FakeSsh();
        ssh.Handler = (m, cmd) =>
        {
            var text = Decode(m, cmd);
            if (text.Contains("disk_free_gb", StringComparison.Ordinal))
            {
                return new SshResult(0, (m.Name == target ? "load=0.1" : "load=0.99") + "\ndisk_free_gb=50\nslots_used=0\ntool=ok\nlocal_time=2026-01-02T23:00:00");
            }

            if (text.Contains("SLOT=", StringComparison.Ordinal))
            {
                return new SshResult(0, "SLOT=1");
            }

            if (IsExecute(text))
            {
                onExecute?.Invoke();
                return new SshResult(jobExit, "job output");
            }

            return IsFetch(text) ? new SshResult(0, fetchOutput) : new SshResult(0, "");
        };
        clock ??= TimeProvider.System;
        var runner = new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, clock), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"), lines.Add, clock: clock);
        return (runner, ssh, lines);
    }

    private JobRequest Req(string[]? fetch = null, string[]? argv = null) =>
        new(["dotnet"], argv ?? ["dotnet", "test"], ".", _dir, TimeSpan.Zero, TimeSpan.FromMinutes(240), null,
            fetch is null ? null : new FetchRequest(fetch, OutRoot, FetchLimits.Default));

    private static List<string> Texts(FakeSsh ssh) =>
        ssh.Calls.Select(c => Decode(c.Machine == "build-02" ? T.Windows() : T.Linux(), c.Command)).ToList();

    [Fact]
    public async Task FetchRunsAfterTheJobAndBeforeCleanup()
    {
        var (runner, ssh, lines) = Make("build-01", 0, Stream(("TestResults/a.trx", "<trx/>")));

        var r = await runner.RunAsync(Req(["TestResults/*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        var texts = Texts(ssh);
        var execute = texts.FindIndex(IsExecute);
        var fetch = texts.FindIndex(IsFetch);
        var cleanup = texts.FindIndex(IsCleanup);
        Assert.True(execute >= 0 && execute < fetch && fetch < cleanup, string.Join(" | ", texts.Select(t => t[..Math.Min(40, t.Length)])));
        Assert.Equal("<trx/>", File.ReadAllText(Path.Combine(OutRoot, r.JobId, "TestResults", "a.trx")));
        Assert.Equal("TestResults/a.trx", Assert.Single(r.Fetch!.Fetched).Path);
        Assert.Contains(lines, l => l.StartsWith("[lanq] fetch (the job completed): 1 file(s)", StringComparison.Ordinal));
    }

    /// <summary>A timed-out job may still run on the worker (it enforces the limit a margin later): it is stopped before the fetch reads its files.</summary>
    [Theory]
    [InlineData("build-01")]
    [InlineData("build-02")]
    public async Task ATimedOutJobIsStoppedBeforeItsFilesAreFetched(string target)
    {
        var (runner, ssh, lines) = Make(target, 0, Stream(("a.trx", "x")));
        var inner = ssh.Handler;
        ssh.Handler = (m, cmd) => IsExecute(Decode(m, cmd)) ? new SshResult(124, "", TimedOut: true) : inner(m, cmd);

        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.TimedOut, r.Status);
        var texts = Texts(ssh);
        var stop = texts.FindIndex(t => t.Contains(RemoteScripts.StopJobMarker, StringComparison.Ordinal));
        Assert.True(texts.FindIndex(IsExecute) < stop && stop < texts.FindIndex(IsFetch) && texts.FindIndex(IsFetch) < texts.FindIndex(IsCleanup));
        Assert.Contains(lines, l => l.Contains("stopping the job before fetching", StringComparison.Ordinal));
        Assert.Single(r.Fetch!.Fetched);
    }

    [Fact]
    public async Task ACompletedJobIsNotStoppedBeforeTheFetch()
    {
        var (runner, ssh, _) = Make("build-01", 1, Stream(("a.trx", "x")));
        await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);
        Assert.DoesNotContain(Texts(ssh), t => t.Contains(RemoteScripts.StopJobMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedJobIsFetchedTooAndKeepsItsExitCode()
    {
        var (runner, _, lines) = Make("build-01", 3, Stream(("TestResults/a.trx", "<failed/>")));

        var r = await runner.RunAsync(Req(["TestResults/*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(3, r.CliExitCode);
        Assert.Single(r.Fetch!.Fetched);
        Assert.Contains(lines, l => l.StartsWith("[lanq] fetch (the job failed (exit 3))", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchProblemsNeverChangeTheJobsExitCode()
    {
        var (runner, _, lines) = Make("build-01", 0, "LQF1 FILE 10 " + Convert.ToBase64String("../x"u8.ToArray()) + "\nAAAA\n");

        var r = await runner.RunAsync(Req(["*"]), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(0, r.CliExitCode);
        Assert.Empty(r.Fetch!.Fetched);
        Assert.NotNull(r.Fetch.Error);
        Assert.Contains(lines, l => l.StartsWith("[lanq] fetch problem:", StringComparison.Ordinal));
    }

    /// <summary>ssh merges nothing for the fetch: a stderr line (a shell warning) arriving mid-frame never breaks the file being received.</summary>
    [Theory]
    [InlineData("build-01")]
    [InlineData("build-02")]
    public async Task AStderrLineInsideAFrameDoesNotBreakIt(string target)
    {
        var stream = Stream(("out/x.log", "hello world, this is the content"));
        var lines = stream.Split((char)10).ToList();
        lines.Insert(2, "STDERR:warning: something odd \u001b[2J happened");
        var (runner, _, output) = Make(target, 0, string.Join((char)10, lines));

        var r = await runner.RunAsync(Req(["out/*.log"]), CancellationToken.None);

        Assert.Equal("out/x.log", Assert.Single(r.Fetch!.Fetched).Path);
        Assert.Empty(r.Fetch.Skipped);
        Assert.Contains(output, l => l.StartsWith("[lanq] fetch stderr: warning: something odd ?[2J happened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWindowsWorkerGetsTheFetchScriptOnStdin()
    {
        var (runner, ssh, _) = Make("build-02", 0, Stream(("out/x.log", "x")));

        var r = await runner.RunAsync(Req(["out/*.log"]), CancellationToken.None);

        Assert.Equal("build-02", r.Machine);
        Assert.Single(r.Fetch!.Fetched);
        Assert.Equal(JobRunner.FetchTimeout, Assert.Single(ssh.StreamingTimeouts));
        Assert.Contains(ssh.Calls, c => c.Command.StartsWith("STDIN:", StringComparison.Ordinal) && c.Command.Contains("LQF1 FILE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoFetchWithoutTheOption()
    {
        var (runner, ssh, _) = Make("build-01", 0, Stream(("a.trx", "x")));
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Null(r.Fetch);
        Assert.DoesNotContain(Texts(ssh), IsFetch);
    }

    [Fact]
    public async Task NoFetchWhenTheJobWasRejected()
    {
        var (runner, ssh, _) = Make("build-01", 0, Stream(("a.trx", "x")));
        var r = await runner.RunAsync(Req(["*.trx"], ["rm", "-rf", "/"]), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Null(r.Fetch);
        Assert.DoesNotContain(Texts(ssh), IsFetch);
    }

    [Fact]
    public async Task NoFetchWhenNoMachineHadCapacity()
    {
        var (runner, ssh, _) = Make("nobody", 0, Stream(("a.trx", "x")));
        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Null(r.Fetch);
        Assert.DoesNotContain(Texts(ssh), IsFetch);
    }

    [Fact]
    public async Task NoFetchWhenThePreparationFailed()
    {
        var (runner, ssh, _) = Make("build-01", 0, Stream(("a.trx", "x")));
        var inner = ssh.Handler;
        ssh.Handler = (m, cmd) => cmd.Contains("bundle verify", StringComparison.Ordinal) ? new SshResult(1, "broken") : inner(m, cmd);

        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Null(r.Fetch);
        Assert.DoesNotContain(Texts(ssh), IsFetch);
        Assert.DoesNotContain(Texts(ssh), IsExecute);
    }

    private static readonly DateTimeOffset FridayElevenPmUtc = new(2026, 1, 2, 23, 0, 0, TimeSpan.Zero);

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Window ends 23:30, grace 15 min: the fetch may run until 23:47 (2 min margin), however long its own timeout is.</summary>
    [Fact]
    public async Task TheFetchDeadlineStopsAtTheWindowEndPlusGraceAndASmallMargin()
    {
        var clock = new SteppingClock(FridayElevenPmUtc);
        var (runner, ssh, _) = Make("build-02", 0, Stream(("a.trx", "x")), "Fri 22:00-23:30", clock, () => clock.Now += TimeSpan.FromMinutes(44));

        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);

        Assert.Single(r.Fetch!.Fetched);
        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(ssh.StreamingTimeouts));
    }

    [Fact]
    public async Task NoFetchOnceTheWindowAndItsMarginHaveEnded()
    {
        var clock = new SteppingClock(FridayElevenPmUtc);
        var (runner, ssh, lines) = Make("build-02", 0, Stream(("a.trx", "x")), "Fri 22:00-23:30", clock, () => clock.Now += TimeSpan.FromMinutes(48));

        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Empty(r.Fetch!.Fetched);
        Assert.Contains("allowed hours", r.Fetch.Error, StringComparison.Ordinal);
        Assert.Empty(ssh.StreamingTimeouts);
        Assert.Contains(lines, l => l.Contains("fetch problem: not fetched", StringComparison.Ordinal));
    }

    /// <summary>A job stopped by its window is fetched too (its partial results are often the point), within the margin.</summary>
    [Fact]
    public async Task AWindowClosedJobIsFetchedWithinTheMargin()
    {
        var clock = new SteppingClock(FridayElevenPmUtc);
        var (runner, ssh, lines) = Make("build-02", 0, Stream(("a.trx", "x")), "Fri 22:00-23:30", clock, () => clock.Now += TimeSpan.FromMinutes(45));
        var inner = ssh.Handler;
        ssh.Handler = (m, cmd) => IsExecute(Decode(m, cmd)) ? Executed(clock) : inner(m, cmd);

        var r = await runner.RunAsync(Req(["*.trx"]), CancellationToken.None);

        Assert.Equal(JobStatus.WindowClosed, r.Status);
        Assert.Single(r.Fetch!.Fetched);
        Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(ssh.StreamingTimeouts));
        Assert.Contains(lines, l => l.Contains("stopped at the end of its machine's allowed hours", StringComparison.Ordinal));
    }

    private static SshResult Executed(SteppingClock clock)
    {
        clock.Now += TimeSpan.FromMinutes(45);
        return new SshResult(124, "", TimedOut: true);
    }

    // ---- lanq run --json ----

    private async Task<(int Code, JsonElement Json, string Stderr)> RunCli(string[] args, string fetchOutput)
    {
        var (_, ssh, _) = Make("build-01", 0, fetchOutput);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(args), T.Config(), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")),
            _dir, _dir, o, e, CancellationToken.None);
        using var doc = JsonDocument.Parse(o.ToString()); // exactly one JSON value
        return (code, doc.RootElement.Clone(), e.ToString());
    }

    [Fact]
    public async Task RunJsonListsFetchedAndSkippedFiles()
    {
        var stream = "LQF1 SKIP link " + Convert.ToBase64String("TestResults/evil\u001b[2J.trx"u8.ToArray()) + "\n" + Stream(("TestResults/a.trx", "<trx/>"));
        var (code, json, stderr) = await RunCli(["run", "--json", "--needs", "dotnet", "--fetch", "TestResults/*.trx", "--fetch-dir", OutRoot, "--", "dotnet", "test"], stream);

        Assert.Equal(0, code);
        Assert.Equal("Completed", json.GetProperty("status").GetString());
        var f = Assert.Single(json.GetProperty("fetched").EnumerateArray().ToList());
        Assert.Equal("TestResults/a.trx", f.GetProperty("path").GetString());
        Assert.Equal(6, f.GetProperty("bytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("<trx/>"u8.ToArray())), f.GetProperty("sha256").GetString());
        var s = Assert.Single(json.GetProperty("fetchSkipped").EnumerateArray().ToList());
        Assert.Equal("TestResults/evil?[2J.trx", s.GetProperty("path").GetString());
        Assert.Contains("link", s.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(OutRoot, json.GetProperty("fetchDir").GetString(), StringComparison.Ordinal);
        Assert.False(json.TryGetProperty("fetchError", out _));
        Assert.Contains("[lanq] fetch (the job completed)", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', stderr);
    }

    [Fact]
    public async Task RunJsonWithFetchStillPrintsOneObjectWhenTheJobNeverRan()
    {
        var (code, json, _) = await RunCli(["run", "--json", "--fetch", "*.trx", "--fetch-dir", OutRoot, "--", "rm", "-rf", "/"], Stream());
        Assert.Equal(2, code);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("fetched").GetArrayLength());
        Assert.Equal(0, json.GetProperty("fetchSkipped").GetArrayLength());
    }

    [Fact]
    public async Task RunJsonWithoutFetchIsUnchanged()
    {
        var (_, json, _) = await RunCli(["run", "--json", "--needs", "dotnet", "--", "dotnet", "test"], Stream());
        Assert.False(json.TryGetProperty("fetched", out _));
        Assert.False(json.TryGetProperty("fetchSkipped", out _));
        Assert.False(json.TryGetProperty("fetchDir", out _));
    }

    [Fact]
    public async Task ABadFetchPatternIsRejectedWithOneJsonObject()
    {
        var o = new StringWriter();
        var code = await Cli.RunAsync(["run", "--json", "--fetch", "../secrets", "--", "dotnet", "test"], o, new StringWriter(), CancellationToken.None, () => T.Config());
        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
    }
}
