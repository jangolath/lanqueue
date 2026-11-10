using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Plan;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run --plan --dry-run (Jobs/JobRunner.PlanFileDry.cs, Cli.PlanFile.cs): what the plan would do now, sending nothing.</summary>
public sealed class PlanFileDryRunTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private static readonly DateTimeOffset Night = new(2026, 1, 5, 23, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-plandry-" + Guid.NewGuid().ToString("N"));

    public PlanFileDryRunTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Home);
    }

    public void Dispose() => TestTree.Delete(_root);

    private string Repo => Path.Combine(_root, "repo");

    private string Home => Path.Combine(_root, "home");

    private JobLog Log => new(Path.Combine(Home, "state"));

    private string PlanPath => Path.Combine(Repo, "lanq.yml");

    private void Plan(string yaml) => File.WriteAllText(PlanPath, yaml);

    /// <summary>
    /// integration (3 fixed shards), unit (shards auto), build (single) and serial (after integration), all needing node, over build-01 and
    /// big-01 with two slots each: four slots for the first wave.
    /// </summary>
    private const string Mixed = """
        version: 1
        defaults:
          needs: [node]
        jobs:
          integration:
            command: ["npx", "jest", "--shard={shard}/{shards}"]
            shards: 3
          unit:
            command: ["npm", "run", "unit", "--", "--shard={shard}/{shards}"]
            shards: auto
          build:
            command: npm run build
          serial:
            command: npm run serial
            after: [integration]
        """;

    private async Task<(int Code, string Stdout, string Stderr)> Dry(PoolSsh ssh, RunnerTests.FakePayloads payloads, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--dry-run", .. args]), ConfigLoader.Parse(PlanFileRunTests.Fleet), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), Log, Home,
            Repo, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    private Task<(int Code, string Stdout, string Stderr)> Dry(PoolSsh ssh, params string[] args) => Dry(ssh, new RunnerTests.FakePayloads(""), args);

    private static JsonElement Json(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }

    private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    private static Dictionary<string, JsonElement> JobsOf(JsonElement json) =>
        json.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j, StringComparer.Ordinal);

    [Fact(Timeout = TestTimeout)]
    public async Task SendsNothingClaimsNothingSnapshotsOnce()
    {
        Plan(Mixed);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("", "src/a.cs", "src/b.cs");

        var (code, stdout, stderr) = await Dry(ssh, payloads, "--json");

        Assert.Equal(0, code);
        Assert.Equal(1, payloads.Built);
        Assert.Equal(PayloadKinds.None, payloads.LastKinds);
        Assert.Equal(2, ssh.Calls.Count); // one probe of each machine, for every job
        Assert.All(ssh.Calls, c => Assert.Contains("disk_free_gb", c.Command, StringComparison.Ordinal));
        Assert.Empty(ssh.Uploads);
        Assert.Empty(ssh.Executes);
        Assert.Empty(Directory.GetFiles(Home, "*", SearchOption.AllDirectories)); // no jobs.jsonl, log or bases.json
        Assert.False(Directory.Exists(Path.Combine(Repo, ".lanq")));
        Assert.Equal(("WouldRun", true), (Json(stdout).GetProperty("status").GetString(), Json(stdout).GetProperty("dryRun").GetBoolean()));
        Assert.Contains("Snapshot: command lines and 2 uncommitted file(s) clean", stderr, StringComparison.Ordinal);
        Assert.StartsWith("[lanq] WouldRun: ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FirstWaveIsDealtOnOneSharedSlotTable()
    {
        Plan(Mixed);

        var (code, stdout, _) = await Dry(new PoolSsh(), "--json");

        Assert.Equal(0, code);
        var jobs = JobsOf(Json(stdout));

        // integration (first in file order) takes three of the four slots; unit's auto N is what is left; build waits for the plan's slots.
        var integration = jobs["integration"];
        Assert.Equal(("now", "WouldRun", 3), (integration.GetProperty("start").GetString(), integration.GetProperty("status").GetString(), integration.GetProperty("shardCount").GetInt32()));
        Assert.Equal(
            ["big-01:1,3", "build-01:2"],
            integration.GetProperty("placement").EnumerateArray().Select(p => p.GetProperty("machine").GetString() + ":" + string.Join(',', p.GetProperty("shards").EnumerateArray().Select(s => s.GetInt32()))));
        var unit = jobs["unit"];
        Assert.Equal(("WouldRun", 1, true), (unit.GetProperty("status").GetString(), unit.GetProperty("shardCount").GetInt32(), unit.GetProperty("shardCountAuto").GetBoolean()));
        Assert.Equal("free slots: build-01 1", unit.GetProperty("shardCountWhy").GetString());
        var build = jobs["build"];
        Assert.Equal(("waits_for_plan", "WouldWait"), (build.GetProperty("start").GetString(), build.GetProperty("status").GetString()));
        Assert.Contains("integration, unit", build.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NothingFreeAndNothingToWaitBehindIsNoCapacity()
    {
        Plan(Mixed);

        // Both machines report every slot in use (another lanq's jobs).
        var busy = new PoolSsh();
        for (var i = 0; i < 2; i++)
        {
            foreach (var m in ConfigLoader.Parse(PlanFileRunTests.Fleet).Machines)
            {
                await busy.RunAsync(m, "echo SLOT=", null, TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }

        var (code, stdout, stderr) = await Dry(busy, "--json");

        Assert.Equal(75, code);
        var json = Json(stdout);
        Assert.Equal(("NoCapacity", true), (json.GetProperty("status").GetString(), json.GetProperty("retryable").GetBoolean()));
        Assert.Equal("NoCapacity", JobsOf(json)["integration"].GetProperty("status").GetString());
        Assert.Equal(["wait", "run_health"], json.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetProperty("action").GetString()));
        Assert.Contains("--dry-run", json.GetProperty("nextSteps")[0].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.StartsWith("[lanq] NoCapacity: job 'integration': ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShowsTheEquivalentRunLinePerJob()
    {
        Plan(Mixed);
        var plan = PlanFileLoader.Load(PlanPath);

        var (_, stdout, _) = await Dry(new PoolSsh());
        var json = Json((await Dry(new PoolSsh(), "--json")).Stdout);

        Assert.StartsWith(Cli.PlanDryRunHeader + "\n", stdout.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        foreach (var job in plan.Jobs)
        {
            var j = JobsOf(json)[job.Name];
            List<string> argv = [.. j.GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()!)];
            Assert.Equal(["lanq", .. job.RunArgs], argv);
            Assert.Equal(job.RunCommand, j.GetProperty("runCommand").GetString());
            Assert.Contains(job.Name.PadRight(13) + job.RunCommand, stdout, StringComparison.Ordinal);

            // The line is a real lanq run command line: it parses to the job's own command.
            Assert.Equal(job.Args.Argv, ParsedArgs.Parse([.. argv.Skip(1)]).Argv);
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LaterJobsArePending()
    {
        Plan(Mixed);

        var (_, stdout, _) = await Dry(new PoolSsh());
        var serial = JobsOf(Json((await Dry(new PoolSsh(), "--json")).Stdout))["serial"];

        Assert.Equal(("after", "Pending"), (serial.GetProperty("start").GetString(), serial.GetProperty("status").GetString()));
        Assert.Equal(["integration"], serial.GetProperty("after").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(JsonValueKind.Null, serial.GetProperty("machine").ValueKind);
        Assert.Equal(2, serial.GetProperty("machines").GetArrayLength()); // judged (from the one probe), not placed
        Assert.Contains("pending: starts after integration", stdout, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task JsonShape()
    {
        Plan(Mixed);

        var (_, stdout, _) = await Dry(new PoolSsh(), "--json", "--log-tail", "20");

        var json = Json(stdout);
        Assert.Equal(
            ["status", "exitCode", "dryRun", "plan", "message", "order", "jobs", "warnings", "retryable", "logTail", "nextSteps"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(PlanPath, json.GetProperty("plan").GetString());
        Assert.Equal(["integration", "unit", "build", "serial"], json.GetProperty("order").EnumerateArray().Select(o => o.GetString()));
        Assert.Empty(json.GetProperty("logTail").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("retryable").ValueKind);
        foreach (var job in json.GetProperty("jobs").EnumerateArray())
        {
            Assert.Equal(
                ["name", "after", "runArgv", "runCommand", "start", "status", "message", "machine", "shardCount", "shardCountAuto", "placement", "shardCountWhy", "shardTiming",
                    "expectedMinutes", "machines", "retryable"],
                job.EnumerateObject().Select(p => p.Name));
        }

        var proceed = json.GetProperty("nextSteps")[0];
        Assert.Equal("proceed", proceed.GetProperty("action").GetString());
        Assert.Equal(["lanq", "run", "--plan", "--json", "--log-tail", "20", "--no-wake"], proceed.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadsButNeverWritesTheJobLog()
    {
        Plan(Mixed);
        Log.Append(new JobRecord("old-1", "build-01", "npm run build", DateTimeOffset.UtcNow.AddDays(-1), 700, 0, "Completed")
        {
            V = 2,
            Key = JobHistory.Key(["npm", "run", "build"]),
            Dir = ".",
            RunSeconds = 600,
        });
        var before = File.ReadAllBytes(Log.FilePath);

        var (code, stdout, _) = await Dry(new PoolSsh());
        var json = Json((await Dry(new PoolSsh(), "--json")).Stdout);

        Assert.Equal(0, code);
        Assert.Equal(before, File.ReadAllBytes(Log.FilePath));
        Assert.Equal(["build", "integration", "unit", "serial"], json.GetProperty("order").EnumerateArray().Select(o => o.GetString())); // history first
        Assert.Equal(10, JobsOf(json)["build"].GetProperty("expectedMinutes").GetDouble());
        Assert.Contains("(longest first from history)", stdout, StringComparison.Ordinal);
        Assert.Contains("about 10 min of work (from history)", stdout, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NeverWakes()
    {
        Plan("""
            version: 1
            jobs:
              build:
                command: npm run build
            """);
        var config = ConfigLoader.Parse(PlanFileRunTests.Fleet);
        foreach (var m in config.Machines)
        {
            m.Wake = new WakeConfig { Mac = "02:00:00:00:00:0" + (m.Name == "big-01" ? "1" : "2") };
        }

        var ssh = new FakeSsh { Handler = (m, _) => new SshResult(255, $"ssh: connect to host {m.Name}.lan port 22: Connection timed out") };
        var clock = new FakeClock(Night);
        var sender = new FakeSender();
        var waking = new HealthProbe(ssh, clock, new WakeService(sender, clock));
        var plan = PlanFileLoader.Load(PlanPath);
        var p = ParsedArgs.Parse(["run", "--plan", "--dry-run", "--wait", "300"]);
        var specs = plan.Jobs.Select(j => new PlanJobSpec(j.Name, Cli.PlanJobRequest(j, p, Repo, fetch: false), j.Args.Shards, j.After)).ToList();

        // Even a runner whose probe could wake (lanq run's) sends nothing in a plan's dry run.
        var runner = new JobRunner(config, ssh, new RunnerTests.FakePayloads(""), waking, Log, Path.Combine(Home, "tmp"), _ => { }, (_, _) => Task.CompletedTask, clock, new BaseStore(null));
        var dry = await runner.PlanFileDryAsync(specs, CancellationToken.None);

        Assert.Equal((nameof(JobStatus.NoCapacity), 75, true), (dry.Status, dry.ExitCode, dry.Retryable));
        Assert.Equal(["asleep", "asleep"], dry.Jobs[0].Assessed.Select(a => a.Code));
        Assert.Empty(sender.Sent);
        Assert.Equal(2, ssh.Calls.Count); // one probe each, no wake, no retry
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RefusalsAndNeverAreAsTheRealRun()
    {
        Plan("""
            version: 1
            jobs:
              build:
                command: npm run build
              gpu:
                command: npm run train
                needs: [cuda]
            """);
        var ssh = new PoolSsh();

        var (code, stdout, stderr) = await Dry(ssh, "--json");

        Assert.Equal(75, code);
        var json = Json(stdout);
        Assert.Equal(("NoCapacity", false), (json.GetProperty("status").GetString(), json.GetProperty("retryable").GetBoolean()));
        Assert.Equal(("NoCapacity", false), (JobsOf(json)["gpu"].GetProperty("status").GetString(), JobsOf(json)["gpu"].GetProperty("retryable").GetBoolean()));
        Assert.Equal("Pending", JobsOf(json)["build"].GetProperty("status").GetString());
        Assert.Equal(["ask_developer", "run_health"], json.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetProperty("action").GetString()));
        Assert.Empty(ssh.Calls);

        Plan("version: 1\njobs:\n  build:\n    command: [rm, -rf, dist]\n");
        var refused = await Dry(ssh, "--json");
        Assert.Equal(2, refused.Code);
        Assert.Equal(("Rejected", true), (Json(refused.Stdout).GetProperty("status").GetString(), Json(refused.Stdout).GetProperty("dryRun").GetBoolean()));
        Assert.StartsWith("[lanq] Rejected: job 'build': ", Lines(refused.Stderr)[^1], StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.StartsWith("[lanq] NoCapacity: job 'gpu': ", Lines(stderr)[^1], StringComparison.Ordinal);
    }
    /// <summary>
    /// A lanq.yml list command with --filter A|B (a native .exe) dry-runs on a Windows-only fleet; adding a job that hands & to npm (a .cmd
    /// shim there) refuses the whole plan, as lanq run would, before any network work.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task AFilterWithABarDryRunsOnWindowsButAnNpmAmpersandIsRefused()
    {
        const string windowsFleet = """
            policy:
              load_threshold: 0.8
              allowed_commands: ["dotnet test", "npm run"]
              allowed_directories: ["."]
            machines:
              - name: win-01
                os: windows
                ssh: user@10.20.30.51
                work_root: C:/lanq
                capabilities: [dotnet]
                max_jobs: 2
            """;
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..])).Contains("disk_free_gb", StringComparison.Ordinal)
                ? new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\nmem_free_gb=3\ntool=ok")
                : new SshResult(0, ""),
        };
        async Task<(int Code, string Stdout, string Stderr)> DryWindows()
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await Cli.RunPlanFile(
                ParsedArgs.Parse(["run", "--plan", "--dry-run", "--json"]), ConfigLoader.Parse(windowsFleet), ssh, new RunnerTests.FakePayloads(""),
                new HealthProbe(ssh, TimeProvider.System), Log, Home, Repo, o, e, CancellationToken.None);
            return (code, o.ToString(), e.ToString());
        }

        Plan("""
            version: 1
            defaults:
              needs: [dotnet]
            jobs:
              unit:
                command: [dotnet, test, --filter, "Category=Unit|Category=Arch"]
            """);
        var (code, stdout, _) = await DryWindows();
        Assert.Equal(0, code);
        var unit = JobsOf(Json(stdout))["unit"];
        Assert.Equal("WouldRun", unit.GetProperty("status").GetString());
        Assert.Contains("win-01", unit.GetRawText(), StringComparison.Ordinal);

        Plan("""
            version: 1
            defaults:
              needs: [dotnet]
            jobs:
              unit:
                command: [dotnet, test, --filter, "Category=Unit|Category=Arch"]
              scripts:
                command: [npm, run, "x&y"]
            """);
        ssh.Calls.Clear();
        var refused = await DryWindows();
        Assert.Equal(2, refused.Code);
        Assert.Equal("Rejected", Json(refused.Stdout).GetProperty("status").GetString());
        Assert.StartsWith("[lanq] Rejected: job 'scripts': ", Lines(refused.Stderr)[^1], StringComparison.Ordinal);
        Assert.Contains(".cmd/.bat shims", refused.Stderr, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }
}
