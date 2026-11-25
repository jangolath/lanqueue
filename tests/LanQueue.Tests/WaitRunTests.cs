using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Phase 1 review M2, M4 and M5: the wait budgets and the memory cap through the real runner paths (a single job, a shard set and a plan),
/// not only at the WaitClock and script-builder level.
/// </summary>
public sealed class WaitRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-waitrun-" + Guid.NewGuid().ToString("N"));

    public WaitRunTests() => Directory.CreateDirectory(Path.Combine(_dir, ".git"));

    public void Dispose() => TestTree.Delete(_dir);

    private const string Fleet = """
        policy:
          allowed_commands: ["dotnet test"]
          allowed_directories: ["."]
        machines:
          - name: big-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [dotnet]
            max_jobs: 4
            min_free_mem_gb: 2
            job_mem_gb: 9
        """;

    private static string Probe(bool paused = false) =>
        $"load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\nmem_total_gb=31.2\nmem_free_gb=30\npaused={(paused ? 1 : 0)}";

    private JobRunner Runner(FakeSsh ssh) =>
        new(ConfigLoader.Parse(Fleet), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")),
            Path.Combine(_dir, "tmp"), _ => { }, delay: (_, _) => Task.CompletedTask);

    private JobRequest Req(int eligible, int slot) =>
        new(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.FromSeconds(Math.Max(eligible, slot))) { Budgets = new WaitBudgets(TimeSpan.FromSeconds(eligible), TimeSpan.FromSeconds(slot)) };

    private static async Task<T> Guarded<T>(Task<T> run)
    {
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(done == run, "the run did not end on the eligible budget (it waited for the slot budget)");
        return await run;
    }

    [Fact]
    public async Task TheMemoryCapReachesTheClaimOfAJobAndOfShards()
    {
        // M5: 31.2 GB total - 2 GB floor covers 3 jobs of 9 GB: max_jobs 4 is capped at 3, and the claim script says so.
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Probe()) : new SshResult(0, "ran"),
        };
        var r = await Runner(ssh).RunAsync(Req(0, 0), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Contains(ssh.Calls, c => c.Command.Contains("SLOT=", StringComparison.Ordinal) && c.Command.Contains("-le 3 ]", StringComparison.Ordinal));
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOT=", StringComparison.Ordinal) && c.Command.Contains("-le 4 ]", StringComparison.Ordinal));

        ssh.Calls.Clear();
        var shards = await Runner(ssh).RunShardsAsync(Req(0, 0), new ShardOptions(2), CancellationToken.None);
        Assert.Equal(nameof(JobStatus.Completed), shards.Status);
        Assert.All(ssh.Calls.Where(c => c.Command.Contains("echo \"SLOT=FULL\"", StringComparison.Ordinal)), c => Assert.Contains("-le 3 ]", c.Command, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCappedClaimCountsEveryLiveSlotAndAnUncappedOneDoesNot()
    {
        // M4: slot-3 and slot-4 are held by runs without the cap; a capped claim (3 of max_jobs 4) gets slot-1, counts 3 live slots
        // (not more than 3), keeps it; with slot-2 held too it counts 4 and gives slot-1 back.
        var os = OperatingSystem.IsWindows() ? MachineOs.Windows : MachineOs.Linux;
        var root = Path.Combine(_dir, "wr").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        var m = new Machine { Name = "big-01", Os = os, WorkRoot = root, MaxJobs = 4 };
        var live = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        foreach (var n in new[] { 3, 4 })
        {
            File.WriteAllText(Path.Combine(root, "locks", "slot-" + n), $"other-{n}\n{live}\n");
        }

        var shell = os == MachineOs.Windows ? "windows" : "linux";
        var (_, kept) = Shells.Run(shell, RemoteScripts.AcquireSlot(m, "job-a", TimeSpan.FromMinutes(5), 3));
        Assert.Contains("SLOT=1", kept, StringComparison.Ordinal);
        File.Delete(Path.Combine(root, "locks", "slot-1"));

        File.WriteAllText(Path.Combine(root, "locks", "slot-2"), $"other-2\n{live}\n");
        var (_, full) = Shells.Run(shell, RemoteScripts.AcquireSlot(m, "job-b", TimeSpan.FromMinutes(5), 3));
        Assert.Contains("SLOT=FULL", full, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "locks", "slot-1"))); // given back

        Assert.DoesNotContain("-gt 4", RemoteScripts.AcquireSlot(m, "j", TimeSpan.FromMinutes(5)), StringComparison.Ordinal);
        Assert.DoesNotContain("$c -gt", RemoteScripts.AcquireSlot(m, "j", TimeSpan.FromMinutes(5), 4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShardClaimThatFailsOverSshEndsOnTheEligibleBudget()
    {
        // M2 (design review M4): the probe answers, every claim fails over ssh: an eligible wait, so --wait-eligible 0 ends it at once,
        // not --wait-slot 600.
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(255, "ssh: connect to host: Connection timed out")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Probe()) : new SshResult(0, ""),
        };
        var r = await Guarded(Runner(ssh).RunShardsAsync(Req(0, 600), new ShardOptions(2), CancellationToken.None));
        Assert.Equal(nameof(JobStatus.NoCapacity), r.Status);
        Assert.Equal(WaitKind.Eligible, r.Waited!.Budget);
    }

    [Fact]
    public async Task SplitBudgetsThroughAShardSetReportTheBudget()
    {
        var ssh = new FakeSsh { Handler = (_, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Probe(paused: true)) : new SshResult(0, "") };
        var r = await Guarded(Runner(ssh).RunShardsAsync(Req(0, 600), new ShardOptions(2), CancellationToken.None));
        Assert.Equal(nameof(JobStatus.NoCapacity), r.Status);
        Assert.Equal(WaitKind.Eligible, r.Waited!.Budget);
        Assert.All(r.Shards, s => Assert.Equal(WaitKind.Eligible, s.Waited!.Budget));
    }

    [Fact]
    public async Task SplitBudgetsThroughAPlanReportTheBudget()
    {
        File.WriteAllText(Path.Combine(_dir, "lanq.yml"), "version: 1\njobs:\n  unit: { command: dotnet test, needs: [dotnet] }\n");
        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(home);
        var ssh = new FakeSsh { Handler = (_, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Probe(paused: true)) : new SshResult(0, "") };
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await Guarded(Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--json", "--wait-eligible", "0", "--wait-slot", "600"]), ConfigLoader.Parse(Fleet), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(home, "state")), home, _dir, o, e, CancellationToken.None, (_, _) => Task.CompletedTask));
        Assert.Equal(75, code);
        Assert.Contains("\"waited\":{\"eligibleSeconds\":0,\"slotSeconds\":0,\"budget\":\"eligible\"}", o.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainWait600ThroughTheRunnerEndsWhere012Did(bool shards)
    {
        // p1 L2 / review A-L2: the --wait 600 timeline through Dispatch and ShardRunner on a fake clock. The pool flips between paused
        // (eligible wait) and full (slot wait) on every look; --wait 600 still ends at 600 s of idle time in all, as 0.12 did, and waited
        // splits the 600 s between the two states.
        long now = 0;
        var looks = 0;
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal)
                ? new SshResult(0, Interlocked.Increment(ref looks) % 2 == 1 ? Probe(paused: true) : Probe().Replace("slots_used=0", "slots_used=4", StringComparison.Ordinal))
                : new SshResult(0, ""),
        };
        var runner = new JobRunner(
            ConfigLoader.Parse(Fleet), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")),
            Path.Combine(_dir, "tmp"), _ => { }, delay: (d, _) =>
            {
                Interlocked.Add(ref now, (long)(d.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
                return Task.CompletedTask;
            })
        {
            Timestamp = () => Interlocked.Read(ref now),
        };
        var req = new JobRequest(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.FromSeconds(600)) { WaitGiven = true };
        var waited = shards
            ? (await Guarded(runner.RunShardsAsync(req, new ShardOptions(2), CancellationToken.None))).Waited!
            : (await Guarded(runner.RunAsync(req, CancellationToken.None))).Waited!;
        Assert.Equal(TimeSpan.FromSeconds(600), waited.Eligible + waited.Slot);
        Assert.True(waited.Eligible > TimeSpan.Zero && waited.Slot > TimeSpan.Zero, "both states were charged");
        Assert.Equal(600, System.Diagnostics.Stopwatch.GetElapsedTime(0, now).TotalSeconds, 0.001);
    }

    [Fact]
    public async Task PlainWaitZeroThroughTheRunnerEndsAtTheFirstLookAsIn012()
    {
        // The --wait golden through Dispatch: --wait 0 (both budgets, Legacy) ends at the first look, whatever the state.
        var ssh = new FakeSsh { Handler = (_, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Probe(paused: true)) : new SshResult(0, "") };
        var r = await Guarded(Runner(ssh).RunAsync(new(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.Zero), CancellationToken.None));
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(WaitKind.Eligible, r.Waited!.Budget);
        Assert.Equal(TimeSpan.Zero, r.Waited.Slot);
    }
}
