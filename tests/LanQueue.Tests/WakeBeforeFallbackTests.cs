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
/// computer first wakes every sleeping pool machine that could take it, all at once, and waits one shared wait of up to 120 s from the first
/// packet (never past a machine's own wake.wait_seconds while it does not answer). The wait ends early only when the job is placed or every woken
/// machine is ruled out for good or paused. A machine known to be paused is never woken and never delays the fallback; off is unchanged; one
/// wake per machine per run; ask wakes first and asks only if still needed. Every packet goes to a fake sender; the tests keep the real default
/// wake.wait_seconds of 120 unless they say otherwise.
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

    /// <summary>
    /// A pool machine: its state, whether it has a wake: setting, its wake.wait_seconds, and after how long a packet brings it up (null: never).
    /// Once up, it may first report <see cref="Busy"/> ("load", "pressure" or "unreadable") for <see cref="BusySeconds"/>, as a Windows machine
    /// does just after boot, or report itself paused (<see cref="PausedOnBoot"/>: a PAUSE file it could not report while asleep).
    /// </summary>
    private sealed record Box(string Name, State State, bool Wol = true, int WaitSeconds = 120, int? BootSeconds = null)
    {
        public string[] Caps { get; init; } = ["dotnet"];

        public string Busy { get; init; } = "load";

        public int BusySeconds { get; init; }

        public bool PausedOnBoot { get; init; }
    }

    private static string MacOf(int i) => $"02:00:00:00:00:{i + 1:x2}";

    private string Yaml(string policy, IReadOnlyList<Box> pool, string devCaps = "dotnet")
    {
        var machines = string.Join("\n", pool.Select((b, i) => $"""
              - name: {b.Name}
                os: linux
                ssh: user@10.20.30.{11 + i}
                work_root: /srv/lanq
                capabilities: [{string.Join(", ", b.Caps)}]
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
                capabilities: [{devCaps}]
            """;
    }

    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok";

    /// <summary>What a machine busy just after boot reports: load or memory pressure from other work (0 lanq slots), or no readable load at all.</summary>
    private static string BusyOutput(string kind) => kind switch
    {
        "load" => "load=99\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0",
        "pressure" => Healthy + "\nmem_pressure=95\npaused=0",
        _ => "disk_free_gb=50\nslots_used=0\ntool=ok\npaused=0",
    };

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
        public System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> WokenAt { get; } = new(StringComparer.Ordinal);

        /// <summary>Called after each packet (a test may cancel the run there).</summary>
        public Action? OnPacket { get; set; }

        /// <summary>Every output line with the fake time it was written at.</summary>
        public List<(TimeSpan At, string Line)> Timed { get; } = [];
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

    private Rig Make(
        string policy, IReadOnlyList<Box> pool, bool ask = false, bool?[]? answers = null, string devCaps = "dotnet",
        Func<Rig, TimeSpan, CancellationToken, Task>? delay = null, IFallbackPrompt? prompt = null, int sleepProbeSeconds = 0)
    {
        var config = ConfigLoader.Parse(Yaml(policy, pool, devCaps));
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

            rig!.OnPacket?.Invoke();
        };
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
            {
                var text = Text(m, cmd);
                var box = pool.FirstOrDefault(b => b.Name == m.Name);
                DateTimeOffset? upAt = null;
                if (box is not null && box.State == State.Asleep)
                {
                    if (!(rig!.WokenAt.TryGetValue(box.Name, out var at) && box.BootSeconds is { } boot && clock.Now >= at + TimeSpan.FromSeconds(boot)))
                    {
                        clock.Now += TimeSpan.FromSeconds(sleepProbeSeconds); // ssh's connect timeout to a sleeper
                        return new SshResult(255, $"ssh: connect to host {m.Name}.lan port 22: Connection timed out");
                    }

                    upAt = at + TimeSpan.FromSeconds(box.BootSeconds!.Value);
                }

                if (text.Contains("disk_free_gb", StringComparison.Ordinal))
                {
                    var paused = box is not null && (box.State == State.Paused || (upAt is not null && box.PausedOnBoot));
                    return box is not null && upAt is { } up && clock.Now < up + TimeSpan.FromSeconds(box.BusySeconds) ? new SshResult(0, BusyOutput(box.Busy))
                        : new SshResult(0, Healthy + (paused ? "\npaused=1" : "\npaused=0"));
                }

                return text.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "ran");
            },
        };
        ScriptedPrompt? scripted = ask ? new ScriptedPrompt(() => sender.Sent.Count, answers ?? [true]) : null;
        prompt ??= scripted;
        var runner = new JobRunner(
            config, ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, clock, new WakeService(sender, clock)), new JobLog(Path.Combine(_dir, ".lanq")),
            Path.Combine(_dir, "tmp"),
            line =>
            {
                lock (_output)
                {
                    _output.Add(line);
                    rig?.Timed.Add((clock.Elapsed, line));
                }
            },
            delay: (d, c) =>
            {
                if (delay is not null)
                {
                    return delay(rig!, d, c);
                }

                if (c.IsCancellationRequested)
                {
                    return Task.FromCanceled(c); // as Task.Delay: a cancelled wait takes no time
                }

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
        Prompts[rig] = scripted;
        return rig;
    }

    private readonly Dictionary<Rig, ScriptedPrompt?> Prompts = [];

    /// <summary>The run's lines with their fake times, for a failure message.</summary>
    private static string Dump(Rig rig) => string.Join("\n", rig.Timed.Select(t => $"{t.At.TotalSeconds,6:0} {t.Line}"));

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
    public void TheWakeWaitNeverRaisesTheJobsOwnBudgets()
    {
        Box[] pool = [new("sleep-01", State.Asleep)];
        var on = Make("fallback: on", pool).Runner;

        // The shared wake wait is its own window, counted from the first packet (review M1); the eligible budget stays the job's own.
        Assert.Equal(TimeSpan.Zero, on.BudgetsOf(Req()).Eligible);
        Assert.Equal(TimeSpan.FromSeconds(120), on.BudgetsOf(Req()).Slot);
        var given = Req() with { WaitGiven = true, EligibleWaitGiven = true, Budgets = new WaitBudgets(TimeSpan.FromSeconds(30), TimeSpan.Zero) };
        Assert.Equal(TimeSpan.FromSeconds(30), on.BudgetsOf(given).Eligible);
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
        Assert.Equal(Start, waker.SentAt("sleep-01"));
        Assert.True(waker.IsLive("sleep-01"));
        clock.Now += TimeSpan.FromSeconds(121);
        Assert.True(waker.IsLive("sleep-01"));                                                   // its own wait_seconds (300) still run
        Assert.Equal(WakeOutcome.Waking, waker.Attempts[0].Outcome);
        clock.Now += TimeSpan.FromSeconds(180);
        Assert.False(waker.IsLive("sleep-01"));
        Assert.Equal(WakeOutcome.NoAnswer, waker.Attempts[0].Outcome);
        waker.MarkUp(m);
        Assert.Equal(WakeOutcome.Awake, waker.Attempts[0].Outcome);
        Assert.True(waker.IsLive("sleep-01"));                                                   // answering: it may still take the job (H1)

        var failing = new WakeService(new FakeSender { Failure = new System.Net.Sockets.SocketException() }, clock);
        failing.TryClaim(m, asleep, clock.Now.DateTime, policy);
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => failing.SendAsync(m, CancellationToken.None));
        Assert.Equal(WakeOutcome.SendFailed, Assert.Single(failing.Attempts).Outcome);
        Assert.Null(failing.SentAt("sleep-01"));                                                 // a failed send starts no wait
        Assert.False(failing.IsLive("sleep-01"));
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
    public async Task ShardsWakeEverySleeperOnceTogetherAndFallBackAfterOneSharedWait()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, WaitSeconds: 300), new("sleep-02", State.Asleep, WaitSeconds: 300)]);

        var r = await rig.Runner.RunShardsAsync(Req(), new ShardOptions(3), CancellationToken.None);

        Assert.Equal(nameof(JobStatus.Completed), r.Status);
        Assert.Equal(3, r.FallbackJobs);
        Assert.Equal(2, rig.Sender.Sent.Count);                                       // both at once, each once
        Assert.Equal(rig.WokenAt["sleep-01"], rig.WokenAt["sleep-02"]);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 120, 150);                     // one shared 120 s wait, not 120 s per sleeper
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ShardsUseAMachineThatWakesBusyOnceItIsFree()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 30) { BusySeconds = 40 }]);

        var r = await rig.Runner.RunShardsAsync(Req(), new ShardOptions(2), CancellationToken.None);

        Assert.Equal(nameof(JobStatus.Completed), r.Status);
        Assert.Equal(0, r.FallbackJobs);
        Assert.All(r.Shards, s => Assert.Equal("sleep-01", s.Machine));
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

        // Review L6: the last line no longer says the run would end at once; it says what wake before fallback does.
        var last = text.TrimEnd().Split('\n')[^1];
        Assert.DoesNotContain("exit 75 at once", last, StringComparison.Ordinal);
        Assert.Contains("up to 120 s", last, StringComparison.Ordinal);
        Assert.Contains("this computer", last, StringComparison.Ordinal);

        // An explicit --wait 0 keeps the old line.
        var waitZero = Cli.PlanText(ParsedArgs.Parse(["run", "--dry-run", "--wait", "0", "--needs", "dotnet", "--", "dotnet", "test"]), on with { WouldWake = null, WouldWakeAll = null });
        Assert.DoesNotContain("Would wake", waitZero, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheDryRunNamesEverySleeperItWouldWake()
    {
        var plan = await Make("fallback: on", [new("sleep-02", State.Asleep), new("sleep-01", State.Asleep), new("paused-01", State.Paused)]).Runner.PlanAsync(Req(), null, CancellationToken.None);

        Assert.Equal("sleep-01", plan.WouldWake);
        Assert.Equal(["sleep-01", "sleep-02"], plan.WouldWakeAll);
        var text = Cli.PlanText(ParsedArgs.Parse(["run", "--dry-run", "--needs", "dotnet", "--", "dotnet", "test"]), plan);
        Assert.Contains("Would wake: sleep-01, sleep-02 (one Wake-on-LAN packet each, at once, then lanq run waits up to 120 s for them", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheDryRunTreatsAskThatCouldPromptAsAsk()
    {
        // Review L6: the dry run has no terminal prompt of its own, but where the real run could ask it must predict the wake and the fallback.
        var config = ConfigLoader.Parse(Yaml("fallback: ask", [new("sleep-01", State.Asleep)]));
        var p = ParsedArgs.Parse(["run", "--dry-run", "--needs", "dotnet", "--", "dotnet", "test"]);
        var stand = Cli.DryRunPrompt(p, config, whyNot: null);
        Assert.NotNull(stand);
        Assert.Null(Cli.DryRunPrompt(p, config, whyNot: "no terminal"));
        Assert.Null(Cli.DryRunPrompt(p, ConfigLoader.Parse(Yaml("fallback: on", [new("sleep-01", State.Asleep)])), whyNot: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stand!.AskAsync("?", CancellationToken.None)); // a dry run never asks

        var plan = await Make("fallback: ask", [new("sleep-01", State.Asleep)], prompt: stand).Runner.PlanAsync(Req(), null, CancellationToken.None);
        Assert.Equal("sleep-01", plan.WouldWake);
        Assert.Equal("dev-pc", plan.WouldFallBack);
        Assert.Null((await Make("fallback: ask", [new("sleep-01", State.Asleep)]).Runner.PlanAsync(Req(), null, CancellationToken.None)).WouldWake); // cannot ask: off
    }

    // ---- review fixes: one shared wait from the first packet (M1), busy after boot (H1), all sleepers at once (M2), only useful wakes (M3) ----

    [Fact(Timeout = TestTimeout)]
    public async Task TwoSleepersAtTheDefault120sAreWokenTogetherAndTheRunFallsBackOnceAfter120s()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep), new("sleep-02", State.Asleep)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.Equal(2, rig.Sender.Sent.Count);
        Assert.Equal(TimeSpan.Zero, rig.WokenAt["sleep-01"] - rig.WokenAt["sleep-02"]); // in one look, not one after the other
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 120, 135);
        var line = Assert.Single(_output, l => l.Contains("Wake-on-LAN packet", StringComparison.Ordinal));
        Assert.Contains("sent Wake-on-LAN packets to sleep-01, sleep-02 (waiting up to 120 s for them before falling back to this computer)", line, StringComparison.Ordinal);
        Assert.All(rig.Runner.WakeAttempts, a => Assert.Equal(WakeOutcome.NoAnswer, a.Outcome));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TheSharedWaitCountsFromTheFirstPacketNotFromTheJobsStart()
    {
        // ssh takes 20 s to give up on a sleeper, so the packet goes out at about 20 s: the 120 s run from there.
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep)], sleepProbeSeconds: 20);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        var sent = rig.WokenAt["sleep-01"] - Start;
        Assert.True(sent >= TimeSpan.FromSeconds(20), "sent at " + sent);
        var fellBack = rig.Timed.Single(t => t.Line.Contains("running on this computer", StringComparison.Ordinal)).At;
        Assert.True(fellBack >= sent + TimeSpan.FromSeconds(120), $"fell back at {fellBack}, packet at {sent}");
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("load")]
    [InlineData("pressure")]
    [InlineData("unreadable")]
    public async Task AWokenMachineBusyAfterBootKeepsTheWaitGoingAndTakesTheJob(string busy)
    {
        // Review H1: it answers ssh at 40 s but is held (load or memory pressure from its own boot, or an unreadable probe) until 70 s.
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 40) { Busy = busy, BusySeconds = 30 }]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("sleep-01", r.Machine);
        Assert.False(r.Fallback);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 70, 120);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AWokenMachineStillBusyAtTheEndOfTheSharedWaitFallsBackThen()
    {
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 40) { BusySeconds = 600 }]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 120, 135);
        Assert.Contains(_output, l => l.Contains("running on this computer (fallback: no pool machine can take it now; Wake-on-LAN: sleep-01 woken and answering ssh)", StringComparison.Ordinal));
        Assert.Equal(WakeOutcome.Awake, Assert.Single(rig.Runner.WakeAttempts).Outcome);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AWokenMachineFoundPausedEndsTheWaitAtOnce()
    {
        // Review H1 (b) and L4: paused while asleep, so it could not say so and was woken; once it answers as paused it no longer delays the fallback.
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep, BootSeconds: 40) { PausedOnBoot = true }]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.True(r.Fallback);
        Assert.Single(rig.Sender.Sent);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 40, 60);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AMachineWithBrokenWakeOnLanDoesNotHoldBackAnotherSleeper()
    {
        // Review M2: a-broken never comes up; b-fine boots in 30 s. Both are woken at once, so the job runs on b-fine at about 30 s.
        var rig = Make("fallback: on", [new("a-broken", State.Asleep), new("b-fine", State.Asleep, BootSeconds: 30)]);

        var r = await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal("b-fine", r.Machine);
        Assert.False(r.Fallback);
        Assert.InRange(rig.Clock.Elapsed.TotalSeconds, 30, 60);
        var wakes = rig.Runner.WakeAttempts.ToDictionary(a => a.Machine, a => a.Outcome);
        Assert.Equal(WakeOutcome.Waking, wakes["a-broken"]);                               // the per-machine outcome is in JSON wakes
        Assert.Equal(WakeOutcome.Awake, wakes["b-fine"]);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task APlanPaysTheSharedWaitOncePerRunNotOncePerJob()
    {
        // Review M2: three jobs one after another and two sleepers that never answer, at the default 120 s: one 120 s wait in all.
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep), new("sleep-02", State.Asleep)]);
        PlanJobSpec Job(string name, params string[] after) => new(name, Req() with { PlanJob = name }, null, after);

        var r = await rig.Runner.RunPlanAsync([Job("a"), Job("b", "a"), Job("c", "b")], false, CancellationToken.None);

        Assert.Equal(3, r.FallbackJobs);
        Assert.Equal(2, rig.Sender.Sent.Count);
        var packet = rig.WokenAt.Values.Min() - Start;
        var fellBack = rig.Timed.Where(t => t.Line.Contains("running on this computer", StringComparison.Ordinal)).Select(t => t.At).ToList();
        Assert.Equal(3, fellBack.Count);
        Assert.True(fellBack[0] - packet >= TimeSpan.FromSeconds(120) && fellBack[0] - packet <= TimeSpan.FromSeconds(135), Dump(rig)); // a: the shared wait
        // b and c: at their first look (after the plan's 30 s start stagger at most), never another 120 s.
        Assert.True(fellBack[1] - fellBack[0] <= JobRunner.PlanStagger && fellBack[2] - fellBack[1] <= JobRunner.PlanStagger, Dump(rig));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ParallelPlanJobsWakeEachSleeperOnceInAll()
    {
        // Review L1: jobs that look at the same time never wake a machine twice, and every sleeper is woken in that first look.
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep), new("sleep-02", State.Asleep), new("sleep-03", State.Asleep)]);
        PlanJobSpec Job(string name) => new(name, Req() with { PlanJob = name }, null, []);

        var r = await rig.Runner.RunPlanAsync([Job("a"), Job("b"), Job("c")], false, CancellationToken.None);

        Assert.Equal(3, r.FallbackJobs);
        Assert.True(rig.Sender.Sent.Count == 3, Dump(rig));
        Assert.Equal(3, rig.WokenAt.Count);
        Assert.Equal(3, rig.Runner.WakeAttempts.Count);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task APlanJobWaitsOnlyForWakesOnMachinesItCouldUse()
    {
        // Review M3: job test (dotnet) wakes build-02, which has no docker; job docker must not wait for it, nor name it.
        var rig = Make("fallback: on", [new("build-02", State.Asleep)], devCaps: "dotnet, docker");
        var test = new PlanJobSpec("test", Req() with { PlanJob = "test" }, null, []);
        var docker = new PlanJobSpec("docker", new JobRequest(["docker"], ["dotnet", "test"], ".", _dir, TimeSpan.Zero) { PlanJob = "docker" }, null, []);

        var r = await rig.Runner.RunPlanAsync([test, docker], false, CancellationToken.None);

        Assert.Equal(2, r.FallbackJobs);
        Assert.Single(rig.Sender.Sent);
        var packet = rig.WokenAt["build-02"] - Start;
        var lines = rig.Timed.Where(t => t.Line.Contains("running on this computer", StringComparison.Ordinal)).ToList();
        var dockerLine = Assert.Single(lines, t => t.Line.Contains("[docker", StringComparison.Ordinal));
        var testLine = Assert.Single(lines, t => t.Line.Contains("[test", StringComparison.Ordinal));
        var dockerStart = rig.Timed.First(t => t.Line.Contains("[docker", StringComparison.Ordinal)).At;
        Assert.DoesNotContain("Wake-on-LAN", dockerLine.Line, StringComparison.Ordinal);
        Assert.True(dockerLine.At == dockerStart, Dump(rig));                                   // at its first look: no wait for build-02
        Assert.Contains("Wake-on-LAN: build-02", testLine.Line, StringComparison.Ordinal);
        Assert.True(testLine.At - packet >= TimeSpan.FromSeconds(120), Dump(rig));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task UnderAskTheWakeLineSaysItAsksBeforeUsingThisComputer()
    {
        // Review L7: under ask nothing falls back without a yes.
        var rig = Make("fallback: ask", [new("sleep-01", State.Asleep)], ask: true);

        await rig.Runner.RunAsync(Req(), CancellationToken.None);

        Assert.Contains(_output, l => l.Contains("sent a Wake-on-LAN packet to sleep-01 (waiting up to 120 s for it before asking to use this computer)", StringComparison.Ordinal));
        Assert.DoesNotContain(_output, l => l.Contains("before falling back", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCDuringTheWakeWaitCancelsAndKeepsTheWake()
    {
        // Review L8: Ctrl+C while the run waits for the woken machine.
        using var cts = new CancellationTokenSource();
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep)]);
        rig.OnPacket = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Runner.RunAsync(Req(), cts.Token));

        Assert.Equal(WakeOutcome.Waking, Assert.Single(rig.Runner.WakeAttempts).Outcome);
        Assert.True(rig.Clock.Elapsed < TimeSpan.FromSeconds(120));
        Assert.DoesNotContain(rig.Ssh.Calls, c => c.Machine == "dev-pc" && c.Command.Contains("SLOT=", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CtrlCDuringTheWakeWaitStillPutsWakesInTheJson()
    {
        // Review L8, through lanq run --json: Cancelled, exit 130, and the packet it sent is still reported.
        using var cts = new CancellationTokenSource();
        var rig = Make("", [new("sleep-01", State.Asleep)]);
        rig.OnPacket = cts.Cancel;
        var config = ConfigLoader.Parse(Yaml("", [new("sleep-01", State.Asleep)]));
        var probe = new HealthProbe(rig.Ssh, rig.Clock, new WakeService(rig.Sender, rig.Clock));
        var o = new StringWriter();

        var code = await Cli.Run(
            ParsedArgs.Parse(["run", "--json", "--needs", "dotnet", "--wait", "300", "--", "dotnet", "test"]), config, rig.Ssh, new RunnerTests.FakePayloads(""), probe,
            new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "home"), _dir, o, new StringWriter(), cts.Token);

        Assert.Equal(130, code);
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Cancelled", doc.RootElement.GetProperty("status").GetString());
        var wake = Assert.Single(doc.RootElement.GetProperty("wakes").EnumerateArray());
        Assert.Equal("sleep-01", wake.GetProperty("machine").GetString());
        Assert.Equal("Waking", wake.GetProperty("result").GetString());
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PlanTimeoutDuringTheWakeWaitEndsThePlanWithoutFallingBack()
    {
        // Review L8: plan_timeout is reached while the job waits for the woken machine.
        var limit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = Make("fallback: on", [new("sleep-01", State.Asleep)], delay: (rig, d, c) =>
        {
            if (d == TimeSpan.FromMinutes(1))
            {
                return limit.Task.WaitAsync(c); // the plan timer
            }

            if (rig.Sender.Sent.Count > 0)
            {
                return Task.Delay(Timeout.InfiniteTimeSpan, c); // the wake wait: held until the limit cancels it
            }

            rig.Clock.Now += d;
            return Task.CompletedTask;
        });
        rig.OnPacket = () => limit.TrySetResult();

        var r = await rig.Runner.RunPlanAsync([new PlanJobSpec("a", Req() with { PlanJob = "a" }, null, [])], false, CancellationToken.None, planTimeoutMinutes: 1);

        Assert.True(r.PlanTimedOut);
        Assert.Equal(0, r.FallbackJobs);
        Assert.Equal(WakeOutcome.Waking, Assert.Single(rig.Runner.WakeAttempts).Outcome);
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
