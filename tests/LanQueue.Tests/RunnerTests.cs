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

    private readonly BaseStore _bases = new(null);

    private sealed class FakePayloads(string diff, params string[] files) : IPayloadBuilder
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

            return Task.FromResult(new JobPayload(Sha, files, diff, archive, RepoId: kinds.HasFlag(PayloadKinds.Bundle) ? T.RepoId : null, Head: Head, CommittedFiles: Committed, CommittedConfigs: Configs));
        }

        public string Sha { get; set; } = "abc";

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
        Assert.Contains("build-02: arguments not safe for Windows cmd shims", r.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x200B)]
    [InlineData(0x200F)]
    [InlineData(0xFEFF)]
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
