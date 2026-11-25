using System.Collections;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Plan;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 B3: this computer as an explicit, last-resort fallback (policy.fallback off|on|ask, default off; a local: true entry kept out of the
/// pool by construction, review H4; an allowlisted environment, H2; the 120 s slot floor, M1; shards and plans, M2; ask only at a terminal,
/// M7; labels, M9).
/// </summary>
public sealed class FallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = (Path.Combine(Path.GetTempPath(), "lanq-fbwork-" + Guid.NewGuid().ToString("N"))).Replace('\\', '/');
    private readonly List<string> _output = [];

    public FallbackTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        TestTree.Delete(_dir);
        TestTree.Delete(_work);
    }

    private static string ThisOs => ConfigLoader.ThisOs == MachineOs.Windows ? "windows" : "linux";

    private static string OtherOs => ConfigLoader.ThisOs == MachineOs.Windows ? "linux" : "windows";

    /// <summary>A pool of one Linux worker with [node] only, and this computer as the fallback with [dotnet].</summary>
    private string Yaml(string policy = "", string fallbackOs = "", string extra = "") => $"""
        policy:
          allowed_commands: ["dotnet test", "dotnet build"]
          allowed_directories: ["."]
        {policy}
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [node]
          - name: dev-pc
            os: {(fallbackOs.Length > 0 ? fallbackOs : ThisOs)}
            local: true
            fallback: true
            work_root: {(fallbackOs.Length == 0 ? _work : fallbackOs == "linux" ? "/srv/dev" : "D:/dev")}
            capabilities: [dotnet]
        {extra}
        """;

    private FleetConfig Config(string policy = "") => ConfigLoader.Parse(Yaml(policy.Length == 0 ? "" : "  " + policy));

    // ---- config (H4, L8) ----

    [Fact]
    public void TheFallbackEntryIsKeptOutOfThePool()
    {
        var c = Config();
        Assert.Equal(["build-01"], c.Machines.Select(m => m.Name));
        Assert.Equal("dev-pc", c.Fallback!.Name);
        Assert.True(c.Fallback.Local);
        Assert.Equal(FallbackMode.Off, c.Policy.Fallback); // declaring the PC is not consent to use it
    }

    [Theory]
    [InlineData("fallback: on", FallbackMode.On)]
    [InlineData("fallback: ask", FallbackMode.Ask)]
    [InlineData("fallback: off", FallbackMode.Off)]
    public void PolicyFallbackReadsItsWords(string line, FallbackMode mode) => Assert.Equal(mode, Config(line).Policy.Fallback);

    [Fact]
    public void PolicyFallbackRefusesOtherWords() =>
        Assert.Contains("policy.fallback", Assert.Throws<ConfigException>(() => Config("fallback: maybe")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("    local: true\n", "goes only with fallback: true")]
    [InlineData("    fallback: true\n", "needs local: true")]
    public void LocalAndFallbackGoTogether(string keys, string message)
    {
        var yaml = $"""
            policy:
              allowed_commands: ["dotnet test"]
            machines:
              - name: build-01
                os: linux
                ssh: user@10.20.30.11
                work_root: /srv/lanq
              - name: dev-pc
                os: {ThisOs}
                work_root: {_work}
            {keys}
            """;
        Assert.Contains(message, Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ssh: me@localhost", "replaces ssh:")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours is not allowed")]
    [InlineData("key_env: LANQ_KEY", "key_env is not allowed")]
    [InlineData("transfer: archive", "transfer: archive is not allowed")]
    [InlineData("wake: { mac: aa:bb:cc:dd:ee:01 }", "wake is not allowed")]
    public void KeysThatOnlyFitARemoteWorkerAreRefusedOnTheFallback(string key, string message)
    {
        var yaml = Yaml().Replace("    fallback: true\n", "    fallback: true\n    " + key + "\n", StringComparison.Ordinal);
        Assert.Contains(message, Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneFallbackAndNeverAPoolOfOnlyAFallback()
    {
        var two = Yaml(extra: $"""
              - name: dev-pc2
                os: {ThisOs}
                local: true
                fallback: true
                work_root: {_work}
            """);
        Assert.Contains("Only one machine may be the fallback", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(two)).Message, StringComparison.Ordinal);

        var alone = $"""
            policy:
              allowed_commands: ["dotnet test"]
            machines:
              - name: dev-pc
                os: {ThisOs}
                local: true
                fallback: true
                work_root: {_work}
            """;
        Assert.Contains("alone is not a pool", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(alone)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOsMismatchDisablesOnlyTheFallback()
    {
        // L8: the same machines.yml shared with a computer of the other OS still loads; only the fallback is off there.
        var c = ConfigLoader.Parse(Yaml(fallbackOs: OtherOs));
        Assert.Null(c.Fallback);
        Assert.Contains("the fallback is off on this computer", c.FallbackProblem, StringComparison.Ordinal);
        Assert.Equal(["build-01"], c.Machines.Select(m => m.Name));
    }

    // ---- the command line and lanq.yml (parity) ----

    [Theory]
    [InlineData("off", FallbackMode.Off)]
    [InlineData("on", FallbackMode.On)]
    [InlineData("ask", FallbackMode.Ask)]
    public void FallbackFlagParsesAndWorksWithPlan(string word, FallbackMode mode)
    {
        Assert.Equal(mode, ParsedArgs.Parse(["run", "--fallback", word, "--", "dotnet", "test"]).Fallback);
        Assert.Equal(mode, ParsedArgs.Parse(["run", "--plan", "--fallback", word]).Fallback);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--fallback", "yes", "--", "dotnet", "test"]));
        Assert.Contains("--fallback", ParsedArgs.PlanTakes, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitGivenIsSetByAnyWaitFlag()
    {
        Assert.False(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).WaitGiven);
        Assert.True(ParsedArgs.Parse(["run", "--wait", "0", "--", "dotnet", "test"]).WaitGiven);
        Assert.True(ParsedArgs.Parse(["run", "--wait-slot", "30", "--", "dotnet", "test"]).WaitGiven);
    }

    private static PlanFile LoadPlan(string yaml) => PlanFileLoader.Parse(yaml, "lanq.yml");

    [Fact]
    public void LanqYmlMayOnlySayFallbackOff()
    {
        var plan = LoadPlan("""
            version: 1
            defaults: { fallback: off }
            jobs:
              unit: { command: dotnet test }
              build: { command: dotnet build }
            """);
        Assert.All(plan.Jobs, j => Assert.Equal(FallbackMode.Off, j.Args.Fallback));

        foreach (var word in new[] { "on", "ask" })
        {
            var ex = Assert.ThrowsAny<Exception>(() => LoadPlan($"version: 1\njobs:\n  unit: {{ command: dotnet test, fallback: {word} }}\n"));
            Assert.Contains("a repository never chooses to run on your PC; use --fallback on", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnNamingTheFallbackIsRefused()
    {
        var (_, error) = Pinning.Resolve(Config(), ["dev-pc"]);
        Assert.Contains("is this computer's fallback, not a worker; use --fallback on", error, StringComparison.Ordinal);
    }

    // ---- the policy for a job ----

    private JobRunner Runner(FleetConfig config, FakeSsh ssh, IFallbackPrompt? prompt = null, bool elevated = false) =>
        new(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"),
            _output.Add)
        {
            Prompt = prompt,
            NoAskReason = prompt is null ? "no terminal" : null,
            Elevated = () => elevated,
        };

    private JobRequest Req(params string[] argv) => new(["dotnet"], argv.Length == 0 ? ["dotnet", "test"] : argv, ".", _dir, TimeSpan.Zero);

    [Fact]
    public void PolicyResolution()
    {
        var off = Runner(Config(), new FakeSsh());
        var on = Runner(Config("fallback: on"), new FakeSsh());
        Assert.Equal(FallbackMode.Off, off.FallbackModeOf(Req()));
        Assert.Equal(FallbackMode.On, off.FallbackModeOf(Req() with { Fallback = FallbackMode.On }));      // --fallback on overrides the policy
        Assert.Equal(FallbackMode.On, on.FallbackModeOf(Req()));
        Assert.Equal(FallbackMode.Off, on.FallbackModeOf(Req() with { Fallback = FallbackMode.Off }));     // --fallback off too
        Assert.Equal(FallbackMode.Off, on.FallbackModeOf(Req() with { Fallback = FallbackMode.On, FallbackOff = true })); // lanq.yml off wins
        Assert.Equal(FallbackMode.Off, on.FallbackModeOf(Req() with { On = ["build-01"] }));              // a named target never falls back
        var none = Runner(ConfigLoader.Parse(Yaml(fallbackOs: OtherOs, policy: "  fallback: on")), new FakeSsh());
        Assert.Equal(FallbackMode.Off, none.FallbackModeOf(Req()));                                      // no usable fallback entry
    }

    [Fact]
    public void WithoutAWaitFlagAFallbackRunWaitsAtLeast120sForASlot()
    {
        // M1: only the slot budget gets the floor, only when the fallback is in play and the caller gave no wait flag.
        var on = Runner(Config("fallback: on"), new FakeSsh());
        Assert.Equal(TimeSpan.FromSeconds(120), on.BudgetsOf(Req()).Slot);
        Assert.Equal(TimeSpan.Zero, on.BudgetsOf(Req()).Eligible);
        Assert.Equal(TimeSpan.Zero, on.BudgetsOf(Req() with { WaitGiven = true }).Slot);
        Assert.Equal(TimeSpan.FromSeconds(600), on.BudgetsOf(Req() with { Wait = TimeSpan.FromSeconds(600), WaitGiven = true }).Slot);
        Assert.Equal(TimeSpan.Zero, Runner(Config(), new FakeSsh()).BudgetsOf(Req()).Slot);
    }

    // ---- the "would end NoCapacity" hook ----

    private static string Healthy(int slots = 0) => $"load=0.1\ndisk_free_gb=50\nslots_used={slots}\ntool=ok";

    private static string Text(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    /// <summary>A fake pool: every probe healthy (build-01 busy when asked), every claim gets slot 1 unless the machine is full.</summary>
    private static FakeSsh Pool(bool poolBusy = false, bool fallbackFull = false) => new()
    {
        Handler = (m, cmd) =>
        {
            var text = Text(m, cmd);
            if (text.Contains("SLOT=", StringComparison.Ordinal))
            {
                return new SshResult(0, m.Local && fallbackFull || m.Name == "build-01" && poolBusy ? "SLOT=FULL" : "SLOT=1");
            }

            return text.Contains("disk_free_gb", StringComparison.Ordinal)
                ? new SshResult(0, Healthy(m.Name == "build-01" && poolBusy ? 1 : 0))
                : new SshResult(0, "ran");
        },
    };

    [Fact]
    public async Task OffOnlyTheFallbackIsCapableEndsNoCapacityWithoutTouchingThisComputer()
    {
        // H4: the only capable machine is the fallback and the policy is off: NoCapacity before any probe, with the --fallback on hint.
        var ssh = Pool();
        var r = await Runner(Config(), ssh).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(FallbackHint.TurnOn, r.FallbackHint);
        Assert.Contains("This computer (dev-pc) could run it: lanq run --fallback on", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc");
        Assert.DoesNotContain(r.Excluded!, e => e.Machine == "dev-pc");
    }

    [Fact]
    public async Task OffShardsAutoNeverCountsTheFallback()
    {
        var ssh = Pool();
        var r = await Runner(Config(), ssh).RunShardsAsync(Req(), new ShardOptions(null), CancellationToken.None);
        Assert.Equal(nameof(JobStatus.NoCapacity), r.Status);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc");
    }

    [Fact]
    public async Task OffPlanAndDryRunEndNoCapacity()
    {
        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(_dir, "lanq.yml"), "version: 1\njobs:\n  unit: { command: dotnet test, needs: [dotnet] }\n");
        var ssh = Pool();
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunPlanFile(
            ParsedArgs.Parse(["run", "--plan", "--json"]), Config(), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System),
            new JobLog(Path.Combine(home, "state")), home, _dir, o, e, CancellationToken.None, (_, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));
        Assert.Equal(75, code);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc");

        (o, e) = (new StringWriter(), new StringWriter());
        code = await Cli.Run(
            ParsedArgs.Parse(["run", "--dry-run", "--json", "--needs", "dotnet", "--", "dotnet", "test"]), Config(), ssh, new RunnerTests.FakePayloads(""),
            new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(home, "state")), home, _dir, o, e, CancellationToken.None);
        Assert.Equal(75, code);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc");
    }

    [Fact]
    public async Task OnRunsOnThisComputerWhenNoPoolMachineCanTakeItAndLabelsIt()
    {
        var ssh = Pool();
        var runner = Runner(Config("fallback: on"), ssh);
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("dev-pc", r.Machine);
        Assert.True(r.Fallback);
        Assert.Contains("[lanq] running on this computer (fallback: no pool machine can take it)", _output); // A-L5: the real reason
        var rec = Assert.Single(new JobLog(Path.Combine(_dir, ".lanq")).ReadAll());
        Assert.True(rec.Fallback);
        Assert.Equal("[lanq] Completed (1 of 1 job on this computer, fallback): x", Cli.StatusLine(r with { Message = "x" }));
    }

    [Fact]
    public async Task OnWithAWaitFlagFallsBackAfterTheBudgetWhenThePoolIsBusy()
    {
        // --wait 0 given: no 120 s floor; the pool's only capable machine is busy, so the job falls back after the first look.
        var config = Config("fallback: on");
        config.Machines[0].Capabilities = ["dotnet"];
        var ssh = Pool(poolBusy: true);
        var r = await Runner(config, ssh).RunAsync(Req() with { WaitGiven = true }, CancellationToken.None);
        Assert.Equal("dev-pc", r.Machine);
        Assert.True(r.Fallback);
    }

    [Fact]
    public async Task ABusyFallbackSlotEndsNotRunNamingBoth()
    {
        // Another lanq run holds this computer's slot: the job ends NoCapacity (NOT RUN), retryable.
        var config = Config("fallback: on");
        config.Machines[0].Capabilities = ["dotnet"];
        var ssh = Pool(poolBusy: true, fallbackFull: true);
        var r = await Runner(config, ssh).RunAsync(Req() with { WaitGiven = true }, CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(true, r.Retryable);
        Assert.Contains(r.Excluded!, e => e.Machine == "build-01");
        Assert.Contains(r.Excluded!, e => e.Machine == "dev-pc");
    }

    [Fact]
    public async Task ElevatedLanqNeverFallsBack()
    {
        var ssh = Pool();
        var r = await Runner(Config("fallback: on"), ssh, elevated: true).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        var row = Assert.Single(r.Excluded!, e => e.Machine == "dev-pc");
        Assert.Equal("fallback_refused", row.Reason);
        Assert.Contains("elevated", row.Why, StringComparison.Ordinal);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc" && Text(c.Machine == "dev-pc" ? Config().Fallback! : T.Linux(), c.Command).Contains("SLOT=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkRootInsideTheRepositoryIsRefused()
    {
        var config = Config("fallback: on");
        config.Fallback!.WorkRoot = Path.Combine(_dir, "lq").Replace('\\', '/');
        var r = await Runner(config, Pool()).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains(r.Excluded!, e => e.Machine == "dev-pc" && e.Reason == "fallback_work_root" && e.Why.Contains("inside the repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkRootAtTheRepositoryTopIsRefusedFromASubfolder()
    {
        // A-M3: lanq runs from a subfolder of the repository; the work_root is beside it, inside the repository top: still refused.
        var sub = Path.Combine(_dir, "tests", "unit");
        Directory.CreateDirectory(sub);
        var config = Config("fallback: on");
        config.Fallback!.WorkRoot = Path.Combine(_dir, ".lq").Replace('\\', '/');
        var r = await Runner(config, Pool()).RunAsync(Req() with { RepoDir = sub }, CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains(r.Excluded!, e => e.Machine == "dev-pc" && e.Reason == "fallback_work_root");
        Assert.Equal(FallbackHint.WorkRoot, r.FallbackHint);
    }

    [Fact]
    public void AWorkRootReachedThroughALinkIsInsideTheRepository()
    {
        // A-M3: a work_root whose path goes through a link into the repository is inside it.
        var link = Path.Combine(_work, "into-repo");
        try
        {
            Directory.CreateSymbolicLink(link, _dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no right to make links here (Windows without developer mode): the CI Linux runner covers it
        }

        Assert.True(JobRunner.WorkRootOverlaps(Path.Combine(link, "lq"), _dir));
        Assert.False(JobRunner.WorkRootOverlaps(Path.Combine(_work, "other"), _dir));
    }

    [Fact]
    public async Task ShardsFallBackWithoutOverloadingThisComputer()
    {
        // M2: three shards, only the fallback can take them (max_jobs 1 there): one at a time, all on this computer, none NOT RUN.
        var ssh = Pool();
        var r = await Runner(Config("fallback: on"), ssh).RunShardsAsync(Req(), new ShardOptions(3), CancellationToken.None);
        Assert.Equal(nameof(JobStatus.Completed), r.Status);
        Assert.Equal(3, r.FallbackJobs);
        Assert.All(r.Shards, s => Assert.True(s.Fallback));
    }

    // ---- ask (M7) ----

    private sealed class ScriptedPrompt(params bool?[] answers) : IFallbackPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool?> AskAsync(string question, CancellationToken ct)
        {
            Questions.Add(question);
            return Task.FromResult(answers[Math.Min(Questions.Count - 1, answers.Length - 1)]);
        }
    }

    [Fact]
    public async Task AskWithoutATerminalActsAsOffAndSaysSo()
    {
        var r = await Runner(Config("fallback: ask"), Pool()).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("fallback: ask needs a terminal (no terminal), so it acted as off", r.Message, StringComparison.Ordinal);
        Assert.Equal(FallbackHint.TurnOn, r.FallbackHint);
    }

    [Fact]
    public async Task AskYesRunsHereAndTheQuestionShowsNoArguments()
    {
        var prompt = new ScriptedPrompt(true);
        var r = await Runner(Config("fallback: ask"), Pool(), prompt).RunAsync(Req("dotnet", "test", "--filter", "Secret=1"), CancellationToken.None);
        Assert.True(r.Fallback);
        var q = Assert.Single(prompt.Questions);
        Assert.Equal("[lanq] the job: no pool machine can take it. Run it on this computer (dev-pc)? A yes also covers the remaining jobs in this run, each only after its own wait runs out. [y/N]", q);
    }

    [Fact]
    public async Task AskWithoutAnAnswerIsNotRepeatedInTheRun()
    {
        var prompt = new ScriptedPrompt([null]);
        var runner = Runner(Config("fallback: ask"), Pool(), prompt);
        var first = await runner.RunAsync(Req(), CancellationToken.None);
        var second = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, first.Status);
        Assert.Equal(JobStatus.NoCapacity, second.Status);
        Assert.Single(prompt.Questions);
        Assert.Contains("[lanq] ask: no answer; not asking again in this run", _output);
    }

    // ---- consent scope and acting as off (review A-M1, A-M2, A-L7, A-L8) ----

    /// <summary>A runner on a fake clock: every 15 s pause moves it on at once, so a whole wait budget runs through the real loop.</summary>
    private JobRunner TimedRunner(FleetConfig config, FakeSsh ssh, Func<long> now, Action<TimeSpan> advance, IFallbackPrompt? prompt = null, bool elevated = false) =>
        new(config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"),
            _output.Add, delay: (d, _) =>
            {
                advance(d);
                return Task.CompletedTask;
            })
        {
            Prompt = prompt,
            NoAskReason = prompt is null ? "no terminal" : null,
            Elevated = () => elevated,
            Timestamp = now,
        };

    [Fact]
    public async Task AYesCoversTheRunButEachJobFallsBackOnlyAfterItsOwnWait()
    {
        // A-M1: the pool's only capable machine stays busy. Job 1 waits its 60 s, the developer says yes, it runs here. Job 2 of the same run
        // is not asked again, but it too waits its own 60 s before this computer is tried: the yes never cuts another job's wait short.
        var config = Config("fallback: ask");
        config.Machines[0].Capabilities = ["dotnet"];
        long now = 0;
        var claims = new List<long>();
        var pool = Pool(poolBusy: true);
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
            {
                if (m.Local && Text(m, cmd).Contains("SLOT=", StringComparison.Ordinal))
                {
                    claims.Add(now);
                }

                return pool.Handler(m, cmd);
            },
        };
        var prompt = new ScriptedPrompt(true);
        var runner = TimedRunner(config, ssh, () => now, d => now += (long)(d.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), prompt);
        var budgets = new LanQueue.Scheduling.WaitBudgets(TimeSpan.Zero, TimeSpan.FromSeconds(60));
        var first = await runner.RunAsync(Req() with { WaitGiven = true, Budgets = budgets }, CancellationToken.None);
        Assert.True(first.Fallback);
        var secondStart = now;
        var second = await runner.RunAsync(Req() with { WaitGiven = true, Budgets = budgets }, CancellationToken.None);
        Assert.True(second.Fallback);
        Assert.Single(prompt.Questions); // the yes covered job 2: not asked again
        Assert.Contains("remaining jobs in this run", prompt.Questions[0], StringComparison.Ordinal);
        Assert.Equal(2, claims.Count);
        Assert.True(claims[0] >= 60 * System.Diagnostics.Stopwatch.Frequency, "job 1 fell back before its own wait ran out");
        Assert.True(claims[1] - secondStart >= 60 * System.Diagnostics.Stopwatch.Frequency, "job 2 fell back before its own wait ran out");
    }

    [Fact]
    public async Task AskThatCannotPromptActsExactlyAsOff()
    {
        // A-M2: ask with no terminal: no 120 s floor (the busy pool ends at the first look, as with off), and a job only this computer could
        // take ends from the config before any probe, never probing the pool or this computer.
        var runner = Runner(Config("fallback: ask"), new FakeSsh());
        Assert.Equal(TimeSpan.Zero, runner.BudgetsOf(Req()).Slot);
        Assert.Equal(FallbackMode.Off, runner.EffectiveFallbackModeOf(Req()));

        var ssh = Pool();
        var r = await Runner(Config("fallback: ask"), ssh).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Empty(ssh.Calls);
        Assert.Equal(FallbackHint.TurnOn, r.FallbackHint);

        var config = Config("fallback: ask");
        config.Machines[0].Capabilities = ["dotnet"];
        var busy = Pool(poolBusy: true);
        var waited = await Runner(config, busy).RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, waited.Status);
        Assert.True(waited.Waited!.Slot + waited.Waited.Eligible < TimeSpan.FromSeconds(15), "ask acting as off waited for a slot");
        Assert.Contains("fallback: ask needs a terminal (no terminal), so it acted as off", waited.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(busy.Calls, c => c.Machine == "dev-pc");
    }

    [Fact]
    public async Task ARefusedFallbackActsAsOffAndSaysSoOnceAtTheStart()
    {
        // A-M2, A-L8: lanq runs elevated with fallback on: no floor, the config-only NoCapacity at once, one line at the start, and the next
        // step says to run lanq without those rights (never --fallback on, which would only be refused again).
        var runner = Runner(Config("fallback: on"), Pool(), elevated: true);
        Assert.Equal(TimeSpan.Zero, runner.BudgetsOf(Req()).Slot);
        var ssh = Pool();
        runner = Runner(Config("fallback: on"), ssh, elevated: true);
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Empty(ssh.Calls);
        Assert.Equal(FallbackHint.Elevated, r.FallbackHint);
        Assert.Single(_output, l => l.Contains("fallback: on, but lanq runs elevated", StringComparison.Ordinal) && l.EndsWith("it will not be used", StringComparison.Ordinal));
        var step = Assert.Single(NextSteps.WithFallback(ParsedArgs.Parse(["run", "--", "dotnet", "test"]), [], FallbackHint.Elevated, 0, 1));
        Assert.Contains("without those rights", step.Reason, StringComparison.Ordinal);
        Assert.Null(step.Argv);
    }

    [Fact]
    public async Task FallbackOnWithNoFallbackEntrySaysSo()
    {
        // A-L7: --fallback on typed, but machines.yml has no usable fallback entry: one line says so, and the job uses the pool only.
        var config = ConfigLoader.Parse(Yaml(fallbackOs: OtherOs));
        Assert.Null(config.Fallback);
        await Runner(config, Pool()).RunAsync(Req() with { Fallback = FallbackMode.On }, CancellationToken.None);
        Assert.Contains(_output, l => l.StartsWith("[lanq] --fallback on: machines.yml has no usable fallback entry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnlockAndHealthCanNameTheFallback()
    {
        // A-M4: a stuck fallback slot has a tool (lanq unlock dev-pc), and lanq health --on dev-pc probes this computer by name; a plain
        // health never touches it, and run still refuses --on dev-pc (OnNamingTheFallbackIsRefused).
        var ssh = new FakeSsh { Handler = (m, cmd) => Text(m, cmd).Contains("REMOVED", StringComparison.Ordinal) ? new SshResult(0, "REMOVED=slot-1 crashed-job\n") : new SshResult(0, Healthy()) };
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await Cli.Unlock(ParsedArgs.Parse(["unlock", "dev-pc"]), Config(), ssh, o, e, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains(ssh.Calls, c => c.Machine == "dev-pc");

        ssh.Calls.Clear();
        code = await Cli.Health(Config(), new HealthProbe(ssh, TimeProvider.System), new StringWriter(), new StringWriter(), CancellationToken.None, on: ["dev-pc"]);
        Assert.NotEqual(2, code);
        Assert.Equal(["dev-pc"], ssh.Calls.Select(c => c.Machine).Distinct());

        ssh.Calls.Clear();
        await Cli.Health(Config(), new HealthProbe(ssh, TimeProvider.System), new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "dev-pc");
    }

    private static Hashtable Env(params (string Name, string Value)[] vars)
    {
        var env = new Hashtable();
        foreach (var (n, v) in vars)
        {
            env[n] = v;
        }

        return env;
    }

    [Fact]
    public void AskPromptsOnlyAtATerminalWithNoAgentOrCi()
    {
        Assert.Null(FallbackAsk.WhyNot(false, false, false, Env(("TERM", "xterm"))));
        Assert.Equal("--json", FallbackAsk.WhyNot(true, false, false, Env()));
        Assert.Equal("no terminal", FallbackAsk.WhyNot(false, true, false, Env()));
        Assert.Equal("no terminal", FallbackAsk.WhyNot(false, false, true, Env()));
        Assert.Equal("CI is set", FallbackAsk.WhyNot(false, false, false, Env(("CI", "true"))));
        Assert.Equal("TERM=dumb", FallbackAsk.WhyNot(false, false, false, Env(("TERM", "dumb"))));
        Assert.Equal("LANQ_NONINTERACTIVE is set", FallbackAsk.WhyNot(false, false, false, Env(("LANQ_NONINTERACTIVE", "1"))));
        Assert.Equal("a coding agent runs lanq", FallbackAsk.WhyNot(false, false, false, Env(("CLAUDECODE", "1"))));
        Assert.Equal("a coding agent runs lanq", FallbackAsk.WhyNot(false, false, false, Env(("CODEX_SANDBOX", "seatbelt"))));
    }

    /// <summary>A reader whose lines arrive when the test releases them.</summary>
    private sealed class SlowReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _lines = [];

        public void Type(string? line) => _lines.Add(line);

        public override string? ReadLine() => _lines.Take();
    }

    [Fact]
    public async Task TheTerminalPromptDiscardsEarlyLinesAndATimeoutKeepsTheReader()
    {
        var input = new SlowReader();
        var err = new StringWriter();
        var prompt = new TerminalPrompt(input, err, TimeSpan.FromSeconds(3));
        input.Type("y"); // typed before any prompt: never taken as an answer
        await Task.Delay(200);
        Assert.Null(await prompt.AskAsync("q1", CancellationToken.None)); // no answer in time
        var second = prompt.AskAsync("q2", CancellationToken.None);
        await Task.Delay(300);
        input.Type("yes");
        Assert.True(await second); // the answer reaches the second prompt, not a read the first one left behind
        input.Type(null);
        Assert.False(await prompt.AskAsync("q3", CancellationToken.None)); // end of input: no
        Assert.Contains("q1", err.ToString(), StringComparison.Ordinal);
    }

    // ---- next steps and JSON (M9) ----

    [Fact]
    public void NextStepsAskTheDeveloperAndNeverRunTheFallback()
    {
        var p = ParsedArgs.Parse(["run", "--json", "--needs", "dotnet", "--", "dotnet", "test"]);
        var turnOn = NextSteps.WithFallback(p, [], FallbackHint.TurnOn, 0, 1);
        var step = Assert.Single(turnOn);
        Assert.Equal("ask_developer", step.Action);
        Assert.False(step.SafeToRunUnattended);
        Assert.Contains("--fallback", step.Argv!);
        Assert.Contains("on", step.Argv!);

        var setUp = Assert.Single(NextSteps.WithFallback(p, [], FallbackHint.SetUp, 0, 1));
        Assert.Contains("Using this PC as a fallback", setUp.Reason, StringComparison.Ordinal);
        Assert.Null(setUp.Argv);

        var evidence = Assert.Single(NextSteps.WithFallback(p, [], null, 2, 7));
        Assert.Equal("ask_developer", evidence.Action);
        Assert.Contains("2 of 7 job(s) ran on this computer (fallback), not on the pool", evidence.Reason, StringComparison.Ordinal);
        Assert.Equal(["lanq", "run", "--json", "--needs", "dotnet", "--fallback", "off", "--wait-slot", "1800", "--", "dotnet", "test"], evidence.Argv);
    }

    [Fact]
    public void RunJsonCarriesFallbackFlags()
    {
        var json = JsonSerializer.Serialize(new RunJson("Completed", 0, "dev-pc", "j", 1, "") { Fallback = true, FallbackJobs = 1 }, LanqJson.Default.RunJson);
        Assert.Contains("\"fallback\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"fallbackJobs\":1", json, StringComparison.Ordinal);
        var pool = JsonSerializer.Serialize(new RunJson("Completed", 0, "build-01", "j", 1, ""), LanqJson.Default.RunJson);
        Assert.DoesNotContain("fallback", pool, StringComparison.Ordinal);
    }

    [Fact]
    public void StatsMarksThisComputer()
    {
        var report = JobStats.Of([new JobRecord("a", "dev-pc", "dotnet test", DateTimeOffset.UtcNow, 1, 0, "Completed") { Fallback = true }]);
        Assert.True(Assert.Single(report.Machines).ThisComputer);
    }

    // ---- the local runner (H2, M8) ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLocalEnvironmentIsBuiltFromAnAllowlist(bool windows)
    {
        var parent = Env(
            ("PATH", "/usr/bin"), ("HOME", "/home/me"), ("SystemRoot", "C:\\Windows"), ("ProgramFiles(x86)", "C:\\PF86"), ("LC_ALL", "C.UTF-8"),
            ("SSH_AUTH_SOCK", "/tmp/agent"), ("GH_TOKEN", "t"), ("GITHUB_TOKEN", "t"), ("MSBuildExtensionsPath", "x"), ("MSBUILD_EXE_PATH", "x"),
            ("DOTNET_HOST_PATH", "x"), ("LANQ_JOB", "outer"), ("NODE_OPTIONS", "--require x"), ("NUGET_PACKAGES", "x"), ("AWS_SECRET_ACCESS_KEY", "x"));
        var env = LocalProcessClient.ChildEnvironment(parent, windows);
        Assert.Contains("PATH", env.Keys);
        foreach (var gone in new[] { "SSH_AUTH_SOCK", "GH_TOKEN", "GITHUB_TOKEN", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH", "LANQ_JOB", "NODE_OPTIONS", "NUGET_PACKAGES", "AWS_SECRET_ACCESS_KEY" })
        {
            Assert.DoesNotContain(gone, env.Keys);
        }

        Assert.Equal(windows, env.ContainsKey("SystemRoot") && env.ContainsKey("ProgramFiles(x86)"));
        Assert.Equal(!windows, env.ContainsKey("HOME") && env.ContainsKey("LC_ALL"));
    }

    [Fact]
    public void WindowsCommandLinesSplitAsCmdWould()
    {
        Assert.Equal(["powershell", "-NoProfile", "-EncodedCommand", "QQ=="], LocalProcessClient.WindowsWords("powershell -NoProfile -EncodedCommand QQ=="));
        Assert.Equal(["powershell", "-Command", "& ([scriptblock]::Create(x))"], LocalProcessClient.WindowsWords("powershell -Command \"& ([scriptblock]::Create(x))\""));
        Assert.Null(LocalProcessClient.WindowsWords("powershell -Command \"unbalanced"));
        Assert.Null(LocalProcessClient.WindowsWords("powershell a\"b"));
        Assert.Throws<ArgumentException>(() => new LocalProcessClient(windows: true).CommandOf("cmd /c del x"));
    }

    [Fact]
    public async Task AnUploadStaysInsideTheWorkRoot()
    {
        var m = Config().Fallback!;
        var src = Path.Combine(_dir, "payload.bundle");
        File.WriteAllText(src, "x");
        var client = new LocalProcessClient();
        Assert.True((await client.UploadAsync(m, src, m.WorkRoot + "/incoming/j.bundle", TimeSpan.FromSeconds(30), CancellationToken.None)).Ok);
        Assert.True(File.Exists(Path.Combine(_work, "incoming", "j.bundle")));
        Assert.False((await client.UploadAsync(m, src, Path.Combine(_dir, "elsewhere.bundle"), TimeSpan.FromSeconds(30), CancellationToken.None)).Ok);
    }

    /// <summary>
    /// The real scripts through the local runner, on this OS (both CI runners): claim a slot, run a job that prints its environment, clean
    /// up. The developer's SSH_AUTH_SOCK, MSBuild variables and an outer LANQ_JOB never reach the job (H2), and nothing is left behind.
    /// </summary>
    [Fact]
    public async Task TheRealScriptsRunLocallyWithACleanEnvironmentAndLeaveNothingBehind()
    {
        var m = Config().Fallback!;
        var parent = Environment.GetEnvironmentVariables();
        parent["SSH_AUTH_SOCK"] = "/tmp/lanq-test-agent";
        parent["MSBuildExtensionsPath"] = "lanq-test-msbuild";
        parent["LANQ_JOB"] = "outer-job";
        var client = new LocalProcessClient(() => parent);
        const string id = "20261125-120000-abcdef";
        var limit = TimeSpan.FromMinutes(2);

        var claim = await client.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.AcquireSlot(m, id, limit)), null, limit, CancellationToken.None);
        var slot = JobRunner.ParseSlot(claim.Output);
        Assert.True(claim.Ok && slot is not null, claim.Output);
        Assert.True((await client.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.EnsureIncoming(m)), null, limit, CancellationToken.None)).Ok);
        Directory.CreateDirectory(Path.Combine(_work, "jobs", id)); // stands in for prepare (the worktree)

        IReadOnlyList<string> argv = m.Os == MachineOs.Windows
            ? ["powershell", "-NoProfile", "-Command", "Get-ChildItem env: | ForEach-Object { $_.Name + '=' + $_.Value }"]
            : ["env"];
        var lines = new List<string>();
        var run = await client.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.Execute(m, id, ".", argv, limit)), lines.Add, limit, CancellationToken.None);
        Assert.True(run.Ok, run.Output);
        Assert.Contains(lines, l => l.TrimEnd() == "LANQ_JOB=" + id);
        Assert.DoesNotContain(lines, l => l.StartsWith("SSH_AUTH_SOCK=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.StartsWith("MSBuildExtensionsPath=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("outer-job", StringComparison.Ordinal));

        var cleanup = await client.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.Cleanup(m, id, slot!, null)), null, limit, CancellationToken.None);
        Assert.True(cleanup.Ok, cleanup.Output);
        Assert.False(Directory.Exists(Path.Combine(_work, "jobs", id)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_work, "locks"), "slot-*"));
    }
}
