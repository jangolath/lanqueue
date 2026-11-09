using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// Availability windows v2: day lists, the worker's clock, "window closing" and the window-close stop. Every time is fixed (2026-01-02 is
/// a Friday) and every zone is a fixed offset, so nothing depends on the machine running the tests or on DST.
/// </summary>
public sealed class WindowTests : IDisposable
{
    private const char NL = (char)10;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-win-" + Guid.NewGuid().ToString("N"));

    public WindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>"Fri 23:00" style: a day of the first week of January 2026 (Fri = Jan 2) at a time.</summary>
    private static DateTime At(string dayTime)
    {
        var parts = dayTime.Split(' ');
        var day = Array.IndexOf(["Fri", "Sat", "Sun", "Mon", "Tue", "Wed", "Thu"], parts[0]);
        return new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified).AddDays(day) + TimeSpan.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    private static TimeSpan? Remaining(string at, params string[] entries) => HoursWindow.Remaining(HoursWindow.ParseAll(entries)!, At(at));

    [Theory]
    [InlineData("22:00-07:00", "Mon 23:00", true)]
    [InlineData("22:00-07:00", "Mon 03:00", true)]
    [InlineData("22:00-07:00", "Mon 12:00", false)]
    [InlineData("09:00-17:00", "Mon 12:00", true)]
    [InlineData("09:00-17:00", "Mon 18:00", false)]
    [InlineData("09:00-17:00", "Mon 09:00", true)]
    [InlineData("09:00-17:00", "Mon 17:00", false)]
    [InlineData("Fri 22:00-07:00", "Fri 23:00", true)]
    [InlineData("Fri 22:00-07:00", "Sat 03:00", true)]
    [InlineData("Fri 22:00-07:00", "Sun 03:00", false)]
    [InlineData("Fri 22:00-07:00", "Fri 03:00", false)]
    [InlineData("Mon-Fri 18:00-23:00", "Wed 19:00", true)]
    [InlineData("Mon-Fri 18:00-23:00", "Sat 19:00", false)]
    [InlineData("Sat,Sun 08:00-22:00", "Sun 09:00", true)]
    [InlineData("Sat,Sun 08:00-22:00", "Mon 09:00", false)]
    [InlineData("Fri-Mon 10:00-11:00", "Sun 10:30", true)]
    [InlineData("Fri-Mon 10:00-11:00", "Tue 10:30", false)]
    [InlineData("Mon,Wed-Thu 10:00-11:00", "Thu 10:30", true)]
    [InlineData("Mon,Wed-Thu 10:00-11:00", "Tue 10:30", false)]
    [InlineData("sat, sun 08:00 - 22:00", "Sat 09:00", true)]
    [InlineData("Sun 22:00-02:00", "Mon 01:00", true)]
    [InlineData("Sun 22:00-02:00", "Sun 01:00", false)]
    public void WindowMath(string entry, string at, bool allowed) =>
        Assert.Equal(allowed, Remaining(at, entry) is not null);

    [Fact]
    public void RemainingRunsToTheEndOfTheWindowTheMachineIsIn()
    {
        Assert.Equal(TimeSpan.FromHours(4), Remaining("Sat 03:00", "Fri 22:00-07:00"));
        Assert.Equal(TimeSpan.FromMinutes(90), Remaining("Wed 21:30", "Mon-Fri 18:00-23:00"));
        Assert.Null(Remaining("Sat 08:00", "Fri 22:00-07:00"));
    }

    [Fact]
    public void ContinuingWindowsAreOneStretch()
    {
        Assert.Equal(TimeSpan.FromHours(9), Remaining("Sat 03:00", "Fri 22:00-07:00", "Sat 07:00-12:00"));
        Assert.Equal(new TimeSpan(27, 59, 0), Remaining("Sat 20:00", "Sat 00:00-23:59", "Sun 00:00-23:59")); // a one-minute gap still joins
        Assert.Equal(TimeSpan.FromHours(3), Remaining("Mon 20:00", "Mon 18:00-23:00", "Mon 19:00-21:00")); // overlapping: the later end wins
        Assert.Equal(TimeSpan.FromHours(2), Remaining("Mon 05:00", "05:00-07:00", "07:30-09:00")); // a 30-minute gap does not join
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Mon-Fry 09:00-17:00")]
    [InlineData("Mon-Tue-Wed 09:00-17:00")]
    [InlineData("Mon,,Tue 09:00-17:00")]
    [InlineData("Monday 09:00-17:00")]
    [InlineData("Mon 9:00-17:00")]
    [InlineData("25:00-07:00")]
    [InlineData("09:00-17:00 Mon")]
    [InlineData("09:00-17:00\n")]
    public void BadEntriesDoNotParse(string entry) => Assert.False(HoursWindow.TryParse(entry, out _));

    private static string WithHours(string yaml) => T.Yaml.Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: " + yaml, StringComparison.Ordinal);

    [Fact]
    public void AllowedHoursAcceptsAStringOrAList()
    {
        Assert.Equal(["22:00-07:00"], T.Windows().AllowedHours);
        var c = ConfigLoader.Parse(WithHours("[\"Mon-Fri 18:00-23:00\", \"Sat,Sun 08:00-22:00\"]"));
        Assert.Equal(["Mon-Fri 18:00-23:00", "Sat,Sun 08:00-22:00"], c.Machines[1].AllowedHours);
        var block = ConfigLoader.Parse(WithHours(NL + "      - Fri 22:00-07:00" + NL + "      - Sat 09:00-12:00"));
        Assert.Equal(["Fri 22:00-07:00", "Sat 09:00-12:00"], block.Machines[1].AllowedHours);
    }

    [Fact]
    public void ABadEntryIsNamedInTheError()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithHours("[\"Mon-Fri 18:00-23:00\", \"Sun 25:00-26:00\"]")));
        Assert.Contains("'Sun 25:00-26:00'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Mon-Fri", ex.Message, StringComparison.Ordinal); // shows the grammar by example
    }

    [Fact]
    public void SameStartAndEndInAListIsRefused()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithHours("[\"Mon 09:00-10:00\", \"Sat 07:30-07:30\"]")));
        Assert.Contains("'Sat 07:30-07:30'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("start and end", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>As in 0.3: a bare "allowed_hours:" or "~" (YAML null) means no hours set, so any time.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("~")]
    [InlineData("null")]
    public void NullAllowedHoursMeansAnyTime(string yaml) =>
        Assert.Null(ConfigLoader.Parse(WithHours(yaml)).Machines[1].AllowedHours);

    [Fact]
    public void AQuotedTildeIsStillAnEntryAndIsRefused() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithHours("\"~\"")));

    [Theory]
    [InlineData("[]")]
    [InlineData("[[\"09:00-10:00\"]]")]
    [InlineData("{ from: \"09:00\" }")]
    public void EmptyOrNestedAllowedHoursIsRefused(string yaml) =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithHours(yaml)));

    [Fact]
    public void WindowPolicyDefaultsAndValidation()
    {
        Assert.Equal(15, T.Config().Policy.WindowGraceMinutes);
        Assert.Equal(10, T.Config().Policy.MinWindowMinutes);
        foreach (var bad in new[] { "window_grace_minutes: -1", "min_window_minutes: -1", "window_grace_minutes: 100000" })
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8" + NL + "  " + bad, StringComparison.Ordinal)));
            Assert.Contains(bad.Split(':')[0], ex.Message, StringComparison.Ordinal);
        }
    }

    // ---- DST: the worker's UTC offset table ----

    /// <summary>A probe-style offset table: <paramref name="before"/> minutes for the first <paramref name="hoursBefore"/> hourly samples, then <paramref name="after"/>.</summary>
    private static UtcOffsetTable Table(DateTimeOffset nowUtc, int before, int hoursBefore, int after) =>
        new(nowUtc.ToUnixTimeSeconds(), [.. Enumerable.Range(0, 37).Select(k => k < hoursBefore ? before : after)]);

    private static TimeSpan? RemainingAt(DateTime localNow, UtcOffsetTable table, params string[] entries) =>
        HoursWindow.Remaining(HoursWindow.ParseAll(entries)!, localNow, table);

    [Fact]
    public void SpringForwardNightCountsRealTimeNotWallClockTime()
    {
        // US Eastern, 2026-03-08: 02:00 EST (07:00Z) jumps to 03:00 EDT. Now: Saturday 23:00 EST = Sunday 04:00Z.
        var nowUtc = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero);
        var table = Table(nowUtc, -300, 3, -240);
        var localNow = new DateTime(2026, 3, 7, 23, 0, 0);
        Assert.Equal(TimeSpan.FromHours(6), RemainingAt(localNow, table, "Sat 22:00-06:00")); // 06:00 EDT = 10:00Z; the wall clock says 7 h
        Assert.Equal(TimeSpan.FromHours(3), RemainingAt(localNow, table, "Sat 22:00-02:30")); // 02:30 does not exist: the window ends at the jump (07:00Z)
    }

    [Fact]
    public void FallBackNightCountsRealTimeAndAnAmbiguousEndIsTheEarlierInstant()
    {
        // US Eastern, 2026-11-01: 02:00 EDT (06:00Z) falls back to 01:00 EST. Now: Saturday 23:00 EDT = Sunday 03:00Z.
        var nowUtc = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
        var table = Table(nowUtc, -240, 3, -300);
        var localNow = new DateTime(2026, 10, 31, 23, 0, 0);
        Assert.Equal(TimeSpan.FromHours(6), RemainingAt(localNow, table, "Sat 22:00-04:00")); // 04:00 EST = 09:00Z; the wall clock says 5 h
        Assert.Equal(TimeSpan.FromMinutes(150), RemainingAt(localNow, table, "Sat 22:00-01:30")); // 01:30 happens twice: the first (05:30Z)
    }

    [Fact]
    public void WithoutATableRemainingIsWallClockTime() =>
        Assert.Equal(TimeSpan.FromHours(7), HoursWindow.Remaining(HoursWindow.ParseAll(["Sat 22:00-06:00"])!, new DateTime(2026, 3, 7, 23, 0, 0)));

    [Fact]
    public async Task TheProbeOffsetTableMovesTheWindowEndAcrossADstChange()
    {
        var nowUtc = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero);
        var offsets = string.Join(',', Enumerable.Range(0, 37).Select(k => k < 3 ? "-300" : "-240"));
        var output = Healthy + "local_time=2026-03-07T23:00:00" + NL + "now_epoch=" + nowUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + NL + "tz_offsets=" + offsets;
        var h = await Probe(output, nowUtc, hours: ["Sat 22:00-06:00"]);
        Assert.Null(h.Hold);
        Assert.Equal(nowUtc + TimeSpan.FromHours(6), h.WindowEndsUtc);
    }

    [Theory]
    [InlineData("tz_offsets=-300")] // too short
    [InlineData("tz_offsets=x,y")]
    [InlineData("")]
    public async Task AMissingOrGarbledTableFallsBackToWallClockTime(string table)
    {
        var nowUtc = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero);
        var h = await Probe(Healthy + "local_time=2026-03-07T23:00:00" + NL + "now_epoch=" + nowUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + NL + table, nowUtc, hours: ["Sat 22:00-06:00"]);
        Assert.Equal(nowUtc + TimeSpan.FromHours(7), h.WindowEndsUtc);
    }

    /// <summary>The sh probe in a real sh, in a zone with DST: its table matches what .NET says that zone's offsets are.</summary>
    [ShFact]
    public void RealShProbeReportsTheUtcOffsetsOfItsZone()
    {
        var m = T.Linux();
        m.WorkRoot = Path.Combine(_dir, "shroot").Replace((char)92, '/');
        var (_, output) = Shells.Run("linux", RemoteScripts.Probe(m), new Dictionary<string, string> { ["TZ"] = "EST5EDT" });
        var v = HealthProbe.Parse(output);
        var table = UtcOffsetTable.TryParse(v.GetValueOrDefault("now_epoch"), v.GetValueOrDefault("tz_offsets"));
        Assert.True(table is not null, "probe output: " + output);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var now = DateTimeOffset.FromUnixTimeSeconds(table.NowEpoch);
        Assert.Equal(Enumerable.Range(0, 37).Select(k => (int)zone.GetUtcOffset(now.AddHours(k)).TotalMinutes), table.OffsetMinutes);
    }

    [Fact]
    public void RealProbeOnThisHostReportsItsUtcOffsets()
    {
        var root = Path.Combine(_dir, "tzroot").Replace((char)92, '/');
        Directory.CreateDirectory(root);
        var output = WorkerHealthTests.RunProbe(root);
        Assert.NotNull(output);
        var v = HealthProbe.Parse(output);
        var table = UtcOffsetTable.TryParse(v.GetValueOrDefault("now_epoch"), v.GetValueOrDefault("tz_offsets"));
        Assert.True(table is not null, "probe output: " + output);
        Assert.Equal(37, table.OffsetMinutes.Count);
        var now = DateTimeOffset.FromUnixTimeSeconds(table.NowEpoch);
        Assert.True(Math.Abs((now - DateTimeOffset.UtcNow).TotalMinutes) < 5);
        for (var k = 0; k < 37; k++)
        {
            Assert.Equal((int)TimeZoneInfo.Local.GetUtcOffset(now.AddHours(k)).TotalMinutes, table.OffsetMinutes[k]);
        }
    }

    // ---- health: the worker's clock ----

    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\n";

    private static Task<MachineHealth> Probe(string output, DateTimeOffset orchestratorNow, string[]? hours = null, PolicyConfig? policy = null)
    {
        var m = T.Windows();
        m.AllowedHours = hours is null ? m.AllowedHours : [.. hours];
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, output) };
        return new HealthProbe(ssh, new FakeClock(orchestratorNow)).ProbeAsync(m, policy ?? T.Config().Policy, CancellationToken.None);
    }

    private static readonly DateTimeOffset MondayNoonUtc = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HoursUseTheWorkerClockWhenItReportsOne()
    {
        // The orchestrator says Monday noon (outside 22:00-07:00); the worker's own clock says 23:30, which is inside.
        var h = await Probe(Healthy + "local_time=2026-01-05T23:30:00", MondayNoonUtc);
        Assert.Null(h.Hold);
        Assert.True(h.WorkerClock);
        Assert.Equal(MondayNoonUtc + TimeSpan.FromMinutes(450), h.WindowEndsUtc); // 07:00 is 7.5 h after 23:30
    }

    [Theory]
    [InlineData("")]
    [InlineData("local_time=garbage")]
    [InlineData("local_time=2026-13-45T99:00:00")]
    public async Task WithoutAWorkerClockTheOrchestratorLocalClockIsUsed(string extra)
    {
        // 04:00 UTC in a fixed UTC+2 zone is 06:00 local: inside 22:00-07:00 with an hour left.
        var at = new DateTimeOffset(2026, 1, 5, 4, 0, 0, TimeSpan.Zero);
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy + extra) };
        var h = await new HealthProbe(ssh, new FakeClock(at, FixedZone(2))).ProbeAsync(T.Windows(), T.Config().Policy, CancellationToken.None);
        Assert.Null(h.Hold);
        Assert.False(h.WorkerClock);
        Assert.Equal(at + TimeSpan.FromHours(1), h.WindowEndsUtc);
    }

    [Fact]
    public async Task OrchestratorFallbackHonoursItsFixedOffset()
    {
        // The same instant in UTC-5 is 23:00 the previous evening: also inside, with eight hours left.
        var at = new DateTimeOffset(2026, 1, 5, 4, 0, 0, TimeSpan.Zero);
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy) };
        var h = await new HealthProbe(ssh, new FakeClock(at, FixedZone(-5))).ProbeAsync(T.Windows(), T.Config().Policy, CancellationToken.None);
        Assert.Equal(at + TimeSpan.FromHours(8), h.WindowEndsUtc);
    }

    [Fact]
    public async Task OutsideTheWorkerWindowHolds()
    {
        var h = await Probe(Healthy + "local_time=2026-01-05T12:00:00", MondayNoonUtc);
        Assert.Equal("outside allowed hours", h.Hold);
        Assert.Null(h.WindowEndsUtc);
    }

    [Fact]
    public async Task AWindowThatIsAboutToCloseDoesNotTakeNewWork()
    {
        var h = await Probe(Healthy + "local_time=2026-01-05T06:55:00", MondayNoonUtc);
        Assert.Equal("window closing (5 min left)", h.Hold);
        var policy = T.Config().Policy;
        policy.MinWindowMinutes = 3;
        Assert.Null((await Probe(Healthy + "local_time=2026-01-05T06:55:00", MondayNoonUtc, policy: policy)).Hold);
    }

    [Fact]
    public async Task MachinesWithoutHoursHaveNoWindowEnd()
    {
        var h = await Probe(Healthy + "local_time=2026-01-05T06:55:00", MondayNoonUtc, hours: []);
        Assert.Null(h.Hold);
        Assert.Null(h.WindowEndsUtc);
    }

    [Fact]
    public void ProbesPrintTheWorkerLocalTime()
    {
        Assert.Contains("date '+%Y-%m-%dT%H:%M:%S'", RemoteScripts.Probe(T.Linux()), StringComparison.Ordinal);
        Assert.Contains("(Get-Date).ToString('yyyy-MM-ddTHH:mm:ss', $inv)", RemoteScripts.Probe(T.Windows()), StringComparison.Ordinal);
    }

    [Fact]
    public void RealProbeOnThisHostReportsItsLocalTime()
    {
        var root = Path.Combine(_dir, "root").Replace('\\', '/');
        Directory.CreateDirectory(root);
        var output = WorkerHealthTests.RunProbe(root);
        if (output is null)
        {
            return;
        }

        Assert.True(
            DateTime.TryParseExact(HealthProbe.Parse(output).GetValueOrDefault("local_time"), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t),
            "probe output: " + output);
        Assert.True(Math.Abs((t - DateTime.Now).TotalMinutes) < 5);
    }

    [Fact]
    public async Task HealthSaysWhenItUsedTheLocalClock()
    {
        var config = T.Config();
        var ssh = new FakeSsh { Handler = (m, _) => new SshResult(0, Healthy + (m.Name == "build-01" ? "local_time=2026-01-05T23:30:00" : "")) };
        config.Machines[0].AllowedHours = ["22:00-07:00"];
        var o = new StringWriter();
        await Cli.Health(config, new HealthProbe(ssh, new FakeClock(MondayNoonUtc)), o, new StringWriter(), CancellationToken.None);
        var lines = o.ToString().Split(NL, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("time: local", lines[0], StringComparison.Ordinal); // build-01 reported its clock
        Assert.Contains("time: local", lines[1], StringComparison.Ordinal); // build-02 did not
    }

    private static TimeZoneInfo FixedZone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone("fixed" + hours, TimeSpan.FromHours(hours), "fixed", "fixed");

    // ---- runner: the window-close stop ----

    private static readonly DateTimeOffset FridayElevenPmUtc = new(2026, 1, 2, 23, 0, 0, TimeSpan.Zero);

    /// <summary>build-01 is held by load, so the job goes to build-02 (Windows), whose window is <paramref name="hours"/>.</summary>
    private (JobRunner Runner, List<TimeSpan> ExecuteTimeouts, JobLog Log) Make(string hours, bool executeTimesOut = false)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = [hours];
        var timeouts = new List<TimeSpan>();
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
            {
                var text = Decode(m, cmd);
                if (text.Contains("disk_free_gb", StringComparison.Ordinal))
                {
                    return new SshResult(0, (m.Name == "build-01" ? "load=0.99" : "load=0.1") + "\ndisk_free_gb=50\nslots_used=0\ntool=ok\nlocal_time=2026-01-02T23:00:00");
                }

                return text.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "");
            },
        };
        var timing = new TimeoutRecordingSsh(ssh, timeouts, executeTimesOut);
        var clock = new FakeClock(FridayElevenPmUtc);
        var log = new JobLog(Path.Combine(_dir, ".lanq"));
        var runner = new JobRunner(config, timing, new EmptyPayloads(), new HealthProbe(timing, clock), log, Path.Combine(_dir, "tmp"), _ => { }, clock: clock);
        return (runner, timeouts, log);
    }

    private JobRequest Req(int timeoutMinutes = 240) => new(["dotnet"], ["dotnet", "test"], ".", _dir, TimeSpan.Zero, TimeSpan.FromMinutes(timeoutMinutes));

    [Fact]
    public async Task AJobMayRunUntilTheWindowEndsPlusGrace()
    {
        var (runner, timeouts, _) = Make("Fri 22:00-23:30");
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, r.Status);
        Assert.Equal("build-02", r.Machine);
        Assert.Equal(TimeSpan.FromMinutes(45), Assert.Single(timeouts)); // 30 min left + 15 min grace, not the 240 min asked for
    }

    [Fact]
    public async Task AShorterTimeoutStillWinsAndStaysTimedOut()
    {
        var (runner, timeouts, _) = Make("Fri 22:00-23:30", executeTimesOut: true);
        var r = await runner.RunAsync(Req(20), CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(20), Assert.Single(timeouts));
        Assert.Equal(JobStatus.TimedOut, r.Status);
    }

    [Fact]
    public async Task AJobStoppedByTheWindowIsWindowClosedWithExit124()
    {
        var (runner, _, log) = Make("Fri 22:00-23:30", executeTimesOut: true);
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.WindowClosed, r.Status);
        Assert.Equal(124, r.CliExitCode);
        Assert.Contains("allowed hours", r.Message, StringComparison.Ordinal);
        Assert.Equal("WindowClosed", Assert.Single(log.ReadAll()).Status);
    }

    [Fact]
    public async Task AClosingWindowIsNotUsed()
    {
        var (runner, timeouts, _) = Make("Fri 22:00-23:05");
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Contains("build-02: window closing (5 min left)", r.Message, StringComparison.Ordinal);
        Assert.Empty(timeouts);
    }

    /// <summary>Upload and prepare took so long that too little of the window is left: the job is not started, it is held.</summary>
    [Theory]
    [InlineData(30)] // 5 min of window + grace left, below min_window_minutes (10)
    [InlineData(40)] // past window end + grace
    public async Task AWindowThatClosesDuringPrepareHoldsInsteadOfStartingTheJob(int prepareMinutes)
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = ["Fri 22:00-23:20"];
        var clock = new SteppingClock(FridayElevenPmUtc);
        var calls = new List<string>();
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) =>
            {
                var text = Decode(m, cmd);
                lock (calls)
                {
                    calls.Add(text);
                }

                if (text.Contains("disk_free_gb", StringComparison.Ordinal))
                {
                    return new SshResult(0, (m.Name == "build-01" ? "load=0.99" : "load=0.1") + "\ndisk_free_gb=50\nslots_used=0\ntool=ok\nlocal_time=2026-01-02T23:00:00");
                }

                if (text.Contains("bundle verify", StringComparison.Ordinal))
                {
                    clock.Now += TimeSpan.FromMinutes(prepareMinutes);
                }

                return text.Contains("SLOT=", StringComparison.Ordinal) ? new SshResult(0, "SLOT=1") : new SshResult(0, "");
            },
        };
        var runner = new JobRunner(config, ssh, new EmptyPayloads(), new HealthProbe(ssh, clock), new JobLog(Path.Combine(_dir, ".lanq")), Path.Combine(_dir, "tmp"), _ => { }, clock: clock);

        var r = await runner.RunAsync(Req(), CancellationToken.None);

        Assert.Equal(JobStatus.NoCapacity, r.Status);
        Assert.Equal(75, r.CliExitCode);
        Assert.Contains("window closing", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, c => c.Contains("Set-Content -LiteralPath $pf", StringComparison.Ordinal)); // Execute never ran
        Assert.Contains(calls, c => c.Contains("locks/slot-1", StringComparison.Ordinal)); // the slot was cleaned up
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task TheCliStatusLineNamesWindowClosed()
    {
        var (runner, _, _) = Make("Fri 22:00-23:30", executeTimesOut: true);
        var r = await runner.RunAsync(Req(), CancellationToken.None);
        Assert.StartsWith("[lanq] WindowClosed: ", Cli.StatusLine(r), StringComparison.Ordinal);
    }

    private static string Decode(Machine m, string cmd) =>
        m.Os == MachineOs.Windows && cmd.Contains("-EncodedCommand ", StringComparison.Ordinal)
            ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(cmd[(cmd.LastIndexOf(' ') + 1)..]))
            : cmd;

    /// <summary>Records the timeout of each job run (the command that records the job PID) and can make it time out.</summary>
    private sealed class TimeoutRecordingSsh(FakeSsh inner, List<TimeSpan> timeouts, bool timesOut) : ISshClient
    {
        public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            if (Decode(machine, remoteCommand).Contains("Set-Content -LiteralPath $pf", StringComparison.Ordinal))
            {
                timeouts.Add(timeout);
                if (timesOut)
                {
                    return Task.FromResult(new SshResult(124, "", TimedOut: true));
                }
            }

            return inner.RunAsync(machine, remoteCommand, onLine, timeout, ct);
        }

        public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
            inner.UploadAsync(machine, localPath, remotePath, timeout, ct);
    }

    private sealed class EmptyPayloads : IPayloadBuilder
    {
        public Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) =>
            Task.FromResult(new JobPayload("abc", [], "", RepoId: T.RepoId, Head: "abc"));

        public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) =>
            throw new InvalidOperationException("not used");

        public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);
            var bundle = Path.Combine(outputDir, jobId + ".bundle");
            File.WriteAllText(bundle, "x");
            return Task.FromResult(new BundleBuild(bundle, null));
        }
    }
}
