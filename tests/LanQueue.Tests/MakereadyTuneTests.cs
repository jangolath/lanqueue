using System.Text.Json;
using System.Text.Json.Nodes;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Makeready;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq makeready --tune (0.12.0): after the scanner's report, the read-only tune scan goes the same way (stdin, run from memory); its
/// outcome is lanqTune (JSON) or a tune section (text), and the exit code is always the scan's.
/// </summary>
public class MakereadyTuneTests
{
    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    private static string LinuxTune() => TuneReportTests.Report(
    [
        TuneReportTests.Item("sysctl.inotify", "WARN", "\"sysctl.tune\""),
        TuneReportTests.Item("services.unused", "WARN", "\"services.unused\"", security: true),
        TuneReportTests.Item("pkgcache", "INFO"),
        TuneReportTests.Item("cpu.governor", "OK"),
        TuneReportTests.Item("smart", "SKIP"),
    ]);

    private static bool IsTune(byte[] input) => input.AsSpan().SequenceEqual(Cli.TuneScript(MachineOs.Linux)) || input.AsSpan().SequenceEqual(Cli.TuneScript(MachineOs.Windows));

    private static async Task<(int Code, string Out, string Err, FakeSsh Ssh)> Run(
        string[] args, string? tune = null, SshResult? tuneResult = null, string? report = null, FleetConfig? config = null, Func<Machine, string, SshResult>? probe = null)
    {
        var ssh = new FakeSsh
        {
            Handler = probe ?? ((_, _) => new SshResult(0, Healthy)),
            InputHandler = (_, _, input) => IsTune(input) ? tuneResult ?? new SshResult(0, tune ?? LinuxTune()) : new SshResult(0, report ?? MakereadyTests.Report()),
        };
        var o = new StringWriter();
        var e = new StringWriter();
        var cfg = config ?? T.Config();
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", .. args]), () => cfg, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString(), ssh);
    }

    [Fact]
    public async Task TheTuneScriptGoesSecondOnStdinWithHoursAndWorkRoot()
    {
        var (code, _, _, ssh) = await Run(["build-01", "--tune"]);
        Assert.Equal(0, code);
        Assert.Equal(2, ssh.InputRuns.Count);
        var tune = ssh.InputRuns[1];
        Assert.Equal(Cli.TuneScript(MachineOs.Linux), tune.Input);
        Assert.Equal("sh -s -- --json - --redact --work-root '/srv/lanq'", tune.Command); // build-01 has no allowed_hours
        Assert.Equal(TimeSpan.FromSeconds(150), tune.Timeout);
        Assert.Equal(TuneReport.MaxChars, tune.MaxOutputChars);
        Assert.Empty(ssh.Uploads);

        (_, _, _, ssh) = await Run(["build-02", "--tune", "--force"]);
        Assert.Equal(
            "powershell -NoProfile -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact -Hours '22:00-07:00' -WorkRoot 'D:/lanq'\"",
            ssh.InputRuns[1].Command);
        Assert.Equal(Cli.TuneScript(MachineOs.Windows), ssh.InputRuns[1].Input);
    }

    [Fact]
    public async Task WithoutTuneOnlyTheScannerRuns()
    {
        var (_, o, _, ssh) = await Run(["build-01", "--json"]);
        Assert.Single(ssh.InputRuns);
        Assert.DoesNotContain("lanqTune", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/", null)] // a whole drive: the tune script refuses it
    [InlineData("C:/lq/./x", null)] // a '.' part
    [InlineData("C:/.", null)]
    [InlineData("C:/lq", "C:/lq")]
    [InlineData("D:/build/lq-01", "D:/build/lq-01")]
    public void TheWorkRootIsPassedOnlyWhenBothRulesAccept(string workRoot, string? expected)
    {
        var m = T.Windows();
        m.WorkRoot = workRoot;
        Assert.Equal(expected, Cli.TuneWorkRoot(m));
    }

    [Fact]
    public void ALinuxWorkRootLongerThan255IsNotPassed()
    {
        var m = T.Linux();
        m.WorkRoot = "/" + new string('a', 254);
        Assert.Equal(m.WorkRoot, Cli.TuneWorkRoot(m));
        m.WorkRoot = "/" + new string('a', 255);
        Assert.Null(Cli.TuneWorkRoot(m));
    }

    [Theory]
    [InlineData(new[] { "22:00-07:00" }, "22:00-07:00")]
    [InlineData(new[] { "Mon-Fri 18:00-23:00", "Sat,Sun 18:00-23:00" }, "18:00-23:00")] // the same times: days dropped
    [InlineData(new[] { "Mon-Fri 18:00 - 23:00" }, "18:00-23:00")] // normalised
    [InlineData(new[] { "Mon-Fri 18:00-23:00", "Sat,Sun 08:00-22:00" }, null)] // different windows
    public void HoursArePassedOnlyForOneCommonWindow(string[] entries, string? expected)
    {
        var m = T.Linux();
        var hours = new HoursList();
        foreach (var e in entries)
        {
            hours.Add(e);
        }

        m.AllowedHours = hours;

        Assert.Equal(expected, Cli.ScanHours(m));
        Assert.Null(Cli.ScanHours(T.Linux()));
    }

    [Fact]
    public async Task AnUnconfiguredPcGetsNeitherHoursNorWorkRoot()
    {
        var (code, _, _, ssh) = await Run(["--ssh", "me@new-pc.lan", "--os", "linux", "--tune"]);
        Assert.Equal(0, code);
        Assert.Equal("sh -s -- --json - --redact", ssh.InputRuns[0].Command);
        Assert.Equal("sh -s -- --json - --redact", ssh.InputRuns[1].Command);
    }

    [Fact]
    public async Task JsonAddsLanqTuneLastWithTheTablesFixIdsAndOwnNextSteps()
    {
        var (code, o, _, _) = await Run(["build-01", "--json", "--tune"]);
        Assert.Equal(0, code);
        var root = JsonNode.Parse(o)!.AsObject();
        Assert.Equal("lanqTune", root.Last().Key);
        var tune = root["lanqTune"]!;
        Assert.Equal("Ok", tune["status"]!.GetValue<string>());
        Assert.Null(tune["error"]);
        var items = tune["report"]!["items"]!.AsArray();
        Assert.Equal("sysctl.tune", items[0]!["fixId"]!.GetValue<string>());
        Assert.True(items[1]!["security"]!.GetValue<bool>());
        Assert.True(items[2]!.AsObject().ContainsKey("fixId")); // always written, null here
        Assert.Null(items[2]!["fixId"]);
        var step = Assert.Single(tune["nextSteps"]!.AsArray())!;
        Assert.Equal("ask_developer", step["action"]!.GetValue<string>());
        Assert.False(step["safeToRunUnattended"]!.GetValue<bool>());
        Assert.Null(step["argv"]); // the fix script is never suggested as a command
        Assert.Contains("2 setting(s)", step["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingFixableIsANoneStep()
    {
        var (_, o, _, _) = await Run(["build-01", "--json", "--tune"], tune: TuneReportTests.Report([TuneReportTests.Item("pkgcache", "INFO")]));
        var step = Assert.Single(JsonNode.Parse(o)!["lanqTune"]!["nextSteps"]!.AsArray())!;
        Assert.Equal("none", step["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWorkerCannotSendItsOwnLanqTune()
    {
        var report = MakereadyTests.Report().Replace("\"generatedAt\"", "\"lanqTune\": { \"status\": \"Ok\", \"nextSteps\": [] }, \"generatedAt\"", StringComparison.Ordinal);
        var (code, o, _, _) = await Run(["build-01", "--json"], report: report);
        Assert.Equal(0, code);
        Assert.DoesNotContain("lanqTune", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout", "TimedOut")]
    [InlineData("ssh", "Unreachable")]
    [InlineData("junk", "Unusable")]
    [InlineData("bad-args", "Unusable")]
    [InlineData("truncated", "Unusable")]
    public async Task ATuneFailureNeverChangesTheExitCode(string name, string status)
    {
        var result = name switch
        {
            "timeout" => new SshResult(124, "", TimedOut: true),
            "ssh" => new SshResult(255, "") { ErrorOutput = "ssh: connect to host build-01.lan port 22: Connection refused" },
            "junk" => new SshResult(0, "rm -rf / \u001b[2J"),
            "bad-args" => new SshResult(2, ""),
            _ => new SshResult(0, "{") { OutputTruncated = true },
        };
        foreach (var (git, archive, code) in new[] { ("OK", "OK", 0), ("FAIL", "FAIL", 1) })
        {
            var (exit, o, _, _) = await Run(["build-01", "--json", "--tune"], tuneResult: result, report: MakereadyTests.Report(git: git, archive: archive));
            Assert.Equal(code, exit);
            var tune = JsonNode.Parse(o)!["lanqTune"]!;
            Assert.Equal(status, tune["status"]!.GetValue<string>());
            Assert.Null(tune["report"]);
            Assert.DoesNotContain("rm -rf", tune["error"]!.GetValue<string>(), StringComparison.Ordinal);
            var step = Assert.Single(tune["nextSteps"]!.AsArray())!;
            Assert.Equal(status == "Unusable" ? "ask_developer" : "retry", step["action"]!.GetValue<string>());
            Assert.Equal(["lanq", "makeready", "build-01", "--tune"], step["argv"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.False(step["safeToRunUnattended"]!.GetValue<bool>());

            (exit, var text, _, _) = await Run(["build-01", "--tune"], tuneResult: result, report: MakereadyTests.Report(git: git, archive: archive));
            Assert.Equal(code, exit);
            Assert.Contains($"Tune: {status}:", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AHeldOrUnreachableMachineIsNotTuned()
    {
        var paused = new Func<Machine, string, SshResult>((_, _) => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)));
        var (code, o, _, ssh) = await Run(["build-01", "--json", "--tune"], probe: paused);
        Assert.Equal(75, code);
        Assert.Empty(ssh.InputRuns);
        Assert.DoesNotContain("lanqTune", o, StringComparison.Ordinal);

        (code, _, _, ssh) = await Run(["build-01", "--tune"], report: "not json");
        Assert.Equal(70, code);
        Assert.Single(ssh.InputRuns); // an unusable scan report: no tune scan
    }

    [Fact]
    public async Task TheTextShowsWarnAndInfoWithAdviceFixIdsAndTheOwnersPreview()
    {
        var (code, o, _, _) = await Run(["build-01", "--tune"]);
        Assert.Equal(0, code);
        Assert.Contains("Tune (lanq-makeready-tune 0.5.0, read-only", o, StringComparison.Ordinal);
        Assert.Contains("2 WARN, 1 INFO, 1 OK, 1 SKIP (not shown)", o, StringComparison.Ordinal);
        Assert.Contains("fix id: sysctl.tune", o, StringComparison.Ordinal);
        Assert.Contains("(security)", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[OK", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[SKIP", o, StringComparison.Ordinal);
        Assert.EndsWith(Cli.TuneFixLine(MachineOs.Linux), o.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("lanq never runs it.", Cli.TuneFixLine(MachineOs.Windows), StringComparison.Ordinal);
        Assert.Contains("lanq-makeready-fix.ps1 -Tune -WhatIf", Cli.TuneFixLine(MachineOs.Windows), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TuneTextIsSanitised()
    {
        var evil = TuneReportTests.Report([TuneReportTests.Item("sysmain", "WARN", detail: "\\u001b[31mred\\u202e", advice: "\\u001b]0;pwned\\u0007")]);
        var (_, o, _, _) = await Run(["build-01", "--tune"], tune: evil);
        Assert.DoesNotContain((char)27, o);
        Assert.DoesNotContain((char)7, o);
        Assert.DoesNotContain('\u202e', o);
    }

    [Theory]
    [InlineData("--ssh", "me@pc.lan", "--os", "linux", "--add", "--name", "pc", "--tune")]
    [InlineData("--ssh", "me@pc.lan", "--os", "linux", "--setup", "--tune")]
    public async Task TuneIsRefusedWithAddAndSetup(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", .. args]));
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["makeready", .. args, "--json"], o, new StringWriter(), CancellationToken.None));
    }

    [Fact]
    public void TheSuggestedRescanCarriesTune()
    {
        var c = Output.SuggestedCommand.Makeready("build-01", "/c/m.yml", tune: true)!;
        Assert.Equal(["lanq", "makeready", "build-01", "--tune", "--config", "/c/m.yml"], c.Argv);
        Assert.False(c.SafeToRunUnattended);
        var adhoc = Output.SuggestedCommand.Makeready(null, null, "me@pc.lan", MachineOs.Windows, "LANQ_KEY", tune: true)!;
        Assert.Equal(["lanq", "makeready", "--ssh", "me@pc.lan", "--os", "windows", "--key-env", "LANQ_KEY", "--tune"], adhoc.Argv);
    }

    private const string Fleet = """
        policy:
          allowed_commands: ["dotnet test"]
          allowed_directories: ["."]
        machines:
          - name: alpha
            os: linux
            ssh: build@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet]
          - name: bravo
            os: windows
            ssh: build@10.20.30.12
            work_root: C:/lanq
            capabilities: [dotnet]
            allowed_hours: ["Mon-Fri 22:00-07:00", "Sat,Sun 22:00-07:00"]
          - name: charlie
            os: linux
            ssh: build@10.20.30.13
            work_root: /srv/lanq
            capabilities: [dotnet]
        """;

    private static async Task<(int Code, string Out, FakeSsh Ssh)> RunAll(params string[] args)
    {
        var ssh = new FakeSsh
        {
            // charlie is paused; bravo (allowed_hours) reports a local time inside its window.
            Handler = (m, _) => new SshResult(0, m.Name switch
            {
                "charlie" => Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal),
                "bravo" => Healthy + "local_time=2026-01-05T23:00:00\n",
                _ => Healthy,
            }),
            InputHandler = (m, _, input) => IsTune(input)
                ? m.Name == "bravo" ? new SshResult(124, "", TimedOut: true) : new SshResult(0, LinuxTune())
                : new SshResult(0, MakereadyTests.Report()),
        };
        var o = new StringWriter();
        var config = ConfigLoader.Parse(Fleet);
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", .. args]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        return (code, o.ToString(), ssh);
    }

    [Fact]
    public async Task AllTuneTunesEachScannedMachineAndKeepsTheExitCode()
    {
        var (code, o, ssh) = await RunAll("--json", "--tune");
        var (plainCode, _, plainSsh) = await RunAll("--json");
        Assert.Equal(plainCode, code); // 75: charlie is held
        Assert.Equal(75, code);
        Assert.Equal(2, plainSsh.InputRuns.Count);
        Assert.Equal(4, ssh.InputRuns.Count); // alpha and bravo, scan and tune; charlie held
        Assert.Contains(ssh.InputRuns, r => r.Machine.Name == "bravo" && r.Command.Contains("-Hours '22:00-07:00' -WorkRoot 'C:/lanq'", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(o);
        var rows = doc.RootElement.GetProperty("machines").EnumerateArray().ToDictionary(r => r.GetProperty("machine").GetString()!);
        Assert.Equal("Ok", rows["alpha"].GetProperty("report").GetProperty("lanqTune").GetProperty("status").GetString());
        Assert.Equal("TimedOut", rows["bravo"].GetProperty("report").GetProperty("lanqTune").GetProperty("status").GetString());
        Assert.False(rows["charlie"].TryGetProperty("report", out _));
        // The rows' own nextSteps never carry the tune steps.
        Assert.DoesNotContain("--tune", rows["bravo"].GetProperty("nextSteps").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("--tune", rows["bravo"].GetProperty("report").GetProperty("lanqTune").GetProperty("nextSteps").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllTuneTextHasATuneColumn()
    {
        var (_, o, _) = await RunAll("--tune");
        Assert.Contains("TUNE", o, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^alpha\s+linux\s+WARN 1\s+2 WARN 1 INFO\s+Pending reboot", o);
        Assert.Matches(@"(?m)^bravo\s+windows\s+WARN 1\s+timed out\s+Pending reboot", o);
        Assert.Matches(@"(?m)^charlie\s+linux\s+held\s+-\s+paused", o);
    }

    /// <summary>H1: the single-machine report (no naming policy) and the --all row (camelCase) write lanqTune identically.</summary>
    [Fact]
    public async Task SingleAndAllWriteTheSameLanqTune()
    {
        var (_, single, _, _) = await Run(["build-01", "--json", "--tune"]);
        var ssh = new FakeSsh
        {
            Handler = (_, _) => new SshResult(0, Healthy),
            InputHandler = (_, _, input) => IsTune(input) ? new SshResult(0, LinuxTune()) : new SshResult(0, MakereadyTests.Report()),
        };
        var config = ConfigLoader.Parse(T.Yaml.Replace("allowed_hours: \"22:00-07:00\"", "", StringComparison.Ordinal));
        var o = new StringWriter();
        await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", "--json", "--tune"]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        var row = JsonNode.Parse(o.ToString())!["machines"]!.AsArray().Single(r => r!["machine"]!.GetValue<string>() == "build-01")!;
        var fromAll = row["report"]!["lanqTune"]!.DeepClone();
        var fromSingle = JsonNode.Parse(single)!["lanqTune"]!.DeepClone();

        // The suggested rescan differs only by what was typed (no --all); there is none here (Ok), so the objects must be equal.
        Assert.True(JsonNode.DeepEquals(fromSingle, fromAll), $"{fromSingle.ToJsonString()}\n{fromAll.ToJsonString()}");
        foreach (var key in new[] { "status", "report", "nextSteps" })
        {
            Assert.NotNull(fromSingle[key]);
        }

        Assert.NotNull(fromSingle["nextSteps"]![0]!["safeToRunUnattended"]);
        Assert.Null(fromSingle["NextSteps"]);
    }
}
