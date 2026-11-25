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
[Collection(nameof(RealJobScripts))]
public sealed class PostStepTests : IDisposable
{
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
        Assert.Equal(new PostResult(3, false, r.Post!.Seconds), r.Post);
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
        Assert.Contains("/locks/post-j1.pid", RemoteScripts.Execute(linux, "j1", ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.Contains("timeout -k 30 300", RemoteScripts.Execute(linux, "j1", ".", ["x"], JobRunner.PostLimit, pidKind: "post"), StringComparison.Ordinal);
        Assert.Contains("kill_recorded \"$R/locks/post-$ID.pid\"", RemoteScripts.Cleanup(linux, "j1", "1", T.RepoId), StringComparison.Ordinal);
        Assert.Contains("$pp = \"$root/locks/post-$id.pid\"", RemoteScripts.Cleanup(windows, "j1", "1", T.RepoId), StringComparison.Ordinal);
        windows.Transfer = TransferMode.Archive;
        Assert.Contains("$pp = \"$root/locks/post-$id.pid\"", RemoteScripts.Cleanup(windows, "j1", "1"), StringComparison.Ordinal);
        Assert.True(JobRunner.PostLimit < RemoteScripts.SlotTransferAllowance); // the slot lease needs no change
    }

    /// <summary>The real post script on this OS: it records locks/post-ID.pid while it runs and removes it after.</summary>
    [Fact]
    public void TheRealPostScriptRecordsItsOwnPidFile()
    {
        var root = Path.Combine(_dir, "wr").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        var windows = OperatingSystem.IsWindows();
        var m = windows ? T.Windows() : T.Linux();
        m.WorkRoot = root;
        var pid = root + "/locks/post-j1.pid";
        string[] argv = windows
            ? ["powershell", "-NoProfile", "-Command", "if (Test-Path '" + pid + "') { 'present' }"]
            : ["sh", "-c", "test -f " + pid + " && echo present"];
        if (windows)
        {
            Assert.Equal(0, Shells.Run("windows", RemoteScripts.EnsureIncoming(m)).Code);
        }

        var (code, output) = Shells.Run(windows ? "windows" : "linux", RemoteScripts.Execute(m, "j1", ".", argv, JobRunner.PostLimit, pidKind: "post"));
        Assert.True(code == 0, output);
        Assert.Contains("present", output, StringComparison.Ordinal);
        Assert.False(File.Exists(pid));
        Assert.False(File.Exists(root + "/locks/job-j1.pid"));
    }
}
