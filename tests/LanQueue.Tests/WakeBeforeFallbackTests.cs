using System.Diagnostics;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0, owner decision "wake before fallback" (replaces final review A-L6): with the fallback on or ask, a job that would fall back to this
/// computer first wakes a sleeping pool machine that could take it and waits up to about 120 s for it (never past its wake.wait_seconds). A
/// paused machine is never woken and never delays the fallback; off is unchanged; one wake per machine per run; ask wakes first and asks only
/// if still needed. Every packet goes to a fake sender.
/// </summary>
public sealed class WakeBeforeFallbackTests : IDisposable
{
    private const int TestTimeout = 60_000;

    private static readonly DateTimeOffset Start = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-wbf-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lanq-wbfwork-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
    private readonly List<string> _output = [];

    public WakeBeforeFallbackTests()
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

    /// <summary>A clock that moves only when the runner waits (its delay), so a 120 s wait takes no real time.</summary>
    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public long Ticks => (long)((Now - Start).TotalSeconds * Stopwatch.Frequency);

        public TimeSpan Elapsed => Now - Start;
    }

    private enum State
    {
        Asleep,
        Paused,
        Awake,
    }

    /// <summary>A pool machine: its state, whether it has a wake: setting, its wake.wait_seconds, and after how long a packet brings it up (null: never).</summary>
    private sealed record Box(string Name, State State, bool Wol = true, int WaitSeconds = 120, int? BootSeconds = null);

    private static string MacOf(int i) => $"02:00:00:00:00:{i + 1:x2}";

    private string Yaml(string policy, IReadOnlyList<Box> pool)
    {
        var machines = string.Join("\n", pool.Select((b, i) => $"""
              - name: {b.Name}
                os: linux
                ssh: user@10.20.30.{11 + i}
                work_root: /srv/lanq
                capabilities: [dotnet]
            {(b.Wol ? $"    wake: {{ mac: \"{MacOf(i)}\", wait_seconds: {b.WaitSeconds} }}" : "")}
            """));
        return $"""
            policy:
              allowed_commands: ["dotnet test"]
              allowed_directories: ["."]
            {(policy.Length == 0 ? "" : "  " + policy)}
            machines:
            {machines}
              - name: dev-pc
                os: {ThisOs}
                local: true
                fallback: true
                work_root: {_work}
                capabilities: [dotnet]
            """;
    }

    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok";

    private static string Text(Machine m, string cmd) =>
        cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    private sealed class Rig
    {
        public required JobRunner Runner { get; init; }

        public required FakeSender Sender { get; init; }

        public required StepClock Clock { get; init; }

        public required FakeSsh Ssh { get; init; }

        /// <summary>When each machine was sent a packet (by the fake clock).</summary>
        public Dictionary<string, DateTimeOffset> WokenAt { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ScriptedPrompt(Func<int> packets, params bool?[] answers) : IFallbackPrompt
    {
        public List<string> Questions { get; } = [];

        /// <summary>How many packets had been sent when each question was asked.</summary>
        public List<int> PacketsBefore { get; } = [];

        public Task<bool?> AskAsync(string question, CancellationToken ct)
        {
            Questions.Add(question);
            PacketsBefore.Add(packets());
            return Task.FromResult(answers[Math.Min(Questions.Count - 1, answers.Length - 1)]);
        }
    }

    private Rig Make(string policy, IReadOnlyList<Box> pool, bool ask = false, bool?[]? answers = null)
    {
        var config = ConfigLoader.Parse(Yaml(policy, pool));
        var clock = new StepClock();
        var sender = new FakeSender();
        Rig? rig = null;
        sender.OnSend = packet =>
        {
            var mac = Convert.ToHexString(packet.AsSpan(6, 6));
            for (var i = 0; i < pool.Count; i++)
            {
                if (MagicPacket.TryParseMac(MacOf(i), out var b) && Convert.ToHexString(b) == mac)
                {
                    rig!.WokenAt[pool[i].Name] = clock.Now;
                }
            }
        };
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
            {
                var text = Text(m, cmd);
                var box = pool.FirstOrDefault(b => b.Name == m.Name);
                if (box is not null && box.State == State.Asleep
                    && !(rig!.WokenAt.TryGetValue(box.Name, out var at) && box.BootSeconds is { } boot && clock.Now >= at + TimeSpan.FromSeconds(boot)))
                {
                    return new SshResult(255, $"ssh: connect to host {m.Name}.lan port 22: Connection timed out");
                }

                return text.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                    : text.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Healthy + (box?.State == State.Paused ? "\npaused=1" : "\npaused=0"))
                    : new SshResult(0, "ran");
            },
        };
        ScriptedPrompt? prompt = ask ? new ScriptedPrompt(() => sender.Sent.Count, answers ?? [true]) : null;
        var runner = new JobRunner(
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, clock, new WakeService(sender, clock)), new JobLog(Path.Combine(_dir, ".lanq")),
            Path.Combine(_dir, "tmp"), _output.Add,
            delay: (d, _) =>
            {
                clock.Now += d;
                return Task.CompletedTask;
            },
            clock: clock)
        {
            Prompt = prompt,
            NoAskReason = prompt is null ? "no terminal" : null,
            Elevated = () => false,
            Timestamp = () => clock.Ticks,
        };
        rig = new Rig { Runner = runner, Sender = sender, Clock = clock, Ssh = ssh };
        Prompts[rig] = prompt;
        return rig;
    }

    private readonly Dictionary<Rig, ScriptedPrompt?> Prompts = [];

    private JobRequest Req() => new(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.Zero);

    // ---- the decision (unit) ----

    [Fact]
    public void WakeBeforeFallbackOnlyWhenTheFallbackCouldReallyTakeTheJob()
    {
        Box[] pool = [new("sleep-01", State.Asleep)];
        var on = Make("fallback: on", pool).Runner;
        var off = Make("", pool).Runner;
        var askAtTerminal = Make("fallback: ask", pool, ask: true).Runner;
        var askNoTerminal = Make("fallback: ask", pool).Runner;

        Assert.True(on.WakesBeforeFallback(Req()));
        Assert.True(askAtTerminal.WakesBeforeFallback(Req()));
        Assert.False(off.WakesBeforeFallback(Req()));                                          // off: unchanged
        Assert.False(askNoTerminal.WakesBeforeFallback(Req()));                                // ask that cannot prompt acts as off
        Assert.False(on.WakesBeforeFallback(Req() with { NoWake = true }));                    // --no-wake
        Assert.False(on.WakesBeforeFallback(Req() with { Fallback = FallbackMode.Off }));      // --fallback off
        Assert.False(on.WakesBeforeFallback(Req() with { On = ["sleep-01"] }));                // --on never falls back
        Assert.False(on.WakesBeforeFallback(Req() with { WaitGiven = true, EligibleWaitGiven = true })); // --wait / --wait-eligible decide
        Assert.True(on.WakesBeforeFallback(Req() with { WaitGiven = true }));                  // --wait-slot alone leaves the eligible wait to lanq
        Assert.False(on.WakesBeforeFallback(new JobRequest(["linux-only"], ["dotnet", "test"], ".", _dir, TimeSpan.Zero))); // the fallback could not take it
    }

    [Fact]
    public void MayWakeNeedsAnEligibleBudgetOrWakeBeforeFallback()
    {
        Box[] pool = [new("sleep-01", State.Asleep)];
        var on = Make("fallback: on", pool).Runner;
        var off = Make("", pool).Runner;

        Assert.False(off.MayWake(Req()));                                                        // 0.12: --wait 0 never wakes
        Assert.True(off.MayWake(Req() with { Wait = TimeSpan.FromSeconds(30) }));
        Assert.True(on.MayWake(Req()));
        Assert.False(on.MayWake(Req() with { WaitGiven = true, EligibleWaitGiven = true }));     // an explicit --wait-eligible 0 / --wait 0
        Assert.True(on.MayWake(Req() with { WaitGiven = true, EligibleWaitGiven = true, Budgets = new WaitBudgets(TimeSpan.FromSeconds(30), TimeSpan.Zero) }));
        Assert.False(on.MayWake(Req() with { NoWake = true }));
    }

    [Fact]
    public void ThePendingWakeRaisesOnlyTheEligibleBudgetAndOnlyWhenLanqChoseIt()
    {
        Box[] pool = [new("sleep-01", State.Asleep)];
        var on = Make("fallback: on", pool).Runner;
        var off = Make("", pool).Runner;
        var s120 = TimeSpan.FromSeconds(120);

        Assert.Equal(new WaitBudgets(s120, s120) { Legacy = true }, on.BudgetsOf(Req(), wakePending: true));
        Assert.Equal(TimeSpan.Zero, on.BudgetsOf(Req(), wakePending: false).Eligible);          // nothing pending (paused, no wake:, none sent): no delay
        Assert.Equal(TimeSpan.Zero, off.BudgetsOf(Req(), wakePending: true).Eligible);          // off: unchanged

        // --wait-slot 600 alone: the slot budget is the caller's, the eligible wait for the woken machine is lanq's.
        var slotOnly = Req() with { WaitGiven = true, Budgets = new WaitBudgets(TimeSpan.Zero, TimeSpan.FromSeconds(600)) };
        Assert.Equal(new WaitBudgets(s120, TimeSpan.FromSeconds(600)), on.BudgetsOf(slotOnly, wakePending: true));

        // --wait-eligible 30 / 300: the caller's eligible budget decides, shorter or longer.
        foreach (var e in new[] { 30, 300 })
        {
            var given = Req() with { WaitGiven = true, EligibleWaitGiven = true, Budgets = new WaitBudgets(TimeSpan.FromSeconds(e), TimeSpan.Zero) };
            Assert.Equal(TimeSpan.FromSeconds(e), on.BudgetsOf(given, wakePending: true).Eligible);
        }
    }

    [Fact]
    public void EligibleWaitGivenComesFromWaitOrWaitEligibleNotWaitSlot()
    {
        Assert.False(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).EligibleWaitGiven);
        Assert.True(ParsedArgs.Parse(["run", "--wait", "0", "--", "dotnet", "test"]).EligibleWaitGiven);
        Assert.True(ParsedArgs.Parse(["run", "--wait-eligible", "0", "--", "dotnet", "test"]).EligibleWaitGiven);
        Assert.True(ParsedArgs.Parse(["run", "--wait-eligible", "30", "--wait-slot", "600", "--", "dotnet", "test"]).EligibleWaitGiven);
        Assert.False(ParsedArgs.Parse(["run", "--wait-slot", "600", "--", "dotnet", "test"]).EligibleWaitGiven);
        Assert.False(ParsedArgs.Parse(["run", "--plan", "--wait-slot", "600"]).EligibleWaitGiven);
    }

    [Fact]
    public async Task TheWakeServiceRecordsOneAttemptPerMachineAndItsResult()
    {
        var clock = new StepClock();
        var sender = new FakeSender();
        var waker = new WakeService(sender, clock);
        var m = new Machine { Name = "sleep-01", Os = MachineOs.Linux, Ssh = "u@h", WorkRoot = "/srv", Wake = new WakeConfig { Mac = MacOf(0), WaitSeconds = 300 } };
        var asleep = new MachineHealth("sleep-01", false, 0, 0, 0, "unreachable", "x") { HoldKind = HoldKind.Unreachable, LooksAsleep = true };
        var policy = T.Config().Policy;

        Assert.Equal(WakeDecision.Wake, waker.TryClaim(m, asleep, clock.Now.DateTime, policy).Decision);
        Assert.Equal(WakeDecision.AlreadyWoken, waker.TryClaim(m, asleep, clock.Now.DateTime, policy).Decision); // never twice in one run
        await waker.SendAsync(m, CancellationToken.None);
        Assert.Equal(WakeOutcome.Waking, Assert.Single(waker.Attempts).Outcome);
        Assert.True(waker.IsPending("sleep-01", TimeSpan.FromSeconds(120)));
        clock.Now += TimeSpan.FromSeconds(121);
        Assert.False(waker.IsPending("sleep-01", TimeSpan.FromSeconds(120)));                   // the fallback waits about 120 s, not 300
        Assert.True(waker.IsPending("sleep-01", TimeSpan.FromSeconds(600)));
        Assert.Equal(WakeOutcome.Waking, waker.Attempts[0].Outcome);                            // its own wait_seconds still run
        clock.Now += TimeSpan.FromSeconds(180);
        Assert.Equal(WakeOutcome.NoAnswer, waker.Attempts[0].Outcome);
        waker.MarkUp(m);
        Assert.Equal(WakeOutcome.Awake, waker.Attempts[0].Outcome);
        Assert.False(waker.IsPending("sleep-01", TimeSpan.FromSeconds(600)));

        var failing = new WakeService(new FakeSender { Failure = new System.Net.Sockets.SocketException() }, clock);
        failing.TryClaim(m, asleep, clock.Now.DateTime, policy);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => failing.SendAsync(m, CancellationToken.None));
        Assert.Equal(WakeOutcome.SendFailed, Assert.Single(failing.Attempts).Outcome);
        Assert.False(failing.IsPending("sleep-01", TimeSpan.FromSeconds(600)));
    }

    // ---- through the runner (fake sender, fake clock) ----

    [Fact(Timeout = TestTimeout)]
    public async Task OnWakesTheSleepingPoolMachineAndUsesItWhenItComesUp()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 40)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("sleep-01", r.Machine);
        Assert.False(r.Fallback);
        Assert.Single(rig.Sender.Sent);
        Assert.Equal(WakeOutcome.Awake, Assert.Single(rig.Runner.WakeAttempts).Outcome);
        Assert.Contains(_output, l => l.Contains("sent a Wake-on-LAN packet to sleep-01 (waiting up to 120 s for it before falling back to this computer)", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Ssh.Calls, c => c.Machine == "dev-pc" && c.Command.Contains("SLOT=", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnFallsBackAfterAbout120sWhenTheWokenMachineNeverAnswers()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, WaitSeconds: 300)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal("dev-pc", r.Machine);
        Assert.True(r.Fallback);
        Assert.Single(rig.Sender.Sent);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 120, 150);
        Assert.Contains("[lanq] running on this computer (fallback: no pool machine can take it now; Wake-on-LAN: sleep-01 woken, no ssh answer yet)", _output);
        Assert.Equal(WakeOutcome.Waking, Assert.Single(rig.Runner.WakeAttempts).Outcome);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AShorterWakeWaitSecondsEndsTheWaitSooner()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, WaitSeconds: 45)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 45, 60);
        Assert.Equal(WakeOutcome.NoAnswer, Assert.Single(rig.Runner.WakeAttempts).Outcome);
        Assert.Contains(_output, l => l.Contains("Wake-on-LAN: sleep-01 woken, no ssh answer within 45 s", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task APausedMachineIsNeverWokenAndDoesNotDelayTheFallback()
    {
        var rig = Make("fallback: on", [new("paused-01", State.Paused)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.Empty(rig.Sender.Sent);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Elapsed);
        Assert.Empty(rig.Runner.WakeAttempts);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AnAsleepMachineWithoutWakeSettingsDoesNotDelayTheFallback()
    {
        var rig = Make("fallback: on", [new("nowol-01", State.Asleep, Wol: false)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.Empty(rig.Sender.Sent);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Elapsed);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PausedAndAsleepTogetherWakesOnlyTheSleeper()
    {
        var rig = Make("fallback: on", [new("a-paused", State.Paused), new("b-sleep", State.Asleep, BootSeconds: 20)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal("b-sleep", r.Machine);
        Assert.Equal(["b-sleep"], rig.WokenAt.Keys);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OffIsUnchangedNoWakeAndNoCapacityAtOnce()
    {
        var rig = Make("", [new("sleep-01", State.Asleep, BootSeconds: 10)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Empty(rig.Sender.Sent);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Elapsed);
        Assert.Empty(rig.Runner.WakeAttempts);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AnExplicitEligibleBudgetOfZeroNeverWakesAndFallsBackAtOnce()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 10)]);

        var r = await rig.Runner.RunAsync(Req() with { WaitGiven = true, EligibleWaitGiven = true }, CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WaitSlotAloneStillWakesBeforeFallingBack()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 30)]);

        var r = await rig.Runner.RunAsync(
            Req() with { WaitGiven = true, Wait = TimeSpan.FromSeconds(600), Budgets = new WaitBudgets(TimeSpan.Zero, TimeSpan.FromSeconds(600)) }, CancellationToken.None);

        Assert.Equal("sleep-01", r.Machine);
        Assert.Single(rig.Sender.Sent);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AskWakesFirstAndDoesNotAskWhenTheMachineComesUp()
    {
        var rig = Make("fallback: ask", [new("sleep-01", State.Asleep, BootSeconds: 30)], ask: true);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal("sleep-01", r.Machine);
        Assert.Empty(Prompts[rig]!.Questions);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AskWakesFirstThenAsksSayingTheWakeGotNoAnswer()
    {
        var rig = Make("fallback: ask", [new("sleep-01", State.Asleep, WaitSeconds: 60)], ask: true);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        var prompt = Prompts[rig]!;
        var q = Assert.Single(prompt.Questions);
        Assert.Equal(1, prompt.PacketsBefore[0]); // the packet went out before the question
        Assert.Contains("no pool machine can take it now; Wake-on-LAN: sleep-01 woken, no ssh answer within 60 s. Run it on this computer (dev-pc)?", q, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ANoAtThePromptEndsNotRunAndTheMessageNamesTheWake()
    {
        var rig = Make("fallback: ask", [new("sleep-01", State.Asleep, WaitSeconds: 60)], ask: true, answers: [false]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("Wake-on-LAN: sleep-01 woken, no ssh answer within 60 s.", r.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShardsWakeOneMachineAtATimeNeverTwiceAndThenFallBack()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, WaitSeconds: 300), new("sleep-02", State.Asleep, WaitSeconds: 300)]);

        var r = await rig.Runner.RunShardsAsync(Req(), new ShardOptions(3), CancellationToken.None);

        Assert.Equal(nameof(JobStatus.Completed), r.Status);
        Assert.Equal(3, r.FallbackJobs);
        Assert.Single(rig.Sender.Sent); // no wake storm: one pending wake at a time, each machine at most once
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 120, 400);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task APlanWakesEachMachineAtMostOncePerRun()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, WaitSeconds: 60), new("sleep-02", State.Asleep, WaitSeconds: 60)]);
        PlanJobSpec Job(string name, params string[] after) => new(name, Req() with { PlanJob = name }, null, after);

        var r = await rig.Runner.RunPlanAsync([Job("a"), Job("b", "a"), Job("c", "b")], false, CancellationToken.None);

        Assert.Equal(3, r.FallbackJobs);
        Assert.Equal(rig.WokenAt.Count, rig.Sender.Sent.Count);   // never the same machine twice
        Assert.Equal(["sleep-01", "sleep-02"], rig.WokenAt.Keys.Order(StringComparer.Ordinal));
        Assert.All(rig.Runner.WakeAttempts, a => Assert.Equal(WakeOutcome.NoAnswer, a.Outcome));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheDryRunPredictsTheWakeBeforeTheFallback()
    {
        Box[] asleep = [new("sleep-01", State.Asleep)];
        var on = await Make("fallback: on", asleep).Runner.PlanAsync(Req(), null, CancellationToken.None);
        Assert.Equal("sleep-01", on.WouldWake);
        Assert.Equal("dev-pc", on.WouldFallBack);

        Assert.Null((await Make("", asleep).Runner.PlanAsync(Req(), null, CancellationToken.None)).WouldWake);
        Assert.Null((await Make("fallback: on", [new("paused-01", State.Paused)]).Runner.PlanAsync(Req(), null, CancellationToken.None)).WouldWake);
        Assert.Null((await Make("fallback: on", asleep).Runner.PlanAsync(Req() with { NoWake = true }, null, CancellationToken.None)).WouldWake);

        var text = Cli.PlanText(ParsedArgs.Parse(["run", "--dry-run", "--needs", "dotnet", "--", "dotnet", "test"]), on);
        Assert.Contains("Would wake: sleep-01 (one Wake-on-LAN packet, then lanq run waits up to 120 s for it, never past its wake.wait_seconds, before falling back to this computer)", text, StringComparison.Ordinal);
    }

    // ---- JSON and next steps ----

    [Fact]
    public void JsonWakesAndNextStepsSayWhatTheWakeDid()
    {
        WakeAttempt[] wakes =
        [
            new("sleep-01", WakeOutcome.NoAnswer, 60),
            new("sleep-02", WakeOutcome.Waking, 300),
            new("sleep-03", WakeOutcome.Awake, 120),
            new("sleep-04", WakeOutcome.SendFailed, 120, "Network is unreachable"),
        ];
        var json = JsonSerializer.Serialize(new RunJson("Completed", 0, "dev-pc", "j", 1, "ok") { Wakes = WakeAttemptJson.Of(wakes) }, LanqJson.Default.RunJson);
        Assert.Contains("\"wakes\":[{\"machine\":\"sleep-01\",\"result\":\"NoAnswer\",\"waitSeconds\":60,\"message\":\"sleep-01 woken, no ssh answer within 60 s\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"Waking\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"Awake\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"SendFailed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("wakes", JsonSerializer.Serialize(new RunJson("Completed", 0, "x", "j", 1, "ok") { Wakes = WakeAttemptJson.Of([]) }, LanqJson.Default.RunJson), StringComparison.Ordinal);

        var steps = NextSteps.WithWakes(ParsedArgs.Parse(["run", "--", "dotnet", "test"]), [], wakes);
        Assert.Equal(3, steps.Count); // nothing for the machine that came up
        Assert.Equal("ask_developer", steps[0].Action);
        Assert.Contains("sleep-01 woken, no ssh answer within 60 s: Wake-on-LAN may be off", steps[0].Reason, StringComparison.Ordinal);
        Assert.Equal("run_health", steps[1].Action);
        Assert.Equal("lanq health --json", steps[1].Command); // read-only, never a wake
        Assert.Equal("ask_developer", steps[2].Action);
        Assert.All(steps, s => Assert.DoesNotContain("wake", s.Command ?? "", StringComparison.Ordinal));
    }
}
