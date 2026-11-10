using System.Collections.Concurrent;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>--on m[,m]: a job (or lanq health) narrowed to the named machines; every other rule still applies.</summary>
public sealed class PinTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private static readonly DateTimeOffset Night = new(2026, 1, 5, 23, 30, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-pin-" + Guid.NewGuid().ToString("N"));

    private readonly ConcurrentQueue<string> _output = new();

    public PinTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobLog Log => new(Path.Combine(_dir, "state"));

    /// <param name="delay">The wait between looks (default: never returns, so a run that would wait hangs the test instead of passing).</param>
    private (JobRunner Runner, PoolSsh Ssh, RunnerTests.FakePayloads Payloads) Pool(FleetConfig? config = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var ssh = new PoolSsh { ProbeExtra = Routing.ProbeExtra };
        var payloads = new RunnerTests.FakePayloads("");
        var runner = new JobRunner(
            config ?? Routing.Config(), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"),
            _output.Enqueue, delay: delay ?? ((_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)), bases: new BaseStore(null));
        return (runner, ssh, payloads);
    }

    private JobRequest Jest(IReadOnlyList<string>? on, TimeSpan? wait = null, params string[] needs) =>
        new(needs.Length > 0 ? needs : ["node"], ["npx", "jest"], ".", _dir, wait ?? TimeSpan.Zero) { On = on };

    private static HashSet<string> Touched(PoolSsh ssh) => [.. ssh.Calls.Select(c => c.Machine)];

    [Fact(Timeout = TestTimeout)]
    public async Task OnProbesOnlyTheNamedMachines()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunAsync(Jest(["big-01"]), CancellationToken.None);

        Assert.Equal((JobStatus.Completed, "big-01"), (r.Status, r.Machine));
        Assert.Equal(["big-01"], Touched(ssh));

        // Two named: the usual ranking among them only (micro-02 has the lower name, both idle).
        var (two, twoSsh, _) = Pool();
        var r2 = await two.RunAsync(Jest(["micro-02", "build-01"]), CancellationToken.None);
        Assert.Contains(r2.Machine, new[] { "micro-02", "build-01" });
        Assert.Equal(["build-01", "micro-02"], Touched(twoSsh).Order(StringComparer.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnUnknownMachineIsRejectedBeforeAnyNetwork()
    {
        var ssh = new PoolSsh { ProbeExtra = Routing.ProbeExtra };
        var payloads = new RunnerTests.FakePayloads("");
        foreach (var extra in new[] { Array.Empty<string>(), ["--shards", "2"], ["--dry-run"] })
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await Cli.Run(
                ParsedArgs.Parse(["run", "--json", "--on", "big-01,big-02", .. extra, "--", "npx", "jest"]),
                Routing.Config(), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, _dir, _dir, o, e, CancellationToken.None);

            Assert.Equal(2, code);
            using var doc = JsonDocument.Parse(o.ToString());
            var root = doc.RootElement;
            Assert.Equal("Rejected", root.GetProperty("status").GetString());
            Assert.Equal(
                "--on names a machine that is not in the config: big-02. Configured: micro-01, micro-02, build-01, big-01.",
                root.GetProperty("message").GetString()!.Replace("shard 1: ", "", StringComparison.Ordinal));
            var step = Assert.Single(root.GetProperty("nextSteps").EnumerateArray());
            Assert.Equal("fix_command", step.GetProperty("action").GetString());
            Assert.StartsWith("[lanq] Rejected: ", e.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1], StringComparison.Ordinal);
        }

        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnMatchesIgnoringCaseAndKeepsConfigSpelling()
    {
        var config = Routing.Config();
        Assert.Equal(["big-01", "micro-01"], Pinning.Resolve(config, ["BIG-01", "Micro-01", "big-01"]).Names);
        Assert.Equal(["micro-01", "big-01"], Pinning.Candidates(config, ["BIG-01", "Micro-01"]).Select(m => m.Name)); // config order
        Assert.Null(Pinning.Resolve(config, null).Names);
        Assert.Equal(["Big-01", "x"], ParsedArgs.Parse(["run", "--on", "Big-01, x", "--", "npx", "jest"]).On);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--on", " , ", "--", "npx", "jest"]));

        var (runner, ssh, _) = Pool();
        var r = await runner.RunAsync(Jest(["BIG-01"]), CancellationToken.None);
        Assert.Equal((JobStatus.Completed, "big-01"), (r.Status, r.Machine));
        Assert.Equal(["big-01"], Touched(ssh));
    }

    /// <summary>The pinned machine is busy: retryable, and the run waits under --wait for it (never for another machine).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task PinnedBusyIsRetryableAndWaits()
    {
        var looks = 0;
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
                cmd.Contains("disk_free_gb", StringComparison.Ordinal)
                    ? new SshResult(0, $"load=0.1\ndisk_free_gb=50\nslots_used={(Interlocked.Increment(ref looks) <= 1 ? 4 : 0)}\n{Routing.ProbeExtra(m)}\ntool=ok")
                    : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "ran"),
        };
        var lines = new List<string>();
        var runner = new JobRunner(
            Routing.Config(), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"), lines.Add,
            (_, _) => Task.CompletedTask, bases: new BaseStore(null));

        var none = await runner.RunAsync(Jest(["big-01"]), CancellationToken.None);
        Assert.Equal((JobStatus.NoCapacity, true), (none.Status, none.Retryable));
        Assert.Equal(("big-01", "busy"), (Assert.Single(none.Excluded!).Machine, none.Excluded![0].Reason));

        looks = 0;
        var waited = await runner.RunAsync(Jest(["big-01"], TimeSpan.FromMinutes(2)), CancellationToken.None);
        Assert.Equal((JobStatus.Completed, "big-01"), (waited.Status, waited.Machine));
        Assert.Contains("[lanq] no eligible machine; waiting 15s", lines);
        Assert.All(ssh.Calls, c => Assert.Equal("big-01", c.Machine));

        // The steps: wait (the same run with --wait 120, keeping --on) and health for the pinned machine.
        var p = ParsedArgs.Parse(["run", "--json", "--on", "big-01", "--needs", "node", "--", "npx", "jest"]);
        var steps = NextSteps.ForRun(p, NextSteps.Outcome(none));
        Assert.Equal(["wait", "run_health"], steps.Select(s => s.Action));
        Assert.Equal(["lanq", "run", "--json", "--on", "big-01", "--needs", "node", "--wait", "120", "--no-wake", "--", "npx", "jest"], steps[0].Argv);
        Assert.Equal(["lanq", "health", "--on", "big-01", "--needs", "node", "--json"], steps[1].Argv);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("paused=1", "paused")]
    [InlineData("hours", "outside_hours")]
    [InlineData("disk", "low_disk")]
    public async Task PinnedOutsideHoursOrPausedIsRetryable(string hold, string reason)
    {
        var config = Routing.Config();
        if (hold == "hours")
        {
            config.Machines[3].AllowedHours = ["01:00-02:00"];
        }

        var clock = new FakeClock(Night);
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal)
                ? new SshResult(0, $"load=0.1\ndisk_free_gb={(hold == "disk" ? 1 : 50)}\nslots_used=0\n{Routing.ProbeExtra(m)}\n{(hold == "paused=1" ? hold : "")}\ntool=ok")
                : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "ran"),
        };
        var runner = new JobRunner(
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, clock), Log, Path.Combine(_dir, "tmp"), _output.Enqueue, (_, _) => Task.CompletedTask, clock,
            new BaseStore(null));

        var r = await runner.RunAsync(Jest(["big-01"]), CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, true), (r.Status, r.Retryable));
        Assert.Equal(reason, Assert.Single(r.Excluded!).Reason);
        Assert.All(ssh.Calls, c => Assert.Equal("big-01", c.Machine));
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOT=", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PinnedLackingCapabilityEndsAtOnceWithoutSsh()
    {
        var (runner, ssh, payloads) = Pool();
        var r = await runner.RunAsync(Jest(["micro-01", "build-01"], TimeSpan.FromMinutes(5), "gpu"), CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, 75, false), (r.Status, r.CliExitCode, r.Retryable));
        Assert.StartsWith("No eligible machine", r.Message, StringComparison.Ordinal);
        Assert.Equal(["micro-01", "build-01"], r.Excluded!.Select(x => x.Machine)); // big-01 has gpu but is not pinned: never listed
        Assert.All(r.Excluded!, x => Assert.Equal("missing_capability", x.Reason));
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);

        // The same job unpinned would run on big-01: --on only narrows.
        Assert.Equal("big-01", (await runner.RunAsync(Jest(null, null, "gpu"), CancellationToken.None)).Machine);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PinNeverBypassesMemOrHolds()
    {
        var (runner, _, _) = Pool();
        var mem = await runner.RunAsync(Jest(["big-01"]) with { MinMemGb = 64 }, CancellationToken.None);
        Assert.Equal((JobStatus.NoCapacity, false), (mem.Status, mem.Retryable));
        Assert.Equal("mem_exceeds_total", Assert.Single(mem.Excluded!).Reason);

        var size = await runner.RunAsync(Jest(["micro-01"]) with { Size = SizeRule.AtLeastText("medium") }, CancellationToken.None);
        Assert.Equal((JobStatus.NoCapacity, false), (size.Status, size.Retryable));
        Assert.Equal("wrong_size", Assert.Single(size.Excluded!).Reason);

        // Windows-unsafe arguments with only a Windows machine pinned: refused as for a fleet of Windows machines.
        var config = Routing.Config();
        config.Machines[0].Os = MachineOs.Windows;
        config.Machines[0].WorkRoot = "D:/lanq";
        var (win, winSsh, _) = Pool(config);
        var unsafeArgs = await win.RunAsync(new JobRequest(["node"], ["npx", "jest", "a&b"], ".", _dir, TimeSpan.Zero) { On = ["micro-01"] }, CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.WindowsArgs), (unsafeArgs.Status, unsafeArgs.RejectKind));
        Assert.Empty(winSsh.Calls);
        Assert.Equal(JobStatus.Completed, (await win.RunAsync(new JobRequest(["node"], ["npx", "jest", "a&b"], ".", _dir, TimeSpan.Zero), CancellationToken.None)).Status);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnlyAPinnedSleeperIsWoken()
    {
        var config = Routing.Config();
        config.Policy.AllowedCommands.Add("dotnet");
        config.Machines[0].Wake = new WakeConfig { Mac = "02:00:00:00:00:21" };
        config.Machines[1].Wake = new WakeConfig { Mac = "02:00:00:00:00:22" };
        var up = new HashSet<string>(StringComparer.Ordinal) { "build-01", "big-01" };
        var clock = new FakeClock(Night);
        var sender = new FakeSender();
        sender.OnSend = packet =>
        {
            var mac = Convert.ToHexString(packet.AsSpan(6, 6));
            up.Add(mac.EndsWith("21", StringComparison.Ordinal) ? "micro-01" : "micro-02");
        };
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => !up.Contains(m.Name) ? new SshResult(255, $"ssh: connect to host {m.Name}.lan port 22: Connection timed out")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, $"load=0.1\ndisk_free_gb=50\nslots_used=0\n{Routing.ProbeExtra(m)}\ntool=ok")
                : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                : new SshResult(0, "ran"),
        };
        var runner = new JobRunner(
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, clock, new WakeService(sender, clock)), Log, Path.Combine(_dir, "tmp"), _output.Enqueue,
            (_, _) => Task.CompletedTask, clock, new BaseStore(null));

        var r = await runner.RunAsync(Jest(["micro-02"], TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.Equal((JobStatus.Completed, "micro-02"), (r.Status, r.Machine));
        Assert.Single(sender.Sent); // micro-01 sorts first by name and sleeps too, but it is not pinned
        Assert.DoesNotContain("micro-01", up);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine is "micro-01" or "build-01" or "big-01");

        // --wait 0 and --no-wake never wake even the pinned sleeper.
        up.Remove("micro-02");
        sender.Sent.Clear();
        Assert.Equal(JobStatus.NoCapacity, (await runner.RunAsync(Jest(["micro-02"]), CancellationToken.None)).Status);
        Assert.Equal(JobStatus.NoCapacity, (await runner.RunAsync(Jest(["micro-02"], TimeSpan.FromMilliseconds(50)) with { NoWake = true }, CancellationToken.None)).Status);
        Assert.Empty(sender.Sent);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExcludedListsOnlyPinnedMachines()
    {
        var (runner, _, _) = Pool();
        var r = await runner.RunAsync(Jest(["micro-02", "micro-01"]) with { Size = SizeRule.AtLeastText("heavy") }, CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(["micro-01", "micro-02"], r.Excluded!.Select(x => x.Machine)); // config order, nobody else
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShardsWithOnSpreadOnlyOverPinnedMachines()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(
            new JobRequest(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, TimeSpan.Zero) { On = ["micro-01", "big-01"] },
            new ShardOptions(null),
            CancellationToken.None);

        Assert.Equal(("Completed", 5), (r.Status, r.Count)); // big-01 4 + micro-01 1
        Assert.All(ssh.Executes, e => Assert.Contains(e.Machine.Name, new[] { "micro-01", "big-01" }));
        Assert.Equal(["big-01", "micro-01"], Touched(ssh).Order(StringComparer.Ordinal));

        var fixedN = await runner.RunShardsAsync(
            new JobRequest(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, TimeSpan.Zero) { On = ["build-01"] }, new ShardOptions(2), CancellationToken.None);
        Assert.Equal("Completed", fixedN.Status);
        Assert.All(fixedN.Shards, s => Assert.Equal("build-01", s.Machine));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunWithOnListsOnlyPinned()
    {
        var ssh = new PoolSsh { ProbeExtra = Routing.ProbeExtra };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.DryRun(
            ParsedArgs.Parse(["run", "--dry-run", "--json", "--on", "big-01,micro-01", "--min-size", "medium", "--", "npx", "jest"]),
            Routing.Config(), ssh, new RunnerTests.FakePayloads(""), Log, _dir, _dir, o, e, CancellationToken.None);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o.ToString());
        var root = doc.RootElement;
        Assert.Equal(("WouldRun", "big-01"), (root.GetProperty("status").GetString(), root.GetProperty("machine").GetString()));
        Assert.Equal(["micro-01", "big-01"], root.GetProperty("machines").EnumerateArray().Select(m => m.GetProperty("machine").GetString()));
        Assert.Equal("wrong_size", root.GetProperty("machines")[0].GetProperty("reason").GetString());
        Assert.Equal(["big-01", "micro-01"], ssh.Calls.Select(c => c.Machine).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("--min-size medium   --on big-01,micro-01 (only these machines)", e.ToString(), StringComparison.Ordinal);
    }
}
