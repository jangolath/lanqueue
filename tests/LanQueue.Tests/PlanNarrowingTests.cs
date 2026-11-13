using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Plan;
using LanQueue.Scheduling;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq run --plan --on / --size / --min-size (0.10.0): narrowing every job of a plan, never widening one (Plan/PlanNarrowing.cs).</summary>
public sealed partial class PlanFileRunTests
{
    /// <summary>Three Linux machines: build-01, big-01 and micro-01.</summary>
    internal const string Trio = """
        policy:
          load_threshold: 0.8
          allowed_commands: ["npm run", "npx jest"]
          allowed_directories: ["."]
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 2
          - name: big-01
            os: linux
            ssh: user@10.20.30.12
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 2
          - name: micro-01
            os: linux
            ssh: user@10.20.30.13
            work_root: /srv/lanq
            capabilities: [node]
            max_jobs: 2
        """;

    /// <summary>build and lint run anywhere; unit-windows only on micro-01.</summary>
    private const string Pinned = """
        version: 1
        defaults:
          needs: [node]
        jobs:
          build:
            command: npm run build
          unit-windows:
            command: npm run unit
            on: [micro-01]
          lint:
            command: npm run lint
        """;

    private Task<(int Code, string Stdout, string Stderr)> RunTrio(PoolSsh ssh, RunnerTests.FakePayloads payloads, params string[] args) =>
        Run(ssh, payloads, args, fleet: Trio);

    [Fact(Timeout = TestTimeout)]
    public async Task OnNarrowsEveryJob()
    {
        Plan(Three);
        var ssh = new PoolSsh();

        var (code, stdout, _) = await RunTrio(ssh, new RunnerTests.FakePayloads(""), "--plan", "--on", "BIG-01", "--json");

        Assert.Equal(0, code);
        Assert.Equal(3, ssh.Executes.Count);
        Assert.All(ssh.Executes, e => Assert.Equal("big-01", e.Machine.Name));
        Assert.All(JobsOf(Json(stdout)).Values, j => Assert.Equal("big-01", j.GetProperty("machine").GetString()));
        Assert.DoesNotContain(ssh.Calls, c => c.Machine != "big-01"); // the other machines are not even probed
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnIntersectsAJobsOwnOn()
    {
        Plan("""
            version: 1
            defaults:
              needs: [node]
            jobs:
              unit:
                command: npm run unit
                on: [build-01, big-01]
              lint:
                command: npm run lint
                on: [big-01]
            """);
        var ssh = new PoolSsh();

        var (code, _, _) = await RunTrio(ssh, new RunnerTests.FakePayloads(""), "--plan", "--on", "big-01,micro-01", "--json");

        Assert.Equal(0, code);
        Assert.Equal(2, ssh.Executes.Count);
        Assert.All(ssh.Executes, e => Assert.Equal("big-01", e.Machine.Name)); // unit: [build-01, big-01] and [big-01, micro-01] leave big-01

        // The run lines: unit's on: narrowed to the machines both allow; lint's own on: kept exactly as written.
        var config = ConfigLoader.Parse(Trio);
        var plan = PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml"));
        var (unit, none) = PlanNarrowing.Narrow(plan.Job("unit")!, config, ["big-01", "micro-01"], "big-01,micro-01", null);
        Assert.Null(none);
        Assert.Equal(["run", "--needs", "node", "--on", "big-01", "--", "npm", "run", "unit"], unit.RunArgs);
        Assert.Equal(["big-01"], unit.Args.On);
        var (lint, _) = PlanNarrowing.Narrow(plan.Job("lint")!, config, ["big-01", "micro-01"], "big-01,micro-01", null);
        Assert.Same(plan.Job("lint"), lint);
    }

    [Theory]
    [InlineData("--size light,medium", "--min-size medium", "--size medium")]
    [InlineData("--min-size medium", "--size light,heavy", "--size heavy")]
    [InlineData("--min-size heavy", "--min-size medium", "--min-size heavy")]
    [InlineData("--min-size light", "--min-size medium", "--min-size medium")]
    [InlineData("--size light,medium,heavy", "--size medium", "--size medium")]
    [InlineData(null, "--min-size medium", "--min-size medium")]
    [InlineData("--size light", "--min-size medium", null)]
    [InlineData("--min-size heavy", "--size light,medium", null)]
    public void SizeIntersectsMinSize(string? own, string cli, string? expected)
    {
        static SizeRule Rule(string text)
        {
            var parts = text.Split(' ');
            return parts[0] == "--size" ? SizeRule.AnyOfText(parts[1]) : SizeRule.AtLeastText(parts[1]);
        }

        var (rule, empty) = PlanNarrowing.IntersectSize(own is null ? null : Rule(own), Rule(cli));

        Assert.Equal(expected is null, empty);
        Assert.Equal(expected, rule?.Text);
    }

    [Fact]
    public void SizeNarrowsAJobsOwnRunLine()
    {
        Plan("""
            version: 1
            jobs:
              unit:
                command: npm run unit
                needs: [node]
                size: [light, medium]
            """);
        var plan = PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml"));

        var (job, why) = PlanNarrowing.Narrow(plan.Jobs[0], ConfigLoader.Parse(Trio), null, null, SizeRule.AtLeastText("medium"));
        var (none, refused) = PlanNarrowing.Narrow(plan.Jobs[0], ConfigLoader.Parse(Trio), null, null, SizeRule.AtLeastText("heavy"));

        Assert.Null(why);
        Assert.Equal(["run", "--needs", "node", "--size", "medium", "--", "npm", "run", "unit"], job.RunArgs);
        Assert.Equal("--size medium", job.Args.Size!.Text);
        Assert.Same(plan.Jobs[0], none);
        Assert.Equal("--min-size heavy leaves job 'unit' no machine (its size: light,medium)", refused);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EmptyIntersectionFailsClosedBeforeSnapshot()
    {
        Plan(Pinned);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, stderr) = await RunTrio(ssh, payloads, "--plan", "--on", "big-01", "--json");

        Assert.Equal(75, code);
        var json = Json(stdout);
        Assert.Equal("NoCapacity", json.GetProperty("status").GetString());
        Assert.False(json.GetProperty("retryable").GetBoolean());
        Assert.Contains("--on big-01 leaves job 'unit-windows' no machine (its on: [micro-01])", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        var jobs = JobsOf(json);
        Assert.Equal("NoCapacity", jobs["unit-windows"].GetProperty("status").GetString());
        Assert.False(jobs["unit-windows"].GetProperty("retryable").GetBoolean());
        Assert.Equal("Skipped", jobs["build"].GetProperty("status").GetString());
        Assert.Equal("Skipped", jobs["lint"].GetProperty("status").GetString());
        Assert.Empty(ssh.Calls); // not even a probe
        Assert.Equal(0, payloads.Built); // no snapshot

        var steps = json.GetProperty("nextSteps");
        Assert.Equal("ask_developer", steps[0].GetProperty("action").GetString());
        Assert.Contains("never edit lanq.yml to widen a job", steps[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("fix_command", steps[1].GetProperty("action").GetString());
        Assert.Equal(["lanq", "run", "--plan", "--on", "big-01", "--json", "--only", "build,lint", "--no-wake"], steps[1].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("unit-windows would not run", steps[1].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("[lanq] NoCapacity: ", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SuggestedOnlyReplacesExistingOnly()
    {
        Plan(Pinned);
        var ssh = new PoolSsh();

        var (code, stdout, _) = await RunTrio(ssh, new RunnerTests.FakePayloads(""), "--plan", "--only", "unit-windows,build", "--min-size", "light", "--on", "build-01", "--wait", "5", "--json");

        Assert.Equal(75, code);
        var argv = Json(stdout).GetProperty("nextSteps")[1].GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal(["lanq", "run", "--plan", "--only", "build", "--min-size", "light", "--on", "build-01", "--wait", "5", "--json"], argv);
        Assert.Single(argv, a => a == "--only");
        Assert.DoesNotContain("unit-windows", string.Join(' ', argv), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task UnknownOnRejectedBeforeNetwork()
    {
        Plan(Three);
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");

        var (code, stdout, stderr) = await RunTrio(ssh, payloads, "--plan", "--on", "big-02", "--json");

        Assert.Equal(2, code);
        var json = Json(stdout);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.StartsWith("--on names a machine that is not in the config: big-02.", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
        Assert.Equal(0, payloads.Built);
        Assert.StartsWith("[lanq] Rejected: --on names a machine", Lines(stderr)[^1], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DryRunShowsEffectiveLines()
    {
        Plan(Pinned);
        var ssh = new PoolSsh { ProbeExtra = _ => "mem_free_gb=6\nmem_total_gb=8" };

        var (code, stdout, stderr) = await RunTrio(ssh, new RunnerTests.FakePayloads(""), "--plan", "--dry-run", "--on", "micro-01", "--min-size", "light", "--json");

        Assert.Equal(0, code);
        var jobs = Json(stdout).GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("name").GetString()!, j => j);
        Assert.Equal(
            ["lanq", "run", "--needs", "node", "--on", "micro-01", "--min-size", "light", "--", "npm", "run", "build"],
            jobs["build"].GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(
            ["lanq", "run", "--needs", "node", "--on", "micro-01", "--min-size", "light", "--", "npm", "run", "unit"],
            jobs["unit-windows"].GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("lanq run --needs node --on micro-01 --min-size light -- npm run lint", stderr, StringComparison.Ordinal); // the text, on stderr with --json

        // Narrowed out: the dry run says so, with the same steps, and looks at no machine.
        var none = new PoolSsh();
        var (dryCode, dryOut, _) = await RunTrio(none, new RunnerTests.FakePayloads(""), "--plan", "--dry-run", "--on", "big-01", "--json");
        Assert.Equal(75, dryCode);
        var dry = Json(dryOut);
        Assert.Equal("NoCapacity", dry.GetProperty("status").GetString());
        Assert.False(dry.GetProperty("retryable").GetBoolean());
        Assert.Equal("fix_command", dry.GetProperty("nextSteps")[1].GetProperty("action").GetString());
        Assert.Contains("--dry-run", dry.GetProperty("nextSteps")[1].GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.Empty(none.Calls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NoNarrowingIsByteIdentical()
    {
        Plan(Pinned);
        var config = ConfigLoader.Parse(Trio);
        var plan = PlanFileLoader.Load(Path.Combine(Repo, "lanq.yml"));

        // No flag, or a flag that takes nothing from a job: the very same job.
        Assert.All(plan.Jobs, j => Assert.Same(j, PlanNarrowing.Narrow(j, config, null, null, null).Job));
        Assert.Same(plan.Job("unit-windows"), PlanNarrowing.Narrow(plan.Job("unit-windows")!, config, ["micro-01", "big-01"], "micro-01,big-01", null).Job);

        // Without --on/--size/--min-size the plan's output is what it was: the same run lines and the same JSON members.
        var (code, stdout, _) = await Run(new PoolSsh(), new RunnerTests.FakePayloads(""), ["--plan", "--dry-run", "--json"], fleet: Trio);
        Assert.Equal(0, code);
        foreach (var j in Json(stdout).GetProperty("jobs").EnumerateArray())
        {
            Assert.Equal(["lanq", .. plan.Job(j.GetProperty("name").GetString()!)!.RunArgs], j.GetProperty("runArgv").EnumerateArray().Select(a => a.GetString()));
        }

        Assert.DoesNotContain("planTimeout", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowingOptionsAreTakenWithPlan()
    {
        var p = ParsedArgs.Parse(["run", "--plan", "--on", "big-01,build-01", "--min-size", "medium"]);

        Assert.Equal(["big-01", "build-01"], p.On);
        Assert.Equal("--min-size medium", p.Size!.Text);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--plan", "--size", "light", "--min-size", "heavy"]));
    }
}
