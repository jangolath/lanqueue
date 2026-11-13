using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-test-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _output = [];

    private BaseStore _bases = new(null);

    internal sealed class FakePayloads(string diff, params string[] files) : IPayloadBuilder
    {
        public int Built { get; private set; }

        public PayloadKinds? LastKinds { get; private set; }

        public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct)
        {
            Built++;
            LastKinds = kinds;
            Directory.CreateDirectory(outputDir);
            string? archive = null;
            if (kinds.HasFlag(PayloadKinds.Archive))
            {
                archive = Path.Combine(outputDir, jobId + ".tar.gz");
                File.WriteAllText(archive, "x");
            }

            return Task.FromResult(new JobPayload(Sha, files, diff, archive, RepoId: kinds.HasFlag(PayloadKinds.Bundle) && !NoRepoId ? T.RepoId : null, Head: Head, CommittedFiles: Committed, CommittedConfigs: Configs));
        }

        public string Sha { get; set; } = "abc";

        public bool NoRepoId { get; set; }

        public List<string> Committed { get; } = ["src/app.cs", ".env.example"];

        public Dictionary<string, string?> Configs { get; } = [];

        public string Head { get; set; } = new('1', 40);

        /// <summary>The base each bundle build was asked for, in order (null = full).</summary>
        public List<string?> BundleBases { get; } = [];

        /// <summary>Whether a build with a base produces an incremental bundle (false models "base not an ancestor any more").</summary>
        public bool BaseUsable { get; set; } = true;

        public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct)
        {
            BundleBases.Add(baseSha);
            Directory.CreateDirectory(outputDir);
            var used = BaseUsable ? baseSha : null;
            var bundle = Path.Combine(outputDir, jobId + (used is null ? "" : "-" + used) + ".bundle");
            File.WriteAllText(bundle, "x");
            return Task.FromResult(new BundleBuild(bundle, used));
        }

        public int ArchivesBuilt { get; private set; }

        public Exception? ArchiveFailure { get; set; }

        public (int Symlinks, int Submodules) Skipped { get; set; }

        public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct)
        {
            ArchivesBuilt++;
            if (ArchiveFailure is not null)
            {
                throw ArchiveFailure;
            }

            Directory.CreateDirectory(outputDir);
            var archive = Path.Combine(outputDir, jobId + ".tar.gz");
            File.WriteAllText(archive, "x");
            return Task.FromResult(new ArchiveBuild(archive, Skipped.Symlinks, Skipped.Submodules));
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
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), log, Path.Combine(_dir, "tmp"), _output.Add, bases: _bases);
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
        Assert.Contains(ssh.Calls, c => DecodedContains(T.Windows(), c.Command, "fetch -q") && DecodedContains(T.Windows(), c.Command, "repos/" + T.RepoId + ".git"));
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
    public async Task CommittedSecretFileIsRefusedBeforeAnythingIsSent()
    {
        var (runner, ssh, payloads, _) = Make();
        payloads.Committed.Add("deploy/prod.pem");
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Contains("deploy/prod.pem", r.Message, StringComparison.Ordinal);
        Assert.Contains("committed", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Uploads);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task CommittedNpmrcWithALiteralTokenIsRefusedWithoutEchoingIt()
    {
        var (runner, ssh, payloads, _) = Make();
        payloads.Committed.Add("web/.npmrc");
        payloads.Configs["web/.npmrc"] = "registry=https://r.example/\n//r.example/:_authToken=npm_SuperSecretValue123\n";
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Contains("web/.npmrc", r.Message, StringComparison.Ordinal);
        Assert.Contains("_authToken", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretValue123", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task CommittedNpmrcWithAnEnvReferenceRuns()
    {
        var (runner, _, payloads, _) = Make();
        payloads.Committed.Add(".npmrc");
        payloads.Configs[".npmrc"] = "//registry.npmjs.org/:_authToken=${NPM_TOKEN}\n";
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
    }

    [Fact]
    public async Task UncommittedRegistryOnlyNpmrcRunsLikeACommittedOne()
    {
        var (runner, _, payloads, _) = Make(files: ["web/.npmrc", ".pypirc"]);
        payloads.Configs["web/.npmrc"] = "registry=https://r.example/\n";
        payloads.Configs[".pypirc"] = "[pypi]\nusername = __token__\n";
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
    }

    [Fact]
    public async Task UncommittedNpmrcWithALiteralTokenIsRefusedByContent()
    {
        var (runner, ssh, payloads, _) = Make(files: [".npmrc"]);
        payloads.Configs[".npmrc"] = "//r.example/:_authToken=npm_SuperSecretValue123\n";
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Contains("_authToken", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecretValue123", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task ManyCommittedSecretFilesAreListedBriefly()
    {
        var (runner, _, payloads, _) = Make();
        payloads.Committed.AddRange(Enumerable.Range(0, 50).Select(i => $"certs/c{i}.pem"));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Contains("and 40 more", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("certs/c49.pem", r.Message, StringComparison.Ordinal);
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
        // Says which capability each machine lacks, instead of a bare "no match".
        Assert.Contains("lacks gpu", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no match", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Uploads);
        Assert.Empty(log.ReadAll());
    }

    /// <summary>A runner whose waits are counted (and last a few milliseconds), over <see cref="T.Config"/> without allowed hours.</summary>
    private (JobRunner Runner, FakeSsh Ssh, List<TimeSpan> Delays) Counted(Func<Config.Machine, string, SshResult> handler)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var ssh = new FakeSsh { Handler = handler };
        var delays = new List<TimeSpan>();
        var runner = new JobRunner(
            config, ssh, new FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _output.Add,
            (d, ct) =>
            {
                lock (delays)
                {
                    delays.Add(d);
                }

                return Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            },
            bases: _bases);
        return (runner, ssh, delays);
    }

    private JobRequest Waiting(TimeSpan wait, params string[] needs) => new(needs, ["dotnet", "test"], ".", _dir, wait);

    [Fact]
    public async Task NoMachineWithTheCapabilityEndsAtOnceWithoutAnySshOrWait()
    {
        var (runner, ssh, delays) = Counted(HealthyFleet);

        var r = await runner.RunAsync(Waiting(TimeSpan.FromSeconds(600), "dotnet", "gpu"), CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, 75), (r.Status, r.CliExitCode));
        Assert.False(r.Retryable);
        Assert.StartsWith("No eligible machine, and no machine can ever take this job as configured (waiting will not help). ", r.Message, StringComparison.Ordinal);
        Assert.Contains("build-01: lacks gpu (capabilities: dotnet, node)", r.Message, StringComparison.Ordinal);
        Assert.Equal(["build-01", "build-02"], r.Excluded!.Select(x => x.Machine));
        Assert.All(r.Excluded!, x => Assert.Equal(("missing_capability", true), (x.Reason, x.Permanent)));
        Assert.Empty(ssh.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task MemAboveEveryMachinesRamEndsAfterOneProbe()
    {
        var (runner, ssh, delays) = Counted((m, cmd) =>
            DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1) + "\nmem_free_gb=3\nmem_total_gb=" + (m.Name == "build-01" ? "15.6" : "7.6"))
            : HealthyFleet(m, cmd));

        var r = await runner.RunAsync(Waiting(TimeSpan.FromSeconds(600), "dotnet") with { MinMemGb = 32 }, CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, 75), (r.Status, r.CliExitCode));
        Assert.False(r.Retryable);
        Assert.StartsWith("No eligible machine, and no machine can ever take this job as configured", r.Message, StringComparison.Ordinal);
        Assert.Contains("build-01: 15.6 GB total memory, --mem needs 32.0 GB", r.Message, StringComparison.Ordinal);
        Assert.All(r.Excluded!, x => Assert.Equal("mem_exceeds_total", x.Reason));
        Assert.Equal(2, ssh.Calls.Count); // one probe of each machine, nothing else
        Assert.Empty(delays);
    }

    [Fact]
    public async Task BusyFleetIsRetryableAndStillWaits()
    {
        var (runner, _, delays) = Counted((m, cmd) =>
            DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=2\ntool=ok") : HealthyFleet(m, cmd));

        var r = await runner.RunAsync(Waiting(TimeSpan.FromMilliseconds(200), "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.True(r.Retryable);
        Assert.StartsWith("No eligible machine. ", r.Message, StringComparison.Ordinal);
        Assert.All(r.Excluded!, x => Assert.Equal(("busy", false), (x.Reason, x.Permanent)));
        Assert.NotEmpty(delays);
    }

    [Fact]
    public async Task OneUnreachableMachineKeepsTheRunRetryable()
    {
        // build-02 lacks node for good; build-01 has it but does not answer now.
        var (runner, _, delays) = Counted((m, cmd) => m.Name == "build-01" ? new SshResult(255, "ssh: connect to host 10.20.30.11 port 22: Connection refused") : HealthyFleet(m, cmd));

        var r = await runner.RunAsync(Waiting(TimeSpan.FromMilliseconds(200), "node") with { Argv = ["npm", "test"] }, CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.True(r.Retryable);
        Assert.Equal([("build-01", "unreachable", false), ("build-02", "missing_capability", true)], r.Excluded!.Select(x => (x.Machine, x.Reason, x.Permanent)));
        Assert.NotEmpty(delays);
    }

    [Fact]
    public async Task TheWindowsArgumentRefusalStillComesBeforeTheCapabilityCheck()
    {
        // Arguments unsafe for Windows, and no machine has gpu at all: still the old Rejected (2), not NoCapacity.
        var (runner, ssh, _) = Counted(HealthyFleet);
        var r = await runner.RunAsync(Waiting(TimeSpan.Zero, "gpu") with { Argv = ["npm", "test", "&calc"] }, CancellationToken.None);

        Assert.Equal((JobStatus.Rejected, 2), (r.Status, r.CliExitCode));
        Assert.Null(r.Retryable);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task NoCapacityShowsWhatToFixOnAMachineThatRefusesTheKey()
    {
        var (runner, _, _, _) = Make((m, _) => new SshResult(255, m.Ssh + ": Permission denied (publickey)."));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("unreachable: ", r.Message, StringComparison.Ordinal);
        Assert.Contains("authorized_keys", r.Message, StringComparison.Ordinal);
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
        Assert.Contains("no free slot", r.Message, StringComparison.Ordinal);
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
        Assert.Equal(PayloadKinds.Bundle, payloads.LastKinds);
        Assert.Equal(1, payloads.ArchivesBuilt);
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
        Assert.Equal(PayloadKinds.None, payloads.LastKinds);
    }

    [Fact]
    public async Task GitOnlyJobNeverBuildsAnArchive()
    {
        var (runner, _, payloads, _) = Make();
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(0, payloads.ArchivesBuilt);
    }

    [Fact]
    public async Task ArchiveBuildFailureDoesNotFailAJobThatAGitMachineCanTake()
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].AllowedHours = null;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
                : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.01 : 0.5)) : new SshResult(0, "ran"),
        };
        var payloads = new FakePayloads("") { ArchiveFailure = new InvalidOperationException("Cannot build an archive: nope") };
        var lines = new List<string>();
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), lines.Add);
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-02", r.Machine);
    }

    [Fact]
    public async Task ArchiveBuildFailureFailsClearlyWhenEveryMachineNeedsAnArchive()
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].Transfer = Config.TransferMode.Archive;
        config.Machines[1].AllowedHours = null;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
                : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1)) : new SshResult(0, "ran"),
        };
        var payloads = new FakePayloads("") { ArchiveFailure = new InvalidOperationException("Cannot build an archive: Archive path 'CON' refused (rule: windows-reserved-device-name).") };
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Contains("windows-reserved-device-name", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task SkippedSymlinksAndSubmodulesAreReportedInTheJobOutput()
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
        var payloads = new FakePayloads("") { Skipped = (2, 1) };
        var lines = new List<string>();
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), lines.Add);
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Contains(lines, l => l.Contains("skipped 2 symlink(s) and 1 submodule(s)", StringComparison.Ordinal));
    }

    private (JobRunner Runner, List<string> Lines) ArchiveFailureRig(Action<Config.FleetConfig> shape, Exception failure, string slot = "SLOT=1", double gitLoad = 0.5)
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].AllowedHours = null;
        shape(config);
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, slot)
                : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.01 : gitLoad)) : new SshResult(0, "ran"),
        };
        var payloads = new FakePayloads("") { ArchiveFailure = failure };
        var lines = new List<string>();
        return (new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), lines.Add), lines);
    }

    [Fact]
    public async Task ArchiveBuildFailureFailsFastWhenTheOnlyGitMachineLacksTheCapability()
    {
        var (runner, lines) = ArchiveFailureRig(c => c.Machines[1].Capabilities = ["windows"], new InvalidOperationException("Cannot build an archive: rule x-rule."));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Equal(70, r.ExitCode);
        Assert.Contains("x-rule", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("waiting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArchiveBuildFailureWaitsForAHeldCapableGitMachineAndEndsWith75WhenTheWaitExpires()
    {
        // The git machine is held for load (0.95 > 0.8) only temporarily, so the archive failure must not end the job at 70.
        var (runner, lines) = ArchiveFailureRig(_ => { }, new InvalidOperationException("Cannot build an archive: rule y-rule."), gitLoad: 0.95);
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(75, r.ExitCode);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("No eligible machine", r.Message, StringComparison.Ordinal);
        Assert.Contains("y-rule", r.Message, StringComparison.Ordinal);
        Assert.Equal(1, lines.Count(l => l.Contains("y-rule", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ArchiveBuildFailureRunsOnTheHeldGitMachineOnceItBecomesFree()
    {
        var config = T.Config();
        config.Machines[0].Transfer = Config.TransferMode.Archive;
        config.Machines[1].AllowedHours = null;
        var busy = true;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
                : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.01 : busy ? 0.95 : 0.5)) : new SshResult(0, "ran"),
        };
        var payloads = new FakePayloads("") { ArchiveFailure = new InvalidOperationException("Cannot build an archive: rule w-rule.") };
        var lines = new List<string>();
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), lines.Add,
            (_, _) =>
            {
                busy = false;
                return Task.CompletedTask;
            });
        var r = await runner.RunAsync(new JobRequest(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-02", r.Machine);
        Assert.Equal(1, lines.Count(l => l.Contains("w-rule", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task ArchiveBuildIoFailureIsAnInfraErrorNotACrash(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type, "disk said no")!;
        var (runner, _) = ArchiveFailureRig(c => c.Machines[1].Transfer = Config.TransferMode.Archive, ex);
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Equal(70, r.ExitCode);
        Assert.Contains("Could not build the archive: disk said no", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveFailureIsPrintedOnceAndAppearsInTheNoEligibleMachineSummary()
    {
        // The git machine is eligible but its slots are full, so the run ends 75 and the summary carries the archive reason.
        var (runner, lines) = ArchiveFailureRig(_ => { }, new InvalidOperationException("Cannot build an archive: rule z-rule."), "SLOT=FULL");
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(75, r.ExitCode);
        Assert.Contains("z-rule", r.Message, StringComparison.Ordinal);
        Assert.Equal(1, lines.Count(l => l.Contains("z-rule", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RemoteControlCharactersNeverReachTheTerminalOrTheLog()
    {
        // The real reader splits on newlines and strips CRLF, so a hostile line carries ESC/OSC/CSI/DEL, a backspace and a bidi override.
        const char Esc = (char)0x1b, Bel = (char)0x07, Csi = (char)0x9b, Del = (char)0x7f, Bs = (char)0x08, Rlo = (char)0x202e, Tab = (char)0x09;
        var hostile = $"a{Esc}]0;pwned{Bel}b{Esc}[2Jc{Csi}d{Del}e{Bs}f{Rlo}g{Tab}h";
        var (runner, _, _, log) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
            : DecodedContains(m, cmd, "LASTEXITCODE") || cmd.Contains("setsid -w", StringComparison.Ordinal) ? new SshResult(0, hostile)
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        var safe = $"a?]0;pwned?b?[2Jc?d?e?f?g{Tab}h";
        Assert.Contains(safe, _output);
        Assert.Contains(safe, File.ReadAllText(log.LogPathFor(r.JobId)), StringComparison.Ordinal);
        Assert.DoesNotContain(_output, l => l.Contains(Esc, StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobPastItsTimeLimitIsReportedAsTimedOutWithExit124()
    {
        var (runner, _, _, log) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
            : DecodedContains(m, cmd, "LASTEXITCODE") || cmd.Contains("setsid -w", StringComparison.Ordinal) ? new SshResult(124, "[lanq] timed out", TimedOut: true)
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet") with { Timeout = TimeSpan.FromMinutes(5) }, CancellationToken.None);

        Assert.Equal(JobStatus.TimedOut, r.Status);
        Assert.Equal(124, r.CliExitCode);
        Assert.Equal("timed out after 5 min", r.Message);
        Assert.DoesNotContain("SSH", r.Message, StringComparison.Ordinal);
        Assert.Equal("TimedOut", Assert.Single(log.ReadAll()).Status);
    }

    [Fact]
    public async Task TheRequestedTimeoutIsPassedToTheJobAndDefaultsToFourHours()
    {
        var timeouts = new List<TimeSpan>();
        var ssh = new RecordingSsh(timeouts);
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var runner = new JobRunner(config, ssh, new FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });

        await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet") with { Timeout = TimeSpan.FromMinutes(7) }, CancellationToken.None);
        Assert.Contains(TimeSpan.FromMinutes(7), timeouts);
        timeouts.Clear();
        await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Contains(TimeSpan.FromHours(4), timeouts);
    }

    private sealed class RecordingSsh(List<TimeSpan> timeouts) : ISshClient
    {
        public Task<SshResult> RunAsync(Config.Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            timeouts.Add(timeout);
            return Task.FromResult(
                DecodedContains(machine, remoteCommand, "SLOT=") ? new SshResult(0, "SLOT=1")
                : DecodedContains(machine, remoteCommand, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
                : new SshResult(0, ""));
        }

        public Task<SshResult> UploadAsync(Config.Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new SshResult(0, ""));

        /// <summary>A split run (the fetch) goes through RunAsync with stdout as the line callback, so this fake serves --fetch runs too.</summary>
        public Task<SshResult> RunSplitAsync(
            Config.Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
            RunAsync(machine, remoteCommand, onStdout, timeout, ct);
    }

    [Fact]
    public async Task MachinesAreProbedConcurrentlyBeforeScheduling()
    {
        var ssh = new GatedProbeSsh();
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var runner = new JobRunner(config, ssh, new FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-02", r.Machine); // ranking still uses each machine's own result
        Assert.Equal(["build-02", "build-01"], ssh.Finished);
    }

    [Fact]
    public async Task ALineArrivingAfterTheJobEndedIsDroppedNotACrash()
    {
        var ssh = new LateLineSsh();
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var runner = new JobRunner(config, ssh, new FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _output.Add);

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        ssh.Release.SetResult();
        await ssh.LateCall!;

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Null(ssh.LateError);
        Assert.DoesNotContain("late", _output);
    }

    /// <summary>Calls the job's line callback once more after the job has already returned, as a slow pipe reader could.</summary>
    private sealed class LateLineSsh : ISshClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? LateCall { get; private set; }

        public Exception? LateError { get; private set; }

        public Task<SshResult> RunAsync(Config.Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            if (DecodedContains(machine, remoteCommand, "SLOT="))
            {
                return Task.FromResult(new SshResult(0, "SLOT=1"));
            }

            if (DecodedContains(machine, remoteCommand, "disk_free_gb"))
            {
                return Task.FromResult(new SshResult(0, Healthy(0.1)));
            }

            if (onLine is not null && (DecodedContains(machine, remoteCommand, "LASTEXITCODE") || remoteCommand.Contains("setsid -w", StringComparison.Ordinal)))
            {
                LateCall = Task.Run(async () =>
                {
                    await Release.Task;
                    try
                    {
                        onLine("late");
                    }
                    catch (Exception ex)
                    {
                        LateError = ex;
                    }
                });
            }

            return Task.FromResult(new SshResult(0, ""));
        }

        public Task<SshResult> UploadAsync(Config.Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new SshResult(0, ""));

        /// <summary>A split run (the fetch) goes through RunAsync with stdout as the line callback, so this fake serves --fetch runs too.</summary>
        public Task<SshResult> RunSplitAsync(
            Config.Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
            RunAsync(machine, remoteCommand, onStdout, timeout, ct);
    }

    [Fact]
    public async Task CmdMetacharacterArgumentSkipsWindowsWorkersAndRunsOnLinux()
    {
        var (runner, ssh, _, _) = Make();
        var r = await runner.RunAsync(Req(["npm", "test", "&calc"]), CancellationToken.None); // build-02 (Windows) is less loaded

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-01", r.Machine);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "build-02" && DecodedContains(T.Windows(), c.Command, "SLOT="));
    }

    [Fact]
    public async Task CmdMetacharacterArgumentWithOnlyWindowsWorkersIsRejectedBeforeAnyNetworkWork()
    {
        var (_, ssh, payloads, _) = Make();
        var config = T.Config();
        config.Machines.RemoveAt(0); // only the Windows machine is left
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { });

        var r = await runner.RunAsync(Req(["npm", "test", "&calc"]), CancellationToken.None);

        Assert.Equal(JobStatus.Rejected, r.Status);
        Assert.Equal(2, r.CliExitCode);
        Assert.Contains("cmd.exe", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task CmdMetacharacterArgumentWaitingForBusyLinuxShowsTheWindowsHoldReason()
    {
        var (runner, _, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=FULL")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(0.1))
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["npm", "test", "&calc"]), CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("build-02: " + Scheduling.Scheduler.WindowsUnsafeHold, r.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Windows worker found that the program given | resolves to a .cmd there: its wrapper started nothing and said so. That is Rejected
    /// (exit 2, windows_args) naming what it resolved to, the marker line is not shown as job output, and nothing is fetched.
    /// </summary>
    [Fact]
    public async Task AWindowsWorkerRefusingANonExeProgramIsRejected()
    {
        var output = new List<string>();
        var (runner, ssh, _, _) = Make((m, cmd) =>
        {
            if (DecodedContains(m, cmd, "SLOT="))
            {
                return new SshResult(0, "SLOT=1");
            }

            if (DecodedContains(m, cmd, "disk_free_gb"))
            {
                return new SshResult(0, Healthy(m.Name == "build-01" ? 0.9 : 0.1)); // build-01 (linux) over the load threshold
            }

            if (DecodedContains(m, cmd, "LANQ_WINARGS="))
            {
                return new SshResult(2, $"{T.WindowsArgsMarkerOf(cmd)}C:\\tools\\dotnet.cmd\n{T.ExitMarkerOf(cmd)}2");
            }

            return new SshResult(0, "");
        });
        var r = await runner.RunAsync(Req(["dotnet", "test", "--filter", "Category=Unit|Category=Arch"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal((JobStatus.Rejected, 2, RejectKind.WindowsArgs, "build-02"), (r.Status, r.CliExitCode, r.RejectKind, r.Machine));
        Assert.Contains("'dotnet' resolves to C:\\tools\\dotnet.cmd there, not a native .exe", r.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was started", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_output, l => l.Contains("LANQ_WINARGS=", StringComparison.Ordinal));
        Assert.Contains(ssh.Calls, c => c.Machine == "build-02" && DecodedContains(T.Windows(), c.Command, "/locks/slot-")); // the slot is still released
    }

    /// <summary>A refusal line from a job that then exits with anything but 2 (its own output, say) is ordinary output, never a refusal.</summary>
    [Fact]
    public async Task ARefusalLineWithoutExitTwoIsOrdinaryOutput()
    {
        var (runner, _, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.9 : 0.1))
            : DecodedContains(m, cmd, "LANQ_WINARGS=") ? new SshResult(1, $"{T.WindowsArgsMarkerOf(cmd)}x\n{T.ExitMarkerOf(cmd)}1")
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test", "--filter", "A|B"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal((JobStatus.Failed, 1, (RejectKind?)null), (r.Status, r.CliExitCode, r.RejectKind));
    }

    /// <summary>
    /// The job itself prints the wrapper's refusal and exit lines (it can know its job id: LANQ_JOB) and exits 2. They lack this run's nonce,
    /// which only the wrapper's script holds, so they are the job's output: Failed with 2, never a Rejected WindowsArgs refusal.
    /// </summary>
    [Fact]
    public async Task AJobPrintingTheWrappersLinesItselfIsOrdinaryOutput()
    {
        var (runner, _, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.9 : 0.1))
            : DecodedContains(m, cmd, "LANQ_WINARGS=") && JobIdOf(m, cmd) is { } job
                ? new SshResult(2, $"LANQ_WINARGS={job}:C:\\tools\\dotnet.cmd\nLANQ_EXIT={job}:2\n{T.ExitMarkerOf(cmd)}2")
            : new SshResult(0, ""));
        var r = await runner.RunAsync(Req(["dotnet", "test", "--filter", "A|B"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal((JobStatus.Failed, 2, (RejectKind?)null), (r.Status, r.CliExitCode, r.RejectKind));
        Assert.Contains(_output, l => l.Contains("LANQ_WINARGS=", StringComparison.Ordinal));
        Assert.Contains(_output, l => l.Contains("LANQ_EXIT=", StringComparison.Ordinal)); // the job's own line, shown as it printed it
    }

    /// <summary>The nonce is in the wrapper's own marker lines only: not in the job's environment (Linux env prefix, Windows $env: lines).</summary>
    [Fact]
    public void TheMarkerNonceIsOnlyInTheWrappersOwnLines()
    {
        const string Nonce = "0123456789abcdef";
        var linux = RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], TimeSpan.FromMinutes(5), nonce: Nonce);
        Assert.Contains("LANQ_EXIT=j1:" + Nonce + ":", linux, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(linux, Nonce));
        Assert.DoesNotContain(Nonce, RemoteScripts.JobEnvironmentPosix(T.Linux(), "j1"), StringComparison.Ordinal);

        var windows = RemoteScripts.Execute(T.Windows(), "j1", ".", ["dotnet", "test", "--filter", "A|B"], TimeSpan.FromMinutes(5), nonce: Nonce);
        Assert.Contains("LANQ_WINARGS=j1:" + Nonce + ":", windows, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(windows, Nonce).Count); // the refusal line, its exit line, the final exit line
        Assert.DoesNotContain(windows.Split('\n'), l => l.Contains("$env:", StringComparison.Ordinal) && l.Contains(Nonce, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j1", ".", ["npm", "test"], nonce: "not-hex"));
    }

    private static string? JobIdOf(Config.Machine m, string cmd)
    {
        var text = m.Os == Config.MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;
        var at = text.IndexOf("'LANQ_EXIT=", StringComparison.Ordinal);
        return at < 0 ? null : text[(at + "'LANQ_EXIT=".Length)..text.IndexOf(':', at)];
    }

    [Theory]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x200B)]
    [InlineData(0x200F)]
    [InlineData(0xFEFF)]
    [InlineData(0x061C)] // Arabic letter mark
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0x2060)] // word joiner
    [InlineData(0x2061)]
    [InlineData(0x2062)]
    [InlineData(0x2063)]
    [InlineData(0x2064)]
    public void BidiAndZeroWidthCharactersAreReplaced(int codePoint) =>
        Assert.Equal("a?b", JobRunner.SafeLine("a" + (char)codePoint + "b"));

    [Fact]
    public void OrdinaryUnicodeIsKept()
    {
        var text = "caf" + (char)0xe9 + " " + char.ConvertFromUtf32(0x1F600) + " " + (char)0x4e2d + (char)0x09 + "ok";
        Assert.Equal(text, JobRunner.SafeLine(text));
    }

    private (JobRunner Runner, FakeSsh Ssh) WithMemory(string build01Mem, string build02Mem)
    {
        var (runner, ssh, _, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
            : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.5 : 0.1) + (char)10 + (m.Name == "build-01" ? build01Mem : build02Mem))
            : new SshResult(0, "ran"));
        return (runner, ssh);
    }

    [Fact]
    public async Task MemOptionSkipsMachinesWithLessAvailableMemory()
    {
        var (runner, _) = WithMemory("mem_free_gb=8", "mem_free_gb=2");
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet") with { MinMemGb = 4 }, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-01", r.Machine); // build-02 is less loaded but has only 2 GB
    }

    [Fact]
    public async Task MemOptionDoesNotTrustAMachineThatCannotReportMemory()
    {
        var (runner, _) = WithMemory("", "mem_free_gb=2");
        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet") with { MinMemGb = 4 }, CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("build-01: free memory unknown", r.Message, StringComparison.Ordinal);
        Assert.Contains("build-02: 2.0 GB free memory, --mem needs 4.0 GB", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutMemOptionUnknownMemoryIsNoObstacle()
    {
        var (runner, _) = WithMemory("", "");
        Assert.Equal(JobStatus.Completed, (await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None)).Status);
    }

    private static SshResult HealthyFleet(Config.Machine m, string cmd) =>
        DecodedContains(m, cmd, "SLOT=") ? new SshResult(0, "SLOT=1")
        : DecodedContains(m, cmd, "disk_free_gb") ? new SshResult(0, Healthy(m.Name == "build-01" ? 0.5 : 0.1))
        : new SshResult(0, "ran");

    [Fact]
    public async Task FirstGitJobSendsAFullBundleRecordsTheBaseAndTheNextJobAsksForAnIncrementalOne()
    {
        var (runner, _, payloads, _) = Make();
        var first = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, first.Status);
        Assert.Equal(payloads.Head, _bases.Get("build-02", T.RepoId));

        var second = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, second.Status);
        Assert.Equal([null, payloads.Head], payloads.BundleBases);
        Assert.Null(_bases.Get("build-01", T.RepoId)); // bases are per machine
    }

    [Fact]
    public async Task IncrementalBundleTheWorkerCannotUseIsReplacedByAFullBundleOnce()
    {
        _bases.Set("build-02", T.RepoId, new string('1', 40));
        var prepares = 0;
        var (runner, ssh, payloads, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "bundle verify") && ++prepares == 1 ? new SshResult(3, RemoteScripts.BundleNotUsable) : HealthyFleet(m, cmd));

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(2, prepares);
        Assert.Equal([new string('1', 40), null], payloads.BundleBases);
        Assert.Equal(2, ssh.Uploads.Count);
        Assert.Single(_output, l => l.Contains("sending a full bundle", StringComparison.Ordinal));
        Assert.Equal(payloads.Head, _bases.Get("build-02", T.RepoId));
    }

    [Fact]
    public async Task RefusedFullBundleIsNotRetriedAndRecordsNoBase()
    {
        var prepares = 0;
        var (runner, ssh, payloads, _) = Make((m, cmd) =>
        {
            var prepare = DecodedContains(m, cmd, "bundle verify");
            prepares += prepare ? 1 : 0;
            return prepare ? new SshResult(3, RemoteScripts.BundleNotUsable) : HealthyFleet(m, cmd);
        });

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Equal(1, prepares);
        Assert.Single(ssh.Uploads);
        Assert.Equal([null], payloads.BundleBases);
        Assert.Null(_bases.Get("build-02", T.RepoId));
    }

    [Fact]
    public async Task BaseIsNotRecordedWhenPrepareFailsForAnotherReason()
    {
        var (runner, _, payloads, _) = Make((m, cmd) =>
            DecodedContains(m, cmd, "bundle verify") ? new SshResult(128, "fatal: disk full") : HealthyFleet(m, cmd));

        await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);
        await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal([null, null], payloads.BundleBases);
        Assert.Null(_bases.Get("build-02", T.RepoId));
    }

    [Fact]
    public async Task BaseThatIsNoLongerUsableLocallyMeansAFullBundleWithoutARetry()
    {
        _bases.Set("build-02", T.RepoId, new string('1', 40));
        var prepares = 0;
        var (runner, ssh, payloads, _) = Make((m, cmd) =>
        {
            prepares += DecodedContains(m, cmd, "bundle verify") ? 1 : 0;
            return HealthyFleet(m, cmd);
        });
        payloads.BaseUsable = false;

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(1, prepares);
        Assert.Single(ssh.Uploads);
        Assert.DoesNotContain(_output, l => l.Contains("full bundle", StringComparison.Ordinal));
    }

    /// <summary>bases.json shared with a parallel lanq (Windows sharing violation): a failing store is retried, then only costs a full bundle.</summary>
    private sealed class FlakyBaseStore(int failingSets, int failingGets, Exception? error = null) : BaseStore(null)
    {
        private readonly Exception _error = error ?? new IOException("The process cannot access the file because it is being used by another process.");

        public int Sets { get; private set; }

        public int Gets { get; private set; }

        public override string? Get(string machine, string repoId) => ++Gets <= failingGets ? throw _error : base.Get(machine, repoId);

        public override void Set(string machine, string repoId, string sha)
        {
            if (++Sets <= failingSets)
            {
                throw _error;
            }

            base.Set(machine, repoId, sha);
        }
    }

    [Fact]
    public async Task ABaseStoreThatCannotBeWrittenNeverFailsTheJob()
    {
        var store = new FlakyBaseStore(failingSets: 99, failingGets: 0, new UnauthorizedAccessException("Access to the path is denied."));
        _bases = store;
        var (runner, _, _, _) = Make();

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal(3, store.Sets);
        Assert.Single(_output, l => l.Contains("could not record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABriefSharingViolationOnTheBaseStoreIsRetried()
    {
        var store = new FlakyBaseStore(failingSets: 2, failingGets: 2);
        _bases = store;
        var (runner, _, payloads, _) = Make();

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal((3, 3), (store.Sets, store.Gets));
        Assert.Equal(payloads.Head, store.Get("build-02", T.RepoId));
        Assert.DoesNotContain(_output, l => l.Contains("warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABaseStoreThatCannotBeReadMeansAFullBundle()
    {
        var store = new FlakyBaseStore(failingSets: 0, failingGets: 99);
        store.Set("build-02", T.RepoId, new string('1', 40));
        _bases = store;
        var (runner, _, payloads, _) = Make();

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal([null], payloads.BundleBases);
        Assert.Single(_output, l => l.Contains("could not read", StringComparison.Ordinal));
    }

    /// <summary>A git payload without a repository id must not make cleanup throw in its finally (that leaked the slot).</summary>
    [Fact]
    public async Task AGitPayloadWithoutARepositoryIdStillCleansUpTheSlot()
    {
        var (runner, ssh, payloads, _) = Make();
        payloads.NoRepoId = true;

        var r = await runner.RunAsync(Req(["dotnet", "test"], ".", "dotnet"), CancellationToken.None);

        Assert.Equal(JobStatus.InfraError, r.Status);
        Assert.Contains(ssh.Calls, c => DecodedContains(T.Windows(), c.Command, "foreach($n in @(1))"));
    }

    [Fact]
    public void CleanupWithoutARepositoryIdSkipsOnlyTheCacheSteps()
    {
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            var s = RemoteScripts.Cleanup(m, "j1", "1", null);
            Assert.Contains(m.Os == Config.MachineOs.Windows ? "foreach($n in @(1))" : "for n in 1; do", s, StringComparison.Ordinal);
            Assert.Contains("jobs/", s, StringComparison.Ordinal);
            Assert.DoesNotContain("repos/", s, StringComparison.Ordinal);
        }

        Assert.Throws<ArgumentException>(() => RemoteScripts.Cleanup(T.Linux(), "j1", "1", "../../etc"));
    }

    [Fact]
    public void BaseStorePersistsAtomicallyAndTreatsAnUnreadableFileAsEmpty()
    {
        var file = Path.Combine(_dir, "state", "bases.json");
        var sha = new string('a', 40);
        new BaseStore(file).Set("m1", T.RepoId, sha);
        new BaseStore(file).Set("m2", T.RepoId, new string('b', 64));

        var reread = new BaseStore(file);
        Assert.Equal(sha, reread.Get("m1", T.RepoId));
        Assert.Equal(new string('b', 64), reread.Get("m2", T.RepoId));
        Assert.Null(reread.Get("m1", "fedcba9876543210"));
        Assert.Equal(["bases.json"], Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName));

        File.WriteAllText(file, "{ not json");
        Assert.Null(new BaseStore(file).Get("m1", T.RepoId));
        File.WriteAllText(file, "{\"m1\":{\"" + T.RepoId + "\":\"--upload-pack=x\"}}");
        Assert.Null(new BaseStore(file).Get("m1", T.RepoId));
    }
}
