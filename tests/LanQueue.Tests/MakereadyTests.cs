using System.Security.Cryptography;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Makeready;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq makeready: the embedded scripts, how they are sent, and how their (untrusted) report is read.</summary>
public class MakereadyTests
{
    /// <summary>From SHA256SUMS of the lanq-makeready v0.4.0 GitHub release (build-provenance attested). Change only via scripts/sync-makeready.sh.</summary>
    private const string Ps1Sha256 = "fa6b1cded7e8265ea1374c9cc410d15d3d18970ace8178768ec43a2569f89140";

    private const string ShSha256 = "ed5ecb1c56a09920a83172684430f28a87dd77a43906fe92adaf4e18fc30998e";

    private const char Esc = (char)27;

    internal static string Report(
        string git = "OK", string archive = "OK", string schema = "lanq-makeready/v1", string caps = "\"dotnet\", \"java\"", string detail = "a restart is pending",
        string advice = "Restart the PC when convenient.", string extra = "", string docker = "SKIP", string extraChecks = "") => $$"""
        {
          "schema": "{{schema}}",
          "tool": { "name": "lanq-makeready", "version": "0.1.0" },
          "generatedAt": "2026-11-09T06:39:14Z",
          "host": { "hostname": "<redacted>", "user": "<redacted>", "os": "Ubuntu 24.04", "arch": "x64" },
          "verdicts": { "worker_git": "{{git}}", "worker_archive": "{{archive}}", "dotnet10": "OK", "docker": "{{docker}}", "overnight": "WARN" },{{extra}}
          "checks": [{{extraChecks}}
            { "id": "os.version", "title": "OS", "status": "OK", "detail": "Ubuntu 24.04", "advice": "", "needsAdmin": false },
            { "id": "reboot.pending", "title": "Pending reboot", "status": "WARN", "detail": "{{detail}}", "advice": "{{advice}}", "needsAdmin": false },
            { "id": "ssh.server", "title": "OpenSSH Server", "status": "{{(git == "FAIL" ? "FAIL" : "OK")}}", "detail": "sshd", "advice": "Install openssh-server yourself.", "needsAdmin": true }
          ],
          "capabilities": [{{caps}}],
          "suggestedMachine": { "name": "old-pc", "os": "linux", "ssh": "<user>@<hostname>.lan", "work_root": "/srv/lq",
            "capabilities": [{{caps}}, "linux"], "max_jobs": 1, "transfer": "git", "allowed_hours": "00:00-07:00" }
        }
        """;

    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    private static async Task<(int Code, string Out, string Err, FakeSsh Ssh)> Run(
        string? report, string[] args, Func<Machine, string, SshResult>? probe = null, SshResult? result = null, FleetConfig? config = null)
    {
        var ssh = new FakeSsh
        {
            Handler = probe ?? ((_, _) => new SshResult(0, Healthy)),
            InputHandler = (_, _, _) => result ?? new SshResult(report is null ? 1 : 0, report ?? ""),
        };
        var o = new StringWriter();
        var e = new StringWriter();
        var cfg = config ?? T.Config();
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", .. args]), () => cfg, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString(), ssh);
    }

    [Fact]
    public void EmbeddedScriptsAreTheVerifiedReleaseBytes()
    {
        Assert.Equal(Ps1Sha256, Convert.ToHexStringLower(SHA256.HashData(Cli.MakereadyScript(MachineOs.Windows))));
        Assert.Equal(ShSha256, Convert.ToHexStringLower(SHA256.HashData(Cli.MakereadyScript(MachineOs.Linux))));
    }

    [Fact]
    public async Task TheScriptGoesOnStdinWithTheDocumentedCommandAndNothingIsWrittenToTheWorker()
    {
        var (code, _, _, ssh) = await Run(Report(), ["build-01"]);
        Assert.Equal(0, code);
        var run = Assert.Single(ssh.InputRuns);
        Assert.Equal("build-01", run.Machine.Name);
        Assert.Equal("sh -s -- --json - --redact", run.Command);
        Assert.Equal(Cli.MakereadyScript(MachineOs.Linux), run.Input);
        Assert.Equal(TimeSpan.FromSeconds(120), run.Timeout);
        Assert.Equal(MakereadyReport.MaxChars, run.MaxOutputChars);
        Assert.Empty(ssh.Uploads);

        (_, _, _, ssh) = await Run(Report(), ["build-02", "--force"]);
        run = Assert.Single(ssh.InputRuns);
        Assert.Equal(
            "powershell -NoProfile -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact\"", run.Command);
        Assert.Equal(Cli.MakereadyScript(MachineOs.Windows), run.Input);
    }

    [Fact]
    public async Task AReadyReportPrintsVerdictsProblemsDiffEntryAndTheOwnersNote()
    {
        var config = T.Config();
        config.Machines[0].Capabilities = ["dotnet", "node", "npm", "linux"];
        var (code, o, _, _) = await Run(Report(), ["build-01"], config: config);

        Assert.Equal(0, code);
        Assert.Contains("worker_git", o, StringComparison.Ordinal);
        Assert.Contains("overnight", o, StringComparison.Ordinal);
        Assert.Contains("[WARN] Pending reboot: a restart is pending", o, StringComparison.Ordinal);
        Assert.Contains("advice: Restart the PC when convenient.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[OK", o, StringComparison.Ordinal); // only WARN/FAIL checks are listed
        Assert.Contains("detected but not configured: java", o, StringComparison.Ordinal);
        Assert.Contains("configured but not detected: node", o, StringComparison.Ordinal);
        Assert.Contains("npm", o, StringComparison.Ordinal); // not something makeready checks
        Assert.Contains("machines:", o, StringComparison.Ordinal);
        Assert.Contains("name: \"build-01\"", o, StringComparison.Ordinal); // a configured machine keeps its name and target
        Assert.Contains("ssh: \"user@build-01.lan\"", o, StringComparison.Ordinal);
        Assert.Contains("allowed_hours: \"00:00-07:00\"", o, StringComparison.Ordinal);
        Assert.EndsWith("lanq never changes a worker. Fixes are for the PC's owner to run on that PC.", o.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotReadyReportExitsOne()
    {
        var (code, o, _, _) = await Run(Report(git: "FAIL", archive: "FAIL"), ["build-01"]);
        Assert.Equal(1, code);
        Assert.Contains("[FAIL] OpenSSH Server", o, StringComparison.Ordinal);
        Assert.Contains("advice: Install openssh-server yourself.", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfiguredPcIsScannedWithTheGivenTargetAndNoKey()
    {
        var (code, o, _, ssh) = await Run(Report(), ["--ssh", "me@new-pc.lan", "--os", "windows"]);
        Assert.Equal(0, code);
        Assert.Empty(ssh.Calls); // no health probe: there is no work_root to look at
        var run = Assert.Single(ssh.InputRuns);
        Assert.Equal("me@new-pc.lan", run.Machine.Ssh);
        Assert.Null(run.Machine.KeyEnv);
        Assert.Equal(MachineOs.Windows, run.Machine.Os);
        Assert.Contains("ssh: \"me@new-pc.lan\"", o, StringComparison.Ordinal);
        Assert.DoesNotContain("configured but not detected", o, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=yes", SshArgs.Ssh(run.Machine, null, run.Command), StringComparer.Ordinal);
    }

    /// <summary>--key-env VAR (with --ssh): the scan uses the key file whose path is in VAR (as key_env), and the suggested entry carries it.</summary>
    [Fact]
    public async Task KeyEnvNamesTheKeyForAnUnconfiguredPc()
    {
        var (code, o, _, ssh) = await Run(Report(), ["--ssh", "me@new-pc.lan", "--os", "linux", "--key-env", "LANQ_KEY_NEW"]);
        Assert.Equal(0, code);
        var run = Assert.Single(ssh.InputRuns);
        Assert.Equal("LANQ_KEY_NEW", run.Machine.KeyEnv);
        Assert.Contains("IdentitiesOnly=yes", SshArgs.Ssh(run.Machine, "/keys/k", run.Command), StringComparer.Ordinal);
        Assert.Contains("    key_env: LANQ_KEY_NEW", o, StringComparison.Ordinal);

        // A variable NAME, and only with --ssh.
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", "me@new-pc.lan", "--os", "linux", "--key-env", "~/.ssh/id_ed25519"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "build-01", "--key-env", "LANQ_KEY"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--all", "--key-env", "LANQ_KEY"]));
    }

    [Theory]
    [InlineData("--ssh", "me@new-pc.lan")] // no --os
    [InlineData("--os", "linux")] // --os without --ssh
    [InlineData("build-01", "--ssh", "me@new-pc.lan", "--os", "linux")] // both
    [InlineData("--ssh", "-oProxyCommand=evil", "--os", "linux")]
    [InlineData("--ssh", "me@new pc", "--os", "linux")]
    [InlineData("no-such-machine")]
    [InlineData]
    public async Task BadArgumentsAreExit2WithoutAnySsh(params string[] args)
    {
        var (code, _, e, ssh) = await Run(Report(), args);
        Assert.Equal(2, code);
        Assert.NotEmpty(e);
        Assert.Empty(ssh.InputRuns);
    }

    [Fact]
    public void AnUnknownOsIsABadArgument() =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", "me@pc", "--os", "macos"]));

    /// <summary>With --json, stdout is exactly one JSON object on every path: the report, or status/exitCode/message.</summary>
    private static void OneJsonObject(string stdout, string status, int exitCode)
    {
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(exitCode, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task JsonPrintsOneObjectForAnArgumentError()
    {
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["makeready", "--json", "--bogus"], o, new StringWriter(), CancellationToken.None));
        OneJsonObject(o.ToString(), "Rejected", 2);
    }

    [Fact]
    public async Task JsonPrintsOneObjectWhenTheMachineIsUnknownHeldOrUnreachable()
    {
        var (code, o, _, _) = await Run(Report(), ["no-such-machine", "--json"]);
        Assert.Equal(2, code);
        OneJsonObject(o, "Rejected", 2);

        (code, o, _, _) = await Run(Report(), ["build-01", "--json"], (_, _) => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)));
        Assert.Equal(75, code);
        OneJsonObject(o, "Held", 75);

        (code, o, _, _) = await Run(Report(), ["build-01", "--json"], result: new SshResult(255, "") { ErrorOutput = "ssh: connect to host build-01.lan port 22: No route to host" });
        Assert.Equal(70, code);
        OneJsonObject(o, "InfraError", 70);

        (code, o, _, _) = await Run("not json", ["build-01", "--json"]);
        Assert.Equal(70, code);
        OneJsonObject(o, "InfraError", 70);
    }

    [Fact]
    public async Task JsonPrintsOneObjectWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, Healthy), InputHandler = (_, _, _) => throw new OperationCanceledException() };
        var o = new StringWriter();
        var code = await Cli.Makeready(ParsedArgs.Parse(["makeready", "build-01", "--json"]), T.Config, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), cts.Token);
        Assert.Equal(130, code);
        OneJsonObject(o.ToString(), "Cancelled", 130);
    }

    [Fact]
    public async Task APausedMachineIsHeldUnlessForced()
    {
        var paused = new Func<Machine, string, SshResult>((_, _) => new SshResult(0, Healthy.Replace("paused=0", "paused=1", StringComparison.Ordinal)));
        var (code, _, e, ssh) = await Run(Report(), ["build-01"], paused);
        Assert.Equal(75, code);
        Assert.Contains("--force", e, StringComparison.Ordinal);
        Assert.Empty(ssh.InputRuns);

        (code, _, _, ssh) = await Run(Report(), ["build-01", "--force"], paused);
        Assert.Equal(0, code);
        Assert.Single(ssh.InputRuns);
    }

    [Fact]
    public async Task AMachineOutsideItsAllowedHoursIsHeld()
    {
        var config = T.Config();
        config.Machines[0].AllowedHours = ["00:00-00:01"];
        var probe = new Func<Machine, string, SshResult>((_, _) => new SshResult(0, Healthy + "local_time=2026-01-05T12:00:00\n"));
        var (code, _, e, ssh) = await Run(Report(), ["build-01"], probe, config: config);
        Assert.Equal(75, code);
        Assert.Contains("outside allowed hours", e, StringComparison.Ordinal);
        Assert.Empty(ssh.InputRuns);
    }

    [Fact]
    public async Task SshFailureAndTimeoutAreInfrastructureErrors()
    {
        var (code, _, e, _) = await Run(null, ["build-01"], result: new SshResult(255, "") { ErrorOutput = "Host key verification failed." });
        Assert.Equal(70, code);
        Assert.Contains("Host key verification failed.", e, StringComparison.Ordinal);

        (code, _, _, _) = await Run(null, ["build-01"], result: new SshResult(124, "", TimedOut: true));
        Assert.Equal(70, code);

        var (unreachable, _, _, _) = await Run(Report(), ["build-01"], (_, _) => new SshResult(255, "ssh: connect to host build-01.lan port 22: No route to host"));
        Assert.Equal(70, unreachable);
    }

    public static TheoryData<string> Unusable() =>
    [
        "",
        "not json",
        Report(schema: "lanq-makeready/v2"),
        Report(git: "MAYBE"),
        Report().Replace("\"checks\": [", "\"checks\": [ { \"id\": \"x\", \"title\": \"x\", \"status\": \"rm -rf /\", \"detail\": \"\", \"advice\": \"\", \"needsAdmin\": false },", StringComparison.Ordinal),
        new string('[', 5000) + new string(']', 5000),
        Report().Replace("\"suggestedMachine\"", "\"other\"", StringComparison.Ordinal),
    ];

    /// <summary>
    /// An otherwise valid report with 30 levels of nesting under a property the schema does not know: only the 16-level MaxDepth of the
    /// source-generated context rejects it (the default, 64, would skip the unknown property and accept the report).
    /// </summary>
    [Fact]
    public async Task DeepNestingUnderAnUnknownPropertyIsRefusedByMaxDepth()
    {
        var nested = Report().Replace("\"generatedAt\"", "\"extra\": " + new string('[', 30) + new string(']', 30) + ", \"generatedAt\"", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => MakereadyReport.Parse(nested));
        var (code, _, e, _) = await Run(nested, ["build-01"]);
        Assert.Equal(70, code);
        Assert.Contains("not valid JSON", e, StringComparison.Ordinal);

        var shallow = Report().Replace("\"generatedAt\"", "\"extra\": [[[1]]], \"generatedAt\"", StringComparison.Ordinal);
        Assert.True(MakereadyReport.Parse(shallow).WorkerReady); // unknown properties themselves are fine
    }

    /// <summary>Report values used in the suggested entry must match their pattern to the very end: a trailing newline is not "the end".</summary>
    [Fact]
    public async Task SuggestedValuesWithATrailingNewlineAreNotUsed()
    {
        var report = Report()
            .Replace("\"name\": \"old-pc\"", "\"name\": \"evil-pc\\n\"", StringComparison.Ordinal)
            .Replace("\"allowed_hours\": \"00:00-07:00\"", "\"allowed_hours\": \"00:00-07:00\\n\"", StringComparison.Ordinal)
            .Replace("\"capabilities\": [\"dotnet\", \"java\", \"linux\"]", "\"capabilities\": [\"dotnet\\n\", \"java\", \"linux\"]", StringComparison.Ordinal);
        var (code, o, _, _) = await Run(report, ["--ssh", "me@new-pc.lan", "--os", "linux"]);
        Assert.Equal(0, code);
        Assert.Contains("name: \"old-pc\"", o, StringComparison.Ordinal);
        Assert.DoesNotContain("evil-pc", o, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed_hours", o, StringComparison.Ordinal);
        Assert.Contains("capabilities: [java, linux]", o, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task AnUnusableReportIsRefusedAsInfrastructure(string report)
    {
        var (code, o, e, _) = await Run(report, ["build-01"]);
        Assert.Equal(70, code);
        Assert.Contains("report", e, StringComparison.Ordinal);
        Assert.DoesNotContain("machines:", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedReportIsRefused()
    {
        var (code, _, e, _) = await Run(null, ["build-01"], result: new SshResult(0, Report()) { OutputTruncated = true });
        Assert.Equal(70, code);
        Assert.Contains("1 MB", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryStringFromTheReportIsSanitisedBeforeDisplay()
    {
        var evil = $"\\u001b]0;pwned\\u0007\\u001b[2J{Esc}x";
        var json = Report(detail: "a\\u001b[31mred", advice: evil, caps: "\"dotnet\", \"bad\\u001bcap\"");
        json = json.Replace(Esc.ToString(), "", StringComparison.Ordinal); // raw ESC is not valid inside a JSON string anyway
        var (code, o, _, _) = await Run(json, ["build-01"]);

        Assert.Equal(0, code);
        Assert.DoesNotContain(Esc, o);
        Assert.DoesNotContain((char)7, o);
        Assert.DoesNotContain("badcap", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonPrintsTheValidatedReportInsteadOfTheText()
    {
        var (code, o, _, _) = await Run(Report(git: "FAIL", archive: "FAIL"), ["build-01", "--json"]);
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("lanq-makeready/v1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("FAIL", doc.RootElement.GetProperty("verdicts").GetProperty("worker_git").GetString());
        Assert.Equal("/srv/lq", doc.RootElement.GetProperty("suggestedMachine").GetProperty("work_root").GetString());
        Assert.DoesNotContain("lanq never changes", o, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealReportFromTheWindowsScriptParses()
    {
        // Shape of ConvertTo-Json output (two-space indents after the colon, escaped angle brackets) from a real -Json - -Redact run.
        var json = Report().Replace("\": ", "\":  ", StringComparison.Ordinal).Replace("<redacted>", "\\u003credacted\\u003e", StringComparison.Ordinal);
        var r = MakereadyReport.Parse(json);
        Assert.Equal("<redacted>", r.Host!.Hostname);
        Assert.True(r.WorkerReady);
    }
}

/// <summary>Opt-in: only runs when LANQ_MAKEREADY_TESTS=1 (it runs the real embedded check on this machine, which takes up to a minute).</summary>
public sealed class OptInMakereadyFactAttribute : FactAttribute
{
    public OptInMakereadyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LANQ_MAKEREADY_TESTS") != "1")
        {
            Skip = "Set LANQ_MAKEREADY_TESTS=1 to run the embedded lanq-makeready script on this machine through stdin.";
        }
    }
}

public class MakereadyLocalRunTests
{
    /// <summary>The same invocation lanq sends over ssh, run locally: the script on stdin, the JSON report alone on stdout, and it parses.</summary>
    [OptInMakereadyFact]
    public async Task TheEmbeddedScriptRunsFromStdinAndItsReportValidates()
    {
        var windows = OperatingSystem.IsWindows();
        var (exe, args) = windows
            ? ("powershell.exe", new List<string> { "-NoProfile", "-NonInteractive", "-Command", "& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Json - -Redact" })
            : ("sh", new List<string> { "-s", "--", "--json", "-", "--redact" });
        var r = await ProcessSshClient.ExecuteWithInput(
            exe, args, Cli.MakereadyScript(windows ? MachineOs.Windows : MachineOs.Linux), MakereadyReport.MaxChars, Cli.MakereadyTimeout, CancellationToken.None);

        Assert.False(r.TimedOut);
        Assert.InRange(r.ExitCode, 0, 1);
        var report = MakereadyReport.Parse(r.Output);
        Assert.Equal("<redacted>", report.Host!.Hostname);
        Assert.Equal(r.ExitCode == 0, report.WorkerReady);
    }
}
