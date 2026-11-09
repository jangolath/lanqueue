using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

internal sealed class FakeSender : IMagicPacketSender
{
    public List<(byte[] Packet, IPAddress Broadcast, int Port)> Sent { get; } = [];

    public Exception? Failure { get; set; }

    public Action? OnSend { get; set; }

    public Task SendAsync(byte[] packet, IPAddress broadcast, int port, CancellationToken ct)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Sent.Add((packet, broadcast, port));
        OnSend?.Invoke();
        return Task.CompletedTask;
    }
}

/// <summary>A TimeProvider whose timers fire only when the test advances time, so backoff and timeouts take no real time.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private sealed class FakeTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Due = null;
            return ValueTask.CompletedTask;
        }
    }

    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(t);
        }

        t.Change(dueTime, period);
        return t;
    }

    /// <summary>Moves the clock to the earliest pending timer and fires it; false when none is pending.</summary>
    public bool AdvanceToNext()
    {
        FakeTimer? next;
        lock (_timers)
        {
            next = _timers.Where(t => t.Due is not null).MinBy(t => t.Due);
        }

        if (next?.Due is not { } due)
        {
            return false;
        }

        _now = due > _now ? due : _now;
        next.Due = null;
        next.Fire();
        return true;
    }

    public void AdvanceTo(DateTimeOffset target)
    {
        while (_timers.Any(t => t.Due <= target) && AdvanceToNext())
        {
        }

        _now = target;
    }

    /// <summary>Awaits <paramref name="task"/> while advancing time whenever it is waiting on a timer.</summary>
    public async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 100_000 && !task.IsCompleted; i++)
        {
            if (!AdvanceToNext())
            {
                await Task.Delay(1);
            }
            else
            {
                await Task.Yield();
            }
        }

        return await task;
    }
}

public sealed class WakeTests
{
    private const string Mac = "aa:bb:cc:dd:ee:ff";
    private static readonly DateTimeOffset Noon = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Night = new(2026, 1, 5, 23, 30, 0, TimeSpan.Zero);

    private const string Up = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok";
    private const string TimedOut = "ssh: connect to host build-02.lan port 22: Connection timed out";

    private static Machine Sleepy(string? hours = "22:00-07:00", int wait = 120)
    {
        var m = T.Windows();
        m.AllowedHours = hours is null ? null : [hours];
        m.Wake = new WakeConfig { Mac = Mac, WaitSeconds = wait };
        return m;
    }

    private static MachineHealth Down(bool asleep = true, bool paused = false) =>
        new("build-02", false, 0, 0, 0, "unreachable", "x") { HoldKind = HoldKind.Unreachable, LooksAsleep = asleep, Paused = paused };

    // ---- packet and values ----

    [Fact]
    public void PacketIsSixFFThenTheMacSixteenTimes()
    {
        Assert.True(MagicPacket.TryParseMac(Mac, out var mac));
        var packet = MagicPacket.Build(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(mac, packet.Skip(6 + (i * 6)).Take(6).ToArray());
        }
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("00:1a:2B:3c:4D:5e")]
    public void GoodMacsAreAccepted(string text) => Assert.True(MagicPacket.TryParseMac(text, out var mac) && mac.Length == 6);

    [Theory]
    [InlineData("")]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("aa:bb:cc:dd:ee:ff:00")]
    [InlineData("aa:bb:cc:dd:ee:fg")]
    [InlineData("aabbccddeeff")]
    [InlineData("aa:bb-cc:dd-ee:ff")]
    [InlineData("aa.bb.cc.dd.ee.ff")]
    [InlineData("aa:bb:cc:dd:ee:ff\n")]
    [InlineData(" aa:bb:cc:dd:ee:ff")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("ff:ff:ff:ff:ff:ff")]
    public void BadMacsAreRefused(string text) => Assert.False(MagicPacket.TryParseMac(text, out _));

    [Theory]
    [InlineData("255.255.255.255", true)]
    [InlineData("10.20.30.255", true)]
    [InlineData("8.8.8.255", true)]
    [InlineData("nas.lan", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("10.20.30", false)]
    [InlineData("127.1", false)]
    [InlineData("010.020.030.255", false)]
    [InlineData("10.20.30.256", false)]
    [InlineData("10.20.30.255\n", false)]
    public void BroadcastMustBeAnIpv4Literal(string text, bool ok) => Assert.Equal(ok, MagicPacket.TryParseBroadcast(text, out _));

    [Theory]
    [InlineData("255.255.255.255", true)]
    [InlineData("10.1.2.255", true)]
    [InlineData("172.16.0.255", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("10.20.30.255", true)]
    [InlineData("169.254.255.255", true)]
    [InlineData("172.32.0.255", false)]
    [InlineData("8.8.8.255", false)]
    [InlineData("203.0.113.255", false)]
    public void OnlyPrivateAndLinkLocalBroadcastsAreQuiet(string text, bool lan)
    {
        var m = Sleepy();
        m.Wake!.Broadcast = text;
        Assert.Equal(lan, MagicPacket.BroadcastWarning(m) is null);
    }

    // ---- config ----

    private static string Yaml(string wake) => T.Yaml.Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: \"22:00-07:00\"\n    " + wake, StringComparison.Ordinal);

    [Fact]
    public void WakeDefaultsAreBroadcastPort9AndTwoMinutes()
    {
        var w = ConfigLoader.Parse(Yaml("wake: { mac: \"AA-BB-CC-DD-EE-FF\" }")).Machines[1].Wake!;

        Assert.Equal("AA-BB-CC-DD-EE-FF", w.Mac);
        Assert.Equal("255.255.255.255", w.Broadcast);
        Assert.Equal(9, w.Port);
        Assert.Equal(120, w.WaitSeconds);
        Assert.Null(ConfigLoader.Parse(T.Yaml).Machines[1].Wake);
    }

    [Fact]
    public void WakeValuesAreRead()
    {
        var w = ConfigLoader.Parse(Yaml("wake: { mac: \"aa:bb:cc:dd:ee:ff\", broadcast: 10.20.30.255, port: 7, wait_seconds: 300 }")).Machines[1].Wake!;

        Assert.Equal("10.20.30.255", w.Broadcast);
        Assert.Equal(7, w.Port);
        Assert.Equal(300, w.WaitSeconds);
    }

    [Theory]
    [InlineData("wake: {}", "wake.mac")]
    [InlineData("wake: { mac: \"nope\" }", "wake.mac")]
    [InlineData("wake: { mac: \"aa:bb:cc:dd:ee:ff\", broadcast: nas.lan }", "wake.broadcast")]
    [InlineData("wake: { mac: \"aa:bb:cc:dd:ee:ff\", broadcast: \"::1\" }", "wake.broadcast")]
    [InlineData("wake: { mac: \"aa:bb:cc:dd:ee:ff\", port: 80 }", "wake.port")]
    [InlineData("wake: { mac: \"aa:bb:cc:dd:ee:ff\", wait_seconds: 0 }", "wake.wait_seconds")]
    [InlineData("wake: { mac: \"aa:bb:cc:dd:ee:ff\", wait_seconds: 601 }", "wake.wait_seconds")]
    public void BadWakeSettingsAreRefusedByName(string wake, string key)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Yaml(wake)));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownWakeKeysAreRefused() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Yaml("wake: { mac: \"aa:bb:cc:dd:ee:ff\", password: x }")));

    // ---- what counts as asleep ----

    [Theory]
    [InlineData(255, "ssh: connect to host h port 22: Connection timed out", true)]
    [InlineData(255, "ssh: connect to host h port 22: No route to host", true)]
    [InlineData(255, "ssh: connect to host h port 22: Connection refused", true)]
    [InlineData(255, "ssh: connect to host h port 22: Network is unreachable", true)]
    [InlineData(255, "user@h: Permission denied (publickey).", false)]
    [InlineData(255, "Host key verification failed.", false)]
    [InlineData(255, "@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@\nConnection refused", false)]
    [InlineData(255, "ssh: Could not resolve hostname h: Name or service not known", false)]
    [InlineData(255, "", false)]
    [InlineData(1, "Connection timed out", false)]
    [InlineData(0, "Connection timed out", false)]
    public void OnlyConnectionTypeFailuresLookAsleep(int exit, string output, bool asleep) =>
        Assert.Equal(asleep, WakeService.LooksAsleep(new SshResult(exit, output)));

    [Fact]
    public void LanqsOwnTimeoutDoesNotLookAsleep() => Assert.False(WakeService.LooksAsleep(new SshResult(255, "Connection timed out", TimedOut: true)));

    // ---- decision table ----

    [Fact]
    public void DecisionTable()
    {
        var waker = new WakeService(new FakeSender(), new FakeClock(Night));
        var inHours = Night.DateTime;
        var outHours = Noon.DateTime;

        Assert.Equal(WakeDecision.Wake, waker.Evaluate(Sleepy(), Down(), inHours));
        Assert.Equal(WakeDecision.Wake, waker.Evaluate(Sleepy(hours: null), Down(), outHours)); // no hours: any time
        Assert.Equal(WakeDecision.OutsideHours, waker.Evaluate(Sleepy(), Down(), outHours));
        Assert.Equal(WakeDecision.NotAsleep, waker.Evaluate(Sleepy(), Down(asleep: false), inHours)); // auth or host-key failure
        Assert.Equal(WakeDecision.Paused, waker.Evaluate(Sleepy(), Down(paused: true), inHours));
        Assert.Equal(WakeDecision.NoWakeConfig, waker.Evaluate(T.Windows(), Down(), inHours));
        Assert.Equal(WakeDecision.NotAsleep, waker.Evaluate(Sleepy(), new MachineHealth("build-02", true, 0, 0, 0, null, null), inHours));
    }

    [Fact]
    public void ClaimWakesAMachineOnlyOncePerInvocation()
    {
        var waker = new WakeService(new FakeSender(), new FakeClock(Night));
        var m = Sleepy();

        Assert.Equal(WakeDecision.Wake, waker.TryClaim(m, Down(), Night.DateTime));
        Assert.Equal(WakeDecision.AlreadyWoken, waker.TryClaim(m, Down(), Night.DateTime));
    }

    // ---- probe integration ----

    private static (HealthProbe Probe, FakeSender Sender, WakeService Waker, FakeSsh Ssh) Rig(DateTimeOffset at, Func<Machine, string, SshResult> handler)
    {
        var sender = new FakeSender();
        var clock = new FakeClock(at);
        var waker = new WakeService(sender, clock);
        var ssh = new FakeSsh { Handler = handler };
        return (new HealthProbe(ssh, clock, waker), sender, waker, ssh);
    }

    [Fact]
    public async Task UnreachableMachineInsideItsHoursIsWokenOnceWithTheRightPacket()
    {
        var (probe, sender, _, _) = Rig(Night, (_, _) => new SshResult(255, TimedOut));
        var m = Sleepy();

        var first = await probe.ProbeAsync(m, T.Config().Policy, CancellationToken.None);
        await probe.ProbeAsync(m, T.Config().Policy, CancellationToken.None);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(102, sent.Packet.Length);
        Assert.Equal(IPAddress.Broadcast, sent.Broadcast);
        Assert.Equal(9, sent.Port);
        Assert.False(first.Reachable);
        Assert.Contains("waking", first.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWakeOutsideHoursOnAuthFailureWithoutWakeConfigOrWhenTheProbeIsNotAllowedToWake()
    {
        var (probe, sender, _, _) = Rig(Noon, (_, _) => new SshResult(255, TimedOut));
        await probe.ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None);
        Assert.Empty(sender.Sent);

        (probe, sender, _, _) = Rig(Night, (_, _) => new SshResult(255, "user@h: Permission denied (publickey)."));
        await probe.ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None);
        Assert.Empty(sender.Sent);

        (probe, sender, _, _) = Rig(Night, (_, _) => new SshResult(255, TimedOut));
        await probe.ProbeAsync(T.Windows(), T.Config().Policy, CancellationToken.None);
        await probe.ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None, allowWake: false);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task PausedMachineIsNeverWoken()
    {
        var (probe, sender, _, _) = Rig(Night, (_, _) => new SshResult(0, Up + "\npaused=1"));
        var h = await probe.ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None);

        Assert.True(h.Paused);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ProbeWithoutAWakerNeverSendsAnything()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(255, TimedOut) };
        var h = await new HealthProbe(ssh, new FakeClock(Night)).ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None, waitForWake: true);

        Assert.False(h.Reachable);
        Assert.Single(ssh.Calls);
    }

    [Fact]
    public async Task SendFailureIsReportedNotThrown()
    {
        var (probe, sender, _, _) = Rig(Night, (_, _) => new SshResult(255, TimedOut));
        sender.Failure = new SocketException((int)SocketError.NetworkUnreachable);

        var h = await probe.ProbeAsync(Sleepy(), T.Config().Policy, CancellationToken.None);

        Assert.False(h.Reachable);
        Assert.Contains("could not be sent", h.Error, StringComparison.Ordinal);
    }

    // ---- backoff and timeout with fake time ----

    [Fact]
    public async Task HealthWaitsWithBackoffUntilTheMachineAnswers()
    {
        var time = new ManualTime(Night);
        var sender = new FakeSender();
        var waker = new WakeService(sender, time);
        var probeTimes = new List<TimeSpan>();
        var ssh = new FakeSsh
        {
            Handler = (_, _) =>
            {
                probeTimes.Add(time.GetUtcNow() - Night);
                // the machine boots 40 s after the packet
                return time.GetUtcNow() - Night >= TimeSpan.FromSeconds(40) ? new SshResult(0, Up) : new SshResult(255, TimedOut);
            },
        };
        var probe = new HealthProbe(ssh, time, waker);
        var m = Sleepy();

        var h = await time.Drive(probe.ProbeAsync(m, T.Config().Policy, CancellationToken.None, waitForWake: true));

        Assert.True(h.Reachable);
        Assert.Single(sender.Sent);
        // first probe at 0 s, then 5 s, 15 s, 35 s (5, +10, +20), then 55 s which sees it up
        Assert.Equal([0, 5, 15, 35, 55], probeTimes.Select(t => (int)t.TotalSeconds));
        Assert.False(waker.AnyPending);
    }

    [Fact]
    public async Task HealthGivesUpAfterWaitSecondsAndSaysWhat()
    {
        var time = new ManualTime(Night);
        var sender = new FakeSender();
        var waker = new WakeService(sender, time);
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(255, TimedOut) };
        var probe = new HealthProbe(ssh, time, waker);
        var m = Sleepy(wait: 60);

        var h = await time.Drive(probe.ProbeAsync(m, T.Config().Policy, CancellationToken.None, waitForWake: true));

        Assert.False(h.Reachable);
        Assert.Contains("no ssh answer within 60 s", h.Error, StringComparison.Ordinal);
        Assert.Contains("BIOS", h.Error, StringComparison.Ordinal);
        Assert.Single(sender.Sent);
        Assert.Equal(TimeSpan.FromSeconds(60), time.GetUtcNow() - Night); // never waits past wait_seconds
        Assert.False(waker.AnyPending);
    }

    [Fact]
    public async Task WithoutWaitTheProbeReturnsAtOnceAndTheWakeStaysPendingUntilItsDeadline()
    {
        var time = new ManualTime(Night);
        var waker = new WakeService(new FakeSender(), time);
        var probe = new HealthProbe(new FakeSsh { Handler = (_, _) => new SshResult(255, TimedOut) }, time, waker);

        var h = await probe.ProbeAsync(Sleepy(wait: 30), T.Config().Policy, CancellationToken.None);

        Assert.Contains("waking", h.Error, StringComparison.Ordinal);
        Assert.True(waker.AnyPending);
        time.AdvanceTo(Night + TimeSpan.FromSeconds(31));
        Assert.False(waker.AnyPending);
    }

    // ---- lanq run keeps going on the other machines and waits for the waking one ----

    [Fact]
    public async Task RunUsesAnotherMachineWhileOneWakesAndWaitsForItWhenItIsTheOnlyChoice()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanq-wake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = T.Config();
            config.Machines.RemoveAt(0); // only the sleepy windows machine
            config.Machines[0].Wake = new WakeConfig { Mac = Mac };
            var clock = new FakeClock(Night);
            var sender = new FakeSender();
            var booted = false;
            sender.OnSend = () => booted = true;
            var ssh = new FakeSsh
            {
                Handler = (m, cmd) =>
                {
                    var text = cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
                        ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
                        : cmd;
                    return !booted ? new SshResult(255, TimedOut)
                        : text.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1")
                        : text.Contains("disk_free_gb", StringComparison.Ordinal) ? new SshResult(0, Up)
                        : new SshResult(0, "ran");
                },
            };
            var probe = new HealthProbe(ssh, clock, new WakeService(sender, clock));
            var runner = new JobRunner(
                config, ssh, new RunnerTests.FakePayloads(""), probe, new JobLog(Path.Combine(dir, ".lanq")), Path.Combine(dir, "tmp"), _ => { }, (_, _) => Task.CompletedTask, clock);

            var r = await runner.RunAsync(new JobRequest(["dotnet"], ["dotnet", "test"], ".", dir, TimeSpan.Zero), CancellationToken.None);

            Assert.Equal(JobStatus.Completed, r.Status); // --wait 0 still waited for the machine it had just woken
            Assert.Equal("build-02", r.Machine);
            Assert.Single(sender.Sent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- lanq wake ----

    [Fact]
    public void WakeArgumentsParse()
    {
        var p = ParsedArgs.Parse(["wake", "build-02", "--wait", "--json"]);

        Assert.Equal("build-02", p.Target);
        Assert.True(p.WaitForSsh);
        Assert.True(p.Json);
        Assert.False(ParsedArgs.Parse(["wake", "build-02"]).WaitForSsh);
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["wake", "build-02", "--needs", "x"]));
        Assert.Equal(30, ParsedArgs.Parse(["run", "--wait", "30", "--", "dotnet", "test"]).WaitSeconds);
    }

    private static async Task<(int Code, string Out, string Err, FakeSender Sender)> RunWake(
        string[] args, Func<Machine, string, SshResult>? handler = null, Action<Machine>? tweak = null, ManualTime? time = null, DateTimeOffset? at = null)
    {
        var config = T.Config();
        config.Machines[1].Wake = new WakeConfig { Mac = Mac, WaitSeconds = 60 };
        tweak?.Invoke(config.Machines[1]);
        var sender = new FakeSender();
        TimeProvider clock = time is null ? new FakeClock(at ?? Noon) : time; // Noon is outside build-02's hours: lanq wake sends anyway
        var ssh = new FakeSsh { Handler = handler ?? ((_, _) => new SshResult(0, Up)) };
        var o = new StringWriter();
        var e = new StringWriter();
        var task = Cli.Wake(ParsedArgs.Parse(args), config, new WakeService(sender, clock), new HealthProbe(ssh, clock), o, e, CancellationToken.None);
        var code = time is null ? await task : await time.Drive(task);
        return (code, o.ToString(), e.ToString(), sender);
    }

    [Fact]
    public async Task WakeSendsEvenOutsideHoursAndSaysSo()
    {
        var (code, o, _, sender) = await RunWake(["wake", "build-02"]);

        Assert.Equal(0, code);
        Assert.Single(sender.Sent);
        Assert.Contains("outside allowed hours", o, StringComparison.Ordinal);
        Assert.Contains("you asked", o, StringComparison.Ordinal);
        Assert.Contains("BIOS", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WakeJsonPrintsExactlyOneObjectAndHumanLinesGoToStderr()
    {
        var (code, o, e, _) = await RunWake(["wake", "build-02", "--json"]);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("Sent", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("build-02", doc.RootElement.GetProperty("machine").GetString());
        Assert.True(doc.RootElement.GetProperty("sent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("awake").ValueKind);
        Assert.Single(o.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("sending a Wake-on-LAN packet", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WakeWaitReportsAwakeWhenSshAnswers()
    {
        var time = new ManualTime(Noon);
        var up = 0;
        var (code, o, _, _) = await RunWake(["wake", "build-02", "--wait", "--json"], (_, _) => ++up < 3 ? new SshResult(255, TimedOut) : new SshResult(0, Up), time: time);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("Awake", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("awake").GetBoolean());
    }

    [Fact]
    public async Task WakeWaitReportsNoAnswerWithExit1AfterWaitSeconds()
    {
        var time = new ManualTime(Noon);
        var (code, o, _, _) = await RunWake(["wake", "build-02", "--wait", "--json"], (_, _) => new SshResult(255, TimedOut), time: time);

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("NoAnswer", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("awake").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(60), time.GetUtcNow() - Noon);
    }

    [Fact]
    public async Task WakeTextWaitSaysNoAnswerAndPointsAtTheDocs()
    {
        var (code, _, e, _) = await RunWake(["wake", "build-02", "--wait"], (_, _) => new SshResult(255, TimedOut), time: new ManualTime(Noon));

        Assert.Equal(1, code);
        Assert.Contains("no ssh answer within 60 s", e, StringComparison.Ordinal);
        Assert.Contains("WORKERS.md", e, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeUnknownMachineAndMissingWakeSettingAreExit2WithOneJsonObject(bool json)
    {
        var extra = json ? new[] { "--json" } : [];
        var (code, o, e, sender) = await RunWake(["wake", "nope", .. extra]);
        Assert.Equal(2, code);
        Assert.Contains("No machine named 'nope'", e, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
        AssertOneErrorObject(o, json, "Rejected", 2);

        (code, o, e, sender) = await RunWake(["wake", "build-01", .. extra]);
        Assert.Equal(2, code);
        Assert.Contains("no wake: setting", e, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
        AssertOneErrorObject(o, json, "Rejected", 2);

        (code, o, _, _) = await RunWake(["wake", .. extra]);
        Assert.Equal(2, code);
        AssertOneErrorObject(o, json, "Rejected", 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeSendFailureIsExit70(bool json)
    {
        var config = T.Config();
        config.Machines[1].Wake = new WakeConfig { Mac = Mac };
        var sender = new FakeSender { Failure = new SocketException((int)SocketError.NetworkUnreachable) };
        var o = new StringWriter();
        var e = new StringWriter();
        var args = json ? new[] { "wake", "build-02", "--json" } : ["wake", "build-02"];

        var code = await Cli.Wake(ParsedArgs.Parse(args), config, new WakeService(sender, TimeProvider.System), new HealthProbe(new FakeSsh(), TimeProvider.System), o, e, CancellationToken.None);

        Assert.Equal(70, code);
        Assert.Contains("could not send", e.ToString(), StringComparison.Ordinal);
        AssertOneErrorObject(o.ToString(), json, "InfraError", 70);
    }

    [Fact]
    public async Task WakeJsonStillPrintsOneObjectForBadArgumentsAndBadConfig()
    {
        var o = new StringWriter();
        var code = await Cli.RunAsync(["wake", "--nonsense", "--json"], o, new StringWriter(), CancellationToken.None);
        Assert.Equal(2, code);
        AssertOneErrorObject(o.ToString(), true, "Rejected", 2);

        o = new StringWriter();
        code = await Cli.RunAsync(["wake", "build-02", "--json"], o, new StringWriter(), CancellationToken.None, () => throw new ConfigException("bad config"));
        Assert.Equal(2, code);
        AssertOneErrorObject(o.ToString(), true, "Rejected", 2);
    }

    private static void AssertOneErrorObject(string stdout, bool json, string status, int code)
    {
        if (!json)
        {
            Assert.DoesNotContain("{", stdout, StringComparison.Ordinal);
            return;
        }

        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(code, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    // ---- doctor ----

    private static async Task<string> DoctorOut(Action<Machine> tweak)
    {
        var config = T.Config();
        tweak(config.Machines[1]);
        var host = new DoctorHost(
            (_, _) => Task.FromResult(new SshResult(0, "ok")),
            _ => null,
            _ => true,
            _ => Task.FromResult(false),
            Path.GetTempPath());
        var o = new StringWriter();
        var ssh = new FakeSsh();
        await Cli.Doctor(ParsedArgs.Parse(["doctor"]), () => config, host, ssh, new HealthProbe(ssh, TimeProvider.System), o, CancellationToken.None);
        return o.ToString();
    }

    [Fact]
    public async Task DoctorReportsWakeOkWithTheBiosNote()
    {
        var o = await DoctorOut(m => m.Wake = new WakeConfig { Mac = Mac, Broadcast = "10.20.30.255" });

        var line = o.Split('\n').Single(l => l.Contains("wake:", StringComparison.Ordinal));
        Assert.StartsWith("OK", line, StringComparison.Ordinal);
        Assert.Contains("10.20.30.255:9", line, StringComparison.Ordinal);
        Assert.Contains("BIOS/UEFI", line, StringComparison.Ordinal);
        Assert.Contains("wired Ethernet", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWarnsOnAPublicBroadcastAndSaysNothingWithoutWake()
    {
        var o = await DoctorOut(m => m.Wake = new WakeConfig { Mac = Mac, Broadcast = "8.8.8.255" });
        Assert.Contains(o.Split('\n'), l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("wake.broadcast 8.8.8.255", StringComparison.Ordinal));

        o = await DoctorOut(_ => { });
        Assert.DoesNotContain("wake", o, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the one real socket test ----

    [Fact]
    public async Task UdpSenderDeliversTheExactPacketToALoopbackListener()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        Assert.True(MagicPacket.TryParseMac(Mac, out var mac));
        var packet = MagicPacket.Build(mac);

        await new UdpMagicPacketSender().SendAsync(packet, IPAddress.Loopback, port, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await listener.ReceiveAsync(cts.Token);
        Assert.Equal(packet, received.Buffer);
    }
}
