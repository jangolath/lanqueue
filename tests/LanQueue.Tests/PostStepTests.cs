using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Plan;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 post: step (owner decision; review H1, L2, L3): an orchestrator-driven step after the job, in the order main, (on timeout) stop,
/// fetch, post, cleanup, with the slot held throughout; its own pid file; a 5-minute limit; its exit code a warning only; run after
/// Completed, Failed and TimedOut, never after WindowClosed or a lost connection; checked like the command.
/// </summary>
public sealed class PostStepTests : IDisposable
{
    // Unique job ids per test (review B-L6): a real cleanup stops every process of this uid that carries LANQ_JOB=<id>.
    private readonly string _j1 = T.NewJobId();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-post-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _output = [];

    public PostStepTests() => Directory.CreateDirectory(Path.Combine(_dir, ".git"));

    public void Dispose() => TestTree.Delete(_dir);

    // ---- the command line and lanq.yml ----

    [Fact]
    public void PostSplitsOnSpacesAndRefusesQuotingAndShardTokens()
    {
        Assert.Equal(["dotnet", "build-server", "status"], ParsedArgs.Parse(["run", "--post", "dotnet  build-server status", "--", "dotnet", "test"]).Post);
        foreach (var bad in new[] { "echo \"a b\"", "echo 'a'", "echo `x`", "echo a\\b", "", "   ", "echo {shard}", "echo {shards}" })
        {
            Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--post", bad, "--", "dotnet", "test"]));
        }

        // A lanq.yml-owned option: refused with --plan (L2).
        Assert.Contains("--post", Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--post", "x"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanqYmlPostTakesAListOrAPlainString()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults: { post: dotnet build-server status }
            jobs:
              unit: { command: dotnet test }
              list: { command: dotnet test, post: [dotnet, "my tool", --x] }
            """);
        Assert.Equal(["dotnet", "build-server", "status"], plan.Jobs[0].Args.Post);
        Assert.Equal(["dotnet", "my tool", "--x"], plan.Jobs[1].Args.Post);

        // B-L1: the run line carries a plain post as --post (a rerun from runArgv keeps it); a list --post cannot carry stays on the job only,
        // and survives a re-parse of the run line (narrowing).
        var run = plan.Jobs[0].RunArgs.ToList();
        Assert.Equal(["--post", "dotnet build-server status"], run.Skip(run.IndexOf("--post")).Take(2));
        Assert.True(run.IndexOf("--post") < run.IndexOf("--"));
        Assert.Equal(["dotnet", "build-server", "status"], ParsedArgs.Parse([.. run]).Post);
        Assert.DoesNotContain("--post", plan.Jobs[1].RunArgs);
        Assert.Equal(["dotnet", "my tool", "--x"], PlanNarrowing.Reparsed(plan.Jobs[1], [.. plan.Jobs[1].RunArgs]).Args.Post);
        var ex = Assert.ThrowsAny<Exception>(() => PlanFileLoader.Parse("version: 1\njobs:\n  unit: { command: dotnet test, post: [echo, \"{shard}\"] }\n"));
        Assert.Contains("post: cannot use {shard}", ex.Message, StringComparison.Ordinal);
    }

    // ---- prechecks ----

    private (JobRunner Runner, FakeSsh Ssh) Make(Func<Machine, string, SshResult> job)
    {
        var config = T.Config();
        config.Policy.AllowedCommands.Add("dotnet build-server");
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok")
                : job(m, cmd),
        };
        return (new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _output.Add), ssh);
    }

    private JobRequest Req(string[] post, TimeSpan? timeout = null) => new(["node"], ["npm", "test"], ".", _dir, TimeSpan.Zero, timeout) { Post = post };

    [Fact]
    public async Task APostOutsideTheAllowlistOrWithASecretIsRefusedBeforeAnything()
    {
        var (runner, ssh) = Make((_, _) => new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["rm", "-rf", "x"]), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Policy), (r.Status, r.RejectKind));
        Assert.StartsWith("post: ", r.Message, StringComparison.Ordinal);

        r = await runner.RunAsync(Req(["npm", "test", "--token=ghp_0123456789abcdefghijABCDEFGHIJ0123456789"]), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Secret), (r.Status, r.RejectKind));
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public void AWindowsUnsafePostKeepsTheJobOffWindows()
    {
        Assert.True(JobRunner.WindowsUnsafe(Req(["npm", "test", "a>b"])));
        Assert.False(JobRunner.WindowsUnsafe(Req(["npm", "test"])));
    }

    // ---- order and outcome ----

    private static bool IsMain(string cmd) => cmd.Contains("locks/job-", StringComparison.Ordinal) && cmd.Contains("setsid -w", StringComparison.Ordinal) && !cmd.Contains("rm -rf", StringComparison.Ordinal);

    private static bool IsPost(string cmd) => cmd.Contains("locks/post-", StringComparison.Ordinal) && cmd.Contains("setsid -w", StringComparison.Ordinal) && !cmd.Contains("rm -rf", StringComparison.Ordinal);

    private static bool IsCleanup(string cmd) => cmd.Contains("rm -rf \"$R/jobs/$ID\"", StringComparison.Ordinal);

    private static bool IsStop(string cmd) => cmd.Contains(RemoteScripts.StopJobMarker, StringComparison.Ordinal);

    private static List<string> Steps(FakeSsh ssh) =>
        [.. ssh.Calls.Select(c => c.Command).Select(c => IsStop(c) ? "stop" : IsCleanup(c) ? "cleanup" : IsPost(c) ? "post" : IsMain(c) ? "main" : null).OfType<string>()];

    [Theory]
    [InlineData(0, JobStatus.Completed)]
    [InlineData(1, JobStatus.Failed)]
    public async Task PostRunsAfterTheJobBeforeCleanupAndItsExitIsAWarningOnly(int jobExit, JobStatus status)
    {
        var (runner, ssh) = Make((_, cmd) =>
            IsPost(cmd) ? new SshResult(3, "post output\n" + T.ExitMarkerOf(cmd) + "3")
            : IsMain(cmd) ? new SshResult(jobExit, "ran\n" + T.ExitMarkerOf(cmd) + jobExit)
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        Assert.Equal(status, r.Status);
        Assert.Equal(jobExit, r.CliExitCode); // never the post's 3
        Assert.Equal(new PostResult(3, false, r.Post!.Seconds) { LogFrom = r.Post.LogFrom }, r.Post);
        Assert.Equal(["main", "post", "cleanup"], Steps(ssh));
        Assert.Contains("[lanq] post: exit 3 (warning only)", _output);
        Assert.Contains("post output", _output);
    }

    [Fact]
    public async Task AfterATimeoutTheJobIsStoppedFirstThenThePostRuns()
    {
        var (runner, ssh) = Make((_, cmd) =>
            IsPost(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0")
            : IsMain(cmd) ? new SshResult(ProcessSshClient.TimedOutExitCode, "", TimedOut: true)
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        Assert.Equal(JobStatus.TimedOut, r.Status);
        Assert.Equal(["main", "stop", "post", "cleanup"], Steps(ssh));
        Assert.Equal(0, r.Post!.ExitCode);
    }

    [Fact]
    public async Task NoPostAfterALostConnection()
    {
        var (runner, ssh) = Make((_, cmd) => IsMain(cmd) ? new SshResult(255, "ssh: connection reset") : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Null(r.Post);
        Assert.DoesNotContain("post", Steps(ssh));
    }

    [Fact]
    public void ThePostHasItsOwnPidFileWhichCleanupStops()
    {
        var linux = T.Linux();
        var windows = T.Windows();
        Assert.Contains("/locks/post-" + _j1 + ".pid", RemoteScripts.Execute(linux, _j1, ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.Contains("timeout -k 30 300", RemoteScripts.Execute(linux, _j1, ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.Contains("kill_recorded \"$R/locks/post-$ID.pid\"", RemoteScripts.Cleanup(linux, _j1, "1", T.RepoId), StringComparison.Ordinal);
        Assert.Contains("$pp = \"$root/locks/post-$id.pid\"", RemoteScripts.Cleanup(windows, _j1, "1", T.RepoId), StringComparison.Ordinal);
        windows.Transfer = TransferMode.Archive;
        Assert.Contains("$pp = \"$root/locks/post-$id.pid\"", RemoteScripts.Cleanup(windows, _j1, "1"), StringComparison.Ordinal);
        Assert.True(JobRunner.PostLimit < RemoteScripts.SlotTransferAllowance); // the slot lease needs no change
    }

    /// <summary>The real post script on this OS: it records locks/post-ID.pid while it runs and removes it after.</summary>
    [Fact]
    public void TheRealPostScriptRecordsItsOwnPidFile()
    {
        var root = Path.Combine(_dir, "wr").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "jobs", _j1));
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        var windows = OperatingSystem.IsWindows();
        var m = windows ? T.Windows() : T.Linux();
        m.WorkRoot = root;
        var pid = root + "/locks/post-" + _j1 + ".pid";
        string[] argv = windows
            ? ["powershell", "-NoProfile", "-Command", "if (Test-Path '" + pid + "') { 'present' }"]
            : ["sh", "-c", "test -f " + pid + " && echo present"];
        if (windows)
        {
            Assert.Equal(0, Shells.Run("windows", RemoteScripts.EnsureIncoming(m)).Code);
        }

        var (code, output) = Shells.Run(windows ? "windows" : "linux", RemoteScripts.Execute(m, _j1, ".", argv, JobRunner.PostLimit, pidKind: "post"));
        Assert.True(code == 0, output);
        Assert.Contains("present", output, StringComparison.Ordinal);
        Assert.False(File.Exists(pid));
        Assert.False(File.Exists(root + "/locks/job-" + _j1 + ".pid"));
    }

    // ---- review B: cancel, the exit marker, the window, the log tail ----

    /// <summary>
    /// FakeSsh, but a command may block until its token is cancelled (as ProcessSshClient throws when cancelled): lines from
    /// <see cref="Handler"/>'s output go out first, then <see cref="Block"/> decides whether to wait.
    /// </summary>
    private sealed class BlockingSsh(FakeSsh inner) : ISshClient
    {
        public Func<string, Task>? Block { get; set; }

        public async Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            var r = await inner.RunAsync(machine, remoteCommand, onLine, timeout, ct);
            if (Block is { } block)
            {
                await block(remoteCommand).WaitAsync(ct);
            }

            return r;
        }

        public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
            inner.UploadAsync(machine, localPath, remotePath, timeout, ct);

        public Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct) =>
            ((ISshClient)inner).RunWithInputAsync(machine, remoteCommand, input, maxOutputChars, timeout, ct);

        public Task<SshResult> RunSplitAsync(
            Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
            ((ISshClient)inner).RunSplitAsync(machine, remoteCommand, input, onStdout, onStderr, timeout, ct);
    }

    private JobRunner Runner(ISshClient ssh)
    {
        var config = T.Config();
        config.Policy.AllowedCommands.Add("dotnet build-server");
        return new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _output.Add);
    }

    private static FakeSsh Worker(Func<string, SshResult> job) => new()
    {
        Handler = (_, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
            : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\nmax_jobs=4\ntool=ok")
            : job(cmd),
    };

    [Fact]
    public async Task ACancelDuringThePostKeepsTheJobsOwnResult()
    {
        // B-M1: the job Completed; Ctrl+C (or plan_timeout, --fail-fast) arrives during its post step: the job stays Completed with its exit
        // code, and the post says it was cancelled.
        using var cts = new CancellationTokenSource();
        var inner = Worker(cmd => IsPost(cmd) ? new SshResult(0, "post starts") : IsMain(cmd) ? new SshResult(0, "ran\n" + T.ExitMarkerOf(cmd) + "0") : new SshResult(0, ""));
        var ssh = new BlockingSsh(inner)
        {
            Block = cmd =>
            {
                if (!IsPost(cmd))
                {
                    return Task.CompletedTask;
                }

                cts.Cancel();
                return Task.Delay(Timeout.Infinite);
            },
        };
        var r = await Runner(ssh).RunAsync(Req(["dotnet", "build-server", "status"]), cts.Token);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(0, r.CliExitCode);
        Assert.True(r.Post!.Cancelled);
        Assert.Equal(130, r.Post.ExitCode);
        Assert.Contains(inner.Calls, c => IsCleanup(c.Command));
        Assert.Contains("[lanq] post: cancelled before it ended (the job's own result stands)", _output);
    }

    [Fact]
    public async Task FailFastDuringASiblingsPostKeepsThatShardCompleted()
    {
        // B-M1: shard 2 Completed and is in its post step when shard 1 fails with --fail-fast: shard 2 stays Completed.
        var postStarted = new TaskCompletionSource();
        var inner = Worker(cmd => IsPost(cmd) ? new SshResult(0, "")
            : IsMain(cmd) ? cmd.Contains("LANQ_SHARD=1", StringComparison.Ordinal) ? new SshResult(1, "red\n" + T.ExitMarkerOf(cmd) + "1") : new SshResult(0, "ok\n" + T.ExitMarkerOf(cmd) + "0")
            : new SshResult(0, ""));
        var ssh = new BlockingSsh(inner)
        {
            Block = cmd =>
            {
                if (IsPost(cmd) && cmd.Contains("LANQ_SHARD=2", StringComparison.Ordinal))
                {
                    postStarted.TrySetResult();
                    return Task.Delay(Timeout.Infinite);
                }

                return IsMain(cmd) && cmd.Contains("LANQ_SHARD=1", StringComparison.Ordinal) ? postStarted.Task : Task.CompletedTask;
            },
        };
        var r = await Runner(ssh).RunShardsAsync(Req(["dotnet", "build-server", "status"]), new ShardOptions(2, FailFast: true), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        var two = Assert.Single(r.Shards, s => s.Index == 2);
        Assert.Equal(nameof(JobStatus.Completed), two.Status);
        Assert.True(two.Post!.Cancelled);
        Assert.Equal(nameof(JobStatus.Failed), Assert.Single(r.Shards, s => s.Index == 1).Status);
    }

    [Fact]
    public async Task ThePostEndsAtItsExitMarkerEvenWhenAChildHoldsTheOutput()
    {
        // B-L3: the step exits 0 at once but leaves a child holding stdout: lanq stops reading at the marker (no 6-minute wait), reports
        // exit 0, not timed out; a step that itself exits 124 is not called timed out either; the worker's limit line is.
        var inner = Worker(cmd => IsPost(cmd) ? new SshResult(0, "started a server\n" + T.ExitMarkerOf(cmd) + "0\nserver log line") : IsMain(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0") : new SshResult(0, ""));
        var ssh = new BlockingSsh(inner) { Block = cmd => IsPost(cmd) ? Task.Delay(Timeout.Infinite) : Task.CompletedTask };
        var r = await Runner(ssh).RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(new PostResult(0, false, r.Post!.Seconds) { LogFrom = r.Post.LogFrom }, r.Post);
        Assert.DoesNotContain("server log line", _output);

        var exits124 = await Runner(Worker(cmd => IsPost(cmd) ? new SshResult(124, T.ExitMarkerOf(cmd) + "124") : IsMain(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0") : new SshResult(0, "")))
            .RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        Assert.Equal(124, exits124.Post!.ExitCode);
        Assert.False(exits124.Post.TimedOut);

        var stopped = await Runner(Worker(cmd => IsPost(cmd) ? new SshResult(124, T.LimitMarkerOf(cmd) + "\n" + T.ExitMarkerOf(cmd) + "124") : IsMain(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0") : new SshResult(0, "")))
            .RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        Assert.True(stopped.Post!.TimedOut);
        Assert.Contains("LANQ_LIMIT=", RemoteScripts.Execute(T.Linux(), _j1, ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.Contains("LANQ_LIMIT=", RemoteScripts.Execute(T.Windows(), _j1, ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.DoesNotContain("LANQ_LIMIT=", RemoteScripts.Execute(T.Linux(), _j1, ".", ["x"], JobRunner.PostLimit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePostNeverRunsPastTheMachinesWindow()
    {
        // B-L2: 10 s left of the allowed hours plus grace: the step is skipped, with a note; the job's result stands.
        var now = DateTimeOffset.UtcNow;
        var config = T.Config();
        config.Policy.AllowedCommands.Add("dotnet build-server");
        config.Policy.WindowGraceMinutes = 0;
        var ssh = Worker(cmd => IsPost(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0") : IsMain(cmd) ? new SshResult(0, T.ExitMarkerOf(cmd) + "0") : new SshResult(0, ""));
        var runner = new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _output.Add);
        var post = await runner.PostForTestAsync(_j1, Req(["dotnet", "build-server", "status"]), config.Machines[0], now.AddSeconds(10), TimeSpan.Zero, _output.Add);
        Assert.True(post.Skipped);
        Assert.DoesNotContain(ssh.Calls, c => IsPost(c.Command));
        Assert.Contains(_output, l => l.StartsWith("[lanq] post: skipped:", StringComparison.Ordinal));
        var capped = await runner.PostForTestAsync(_j1, Req(["dotnet", "build-server", "status"]), config.Machines[0], now.AddSeconds(120), TimeSpan.Zero, _output.Add);
        Assert.False(capped.Skipped);
        Assert.Contains(ssh.Calls, c => IsPost(c.Command) && !c.Command.Contains("timeout -k 30 300", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLogTailEndsWhereThePostBegins()
    {
        // B-L4: a failed job with a chatty post step: --log-tail shows the job's own last lines, not the hook's.
        var r = await Runner(Worker(cmd =>
                IsPost(cmd) ? new SshResult(0, string.Join('\n', Enumerable.Range(1, 50).Select(i => "hook " + i)) + "\n" + T.ExitMarkerOf(cmd) + "0")
                : IsMain(cmd) ? new SshResult(1, "error CS1002: the real failure\n" + T.ExitMarkerOf(cmd) + "1")
                : new SshResult(0, "")))
            .RunAsync(Req(["dotnet", "build-server", "status"]), CancellationToken.None);
        var tail = JobLog.Tail(r.LogPath!, 5, r.Post!.LogFrom);
        Assert.Contains("error CS1002: the real failure", tail);
        Assert.DoesNotContain(tail, l => l.StartsWith("hook ", StringComparison.Ordinal));
        Assert.Contains(JobLog.Tail(r.LogPath!, 5), l => l.StartsWith("hook ", StringComparison.Ordinal)); // the whole log keeps them
    }
}
