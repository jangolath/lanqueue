using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run --dry-run: the same checks, snapshot scan, probe and choice as lanq run, and nothing sent, claimed, logged or woken.</summary>
public sealed class DryRunTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private static readonly DateTimeOffset Night = new(2026, 1, 5, 23, 30, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-dry-" + Guid.NewGuid().ToString("N"));

    public DryRunTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobLog Log => new(Path.Combine(_dir, "state"));

    private static string Healthy(double load, string extra = "") => $"load={load}\ndisk_free_gb=50\nslots_used=0\ntool=ok" + extra;

    private static string Decoded(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    private static bool IsProbe(Machine m, string cmd) => Decoded(m, cmd).Contains("disk_free_gb", StringComparison.Ordinal);

    /// <summary>build-01 (linux, dotnet+node, load 0.5) and build-02 (windows, dotnet, load 0.1, no hours): every ssh call answered like a healthy worker.</summary>
    private static FakeSsh Fleet(Func<Machine, string>? probe = null) => new()
    {
        Handler = (m, cmd) =>
            Decoded(m, cmd).Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
            : IsProbe(m, cmd) ? new SshResult(0, probe?.Invoke(m) ?? Healthy(m.Name == "build-01" ? 0.5 : 0.1))
            : new SshResult(0, "ran"),
    };

    private static FleetConfig Config()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        return config;
    }

    private JobRunner Runner(FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, HealthProbe? probe = null, TimeProvider? clock = null) =>
        new(config, ssh, payloads, probe ?? new HealthProbe(ssh, clock ?? TimeProvider.System), Log, Path.Combine(_dir, "tmp"), _ => { }, (_, _) => Task.CompletedTask, clock, new BaseStore(null));

    private JobRequest Req(string[] argv, params string[] needs) => new(needs, argv, ".", _dir, TimeSpan.Zero);

    /// <summary>lanq run --dry-run ARGS through Cli.DryRun (the probe and runner it builds itself), over the given config and fakes.</summary>
    private async Task<(int Code, string Stdout, string Stderr)> Cli(FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, TimeProvider? clock, CancellationToken ct, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await LanQueue.Cli.DryRun(ParsedArgs.Parse(["run", "--dry-run", .. args]), config, ssh, payloads, Log, _dir, _dir, o, e, ct, clock);
        return (code, o.ToString(), e.ToString());
    }

    private Task<(int Code, string Stdout, string Stderr)> Cli(FleetConfig config, ISshClient ssh, params string[] args) =>
        Cli(config, ssh, new RunnerTests.FakePayloads(""), null, CancellationToken.None, args);

    private static JsonElement Json(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout); // exactly one JSON value
        return doc.RootElement.Clone();
    }

    private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    /// <summary>A payload builder that runs <paramref name="build"/> for the snapshot (to fail it, or to see the cancellation).</summary>
    private sealed class ScriptedPayloads(Func<CancellationToken, JobPayload> build) : IPayloadBuilder
    {
        public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) => Task.FromResult(build(ct));

        public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => throw new InvalidOperationException("no archive in a dry run");

        public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) => throw new InvalidOperationException("no bundle in a dry run");
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunSendsNothingClaimsNoSlotAndWritesNoLog()
    {
        foreach (var extra in new[] { Array.Empty<string>(), ["--shards", "3"], ["--shards", "auto"] })
        {
            var ssh = new PoolSsh();
            var o = new StringWriter();
            var e = new StringWriter();

            // Through Cli.Run, as lanq run --dry-run gets there: the dry run must turn off before anything a real run does.
            var code = await LanQueue.Cli.Run(
                ParsedArgs.Parse(["run", "--dry-run", "--json", "--wait", "300", "--fetch", "out/**", .. extra, "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}"]),
                ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _dir, _dir, o, e, CancellationToken.None);

            Assert.Equal(0, code);
            var r = Json(o.ToString());
            Assert.Equal(("WouldRun", 0, true), (r.GetProperty("status").GetString(), r.GetProperty("exitCode").GetInt32(), r.GetProperty("dryRun").GetBoolean()));
            Assert.All(ssh.Calls, c => Assert.Contains("disk_free_gb", c.Command, StringComparison.Ordinal)); // probes only: no slot claimed
            Assert.Empty(ssh.Uploads);
            Assert.Empty(ssh.Executes);
            Assert.Equal((0, 0), (ssh.Used("tiny-01"), ssh.Used("big-01")));

            // No jobs.jsonl, no log, no bases.json, no .lanq folder for --fetch, no snapshot file: not one file anywhere under home or the repository.
            Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(_dir, ".lanq")));
            Assert.False(Directory.Exists(Path.Combine(_dir, "state", "logs")));
            Assert.StartsWith("[lanq] WouldRun: would run ", Lines(e.ToString())[^1], StringComparison.Ordinal);
        }
    }

    private static Machine Sleeper(string name) => new()
    {
        Name = name,
        Os = MachineOs.Linux,
        Ssh = $"u@{name}.lan",
        WorkRoot = "/srv/lanq",
        Capabilities = ["dotnet"],
        Wake = new WakeConfig { Mac = "02:00:00:00:00:" + (name == "sleep-1" ? "01" : "02") },
    };

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunNeverWakes()
    {
        var config = Config();
        config.Machines = [Sleeper("sleep-2"), Sleeper("sleep-1")];
        var up = new HashSet<string>(StringComparer.Ordinal);
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => !up.Contains(m.Name) ? new SshResult(255, $"ssh: connect to host {m.Name}.lan port 22: Connection timed out")
                : cmd.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                : cmd.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Healthy(0.1))
                : new SshResult(0, "ran"),
        };
        var clock = new FakeClock(Night);
        var sender = new FakeSender { OnSend = packet => up.Add(packet[11] == 1 ? "sleep-1" : "sleep-2") };
        var waking = new HealthProbe(ssh, clock, new WakeService(sender, clock));

        // Even a runner whose probe could wake (lanq run's) never wakes in a plan: it only asks the rules.
        var plan = await Runner(config, ssh, new RunnerTests.FakePayloads(""), waking, clock).PlanAsync(Req(["dotnet", "test"], "dotnet") with { Wait = TimeSpan.FromSeconds(300) }, null, CancellationToken.None);

        Assert.Equal((nameof(JobStatus.NoCapacity), 75, true), (plan.Status, plan.ExitCode, plan.Retryable));
        Assert.Equal("sleep-1", plan.WouldWake);
        Assert.Empty(sender.Sent);
        Assert.Equal(2, ssh.Calls.Count); // one probe each

        // lanq run itself wakes exactly that machine.
        var run = await Runner(config, ssh, new RunnerTests.FakePayloads(""), waking, clock).RunAsync(Req(["dotnet", "test"], "dotnet") with { Wait = TimeSpan.FromSeconds(300) }, CancellationToken.None);
        Assert.Equal((JobStatus.Completed, "sleep-1"), (run.Status, run.Machine));
        Assert.Single(sender.Sent);
        up.Clear();

        // The CLI builds its probe without a waker: --wait 300 names the sleeper; --wait 0 and --no-wake name none (as lanq run would not wake).
        var json = Json((await Cli(config, ssh, new RunnerTests.FakePayloads(""), clock, CancellationToken.None, "--json", "--wait", "300", "--needs", "dotnet", "--", "dotnet", "test")).Stdout);
        Assert.Equal("sleep-1", json.GetProperty("wouldWake").GetString());
        Assert.Equal(["asleep", "asleep"], json.GetProperty("machines").EnumerateArray().Select(m => m.GetProperty("reason").GetString()));
        var text = await Cli(config, ssh, new RunnerTests.FakePayloads(""), clock, CancellationToken.None, "--wait", "300", "--needs", "dotnet", "--", "dotnet", "test");
        Assert.Equal(75, text.Code);
        Assert.Contains("Would wake: sleep-1 (", text.Stdout, StringComparison.Ordinal);
        foreach (var args in new[] { new[] { "--wait", "0" }, ["--wait", "300", "--no-wake"] })
        {
            var none = Json((await Cli(config, ssh, new RunnerTests.FakePayloads(""), clock, CancellationToken.None, ["--json", .. args, "--needs", "dotnet", "--", "dotnet", "test"])).Stdout);
            Assert.Equal(JsonValueKind.Null, none.GetProperty("wouldWake").ValueKind);
        }

        Assert.Single(sender.Sent); // still only lanq run's packet
        Assert.Empty(up);
    }

    public static TheoryData<string[], string> Refusals => new()
    {
        { ["rm", "-rf", "/"], "is not on policy.allowed_commands" },
        { ["dotnet", "test", "--dangerously-skip-permissions"], "Permission-skipping flag rejected" },
        { ["npm", "test", "--token=ghp_abcdefghijklmnopqrstuvwxyz0123456789"], "possible secrets" },
        { ["npm", "test", "&calc"], "which Windows workers pass only to a native .exe" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task DryRunRefusesWhatRunRefusesBeforeAnyNetwork(string[] argv, string why)
    {
        // Only the Windows machine: arguments unsafe there are refused outright, as lanq run does.
        var config = Config();
        config.Machines = [config.Machines[1]];
        var ssh = Fleet();
        var payloads = new RunnerTests.FakePayloads("");

        var plan = await Runner(config, ssh, payloads).PlanAsync(Req(argv, "dotnet"), null, CancellationToken.None);
        var run = await Runner(config, ssh, payloads).RunAsync(Req(argv, "dotnet"), CancellationToken.None);

        Assert.Equal((nameof(JobStatus.Rejected), 2), (plan.Status, plan.ExitCode));
        Assert.Equal((run.Status.ToString(), run.CliExitCode, run.Message), (plan.Status, plan.ExitCode, plan.Message));
        Assert.Contains(why, plan.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);

        // The same through --shards, with the "shard 1: " lanq run puts in front.
        var shards = await Runner(config, ssh, payloads).PlanAsync(Req(argv, "dotnet"), new ShardOptions(2), CancellationToken.None);
        var shardRun = await Runner(config, ssh, payloads).RunShardsAsync(Req(argv, "dotnet"), new ShardOptions(2), CancellationToken.None);
        Assert.Equal((shardRun.Status, shardRun.ExitCode, shardRun.Message), (shards.Status, shards.ExitCode, shards.Message));
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task DryRunScansUncommittedChangesForSecrets()
    {
        foreach (var payloads in new[] { new RunnerTests.FakePayloads("+aws = AKIAABCDEFGHIJKLMNOP", "src/a.cs"), new RunnerTests.FakePayloads("", ".env") })
        {
            var ssh = Fleet();
            var plan = await Runner(Config(), ssh, payloads).PlanAsync(Req(["dotnet", "test"], "dotnet"), null, CancellationToken.None);
            var run = await Runner(Config(), ssh, payloads).RunAsync(Req(["dotnet", "test"], "dotnet"), CancellationToken.None);

            Assert.Equal((nameof(JobStatus.Rejected), 2), (plan.Status, plan.ExitCode));
            Assert.Equal(run.Message, plan.Message);
            Assert.StartsWith("Refused: possible secrets in payload", plan.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("AKIA", plan.Message, StringComparison.Ordinal);
            Assert.Empty(ssh.Calls);
        }

        // Clean changes: the text says how many uncommitted files the scan covered.
        var (code, stdout, _) = await Cli(Config(), Fleet(), new RunnerTests.FakePayloads("+x", "src/a.cs", "src/b.cs", "tests/c.cs"), null, CancellationToken.None, "--needs", "dotnet", "--", "dotnet", "test");
        Assert.Equal(0, code);
        Assert.Contains("Command:  dotnet test   (allowed by \"dotnet test\"; dir .; secret scan: command line and 3 uncommitted file(s) clean)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunBuildsNoBundleOrArchive()
    {
        var config = Config();
        config.Machines[1].Transfer = TransferMode.Archive; // the chosen machine (lower load) is an archive machine
        var ssh = Fleet();
        var payloads = new RunnerTests.FakePayloads("");

        foreach (var shards in new ShardOptions?[] { null, new(3) })
        {
            var plan = await Runner(config, ssh, payloads).PlanAsync(Req(["dotnet", "test"], "dotnet"), shards, CancellationToken.None);
            Assert.Equal(JobPlan.WouldRun, plan.Status);
        }

        Assert.Equal(2, payloads.Built);
        Assert.Equal(PayloadKinds.None, payloads.LastKinds);
        Assert.Empty(payloads.BundleBases);
        Assert.Equal(0, payloads.ArchivesBuilt);
        Assert.Empty(ssh.Uploads);
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, "build-02")]
    [InlineData(4.0, "build-01")]
    public async Task DryRunPicksTheMachineRunPicks(double? mem, string expected)
    {
        // build-02 is less loaded, but only build-01 has 4 GB free.
        string Probe(Machine m) => Healthy(m.Name == "build-01" ? 0.5 : 0.1, m.Name == "build-01" ? "\nmem_free_gb=8\nmem_total_gb=16" : "\nmem_free_gb=2\nmem_total_gb=8");
        var req = Req(["dotnet", "test"], "dotnet") with { MinMemGb = mem };

        var plan = await Runner(Config(), Fleet(Probe), new RunnerTests.FakePayloads("")).PlanAsync(req, null, CancellationToken.None);
        var run = await Runner(Config(), Fleet(Probe), new RunnerTests.FakePayloads("")).RunAsync(req, CancellationToken.None);

        Assert.Equal(JobStatus.Completed, run.Status);
        Assert.Equal((JobPlan.WouldRun, 0, expected), (plan.Status, plan.ExitCode, plan.Machine));
        Assert.Equal(run.Machine, plan.Machine);
        Assert.Equal("would run on " + expected, plan.Message);
    }

    private const string MixedYaml = """
        policy:
          allowed_commands: ["dotnet test"]
          allowed_directories: [".", "tests"]
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet, linux]
            max_jobs: 2
          - name: micro-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [dotnet, linux]
            max_jobs: 1
          - name: family-pc
            os: linux
            ssh: user@10.20.30.13
            work_root: /srv/lanq
            capabilities: [dotnet, linux]
            allowed_hours: "22:00-07:00"
          - name: win-01
            os: windows
            ssh: user@10.20.30.14
            work_root: D:/lanq
            capabilities: [dotnet, windows]
        """;

    /// <summary>build-01 free (load 0.12), micro-01 busy, family-pc outside its hours (its own clock says noon), win-01 healthy.</summary>
    private static FakeSsh Mixed() => Fleet(m => m.Name switch
    {
        "build-01" => "load=0.12\ndisk_free_gb=50\nslots_used=0\ntool=ok",
        "micro-01" => "load=0.3\ndisk_free_gb=50\nslots_used=1\ntool=ok",
        "family-pc" => "load=0.0\ndisk_free_gb=50\nslots_used=0\ntool=ok\nlocal_time=2026-01-05T12:00:00",
        _ => Healthy(0.1),
    });

    [Fact]
    public async Task DryRunListsEveryExcludedMachineWithItsReason()
    {
        var config = ConfigLoader.Parse(MixedYaml);
        var (code, stdout, stderr) = await Cli(config, Mixed(), "--needs", "dotnet,linux", "--dir", "tests", "--", "dotnet", "test", "-m:1");

        Assert.Equal(0, code);
        Assert.Equal(
            [
                LanQueue.Cli.DryRunHeader,
                "Command:  dotnet test -m:1   (allowed by \"dotnet test\"; dir tests; secret scan: command line and 0 uncommitted file(s) clean)",
                "Needs:    dotnet, linux",
                "Would run on: build-01 (load 0.12, slots free 2/2)",
                "Excluded:",
                "  micro-01   busy (slots 1/1)                                     can change",
                "  family-pc  outside allowed hours                                can change",
                "  win-01     lacks linux (capabilities: dotnet, windows)          never (config)",
                "With --wait 0, lanq run would end with exit 75 at once if nothing were free.",
            ],
            Lines(stdout));
        Assert.Equal("[lanq] WouldRun: would run on build-01", Lines(stderr)[^1]);

        // JSON: every machine in config order, the plan's text on stderr before the status line, which stays last.
        var (jsonCode, json, jsonErr) = await Cli(config, Mixed(), "--json", "--needs", "dotnet,linux", "--", "dotnet", "test");
        Assert.Equal(0, jsonCode);
        var r = Json(json);
        Assert.Equal("build-01", r.GetProperty("machine").GetString());
        Assert.Equal(["build-01"], r.GetProperty("ranked").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(
            [("build-01", true, null, false, 2, 2), ("micro-01", false, "busy", false, 0, 0), ("family-pc", false, "outside_hours", false, 1, 0), ("win-01", false, "missing_capability", true, 1, 0)],
            r.GetProperty("machines").EnumerateArray().Select(m => (
                m.GetProperty("machine").GetString(), m.GetProperty("eligible").GetBoolean(), m.GetProperty("reason").GetString(), m.GetProperty("permanent").GetBoolean(),
                m.GetProperty("freeSlots").GetInt32(), m.GetProperty("jobSlots").GetInt32())));
        Assert.Equal("lacks linux (capabilities: dotnet, windows)", r.GetProperty("machines")[3].GetProperty("why").GetString());
        foreach (var name in new[] { "shardCount", "shardCountAuto", "placement", "wouldWake", "retryable" })
        {
            Assert.Equal(JsonValueKind.Null, r.GetProperty(name).ValueKind);
        }

        Assert.False(r.TryGetProperty("logTail", out _));
        // WouldRun: one step, the same command without --dry-run (--no-wake added: with --wait 0 it could never wake a machine).
        var step = Assert.Single(r.GetProperty("nextSteps").EnumerateArray());
        Assert.Equal("proceed", step.GetProperty("action").GetString());
        Assert.Equal("lanq run --json --needs dotnet,linux --no-wake -- dotnet test", step.GetProperty("command").GetString());
        Assert.True(step.GetProperty("safeToRunUnattended").GetBoolean());
        var errLines = Lines(jsonErr);
        Assert.Equal(LanQueue.Cli.DryRunHeader, errLines[0]);
        Assert.Equal("[lanq] WouldRun: would run on build-01", errLines[^1]);
    }

    private (JobRunner Runner, PoolSsh Ssh) Pool(string extraProbe = "mem_free_gb=3")
    {
        var ssh = new PoolSsh { ProbeExtra = _ => extraProbe };
        return (new JobRunner(
            ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, Path.Combine(_dir, "tmp"), _ => { },
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), bases: new BaseStore(null)), ssh);
    }

    private JobRequest Jest(double? mem = null) => new(["node"], ["npx", "jest", "--shard={shard}/{shards}"], ".", _dir, TimeSpan.Zero, MinMemGb: mem);

    [Theory(Timeout = TestTimeout)]
    [InlineData(null, 5)]
    [InlineData(1.0, 4)]
    [InlineData(2.0, 2)]
    public async Task DryRunShardsAutoPicksTheSameNAsRun(double? mem, int expected)
    {
        var plan = await Pool().Runner.PlanAsync(Jest(mem), new ShardOptions(null), CancellationToken.None);
        var (runner, ssh) = Pool();
        var run = await runner.RunShardsAsync(Jest(mem), new ShardOptions(null), CancellationToken.None);

        Assert.Equal(nameof(JobStatus.Completed), run.Status);
        Assert.Equal((expected, true, 0), (plan.ShardCount, plan.ShardCountAuto, plan.Waiting));
        Assert.Equal(run.Count, plan.ShardCount);
        Assert.Equal(expected, ssh.Executes.Count);

        // The text says what auto picked and why, in lanq run's words.
        var (_, stdout, _) = await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), "--shards", "auto", "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");
        Assert.Contains("--shards auto would pick 5 (free slots: big-01 4, tiny-01 1)", stdout, StringComparison.Ordinal);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(3, "big-01 shards 1,3; tiny-01 shard 2; 0 waiting")]
    [InlineData(7, "big-01 shards 1,3,4,5; tiny-01 shard 2; 2 waiting")]
    public async Task DryRunPlacementMatchesDeal(int count, string text)
    {
        var plan = await Pool().Runner.PlanAsync(Jest(), new ShardOptions(count), CancellationToken.None);

        // The plan is Scheduler.Deal over the ranked free slots of its own probe.
        var shards = Enumerable.Range(1, count).Select(i => new ShardInfo(i, count)).ToList();
        var dealt = Scheduler.Deal(
            [.. Scheduler.Rank(plan.Assessed).Select(a => (a, Scheduler.FreeFor(a.Machine, a.Health, null, 0, 0))).Where(x => x.Item2 > 0)], shards, (_, _) => true);
        Assert.Equal(
            dealt.GroupBy(d => d.Machine.Machine.Name).Select(g => (g.Key, string.Join(',', g.Select(d => d.Item.Index).Order()))),
            plan.Placement!.Select(p => (p.Machine.Name, string.Join(',', p.Shards))));
        Assert.Equal(count - dealt.Count, plan.Waiting);
        Assert.Equal("big-01", plan.Machine);

        // lanq run starts the same shards on the same machines (with 7 the rest wait and go wherever a slot frees up later).
        var (runner, ssh) = Pool();
        var started = new Countdown(dealt.Count);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.OnExecute = async (e, ct) =>
        {
            started.Signal();
            await release.Task.WaitAsync(ct);
            return e.Exit(0);
        };
        var running = runner.RunShardsAsync(Jest(), new ShardOptions(count), CancellationToken.None);
        await started.Reached;
        var first = ssh.Executes.Take(dealt.Count).Select(e => (e.Shard!.Value, e.Machine.Name)).Order().ToList();
        release.SetResult();
        Assert.Equal(nameof(JobStatus.Completed), (await running).Status);
        Assert.Equal(dealt.Select(d => (d.Item.Index, d.Machine.Machine.Name)).Order(), first);

        var (_, stdout, _) = await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), "--shards", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");
        Assert.Contains("Placement now: " + text, stdout, StringComparison.Ordinal);
        var json = Json((await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), "--json", "--shards", "3", "--needs", "node", "--", "npx", "jest")).Stdout);
        Assert.Equal((3, false), (json.GetProperty("shardCount").GetInt32(), json.GetProperty("shardCountAuto").GetBoolean()));
        Assert.Equal(
            [("big-01", "1,3"), ("tiny-01", "2")],
            json.GetProperty("placement").EnumerateArray().Select(p => (p.GetProperty("machine").GetString(), string.Join(',', p.GetProperty("shards").EnumerateArray().Select(i => i.GetInt32())))));
    }

    [Theory]
    [InlineData("ok", 0, "WouldRun")]
    [InlineData("busy", 75, "NoCapacity")]
    [InlineData("never", 75, "NoCapacity")]
    [InlineData("refused", 2, "Rejected")]
    [InlineData("snapshot", 70, "InfraError")]
    [InlineData("cancelled", 130, "Cancelled")]
    public async Task DryRunExitCodes(string scenario, int code, string status)
    {
        using var cts = new CancellationTokenSource();
        var ssh = scenario == "busy" ? Fleet(_ => "load=0.1\ndisk_free_gb=50\nslots_used=2\ntool=ok") : Fleet();
        IPayloadBuilder payloads = scenario switch
        {
            "snapshot" => new ScriptedPayloads(_ => throw new InvalidOperationException("git rev-parse failed: not a git repository")),
            "cancelled" => new ScriptedPayloads(ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return new JobPayload("abc", [], "");
            }),
            _ => new RunnerTests.FakePayloads(""),
        };
        string[] argv = scenario == "refused" ? ["rm", "-rf", "/"] : ["dotnet", "test"];
        var needs = scenario == "never" ? "gpu" : "dotnet";

        foreach (var json in new[] { false, true })
        {
            var (exit, stdout, stderr) = await Cli(Config(), ssh, payloads, null, cts.Token, [.. json ? ["--json"] : Array.Empty<string>(), "--needs", needs, "--", .. argv]);

            Assert.Equal(code, exit);
            Assert.StartsWith($"[lanq] {status}: ", Lines(stderr)[^1], StringComparison.Ordinal);
            if (json)
            {
                var r = Json(stdout);
                Assert.Equal((status, code, true), (r.GetProperty("status").GetString(), r.GetProperty("exitCode").GetInt32(), r.GetProperty("dryRun").GetBoolean()));
                var retryable = r.GetProperty("retryable");
                if (status == "NoCapacity")
                {
                    Assert.Equal(scenario != "never", retryable.GetBoolean());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, retryable.ValueKind);
                }

                Assert.Equal(status == "WouldRun" ? JsonValueKind.String : JsonValueKind.Null, r.GetProperty("machine").ValueKind);
            }
            else
            {
                Assert.StartsWith(LanQueue.Cli.DryRunHeader, stdout, StringComparison.Ordinal);
            }
        }

        Assert.Empty(ssh.Uploads);
        Assert.DoesNotContain(ssh.Calls, c => !IsProbe(c.Machine == "build-01" ? T.Linux() : T.Windows(), c.Command));
    }

    [Fact]
    public async Task DryRunJsonIsOneObjectAlsoOnArgumentErrors()
    {
        foreach (var args in new[]
        {
            new[] { "run", "--dry-run", "--json", "--bogus", "--", "dotnet", "test" },
            ["run", "--dry-run", "--json", "--shards", "2", "--wait", "x", "--", "dotnet", "test"],
            ["run", "--dry-run", "--json", "--log-tail", "5", "--fetch-max", "1G", "--", "dotnet", "test"],
            ["run", "--dry-run", "--json", "--log-tail", "5", "--config", Path.Combine(_dir, "missing.yml"), "--", "dotnet", "test"],
        })
        {
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.Equal(2, await LanQueue.Cli.RunAsync(args, o, e, CancellationToken.None));

            var r = Json(o.ToString());
            Assert.Equal(("Rejected", 2, true), (r.GetProperty("status").GetString(), r.GetProperty("exitCode").GetInt32(), r.GetProperty("dryRun").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, r.GetProperty("machine").ValueKind);
            Assert.Equal(0, r.GetProperty("machines").GetArrayLength());
            Assert.Equal(0, r.GetProperty("ranked").GetArrayLength());
            Assert.False(r.TryGetProperty("shards", out _)); // the plan object, never the --shards run object
            Assert.Equal(args.Contains("--log-tail"), r.TryGetProperty("logTail", out var tail));
            Assert.True(!args.Contains("--log-tail") || tail.GetArrayLength() == 0);
        }

        // A refused command line (found by the plan itself) is one object too.
        var (code, stdout, _) = await Cli(Config(), Fleet(), "--json", "--needs", "dotnet", "--", "rm", "-rf", "/");
        Assert.Equal(2, code);
        Assert.Equal("Rejected", Json(stdout).GetProperty("status").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunAcceptsEveryRunOption()
    {
        string[] args =
        [
            "run", "--dry-run", "--needs", "node", "--dir", ".", "--wait", "120", "--timeout", "30", "--mem", "1", "--json", "--no-wake",
            "--fetch", "out/**", "--fetch-dir", Path.Combine(_dir, "fetched"), "--fetch-max", "10M", "--fetch-max-files", "20",
            "--shards", "2", "--shard-retries", "1", "--fail-fast", "--log-tail", "5", "--config", "machines.yml",
            "--", "npx", "jest", "--shard={shard}/{shards}",
        ];
        var p = ParsedArgs.Parse(args);
        Assert.True(p.DryRun);

        var ssh = new PoolSsh();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await LanQueue.Cli.Run(p, ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _dir, _dir, o, e, CancellationToken.None);

        Assert.Equal(0, code);
        var r = Json(o.ToString());
        Assert.Equal(("WouldRun", 2, false), (r.GetProperty("status").GetString(), r.GetProperty("shardCount").GetInt32(), r.GetProperty("shardCountAuto").GetBoolean()));
        Assert.Equal(0, r.GetProperty("logTail").GetArrayLength());
        Assert.False(r.TryGetProperty("fetched", out _));
        Assert.Empty(ssh.Uploads);
        Assert.False(Directory.Exists(Path.Combine(_dir, "fetched")));
        Assert.False(Directory.Exists(Path.Combine(_dir, ".lanq")));
        Assert.Contains("Options:  --timeout 30 min; --fetch out/** into ", e.ToString(), StringComparison.Ordinal);
        Assert.Contains("a dry run fetches nothing); --log-tail 5 (empty: a dry run starts no job)", e.ToString(), StringComparison.Ordinal);
        Assert.Contains("Needs:    node   --mem 1", e.ToString(), StringComparison.Ordinal);

        // Only lanq run takes it.
        foreach (var command in new[] { "health", "doctor", "makeready", "wake", "init", "unlock", "stats" })
        {
            Assert.Throws<ArgumentException>(() => ParsedArgs.Parse([command, "--dry-run"]));
        }
    }

    private static readonly string[] JestAuto = ["--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}"];

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunShowsTheTimingLineAndShardTimingJson()
    {
        ShardTimingTests.SeedHistory(Log, DateTimeOffset.UtcNow);
        var (code, stdout, _) = await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), ["--shards", "auto", "--target-minutes", "5", .. JestAuto]);
        Assert.Equal(0, code);
        var lines = Lines(stdout);
        var auto = Array.FindIndex(lines, l => l.StartsWith("--shards auto would pick", StringComparison.Ordinal));
        Assert.Equal("--shards auto would pick 4 (free slots: big-01 4, tiny-01 1)", lines[auto]);
        Assert.Equal("Timing:   " + ShardTimingTests.SeededWhy, lines[auto + 1]);

        var (_, json, _) = await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), ["--json", "--shards", "auto", "--target-minutes", "5", .. JestAuto]);
        var r = Json(json);
        Assert.Equal((4, true), (r.GetProperty("shardCount").GetInt32(), r.GetProperty("shardCountAuto").GetBoolean()));
        Assert.Equal(ShardTimingTests.SeededWhy + "; free slots: big-01 4, tiny-01 1", r.GetProperty("shardCountWhy").GetString());
        var t = r.GetProperty("shardTiming");
        Assert.Equal(
            ["targetMinutes", "command", "dir", "basis", "runsUsed", "lastRunUtc", "workMinutes", "overheadSeconds", "slowestMachine", "speedFactor", "wanted",
                "freeSlots", "capped", "picked", "expectedShardMinutes", "why"],
            t.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            (5, ShardTimingTests.JestKey, ".", "history", 3, 10.7, 30.0, "tiny-01", 1.62, 4, 5, 4, 4.8),
            (t.GetProperty("targetMinutes").GetInt32(), t.GetProperty("command").GetString(), t.GetProperty("dir").GetString(), t.GetProperty("basis").GetString(),
                t.GetProperty("runsUsed").GetInt32(), t.GetProperty("workMinutes").GetDouble(), t.GetProperty("overheadSeconds").GetDouble(),
                t.GetProperty("slowestMachine").GetString(), t.GetProperty("speedFactor").GetDouble(), t.GetProperty("wanted").GetInt32(),
                t.GetProperty("freeSlots").GetInt32(), t.GetProperty("picked").GetInt32(), t.GetProperty("expectedShardMinutes").GetDouble()));
        Assert.Equal(JsonValueKind.Null, t.GetProperty("capped").ValueKind);
        Assert.EndsWith("Z", t.GetProperty("lastRunUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(ShardTimingTests.SeededWhy, t.GetProperty("why").GetString());

        // The suggested real run keeps --target-minutes, so it picks the same way.
        var proceed = Assert.Single(r.GetProperty("nextSteps").EnumerateArray());
        Assert.Contains("--target-minutes", proceed.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunShardTimingIsNullWithoutTargetMinutes()
    {
        ShardTimingTests.SeedHistory(Log, DateTimeOffset.UtcNow);
        var (_, text, _) = await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), ["--shards", "auto", .. JestAuto]);
        Assert.Contains("--shards auto would pick 5 (free slots: big-01 4, tiny-01 1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Timing:", text, StringComparison.Ordinal);

        var auto = Json((await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), ["--json", "--shards", "auto", .. JestAuto])).Stdout);
        Assert.Equal("free slots: big-01 4, tiny-01 1", auto.GetProperty("shardCountWhy").GetString());
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("shardTiming").ValueKind);

        var fixedN = Json((await Cli(ConfigLoader.Parse(ShardTests.PoolYaml), new PoolSsh(), ["--json", "--shards", "3", .. JestAuto])).Stdout);
        Assert.Equal(JsonValueKind.Null, fixedN.GetProperty("shardCountWhy").ValueKind);
        Assert.Equal(JsonValueKind.Null, fixedN.GetProperty("shardTiming").ValueKind);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(30)]
    public async Task DryRunAndRunPickTheSameNFromTheSameHistoryAndProbe(int minutes)
    {
        ShardTimingTests.SeedHistory(Log, DateTimeOffset.UtcNow);
        var options = new ShardOptions(null, TargetMinutes: minutes);
        var plan = await Pool().Runner.PlanAsync(Jest(), options, CancellationToken.None);
        var (runner, _) = Pool();
        var run = await runner.RunShardsAsync(Jest(), options, CancellationToken.None);

        Assert.Equal(nameof(JobStatus.Completed), run.Status);
        Assert.Equal(run.Count, plan.ShardCount);
        Assert.Equal(run.Timing, plan.Timing);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunReadsButNeverWritesTheJobLog()
    {
        ShardTimingTests.SeedHistory(Log, DateTimeOffset.UtcNow);
        var state = Path.Combine(_dir, "state");
        string[] Files() => [.. Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        var before = Files();
        var bytes = File.ReadAllBytes(Path.Combine(state, "jobs.jsonl"));

        var ssh = new PoolSsh();
        var o = new StringWriter();
        var code = await LanQueue.Cli.Run(
            ParsedArgs.Parse(["run", "--dry-run", "--json", "--shards", "auto", "--target-minutes", "5", .. JestAuto]),
            ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), Log, _dir, _dir, o, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("history", Json(o.ToString()).GetProperty("shardTiming").GetProperty("basis").GetString()); // it did read the history
        Assert.Equal(before, Files());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(state, "jobs.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(state, "logs")));
        Assert.Empty(ssh.Executes);
    }
}
