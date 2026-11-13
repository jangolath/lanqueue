using System.Text.Json;
using System.Text.Json.Nodes;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The fields lanq-makeready 0.2.0 added (summary, suitedFor, suggestedMachine.min_free_mem_gb and .wake): shown in the text, kept in --json,
/// and a 0.1 report (none of them) still works. The fixtures are real -Json -Redact runs of the 0.2.0 and 0.1.0 Windows scripts; only the
/// startup item names and the CPU model were replaced (they describe the PC that ran them).
/// </summary>
public class MakereadySummaryTests
{
    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static string V020 => Fixture("makeready-0.2.0-windows.json");

    internal static string V010 => Fixture("makeready-0.1.0-windows.json");

    /// <summary>The 0.2.0 fixture with a suggested wake: block (the script adds one only when its Wake-on-LAN check is OK).</summary>
    private static string WithWake(string mac) =>
        V020.Replace("\"min_free_mem_gb\":  2,", $"\"min_free_mem_gb\":  2, \"wake\": {{ \"mac\": \"{mac}\" }},", StringComparison.Ordinal);

    internal static string Summary(string verdict, string text, int blockers, int warnings, params string[] steps) =>
        $$"""
          "summary": { "verdict": "{{verdict}}", "text": "{{text}}", "blockers": {{blockers}}, "warnings": {{warnings}}, "nextSteps": [{{string.Join(", ", steps)}}] },
        """;

    internal static string Step(int n, string id, string title, string status, bool blocker, string detail, string action, bool admin) =>
        $$"""{ "step": {{n}}, "id": "{{id}}", "title": "{{title}}", "status": "{{status}}", "blocker": {{(blocker ? "true" : "false")}}, "detail": "{{detail}}", "action": "{{action}}", "adminToFix": {{(admin ? "true" : "false")}} }""";

    private static async Task<(int Code, string Out, string Err)> Run(string report, params string[] args)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy), InputHandler = (_, _, _) => new SshResult(0, report) };
        var o = new StringWriter();
        var e = new StringWriter();
        var config = T.Config();
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", .. args]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public async Task TheScriptsVerdictThenItsNumberedNextStepsThenSuitedFor()
    {
        var (code, o, _) = await Run(V020, "--ssh", "me@new-pc.lan", "--os", "windows");
        Assert.Equal(1, code);
        var lines = Lines(o).ToList();
        string[] expected =
        [
            "VERDICT: NOT READY (1 blocker)",
            "Next steps (blockers first; nothing here is run for you):",
            " 1. [MUST FIX] OpenSSH Server: not installed",
            "    do (needs admin: elevated PowerShell): Install OpenSSH Server yourself (elevated PowerShell): Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0   then: Start-Service sshd ; Set-Service -Name sshd -StartupType Automatic",
            " 2. [should fix] SSH keys: user is an administrator; administrators_authorized_keys not found",
            " 3. [should fix] git: git version 2.48.1.windows.1, core.longpaths not enabled",
            "    do (needs admin: elevated PowerShell): Optional: git config --system core.longpaths true (elevated), and keep work_root short, for example C:/lq.",
            " 4. [should fix] Pending reboot: a restart is pending",
            "    do (no admin needed): A restart is pending; the PC may reboot during a job. Restart at a time you choose: Start > Power > Update and restart (or Restart).",
            " 6. [should fix] Startup items: 31 items, e.g. Updater, Sync, Chat, Fan control, Games",
            "Suited for: heavy: full suites and large builds + container builds",
        ];
        var at = expected.Select(l => lines.IndexOf(l)).ToList();
        Assert.All(at.Zip(expected), x => Assert.True(x.First >= 0, "missing: " + x.Second));
        Assert.Equal(at.Order(), at); // in this order
        Assert.DoesNotContain("Checks to look at", o, StringComparison.Ordinal); // the steps replace the 0.1 list, nothing twice
    }

    [Fact]
    public async Task ALinuxReportSaysRootAndAnEmptyActionPointsAtTheDetail()
    {
        var report = MakereadyTests.Report(extra: Summary(
            "READY_WITH_WARNINGS", "READY WITH WARNINGS (2)", 0, 2,
            Step(1, "reboot.pending", "Pending reboot", "WARN", false, "a restart is pending", "Restart the PC when convenient.", false),
            Step(2, "power.sleep", "Sleep", "WARN", false, "suspends after 30 min", "", true)));
        var (code, o, _) = await Run(report, "build-01");
        Assert.Equal(0, code);
        var lines = Lines(o);
        Assert.Contains("VERDICT: READY WITH WARNINGS (2)", lines);
        Assert.Contains("    do (no root needed): Restart the PC when convenient.", lines);
        Assert.Contains(" 2. [should fix] Sleep: suspends after 30 min", lines);
        Assert.Contains("    do (needs root): see the detail above", lines);
    }

    [Fact]
    public async Task EveryNewStringIsSanitised()
    {
        var bad = "\\u001b]0;pwned\\u0007\\u202e\\u001b[2J";
        var report = MakereadyTests.Report(extra: Summary(
            "READY_WITH_WARNINGS", "READY " + bad, 0, 1, Step(1, "x", "T" + bad, "WARN", false, "d" + bad, "a" + bad, false))
            + $$"""
              "suitedFor": { "tier": "light", "containers": false, "text": "light{{bad}}" },
            """);
        var (code, o, _) = await Run(report, "build-01");
        Assert.Equal(0, code);
        Assert.Contains("VERDICT: READY ", o, StringComparison.Ordinal);
        Assert.Contains("Suited for: light", o, StringComparison.Ordinal);
        Assert.DoesNotContain((char)27, o);
        Assert.DoesNotContain((char)7, o);
        Assert.DoesNotContain('\u202e', o);
    }

    [Fact]
    public async Task TheEntryGetsMinFreeMemAndACommentedWakeBlockThatLoadsWhenUncommented()
    {
        var (_, o, _) = await Run(V020, "--ssh", "me@new-pc.lan", "--os", "windows");
        Assert.Contains("    min_free_mem_gb: 2", o, StringComparison.Ordinal);
        Assert.DoesNotContain("wake:", o, StringComparison.Ordinal); // the script suggested none (its Wake-on-LAN check is a WARN)

        (_, o, _) = await Run(WithWake("<mac>"), "--ssh", "me@new-pc.lan", "--os", "windows");
        Assert.Contains("    # wake:", o, StringComparison.Ordinal);
        Assert.Contains("    #   mac: \"<mac>\"   # placeholder", o, StringComparison.Ordinal); // a redacted MAC: ConfigLoader refuses it until filled in

        (_, o, _) = await Run(WithWake("10-20-30-40-50-60"), "--ssh", "me@new-pc.lan", "--os", "windows");
        var entry = Lines(o).SkipWhile(l => l != "machines:").TakeWhile(l => l.Length > 0).ToList();
        Assert.Contains("    #   mac: \"10-20-30-40-50-60\"", entry);
        Assert.DoesNotContain(entry, l => l.StartsWith("    wake:", StringComparison.Ordinal)); // commented: the owner opts in

        // Uncommented, the entry is a valid machines.yml entry with the wake: and min_free_mem_gb keys ConfigLoader knows.
        var uncommented = entry.Select(l => l.StartsWith("    # wake:", StringComparison.Ordinal) ? "    wake:" : l.StartsWith("    #   ", StringComparison.Ordinal) ? "      " + l[8..] : l);
        var yaml = "policy:\n  allowed_commands: [\"dotnet test\"]\n  allowed_directories: [\".\"]\n" + string.Join('\n', uncommented) + "\n";
        var m = Assert.Single(ConfigLoader.Parse(yaml).Machines);
        Assert.Equal("10-20-30-40-50-60", m.Wake!.Mac);
        Assert.Equal(2, m.MinFreeMemGb);
    }

    [Theory]
    [InlineData("1.5", "    min_free_mem_gb: 1.5")]
    [InlineData("-1", null)]
    [InlineData("1e9", null)]
    public async Task MinFreeMemIsShownOnlyWhenSane(string value, string? line)
    {
        var report = V020.Replace("\"min_free_mem_gb\":  2", "\"min_free_mem_gb\":  " + value, StringComparison.Ordinal);
        var (_, o, _) = await Run(report, "--ssh", "me@new-pc.lan", "--os", "windows");
        if (line is null)
        {
            Assert.DoesNotContain("min_free_mem_gb", o, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(line + "   #", o, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> Reports() => [V020, WithWake("<mac>"), V010];

    /// <summary>--json prints the validated report as the script sent it: every field, the 0.2.0 ones included, and nothing added.</summary>
    [Theory]
    [MemberData(nameof(Reports))]
    public async Task JsonIsTheReportAsSent(string report)
    {
        var (code, o, _) = await Run(report, "--ssh", "me@new-pc.lan", "--os", "windows", "--json");
        Assert.Equal(1, code);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(report), JsonNode.Parse(o)), o);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task AllJsonCarriesEachReportAsSent(string report)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy), InputHandler = (_, _, _) => new SshResult(0, report) };
        var o = new StringWriter();
        var config = T.Config();
        await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", "--json", "--force"]), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        var machines = JsonNode.Parse(o.ToString())!["machines"]!.AsArray();
        Assert.Equal(2, machines.Count);
        Assert.All(machines, m => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(report), m!["report"]), m!.ToJsonString()));
    }

    [Fact]
    public async Task AZeroPointOneReportStillWorks()
    {
        var (code, o, _) = await Run(V010, "--ssh", "me@new-pc.lan", "--os", "windows");
        Assert.Equal(1, code);
        Assert.Contains("Checks to look at", o, StringComparison.Ordinal);
        Assert.Contains("[FAIL] OpenSSH Server: not installed", o, StringComparison.Ordinal);
        Assert.Contains("machines:", o, StringComparison.Ordinal);
        Assert.DoesNotContain("VERDICT:", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Suited for", o, StringComparison.Ordinal);
        Assert.DoesNotContain("min_free_mem_gb", o, StringComparison.Ordinal);
        Assert.DoesNotContain("wake", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"verdict\": \"READY\"", "\"verdict\": \"MAYBE\"")]
    [InlineData("\"blockers\": 0", "\"blockers\": -1")]
    [InlineData("\"status\": \"WARN\", \"blocker\"", "\"status\": \"OK\", \"blocker\"")]
    [InlineData("\"step\": 1", "\"step\": 0")]
    [InlineData("\"nextSteps\": [", "\"nextSteps\": [null, ")]
    public async Task AMalformedSummaryIsAnUnusableReport(string from, string to)
    {
        var report = MakereadyTests.Report(extra: Summary("READY", "READY", 0, 1, Step(1, "reboot.pending", "Pending reboot", "WARN", false, "d", "a", false)))
            .Replace(from, to, StringComparison.Ordinal);
        var (code, _, e) = await Run(report, "build-01");
        Assert.Equal(70, code);
        Assert.Contains("unusable lanq-makeready report", e, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixturesParse()
    {
        Assert.False(Makeready.MakereadyReport.Parse(V020).WorkerReady);
        Assert.False(Makeready.MakereadyReport.Parse(V010).WorkerReady);
        using var doc = JsonDocument.Parse(V020);
        Assert.Equal("0.2.0", doc.RootElement.GetProperty("tool").GetProperty("version").GetString());
    }

    private const string FixPointer = "its owner can run lanq-makeready-fix there, local and interactive only (see lanq-makeready's README). lanq never runs it.";

    /// <summary>
    /// 0.9.1: a WARN/FAIL check that lanq-makeready-fix covers gets one pointer line for the PC's owner, before the closing lines. On the 0.2.0
    /// Windows fixture only git (core.longpaths) qualifies: OpenSSH not installed and no administrators_authorized_keys are not fixes it makes.
    /// </summary>
    [Fact]
    public async Task AFixableCheckPointsTheOwnerAtTheFixScript()
    {
        var (_, o, _) = await Run(V020, "--ssh", "me@new-pc.lan", "--os", "windows");
        var lines = Lines(o).ToList();
        var at = lines.IndexOf("Fixable at that PC (git): " + FixPointer);
        Assert.True(at >= 0, o);
        Assert.True(at < lines.IndexOf("lanq never changes a worker. Fixes are for the PC's owner to run on that PC."));
        Assert.Single(lines, l => l.Contains("lanq-makeready-fix", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "id": "power.sleep", "title": "Sleep/suspend", "status": "WARN", "detail": "may sleep: logind IdleAction=suspend", "advice": "", "needsAdmin": false },""", "power.sleep")]
    [InlineData("""{ "id": "ssh.server", "title": "SSH server", "status": "FAIL", "detail": "sshd installed but not running", "advice": "", "needsAdmin": false },""", "ssh.server")]
    [InlineData("""{ "id": "power.sleep", "title": "Sleep/suspend", "status": "OK", "detail": "will not sleep (masked)", "advice": "", "needsAdmin": false },""", null)]
    [InlineData("""{ "id": "longpaths", "title": "Long paths", "status": "WARN", "detail": "LongPathsEnabled is off", "advice": "", "needsAdmin": false },""", null)] // a Windows-only fix
    [InlineData("""{ "id": "git", "title": "git", "status": "WARN", "detail": "not found", "advice": "", "needsAdmin": false },""", null)]
    public async Task OnLinuxOnlySshStartAndPowerSleepArePointedAt(string check, string? expected)
    {
        var (_, o, _) = await Run(MakereadyTests.Report(extraChecks: check), "build-01");
        if (expected is null)
        {
            Assert.DoesNotContain("lanq-makeready-fix", o, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Fixable at that PC ({expected}): " + FixPointer, Lines(o));
        }
    }

    [Fact]
    public async Task JsonNeverCarriesTheFixPointer()
    {
        var (_, o, _) = await Run(V020, "--ssh", "me@new-pc.lan", "--os", "windows", "--json");
        Assert.DoesNotContain("lanq-makeready-fix", o, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(o);
    }
}
