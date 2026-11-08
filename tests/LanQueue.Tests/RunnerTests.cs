using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-test-" + Guid.NewGuid().ToString("N"));

    private sealed class FakePayloads(string diff, params string[] files) : IPayloadBuilder
    {
        public int Built { get; private set; }

        public PayloadKinds? LastKinds { get; private set; }

        public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct)
        {
            Built++;
            LastKinds = kinds;
            Directory.CreateDirectory(outputDir);
            string? bundle = null, archive = null;
            if (kinds.HasFlag(PayloadKinds.Bundle))
            {
                bundle = Path.Combine(outputDir, jobId + ".bundle");
                File.WriteAllText(bundle, "x");
            }

            if (kinds.HasFlag(PayloadKinds.Archive))
            {
                archive = Path.Combine(outputDir, jobId + ".tar.gz");
                File.WriteAllText(archive, "x");
            }

            return Task.FromResult(new JobPayload(bundle, "abc", files, diff, archive));
        }
    }

    public RunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Healthy(double load) => $"load={load}\ndisk_free_gb=50\nslots_used=0\ntool=ok";

    private (JobRunner Runner, FakeSsh Ssh, FakePayloads Payloads, JobLog Log) Make(Func<Config.Machine, string, SshResult>? handler = null, string diff = "", params string[] files)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null; // remove the hours gate for these tests
        var ssh = new FakeSsh();
        var payloads = new FakePayloads(diff, files);
        var log = new JobLog(Path.Combine(_dir, ".lanq"));
        ssh.Handler = handler ?? ((m, cmd) =>
        {
            if (cmd.Contains("SLOT=", StringComparison.Ordinal) || DecodedContains(m, cmd, "SLOT="))
            {
                return new SshResult(0, "SLOT=1");
            }

            if (DecodedContains(m, cmd, "disk_free_gb"))
            {
                return new SshResult(0, Healthy(m.Name == "build-01" ? 0.5 : 0.1));
            }

            return new SshResult(0, "ran");
        });
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), log, Path.Combine(_dir, "tmp"), _ => { });
        return (runner, ssh, payloads, log);
    }

    private static bool DecodedContains(Config.Machine m, string cmd, string needle)
    {
        var text = m.Os == Config.MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;
        return text.Contains(needle, StringComparison.Ordinal);
    }

    private JobRequest Req(string[] argv, string dir = ".", params string[] needs) => new(needs, argv, dir, _dir, TimeSpan.Zero);

    [Fact]
    public async Task HappyPathRunsOnLeastLoadedMachineUploadsCleansUpAndLogsWallClock()
    {
        var (runner, ssh, _, log) = Make();
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-02", r.Machine); // load 0.1 beats 0.5
        Assert.Single(ssh.Uploads);
        Assert.EndsWith(".bundle", ssh.Uploads[0], StringComparison.Ordinal);
        Assert.Contains(ssh.Calls, c => DecodedContains(T.Windows(), c.Command, "worktree remove --force"));
        var rec = Assert.Single(log.ReadAll());
        Assert.Equal("build-02", rec.Machine);
        Assert.Equal("Completed", rec.Status);
        Assert.True(rec.WallSeconds >= 0);
    }

    [Fact]
    public async Task RemoteFailureExitCodeIsPropagatedAndCleanupStillRuns()
    {
        var (runner, ssh, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
            : DecodedContains(m, cmd, "LASTEXITCODE") || cmd.Contains("setsid -w", StringComparison.Ordinal) ? new SshResult(3, "boom")
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(3, r.CliExitCode);
        Assert.Contains(ssh.Calls, c => DecodedContains(T.Windows(), c.Command, "worktree remove --force") || c.Command.Contains("worktree remove --force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermissionSkippingFlagIsRejectedBeforeAnyNetworkOrGitWork()
    {
        var (runner, ssh, payloads, _) = Make();
        var config = T.Config();
        config.Policy.AllowedCommands.Add("claude");
        var r = await new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(_dir), _dir, _ => { })
            .RunAsync(Req(["claude", "-p", "x", "--dangerously-skip-permissions"]), CancellationToken.None);

        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
        _ = runner;
    }

    [Fact]
    public async Task CommandOutsideAllowlistIsRejected()
    {
        var (runner, ssh, _, _) = Make();
        var r = await runner.RunAsync(Req(["rm", "-rf", "/"]), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task SecretInUncommittedDiffIsRefusedAndNothingIsSent()
    {
        var (runner, ssh, _, _) = Make(diff: "+aws = AKIAABCDEFGHIJKLMNOP");
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Empty(ssh.Calls);
        Assert.Empty(ssh.Uploads);
        Assert.DoesNotContain("AKIA", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretFileInOverlayIsRefused()
    {
        var (runner, ssh, _, _) = Make(files: [".env"]);
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task SecretInCommandLineIsRefused()
    {
        var (runner, ssh, _, _) = Make();
        var r = await runner.RunAsync(Req(["npm", "test", "--token=ghp_abcdefghijklmnopqrstuvwxyz0123456789"]), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task NoMatchingCapabilityMeansNoCapacityNotAFailedRun()
    {
        var (runner, ssh, _, log) = Make();
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "gpu"), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(75, r.CliExitCode);
        Assert.Empty(ssh.Uploads);
        Assert.Empty(log.ReadAll());
    }

    [Fact]
    public async Task BusyMachineFallsThroughToNextAndFullFleetReportsNoCapacity()
    {
        var (runner, ssh, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=FULL")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task JobLogRoundTripsAndStatsAddUp()
    {
        var log = new JobLog(Path.Combine(_dir, "log"));
        log.Append(new JobRecord("a", "m1", "dotnet test", DateTimeOffset.UtcNow, 12.5, 0, "Completed"));
        log.Append(new JobRecord("b", "m1", "dotnet build", DateTimeOffset.UtcNow, 7.5, 1, "Failed"));
        var all = log.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(20, all.Sum(r => r.WallSeconds));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ArchiveMachineGetsTheTarballAndNoGitCommands()
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].AllowedHours = null;
        config.Machines[1].Capabilities = ["windows"];
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
                : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1)) : new SshResult(0, "ran"),
        };
        var payloads = new FakePayloads("");
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-01", r.Machine);
        Assert.Equal(PayloadKinds.Archive | PayloadKinds.Bundle, payloads.LastKinds);
        Assert.Equal("build-01:/srv/lanq/incoming/" + r.JobId + ".tar.gz", Assert.Single(ssh.Uploads));
        Assert.Contains(ssh.Calls, c => c.Command.Contains("-tzf", StringComparison.Ordinal));
        Assert.DoesNotContain(ssh.Calls.Where(c => c.Machine == "build-01"), c => c.Command.Contains("worktree", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task OnlyArchiveMachinesBuildsNoBundle()
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].Transfer = Config.TransferMode.Archive;
        var payloads = new FakePayloads("");
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(255, "down") };
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });
        await runner.RunAsync(Req(["dotnet", "test"]), CancellationToken.None);
        Assert.Equal(PayloadKinds.Archive, payloads.LastKinds);
    }
}
