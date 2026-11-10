using System.Collections.Concurrent;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>A mixed pool for routing tests: two light micro PCs, a medium build box and a heavy one, all Linux with node and dotnet.</summary>
internal static class Routing
{
    public const string Yaml = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npx jest", "dotnet test"]
          allowed_directories: ["."]
        machines:
          - name: micro-01
            os: linux
            ssh: build@10.20.30.21
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 1
          - name: micro-02
            os: linux
            ssh: build@10.20.30.22
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 1
          - name: build-01
            os: linux
            ssh: build@10.20.30.31
            work_root: /srv/lanq
            capabilities: [node, dotnet]
            max_jobs: 2
          - name: big-01
            os: linux
            ssh: build@10.20.30.41
            work_root: /srv/lanq
            capabilities: [node, dotnet, gpu]
            max_jobs: 4
        """;

    public static FleetConfig Config() => ConfigLoader.Parse(Yaml);

    /// <summary>Total memory and cores each machine reports: micro 3.8 GB / 2 (light), build-01 7.8 GB / 4 (medium), big-01 31.9 GB / 8 (heavy).</summary>
    public static (double Total, int Cores) Spec(string name) => name switch
    {
        "build-01" => (7.8, 4),
        "big-01" => (31.9, 8),
        _ => (3.8, 2),
    };

    /// <summary>The probe lines PoolSsh adds for a machine (memory and cores).</summary>
    public static string ProbeExtra(Machine m) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $"mem_free_gb=3\nmem_total_gb={Spec(m.Name).Total}\ncores={Spec(m.Name).Cores}");

    public static MachineHealth Up(string name, double? total, int? cores, int slots = 0) =>
        new(name, true, 0.1, 50, slots, null, null) { MemFreeGb = 3, MemTotalGb = total, Cores = cores };

    public static MachineHealth Down(string name) =>
        new(name, false, 0, 0, 0, "unreachable", "ssh: connect to host x port 22: Connection refused") { HoldKind = HoldKind.Unreachable };

    public static List<(Machine, MachineHealth)> Probed(FleetConfig c) => [.. c.Machines.Select(m => (m, Up(m.Name, Spec(m.Name).Total, Spec(m.Name).Cores)))];
}

/// <summary>--size / --min-size and the machine size: key: routing by machine size, judged like every other rule.</summary>
public sealed class SizeRoutingTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-size-" + Guid.NewGuid().ToString("N"));

    private readonly ConcurrentQueue<string> _output = new();

    public SizeRoutingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (JobRunner Runner, PoolSsh Ssh, RunnerTests.FakePayloads Payloads) Pool(FleetConfig? config = null)
    {
        var ssh = new PoolSsh { ProbeExtra = Routing.ProbeExtra };
        var payloads = new RunnerTests.FakePayloads("");
        var runner = new JobRunner(
            config ?? Routing.Config(), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"),
            _output.Enqueue, delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), bases: new BaseStore(null));
        return (runner, ssh, payloads);
    }

    private JobRequest Jest(SizeRule? size, TimeSpan? wait = null) =>
        new(["node"], ["npx", "jest"], ".", _dir, wait ?? TimeSpan.Zero) { Size = size };

    private static JobNeeds Needs(SizeRule? size) => new(["node"], null, false) { Size = size };

    [Fact]
    public void MinSizeKeepsThatClassAndLarger()
    {
        var assessed = Scheduler.Assess(Routing.Probed(Routing.Config()), Needs(SizeRule.AtLeastText("medium")));

        Assert.Equal(["wrong_size", "wrong_size", null, null], assessed.Select(a => a.Code));
        Assert.All(assessed.Take(2), a => Assert.True(a.Permanent));
        Assert.Equal("size light (probe: 4 GB, 2 cores); --min-size medium", assessed[0].Why);
        Assert.Equal(["big-01", "build-01"], Scheduler.Rank(assessed).Select(a => a.Machine.Name).Order(StringComparer.Ordinal));

        var heavy = SizeRule.AtLeastText("HEAVY");
        Assert.Equal("--min-size heavy", heavy.Text);
        Assert.False(heavy.Allows(SizeClass.Medium));
        Assert.True(heavy.Allows(SizeClass.Heavy));
        Assert.False(SizeRule.AtLeastText("light").Allows(SizeClass.Unknown));
        Assert.Throws<ArgumentException>(() => SizeRule.AtLeastText("unknown"));
        Assert.Throws<ArgumentException>(() => SizeRule.AtLeastText("16"));
        Assert.Throws<ArgumentException>(() => SizeRule.AtLeastText("light,heavy"));
    }

    [Fact]
    public void SizeListKeepsOnlyTheNamedClasses()
    {
        var rule = SizeRule.AnyOfText(" Heavy, light ,light");
        Assert.Equal("--size light,heavy", rule.Text);
        var assessed = Scheduler.Assess(Routing.Probed(Routing.Config()), Needs(rule));

        Assert.Equal([null, null, "wrong_size", null], assessed.Select(a => a.Code));
        Assert.Equal("size medium (probe: 8 GB, 4 cores); --size light,heavy", assessed[2].Why);

        Assert.Throws<ArgumentException>(() => SizeRule.AnyOfText("light,unknown"));
        Assert.Throws<ArgumentException>(() => SizeRule.AnyOfText(" , "));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--size", "huge", "--", "npx", "jest"]));
        Assert.Equal("--size light,heavy", ParsedArgs.Parse(["run", "--size", "heavy,LIGHT", "--", "npx", "jest"]).Size!.Text);
        Assert.Equal("--min-size medium", ParsedArgs.Parse(["health", "--min-size", "Medium"]).Size!.Text);
    }

    /// <summary>A 16 GB laptop someone works on probes heavy; its owner declared it medium, and the declared size is what routing uses.</summary>
    [Fact]
    public void DeclaredSizeWinsOverTheProbe()
    {
        var laptop = ConfigLoader.Parse(Routing.Yaml.Replace("  - name: big-01", "  - name: laptop-01\n    size: medium\n    os: linux\n    ssh: build@10.20.30.51\n    work_root: /srv/lanq\n    capabilities: [node]\n  - name: big-01", StringComparison.Ordinal))
            .Machines.Single(m => m.Name == "laptop-01");
        var probed = Routing.Up("laptop-01", 15.8, 8);
        Assert.Equal(SizeClass.Heavy, MachineSize.Classify(15.8, 8));
        Assert.Equal(SizeClass.Medium, MachineSize.Effective(laptop, 15.8, 8));

        var heavy = Scheduler.Assess([(laptop, probed)], Needs(SizeRule.AtLeastText("heavy")))[0];
        Assert.Equal(("wrong_size", true), (heavy.Code, heavy.Permanent));
        Assert.Equal("size medium (config); --min-size heavy", heavy.Why);
        Assert.True(Scheduler.Assess([(laptop, probed)], Needs(SizeRule.AnyOfText("medium")))[0].Eligible);

        // Declared is known without a probe: judged even while the machine does not answer.
        var down = Scheduler.Assess([(laptop, Routing.Down("laptop-01"))], Needs(SizeRule.AtLeastText("heavy")))[0];
        Assert.Equal(("wrong_size", true), (down.Code, down.Permanent));

        // health shows both: the effective size (config) and what the probe says.
        var row = Cli.HealthRow(laptop, probed);
        Assert.Equal(("medium", "config"), (row.Size, row.SizeSource));
        Assert.StartsWith("medium:", row.SuitedFor, StringComparison.Ordinal);
        Assert.Contains("suited: medium: test shards and builds (size: medium in machines.yml; probe: heavy)", Cli.HealthLine(laptop, probed), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ADeclaredWrongSizeEndsBeforeAnySsh()
    {
        var config = Routing.Config();
        foreach (var m in config.Machines)
        {
            m.Size = "light";
        }

        var (runner, ssh, payloads) = Pool(config);
        var r = await runner.RunAsync(Jest(SizeRule.AtLeastText("heavy"), TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, 75, false), (r.Status, r.CliExitCode, r.Retryable));
        Assert.StartsWith(JobRunner.NeverMessage, r.Message, StringComparison.Ordinal);
        Assert.All(r.Excluded!, x => Assert.Equal(("wrong_size", true), (x.Reason, x.Permanent)));
        Assert.Equal("size light (config); --min-size heavy", r.Excluded![0].Why);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);

        var shards = await runner.RunShardsAsync(Jest(SizeRule.AtLeastText("heavy")), new ShardOptions(3), CancellationToken.None);
        Assert.Equal(("NoCapacity", 75, false), (shards.Status, shards.ExitCode, shards.Retryable));
        Assert.Empty(ssh.Calls);

        var plan = await runner.PlanAsync(Jest(SizeRule.AtLeastText("heavy")), null, CancellationToken.None);
        Assert.Equal(("NoCapacity", false, true), (plan.Status, plan.Retryable, plan.Never));
        Assert.Empty(ssh.Calls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AProbedWrongSizeIsPermanent()
    {
        var config = Routing.Config();
        config.Machines.RemoveAt(3); // no heavy machine left
        var (runner, ssh, _) = Pool(config);

        // A long --wait with a delay that never returns: the run must end at once, not wait.
        var r = await runner.RunAsync(Jest(SizeRule.AtLeastText("heavy"), TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.Equal((JobStatus.NoCapacity, false), (r.Status, r.Retryable));
        Assert.Equal(["wrong_size", "wrong_size", "wrong_size"], r.Excluded!.Select(x => x.Reason));
        Assert.Equal("size medium (probe: 8 GB, 4 cores); --min-size heavy", r.Excluded![2].Why);
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOT=", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreachableMachineWithoutDeclaredSizeStaysRetryable()
    {
        var c = Routing.Config();
        var a = Scheduler.Assess([(c.Machines[3], Routing.Down("big-01"))], Needs(SizeRule.AtLeastText("heavy")))[0];
        Assert.Equal(("unreachable", false), (a.Code, a.Permanent));

        c.Machines[3].Size = "heavy";
        var declared = Scheduler.Assess([(c.Machines[3], Routing.Down("big-01"))], Needs(SizeRule.AtLeastText("heavy")))[0];
        Assert.Equal(("unreachable", false), (declared.Code, declared.Permanent));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NoReportedMemoryIsSizeUnknownAndPermanent()
    {
        var c = Routing.Config();
        var silent = Routing.Up("big-01", null, 8);
        var a = Scheduler.Assess([(c.Machines[3], silent)], Needs(SizeRule.AnyOfText("heavy")))[0];
        Assert.Equal(("size_unknown", true), (a.Code, a.Permanent));
        Assert.Contains("size unknown", a.Why, StringComparison.Ordinal);
        Assert.Equal("unknown", Cli.HealthRow(c.Machines[3], silent).Size);

        // A declared size needs no memory report.
        c.Machines[3].Size = "heavy";
        Assert.True(Scheduler.Assess([(c.Machines[3], silent)], Needs(SizeRule.AnyOfText("heavy")))[0].Eligible);

        // Without --size / --min-size nothing changes for such a machine.
        Assert.True(Scheduler.Assess([(Routing.Config().Machines[3], silent)], Needs(null))[0].Eligible);

        // lanq run: the only machine never reports memory, so waiting cannot help.
        var one = Routing.Config();
        one.Machines.RemoveRange(0, 3);
        var ssh = new PoolSsh { ProbeExtra = _ => "mem_free_gb=3" };
        var runner = new JobRunner(
            one, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), Path.Combine(_dir, "tmp"),
            _output.Enqueue, delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), bases: new BaseStore(null));
        var r = await runner.RunAsync(Jest(SizeRule.AtLeastText("light"), TimeSpan.FromMinutes(5)), CancellationToken.None);
        Assert.Equal((JobStatus.NoCapacity, false), (r.Status, r.Retryable));
        Assert.Equal("size_unknown", Assert.Single(r.Excluded!).Reason);
    }

    /// <summary>
    /// Every combination of declared size, probed memory, reachability and rule: without a rule nothing changes, and with one a machine is
    /// excluded exactly when its effective size (declared, else probed while it answers) is not allowed, in the documented order.
    /// </summary>
    [Fact]
    public void AssessAgreesWithRankOnEveryCombinationWithSize()
    {
        string?[] declared = [null, "light", "medium", "heavy"];
        (double? Total, int? Cores)[] probes = [(null, 4), (3.8, 2), (7.8, 4), (31.9, 8), (31.9, 2)];
        SizeRule?[] rules =
        [
            null, SizeRule.AtLeastText("light"), SizeRule.AtLeastText("medium"), SizeRule.AtLeastText("heavy"), SizeRule.AnyOfText("light"),
            SizeRule.AnyOfText("medium,heavy"), SizeRule.AnyOfText("light,heavy"),
        ];
        var fleet = new List<(Machine, MachineHealth)>();
        var i = 0;
        foreach (var d in declared)
        {
            foreach (var (total, cores) in probes)
            {
                foreach (var up in new[] { true, false })
                {
                    foreach (var slots in new[] { 0, 2 })
                    {
                        var name = "m" + (i++).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                        var m = new Machine { Name = name, Os = MachineOs.Linux, Ssh = "u@" + name, WorkRoot = "/srv/lanq", Capabilities = ["node"], MaxJobs = 2, Size = d };
                        fleet.Add((m, up ? Routing.Up(name, total, cores, slots) : Routing.Down(name)));
                    }
                }
            }
        }

        var plain = Scheduler.Assess(fleet, Needs(null));
        foreach (var rule in rules)
        {
            var assessed = Scheduler.Assess(fleet, Needs(rule));
            Assert.Equal(fleet.Select(f => f.Item1.Name), assessed.Select(a => a.Machine.Name)); // config order kept
            for (var k = 0; k < fleet.Count; k++)
            {
                var (m, h) = fleet[k];
                var a = assessed[k];
                string? expected;
                if (rule is null)
                {
                    expected = plain[k].Code;
                }
                else if (MachineSize.Declared(m) is { } own && !rule.Allows(own))
                {
                    expected = "wrong_size";
                }
                else if (!h.Reachable || MachineSize.Declared(m) is not null)
                {
                    expected = plain[k].Code;
                }
                else
                {
                    var probed = MachineSize.Classify(h.MemTotalGb, h.Cores);
                    expected = probed == SizeClass.Unknown ? "size_unknown" : !rule.Allows(probed) ? "wrong_size" : plain[k].Code;
                }

                Assert.Equal(expected, a.Code);
                Assert.Equal(Eligibility.IsPermanent(a.Reason), a.Permanent);
                Assert.Equal(a.Eligible, a.Why is null);
            }

            // Rank keeps the plain ranking's order, minus the machines the rule excludes.
            Assert.Equal(
                Scheduler.Rank(plain).Select(a => a.Machine.Name).Where(n => assessed.Single(a => a.Machine.Name == n).Eligible),
                Scheduler.Rank(assessed).Select(a => a.Machine.Name));
        }
    }

    [Fact]
    public void WakeSkipsASleeperDeclaredTooSmall()
    {
        var m = Routing.Config().Machines[0];
        m.Wake = new WakeConfig { Mac = "02:00:00:00:00:21" };
        var asleep = Routing.Down("micro-01") with { LooksAsleep = true };
        var heavy = new JobNeeds(["node"], null, false) { Size = SizeRule.AtLeastText("heavy") };

        Assert.True(JobRunner.CouldWakeFor(m, asleep, heavy, archiveFailed: false)); // no size: its size is unknown while it sleeps
        m.Size = "light";
        Assert.False(JobRunner.CouldWakeFor(m, asleep, heavy, archiveFailed: false));
        Assert.True(JobRunner.CouldWakeFor(m, asleep, new JobNeeds(["node"], null, false) { Size = SizeRule.AnyOfText("light") }, archiveFailed: false));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShardsAutoCountsOnlyMachinesOfTheSize()
    {
        var (runner, ssh, _) = Pool();
        var r = await runner.RunShardsAsync(
            new JobRequest(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, TimeSpan.Zero) { Size = SizeRule.AtLeastText("medium") },
            new ShardOptions(null),
            CancellationToken.None);

        Assert.Equal(("Completed", 6), (r.Status, r.Count)); // build-01 2 + big-01 4; the light micro PCs take none
        Assert.All(ssh.Executes, e => Assert.Contains(e.Machine.Name, new[] { "build-01", "big-01" }));
        Assert.Contains(_output, l => l.Contains("free slots: big-01 4, build-01 2", StringComparison.Ordinal) || l.Contains("free slots: build-01 2, big-01 4", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RunMinSizeJsonListsWrongSizeAndIsNotRetryable()
    {
        var config = Routing.Config();
        config.Machines.RemoveAt(3);
        var ssh = new PoolSsh { ProbeExtra = Routing.ProbeExtra };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "node", "--min-size", "heavy", "--", "npx", "jest"]),
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, "state")), _dir, _dir, o, e,
            CancellationToken.None);

        Assert.Equal(75, code);
        using var doc = JsonDocument.Parse(o.ToString());
        var root = doc.RootElement;
        Assert.Equal("NoCapacity", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
        Assert.All(root.GetProperty("excluded").EnumerateArray(), x => Assert.Equal("wrong_size", x.GetProperty("reason").GetString()));
        var health = root.GetProperty("nextSteps").EnumerateArray().Single(s => s.GetProperty("action").GetString() == "run_health");
        Assert.Equal(["lanq", "health", "--needs", "node", "--min-size", "heavy", "--json"], health.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.StartsWith("[lanq] NoCapacity: No eligible machine", e.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1], StringComparison.Ordinal);
    }
}
