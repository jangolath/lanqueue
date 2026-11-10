using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq doctor: local checks with a stand-in for the local tools, remote checks with FakeSsh and with the real scripts.</summary>
public sealed class DoctorTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "lanq-doctor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    /// <summary>The local commands doctor ran, as "exe arg arg".</summary>
    private readonly List<string> _ran = [];

    private const string KnownHosts = "/home/me/.ssh/known_hosts";

    /// <summary>A healthy local machine: tools present, every host known, keys with passphrases. Override pieces per test.</summary>
    private DoctorHost Host(
        Func<string, SshResult?>? exec = null, Func<string, string?>? env = null, Func<string, bool>? exists = null, bool unencrypted = false, string? home = null) =>
        new(
            (exe, args) =>
            {
                var line = exe + " " + string.Join(' ', args);
                lock (_ran)
                {
                    _ran.Add(line);
                }

                return Task.FromResult(exec?.Invoke(line) ?? line switch
                {
                    "ssh -V" => new SshResult(0, "OpenSSH_9.6p1, OpenSSL 3.0.13"),
                    "git --version" => new SshResult(0, "git version 2.47.0"),
                    _ when line.StartsWith("ssh -G ", StringComparison.Ordinal) =>
                        new SshResult(0, $"user me\nhostname {line.Split('@')[^1]}\nport 22\nuserknownhostsfile ~/.ssh/known_hosts ~/.ssh/known_hosts2\n"),
                    _ when line.StartsWith("ssh-keygen -F ", StringComparison.Ordinal) => new SshResult(0, "# Host found: line 3\nhost ssh-ed25519 AAAA"),
                    _ => new SshResult(1, "usage: ..."), // scp and ssh-keygen without arguments print usage
                });
            },
            env ?? (k => k switch { "HOME" => "/home/me", "LANQ_KEY_BUILD01" => "/keys/build01", _ => null }),
            exists ?? (path => path is KnownHosts or "/keys/build01"),
            _ => Task.FromResult(unencrypted),
            home ?? _home);

    private static async Task<(int Code, string Out)> Doctor(
        DoctorHost host, ISshClient? ssh = null, Func<FleetConfig>? config = null, bool remote = false, bool force = false, TimeProvider? clock = null,
        Func<Machine, string>? doctorScript = null)
    {
        var (code, o, e) = await DoctorRun(host, ssh, config, remote, force, clock, doctorScript);
        Assert.Equal("", e); // text mode: nothing on stderr
        return (code, o);
    }

    /// <summary>lanq doctor with stdout and stderr apart; <paramref name="json"/> adds --json.</summary>
    private static async Task<(int Code, string Out, string Err)> DoctorRun(
        DoctorHost host, ISshClient? ssh = null, Func<FleetConfig>? config = null, bool remote = false, bool force = false, TimeProvider? clock = null,
        Func<Machine, string>? doctorScript = null, bool json = false, CancellationToken ct = default, string? configPath = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var p = ParsedArgs.Parse([
            "doctor", .. remote ? ["--remote"] : (string[])[], .. force ? ["--force"] : (string[])[], .. json ? ["--json"] : (string[])[],
            .. configPath is null ? (string[])[] : ["--config", configPath]]);
        ssh ??= new FakeSsh();
        var code = await Cli.Doctor(p, config ?? T.Config, host, ssh, new HealthProbe(ssh, clock ?? TimeProvider.System), o, e, ct, doctorScript);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>stdout of doctor --json: exactly one object (parsing fails on anything else).</summary>
    private static JsonElement OneObject(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> Checks(JsonElement json) => [.. json.GetProperty("checks").EnumerateArray()];

    private static List<JsonElement> Steps(JsonElement json) => [.. json.GetProperty("nextSteps").EnumerateArray()];

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    /// <summary>A text line rebuilt from one JSON check: LEVEL, two spaces, the machine prefix by scope, the detail.</summary>
    private static string TextOf(JsonElement check)
    {
        var prefix = Str(check, "scope") switch
        {
            "machine" => Str(check, "machine") + ": ",
            "remote" => Str(check, "machine") + " (remote): ",
            _ => "",
        };
        return $"{Str(check, "level")!.ToUpperInvariant(),-4}  {prefix}{Str(check, "detail")}";
    }

    private static string[] NonEmptyLines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>--remote where build-01 has a missing tool and an unknown capability, and build-02 is outside its allowed hours (noon).</summary>
    private static (FleetConfig Config, FakeSsh Ssh, FakeClock Noon) HeldAndFailing()
    {
        var config = T.Config();
        config.Machines[0].Capabilities = ["dotnet", "node", "gpu"];
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => IsProbe(cmd)
                ? new SshResult(0, HealthyProbe + "local_time=2026-01-05T12:00:00\n")
                : new SshResult(0, "root=ok\ntool=ok\ngit_version=git version 2.47.0\ncap.dotnet=0|10.0.100\ncap.node=127|sh: 1: node: not found\n"),
        };
        return (config, ssh, new FakeClock(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task DoctorJsonIsOneObjectAndTheLinesGoToStderr()
    {
        var (text, textOut) = await Doctor(Host());
        var (code, o, e) = await DoctorRun(Host(), json: true);

        Assert.Equal(0, code);
        var json = OneObject(o);
        Assert.Equal("Ok", Str(json, "status"));
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.False(json.GetProperty("remote").GetBoolean());
        Assert.False(string.IsNullOrEmpty(Str(json, "message")));
        var counts = json.GetProperty("counts");
        Assert.Equal(Checks(json).Count, counts.GetProperty("ok").GetInt32());
        foreach (var level in new[] { "warn", "fail", "skip", "hold" })
        {
            Assert.Equal(0, counts.GetProperty(level).GetInt32());
        }

        // Every key of every check is present (null when it does not apply).
        Assert.All(Checks(json), c => Assert.Equal(["scope", "machine", "name", "level", "detail", "fix", "capability"], c.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("none", Str(Assert.Single(Steps(json)), "action"));

        // The lines are the text output, now on stderr; with nothing to do there is no Next: line.
        Assert.Equal(0, text);
        Assert.Equal(textOut, e);
        Assert.DoesNotContain("Next:", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryTextLineIsACheckWithTheSameDetail()
    {
        var (config, ssh, noon) = HeldAndFailing();
        var (_, text) = await Doctor(Host(), ssh, () => config, remote: true, clock: noon);
        var (_, o, _) = await DoctorRun(Host(), ssh, () => config, remote: true, clock: noon, json: true);

        Assert.Equal(NonEmptyLines(text), Checks(OneObject(o)).Select(TextOf));
    }

    [Fact]
    public async Task DoctorJsonStatusAndExitCodeMatchTheText()
    {
        Directory.CreateDirectory(_home);
        var key = Path.Combine(_home, "id_test");
        File.WriteAllText(key, "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");
        var unencrypted = Host(
            env: k => k switch { "HOME" => "/home/me", "LANQ_KEY_BUILD01" => key, _ => null },
            exists: p => p == KnownHosts || p == key,
            unencrypted: true);
        var noGit = Host(exec: line => line.StartsWith("git ", StringComparison.Ordinal) ? new SshResult(255, "Could not start git: not found") : null);

        foreach (var (host, status, exit) in new[] { (Host(), "Ok", 0), (unencrypted, "Warnings", 1), (noGit, "Failing", 1) })
        {
            var (textCode, _) = await Doctor(host);
            var (code, o, _) = await DoctorRun(host, json: true);
            var json = OneObject(o);
            Assert.Equal(exit, textCode);
            Assert.Equal(exit, code);
            Assert.Equal(status, Str(json, "status"));
            Assert.Equal(exit, json.GetProperty("exitCode").GetInt32());
        }
    }

    [Fact]
    public async Task AConfigErrorIsAFailingCheckNotAnErrorObject()
    {
        var (code, o, e) = await DoctorRun(Host(), config: () => throw new ConfigException("Machine 'x': ssh target must be user@host"), json: true, configPath: "fleet.yml");

        Assert.Equal(1, code);
        var json = OneObject(o);
        Assert.Equal("Failing", Str(json, "status"));
        Assert.Equal(1, json.GetProperty("counts").GetProperty("fail").GetInt32());
        var config = Assert.Single(Checks(json), c => Str(c, "level") == "fail");
        Assert.Equal(("local", "config"), (Str(config, "scope"), Str(config, "name")));
        Assert.Contains("ssh target must be user@host", Str(config, "detail"), StringComparison.Ordinal);
        Assert.Contains("ssh target must be user@host", Str(json, "message"), StringComparison.Ordinal);
        Assert.DoesNotContain(Checks(json), c => Str(c, "scope") != "local"); // no machine was checked

        // For the developer only: no command (a doctor suggestion would only repeat this).
        var step = Assert.Single(Steps(json));
        Assert.Equal("ask_developer", Str(step, "action"));
        Assert.False(step.TryGetProperty("argv", out _));
        Assert.Contains("Next: the config could not be loaded", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteChecksCarryMachineScopeAndCapability()
    {
        var (config, ssh, noon) = HeldAndFailing();
        var (code, o, _) = await DoctorRun(Host(), ssh, () => config, remote: true, clock: noon, json: true);

        Assert.Equal(1, code);
        var json = OneObject(o);
        Assert.Equal("Failing", Str(json, "status"));
        Assert.True(json.GetProperty("remote").GetBoolean());
        var checks = Checks(json);
        Assert.All(checks.Where(c => Str(c, "scope") == "local"), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("machine").ValueKind));
        Assert.Equal(["ssh", "scp", "git", "ssh_keygen", "state_folder", "config", "logs"], checks.Where(c => Str(c, "scope") == "local").Select(c => Str(c, "name")));
        Assert.Equal(
            ["known_hosts", "key_env", "work_root", "known_hosts", "work_root"],
            checks.Where(c => Str(c, "scope") == "machine").Select(c => Str(c, "name")));

        var remote = checks.Where(c => Str(c, "scope") == "remote" && Str(c, "machine") == "build-01").ToList();
        Assert.Equal(["work_root_writable", "transfer_tool", "capability", "capability", "capability"], remote.Select(c => Str(c, "name")));
        Assert.Equal(["dotnet", "node", "gpu"], remote.Where(c => Str(c, "name") == "capability").Select(c => Str(c, "capability")));
        Assert.Equal(["ok", "fail", "skip"], remote.Where(c => Str(c, "name") == "capability").Select(c => Str(c, "level")));
        var node = remote.Single(c => Str(c, "capability") == "node");
        Assert.Equal("add the tool's folder to path_prepend for this machine", Str(node, "fix"));
        Assert.All(remote.Where(c => Str(c, "name") != "capability"), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("capability").ValueKind));

        // The fail is for the developer, named with its machine and capability; never a command.
        var ask = Assert.Single(Steps(json), s => Str(s, "action") == "ask_developer");
        Assert.Contains("build-01 capability node", Str(ask, "reason"), StringComparison.Ordinal);
        Assert.Contains("checks[].fix", Str(ask, "reason"), StringComparison.Ordinal);
        Assert.False(ask.TryGetProperty("argv", out _));
    }

    [Fact]
    public async Task HeldMachinesAreHoldChecksAndAWaitStepWithoutForce()
    {
        var config = T.Config();
        var noon = new FakeClock(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => IsProbe(cmd)
                ? new SshResult(0, HealthyProbe.Replace("paused=0", m.Name == "build-01" ? "paused=1" : "paused=0", StringComparison.Ordinal) + "local_time=2026-01-05T12:00:00\n")
                : new SshResult(0, "root=ok\ntool=ok\n"),
        };
        var (code, o, e) = await DoctorRun(Host(), ssh, () => config, remote: true, clock: noon, json: true);

        Assert.Equal(0, code); // a held machine is not a problem with the setup
        var json = OneObject(o);
        Assert.Equal("Ok", Str(json, "status"));
        Assert.Equal(2, json.GetProperty("counts").GetProperty("hold").GetInt32());
        var held = Checks(json).Where(c => Str(c, "level") == "hold").ToList();
        Assert.Equal(["build-01", "build-02"], held.Select(c => Str(c, "machine")));
        Assert.All(held, c => Assert.Equal(("remote", "held"), (Str(c, "scope"), Str(c, "name"))));
        Assert.All(held, c => Assert.Equal("the developer can add --force to check it anyway", Str(c, "fix")));

        var wait = Assert.Single(Steps(json));
        Assert.Equal("wait", Str(wait, "action"));
        Assert.Contains("build-01, build-02", Str(wait, "reason"), StringComparison.Ordinal);
        Assert.False(wait.TryGetProperty("argv", out _));
        Assert.DoesNotContain("--force", Str(wait, "reason"), StringComparison.Ordinal);
        Assert.Contains("Next: not checked now", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorJsonCtrlCKeepsTheLocalChecks()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (code, o, _) = await DoctorRun(Host(), remote: true, json: true, ct: cts.Token);

        Assert.Equal(130, code);
        var json = OneObject(o);
        Assert.Equal("Cancelled", Str(json, "status"));
        Assert.Equal(130, json.GetProperty("exitCode").GetInt32());
        Assert.Contains(Checks(json), c => Str(c, "name") == "config" && Str(c, "level") == "ok");
        Assert.Contains(Checks(json), c => Str(c, "scope") == "machine");
        Assert.DoesNotContain(Checks(json), c => Str(c, "scope") == "remote");
        Assert.Equal("none", Str(Assert.Single(Steps(json)), "action"));

        // Text mode is unchanged: Ctrl+C ends lanq (Program.cs exits 130 with no output of its own).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DoctorRun(Host(), remote: true, ct: cts.Token));
    }

    [Fact]
    public async Task DoctorJsonUnexpectedErrorIs70()
    {
        var host = Host(exec: line => line.StartsWith("ssh -G ", StringComparison.Ordinal) ? throw new InvalidOperationException("boom\u001b[2J") : (SshResult?)null);
        var (code, o, e) = await DoctorRun(host, json: true, configPath: "fleet.yml");

        Assert.Equal(70, code);
        var json = OneObject(o);
        Assert.Equal("InfraError", Str(json, "status"));
        Assert.Equal(70, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("unexpected error: InvalidOperationException: boom", Str(json, "message"), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", o, StringComparison.Ordinal);
        Assert.Contains(Checks(json), c => Str(c, "name") == "config"); // the checks so far

        // Once more, the same read-only doctor with --json, never --force.
        var retry = Assert.Single(Steps(json));
        Assert.Equal("retry", Str(retry, "action"));
        Assert.Equal(["lanq", "doctor", "--json", "--config", "fleet.yml"], retry.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.True(retry.GetProperty("safeToRunUnattended").GetBoolean());
        Assert.Contains("Next: lanq doctor broke unexpectedly", e, StringComparison.Ordinal);

        // Text mode is unchanged: the error is not caught.
        await Assert.ThrowsAsync<InvalidOperationException>(() => DoctorRun(host));
    }

    [Fact]
    public async Task DoctorNextStepsNeverSuggestForceOrACommandForKnownHosts()
    {
        var host = Host(exec: line => line switch
        {
            "ssh -G user@build-01.lan" => new SshResult(0, "hostname 192.0.2.10\nport 2222\n"),
            _ when line.StartsWith("ssh-keygen -F [192.0.2.10]:2222", StringComparison.Ordinal) => new SshResult(1, ""),
            _ => null,
        });
        var (config, ssh, noon) = HeldAndFailing();
        var (code, o, _) = await DoctorRun(host, ssh, () => config, remote: true, clock: noon, json: true);

        Assert.Equal(1, code);
        var json = OneObject(o);
        var known = Assert.Single(Checks(json), c => Str(c, "name") == "known_hosts" && Str(c, "level") == "fail");
        Assert.Equal("connect once with ssh user@build-01.lan, compare the fingerprint with the machine's owner, then accept it", Str(known, "fix"));
        Assert.Contains(Steps(json), s => Str(s, "reason")!.Contains("build-01 known_hosts", StringComparison.Ordinal));
        Assert.Contains(Steps(json), s => Str(s, "action") == "wait");

        // No step carries a command at all, so certainly never --force (nor ssh, nor ssh-keygen).
        Assert.All(Steps(json), s => Assert.False(s.TryGetProperty("argv", out _)));
        Assert.All(Steps(json), s => Assert.False(s.TryGetProperty("command", out _)));
        Assert.All(Steps(json), s => Assert.DoesNotContain("--force", Str(s, "reason"), StringComparison.Ordinal));
    }

    /// <summary>The logs check counts lanq's own job logs (as pruning would see them) and their size; other files in logs/ do not count.</summary>
    [Fact]
    public async Task TheLogsCheckReportsLanqsJobLogsAndTheirSize()
    {
        var logs = Path.Combine(_home, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllBytes(Path.Combine(logs, "20261109-101500-0a1b2c.log"), new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(logs, "20261109-101501-0a1b2d.log"), new byte[512 * 1024]);
        File.WriteAllText(Path.Combine(logs, "notes.log"), "not lanq's");
        File.WriteAllText(Path.Combine(logs, LogPrune.MarkerName), "");

        var (code, o) = await Doctor(Host());
        Assert.Equal(0, code);
        Assert.Contains("OK    logs: 2 files, 1.5 MB (kept 30 days)" + Environment.NewLine, o, StringComparison.Ordinal);

        var config = T.Config();
        config.Policy.KeepLogsDays = 0;
        (code, o) = await Doctor(Host(), config: () => config);
        Assert.Equal(0, code); // small and never pruned: fine
        Assert.Contains("OK    logs: 2 files, 1.5 MB (kept for ever: policy.keep_logs_days is 0)", o, StringComparison.Ordinal);
    }

    /// <summary>Over 2 GB of logs is a WARN only when they are never pruned (policy.keep_logs_days 0).</summary>
    [Theory]
    [InlineData(0, 3L * 1024 * 1024 * 1024, "warn")]
    [InlineData(30, 3L * 1024 * 1024 * 1024, "ok")]
    [InlineData(0, 2L * 1024 * 1024 * 1024, "ok")]
    public async Task TheLogsCheckWarnsOverTwoGigabytesWhenNeverPruned(int keepDays, long bytes, string level)
    {
        var config = T.Config();
        config.Policy.KeepLogsDays = keepDays;
        var host = Host() with { MeasureLogs = _ => (6000, bytes) };
        var (code, o, _) = await DoctorRun(host, config: () => config, json: true);

        var logs = Assert.Single(Checks(OneObject(o)), c => Str(c, "name") == "logs");
        Assert.Equal(("local", level), (Str(logs, "scope"), Str(logs, "level")));
        Assert.Equal(level == "warn" ? 1 : 0, code);
        Assert.Contains("logs: 6000 files, ", Str(logs, "detail"), StringComparison.Ordinal);
        if (level == "warn")
        {
            Assert.Contains("policy.keep_logs_days", Str(logs, "fix"), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, logs.GetProperty("fix").ValueKind);
        }
    }

    [Fact]
    public async Task AnUnreadableLogsFolderIsSkippedNotAProblem()
    {
        var host = Host() with { MeasureLogs = _ => throw new UnauthorizedAccessException("Access to the path is denied.") };
        var (code, o) = await Doctor(host);
        Assert.Equal(0, code);
        Assert.Contains(Lines(o, "SKIP"), l => l.Contains("logs: ", StringComparison.Ordinal) && l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void ManyProblemsAreNamedFiveAtATime()
    {
        var checks = Enumerable.Range(1, 7).Select(i => new DoctorCheck("machine", $"build-0{i}", "known_hosts", "fail", "no entry", "fix")).ToList();
        var ask = Assert.Single(LanQueue.Output.NextSteps.ForDoctor("Failing", checks, false, null, "7 checks"));
        Assert.Contains("build-01 known_hosts, build-02 known_hosts, build-03 known_hosts, build-04 known_hosts, build-05 known_hosts and 2 more", ask.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("build-06", ask.Reason, StringComparison.Ordinal);
        Assert.Null(ask.Argv);
    }

    [Fact]
    public async Task TextDoctorPrintsNoNextLines()
    {
        // Problems, a held machine and a config error: text mode prints only the lines, on stdout, and nothing on stderr (Doctor asserts it).
        var (config, ssh, noon) = HeldAndFailing();
        var (code, o) = await Doctor(Host(env: k => k == "HOME" ? "/home/me" : null), ssh, () => config, remote: true, clock: noon);
        Assert.Equal(1, code);
        Assert.DoesNotContain("Next:", o, StringComparison.Ordinal);
        Assert.All(NonEmptyLines(o), l => Assert.Matches("^(OK|WARN|FAIL|SKIP|HOLD) ", l));

        (code, o) = await Doctor(Host(), config: () => throw new ConfigException("bad"));
        Assert.Equal(1, code);
        Assert.DoesNotContain("Next:", o, StringComparison.Ordinal);
    }

    private const string HealthyProbe = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    private static bool IsProbe(string command) => command.Contains("disk_free_gb", StringComparison.Ordinal) || Decoded(command).Contains("disk_free_gb", StringComparison.Ordinal);

    private static string Decoded(string command) => command.Contains("-EncodedCommand ", StringComparison.Ordinal)
        ? System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command[(command.LastIndexOf(' ') + 1)..]))
        : command;

    /// <summary>Like lanq makeready, doctor --remote leaves a paused machine and one outside its hours alone unless --force.</summary>
    [Fact]
    public async Task RemoteRespectsAPauseFileAndAllowedHoursUnlessForced()
    {
        var config = T.Config(); // build-02 is allowed 22:00-07:00
        var noon = new FakeClock(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        var ssh = new FakeSsh
        {
            Handler = (m, cmd) => IsProbe(cmd)
                ? new SshResult(0, HealthyProbe.Replace("paused=0", m.Name == "build-01" ? "paused=1" : "paused=0", StringComparison.Ordinal) + "local_time=2026-01-05T12:00:00\n")
                : new SshResult(0, "root=ok\ntool=ok\ngit_version=git version 2.47.0\ncap.dotnet=0|10.0.100\ncap.node=0|v22\n"),
        };

        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true, clock: noon);

        Assert.Equal(0, code); // a held machine is not a problem with the setup
        Assert.DoesNotContain(ssh.Calls, c => !IsProbe(c.Command)); // nothing but the probe ran on either machine
        Assert.Contains(Lines(o, "HOLD"), l => l.Contains("build-01", StringComparison.Ordinal) && l.Contains("paused", StringComparison.Ordinal) && l.Contains("--force", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "HOLD"), l => l.Contains("build-02", StringComparison.Ordinal) && l.Contains("outside allowed hours", StringComparison.Ordinal));

        ssh.Calls.Clear();
        (code, o) = await Doctor(Host(), ssh, () => config, remote: true, force: true, clock: noon);
        Assert.Empty(Lines(o, "HOLD"));
        Assert.DoesNotContain(ssh.Calls, c => IsProbe(c.Command));
        Assert.Equal(3, ssh.Calls.Count); // the checks on both machines, and the WMI check on the Windows one
    }

    [Fact]
    public void ForceIsADoctorOption() => Assert.True(ParsedArgs.Parse(["doctor", "--remote", "--force"]).Force);

    /// <summary>A check that hangs is reported as timed out; the others still report.</summary>
    [Fact]
    public async Task RemoteReportsATimedOutCheckAndKeepsTheOthers()
    {
        var config = T.Config();
        config.Machines.RemoveAt(1);
        var ssh = new FakeSsh { Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe) : new SshResult(0, "root=ok\ntool=ok\ncap.dotnet=timeout|\ncap.node=0|v22.1.0\n") };
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("dotnet", StringComparison.Ordinal) && l.Contains("timed out", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "OK"), l => l.Contains("node", StringComparison.Ordinal) && l.Contains("v22.1.0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The dotnet on PATH has runtimes only (a job's build would fail): doctor says so, searches the worker's install folders and suggests the
    /// path_prepend: line (and DOTNET_ROOT) for the folder with the highest SDK. It never edits the config.
    /// </summary>
    [Fact]
    public async Task RuntimeOnlyDotnetIsMissingAndDoctorFixSuggestsPathPrepend()
    {
        var config = T.Config();
        config.Machines.RemoveAt(1);
        config.Machines[0].Capabilities = ["dotnet"];
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe)
                : new SshResult(0, "root=ok\ntool=ok\ngit_version=git version 2.47.0\ncap.dotnet=145|The command could not be loaded, possibly because:\n"),
            InputHandler = (_, cmd, input) => cmd == RemoteScripts.FindToolchainCommand(MachineOs.Linux) && Encoding.UTF8.GetString(input).StartsWith(RemoteScripts.FindToolchainMarker, StringComparison.Ordinal)
                ? new SshResult(0, "runtime.dotnet=6.0.11\nfound.dotnet=/usr/share/dotnet|8.0.404\nfound.dotnet=/srv/build/dotnet|10.0.102\n")
                : new SshResult(1, ""),
        };
        var (code, o, _) = await DoctorRun(Host(), ssh, () => config, remote: true, json: true);
        Assert.Equal(1, code);
        var dotnet = Checks(OneObject(o)).Single(c => Str(c, "name") == "capability");
        Assert.Equal("fail", Str(dotnet, "level"));
        Assert.Contains("dotnet 6.0.11 runtime only, no SDK", Str(dotnet, "detail"), StringComparison.Ordinal);
        Assert.Contains(".NET SDK 10.0.102 is installed in /srv/build/dotnet", Str(dotnet, "detail"), StringComparison.Ordinal);
        Assert.Equal(
            "add `path_prepend: [/srv/build/dotnet]` to build-01 in machines.yml (found .NET SDK 10.0.102 there), and `DOTNET_ROOT: /srv/build/dotnet` under its env: so the SDK's tools find the runtimes next to it",
            Str(dotnet, "fix"));
        Assert.Equal(1, ssh.Calls.Count(c => !IsProbe(c.Command))); // the checks, then one search on stdin; nothing else ran on the worker
        Assert.Single(ssh.InputRuns);
    }

    /// <summary>A Windows build account denied WMI is a WARN with what the owner could grant; lanq works without it. Linux is never asked.</summary>
    [Fact]
    public async Task WmiDeniedIsAWarnWithAFix()
    {
        var config = T.Config();
        config.Machines[1].Capabilities = [];
        var ssh = new FakeSsh { Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe) : new SshResult(0, "root=ok~tool=ok~git_version=git version 2.47.0~wmi=fail|Access denied~cap.dotnet=0|10.0.100~cap.node=0|v22~".Replace('~', (char)10)) };
        var (code, o, _) = await DoctorRun(Host(), ssh, () => config, remote: true, force: true, json: true);
        Assert.Equal(1, code);
        var wmi = Assert.Single(Checks(OneObject(o)), c => Str(c, "name") == "wmi");
        Assert.Equal(("remote", "build-02", "warn"), (Str(wmi, "scope"), Str(wmi, "machine"), Str(wmi, "level")));
        Assert.Contains("Access denied", Str(wmi, "detail"), StringComparison.Ordinal);
        Assert.Equal(Cli.WmiFix, Str(wmi, "fix"));
        Assert.Contains("wmimgmt.msc", Cli.WmiFix, StringComparison.Ordinal);
    }

    /// <summary>The only SDK found is in a folder other local users can write: no path_prepend is proposed, and the failure says why.</summary>
    [Fact]
    public async Task AnSdkInAFolderOthersCanWriteIsNamedButNotProposed()
    {
        var config = T.Config();
        config.Machines.RemoveAt(1);
        config.Machines[0].Capabilities = ["dotnet"];
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe) : new SshResult(0, "root=ok\ntool=ok\ncap.dotnet=127|sh: 1: dotnet: not found\n"),
            InputHandler = (_, cmd, input) => cmd == RemoteScripts.FindToolchainCommand(MachineOs.Linux) && Encoding.UTF8.GetString(input).StartsWith(RemoteScripts.FindToolchainMarker, StringComparison.Ordinal)
                ? new SshResult(0, "open.dotnet=/srv/shared/dotnet|10.0.102\n")
                : new SshResult(1, ""),
        };
        var (code, o, _) = await DoctorRun(Host(), ssh, () => config, remote: true, json: true);
        Assert.Equal(1, code);
        var dotnet = Checks(OneObject(o)).Single(c => Str(c, "name") == "capability");
        Assert.Equal("fail", Str(dotnet, "level"));
        Assert.Contains("a dotnet in /srv/shared/dotnet is not proposed: other local users can write there", Str(dotnet, "detail"), StringComparison.Ordinal);
        Assert.DoesNotContain("path_prepend: [/srv/shared", o, StringComparison.Ordinal);
    }

    /// <summary>Nothing found: the plain failure stays, with the usual path_prepend advice.</summary>
    [Fact]
    public async Task AMissingToolNotFoundAnywhereKeepsThePlainFailure()
    {
        var config = T.Config();
        config.Machines.RemoveAt(1);
        config.Machines[0].Capabilities = ["dotnet"];
        var ssh = new FakeSsh { Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe) : new SshResult(0, "root=ok\ntool=ok\ncap.dotnet=127|sh: 1: dotnet: not found\n") };
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("'dotnet --version' failed in the job environment (sh: 1: dotnet: not found)", StringComparison.Ordinal));
    }

    [Fact]
    public void PicksTheHighestSdkAndIgnoresGarbage()
    {
        var found = RemoteScripts.ParseFound(
            "found.dotnet=C:/Program Files/dotnet|8.0.404\nfound.dotnet=D:/build/dotnet|10.0.102\nfound.dotnet=E:/x|not-a-version\nfound.node=/x|22.0.0\nnoise\nfound.dotnet=F:/y|10.0.100-rc.2\n",
            "dotnet");
        Assert.Equal([("D:/build/dotnet", "10.0.102"), ("F:/y", "10.0.100-rc.2"), ("C:/Program Files/dotnet", "8.0.404")], found);
    }

    /// <summary>The search is read-only and bounded: fixed places, one level below each fixed drive (64 folders at most), about five seconds.</summary>
    [Fact]
    public void SearchIsBoundedAndTimesOut()
    {
        var windows = RemoteScripts.FindToolchain(T.Windows(), "dotnet");
        Assert.Contains("++$n -gt 64", windows, StringComparison.Ordinal);
        Assert.Contains("$sw.Elapsed.TotalSeconds -gt 5", windows, StringComparison.Ordinal);
        Assert.Contains("InstalledVersions", windows, StringComparison.Ordinal);
        Assert.Contains(@"$env:LOCALAPPDATA\Microsoft\dotnet", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("-Recurse", windows, StringComparison.Ordinal);
        var linux = RemoteScripts.FindToolchain(T.Linux(), "dotnet");
        Assert.Contains("/etc/dotnet/install_location", linux, StringComparison.Ordinal);
        Assert.Contains("end=$(( $(date +%s) + 5 ))", linux, StringComparison.Ordinal);
        Assert.Equal("sh -s", RemoteScripts.FindToolchainCommand(MachineOs.Linux));
        Assert.Contains("[Console]::In.ReadToEnd()", RemoteScripts.FindToolchainCommand(MachineOs.Windows), StringComparison.Ordinal);
        foreach (var script in new[] { windows, linux })
        {
            foreach (var write in new[] { "Set-Content", "New-Item", "Remove-Item", "mkdir", "> /", "Set-Acl", "chmod", "icacls" })
            {
                Assert.DoesNotContain(write, script, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch(@"(^|[\s;|&(])rm\s", script); // rm as a command (not the "rm " inside find's -perm)
        }

        Assert.Throws<ArgumentException>(() => RemoteScripts.FindToolchain(T.Linux(), "gpu"));
    }

    /// <summary>A real search on this host finds an SDK in a private folder named by DOTNET_ROOT (no SDK is run; the folders are fake).</summary>
    [Fact]
    public void FindsSdkInAPrivateFolder()
    {
        var shell = OperatingSystem.IsWindows() ? "windows" : "linux";
        if (shell == "linux" && Shells.Sh is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "lanq-find-" + Guid.NewGuid().ToString("N"));
        var dotnet = Path.Combine(root, "build", "dotnet");
        Directory.CreateDirectory(Path.Combine(dotnet, "sdk", "10.0.102"));
        Directory.CreateDirectory(Path.Combine(dotnet, "sdk", "9.0.300"));
        Directory.CreateDirectory(Path.Combine(dotnet, "sdk", "11.0.100")); // no dotnet.dll: not an SDK
        File.WriteAllText(Path.Combine(dotnet, "sdk", "10.0.102", "dotnet.dll"), "");
        File.WriteAllText(Path.Combine(dotnet, "sdk", "9.0.300", "dotnet.dll"), "");
        var exe = Path.Combine(dotnet, shell == "windows" ? "dotnet.exe" : "dotnet");
        File.WriteAllText(exe, "");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var m = shell == "windows" ? T.Windows() : T.Linux();
            var (code, output) = Shells.Run(shell, RemoteScripts.FindToolchain(m, "dotnet"), new Dictionary<string, string> { ["DOTNET_ROOT"] = dotnet });
            Assert.True(code == 0, output);

            // Found, or open when this host's temp folder lets other accounts write (GitHub's Windows runner, a PC with sandbox accounts):
            // which one is the folder rule's call, tested on exact security descriptors elsewhere; this test is about finding the folder.
            var dir = dotnet.Replace('\\', '/');
            Assert.True(
                RemoteScripts.ParseFound(output, "dotnet").Contains((dir, "10.0.102")) || RemoteScripts.ParseFound(output, "dotnet", "open").Contains((dir, "10.0.102")),
                output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A folder other local users can write to (Everyone may modify it on Windows, world-writable on Linux) could hold a planted dotnet:
    /// the search reports it as open.dotnet, which ParseFound never proposes for path_prepend.
    /// </summary>
    [Fact]
    public void AFolderOtherUsersCanWriteIsNeverProposed()
    {
        var shell = OperatingSystem.IsWindows() ? "windows" : "linux";
        if (shell == "linux" && Shells.Sh is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "lanq-open-" + Guid.NewGuid().ToString("N"));
        var dotnet = Path.Combine(root, "dotnet");
        Directory.CreateDirectory(Path.Combine(dotnet, "sdk", "10.0.102"));
        File.WriteAllText(Path.Combine(dotnet, "sdk", "10.0.102", "dotnet.dll"), "");
        var exe = Path.Combine(dotnet, shell == "windows" ? "dotnet.exe" : "dotnet");
        File.WriteAllText(exe, "");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", [dotnet, "/grant", "*S-1-1-0:(OI)(CI)(M)"]) { RedirectStandardOutput = true, UseShellExecute = false })!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            else
            {
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(dotnet, (UnixFileMode)0b111_111_111); // 0777
            }

            var m = shell == "windows" ? T.Windows() : T.Linux();
            var (code, output) = Shells.Run(shell, RemoteScripts.FindToolchain(m, "dotnet"), new Dictionary<string, string> { ["DOTNET_ROOT"] = dotnet });
            Assert.True(code == 0, output);
            Assert.Contains("open.dotnet=" + dotnet.Replace('\\', '/') + "|10.0.102", output, StringComparison.Ordinal);
            Assert.DoesNotContain(RemoteScripts.ParseFound(output, "dotnet"), f => f.Dir == dotnet.Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A Windows worker gets the PowerShell search on stdin through the command that runs it from memory.</summary>
    [Fact]
    public async Task TheWindowsSearchGoesOnStdinToPowerShell()
    {
        var ssh = new FakeSsh { InputHandler = (_, _, _) => new SshResult(0, "found.dotnet=D:/build/dotnet|10.0.102\n") };
        var search = await Cli.FindToolchain(T.Windows(), ssh, "dotnet", CancellationToken.None);
        Assert.Equal("D:/build/dotnet", search!.Value.Found!.Value.Dir);
        var run = Assert.Single(ssh.InputRuns);
        Assert.Equal(RemoteScripts.FindToolchainCommand(MachineOs.Windows), run.Command);
        Assert.StartsWith(RemoteScripts.FindToolchainMarker + " dotnet", Encoding.UTF8.GetString(run.Input), StringComparison.Ordinal);
        Assert.Contains("Test-SafeFolder", Encoding.UTF8.GetString(run.Input), StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public void OpenFoldersAreNotParsedAsFound() =>
        Assert.Equal([("D:/build/dotnet", "10.0.102")], RemoteScripts.ParseFound("open.dotnet=C:/tools/dotnet|11.0.100\nfound.dotnet=D:/build/dotnet|10.0.102\n", "dotnet"));

    /// <summary>If the whole ssh call runs out of time, what did arrive is still reported, and the rest says it did not finish.</summary>
    [Fact]
    public async Task RemoteParsesPartialOutputWhenTheWholeCallTimesOut()
    {
        var config = T.Config();
        config.Machines.RemoveAt(1);
        var ssh = new FakeSsh { Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe) : new SshResult(124, "root=ok\ntool=ok\ncap.dotnet=0|10.0.100\n", TimedOut: true) };
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "OK"), l => l.Contains("dotnet", StringComparison.Ordinal) && l.Contains("10.0.100", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("node", StringComparison.Ordinal) && l.Contains("did not finish", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(o, "FAIL"), l => l.Contains("unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRemoteCheckHasItsOwnTimeout()
    {
        var linux = T.Linux();
        linux.Capabilities = ["dotnet", "rust"];
        var sh = RemoteScripts.Doctor(linux, checkSeconds: 7);
        Assert.Contains("if timeout -k 5 30 true >/dev/null 2>&1; then T=\"timeout -k 5 7\"; fi", sh, StringComparison.Ordinal);
        Assert.Contains("$T /bin/sh -c 'dotnet --version'", sh, StringComparison.Ordinal);
        Assert.Contains("=timeout|", sh, StringComparison.Ordinal);

        var windows = T.Windows();
        windows.Capabilities = ["dotnet", "rust"];
        var ps = RemoteScripts.Doctor(windows, checkSeconds: 7);
        Assert.Contains("WaitForExit(7000)", ps, StringComparison.Ordinal);
        Assert.Contains("{ LqKill $p.Id; \"cap.$n=timeout|\"; return }", ps, StringComparison.Ordinal); // the check's tree, without WMI
        Assert.Contains(RemoteScripts.WindowsJobHelperBase64, ps, StringComparison.Ordinal); // the helper runs only when it is lanq's own text
        Assert.DoesNotContain(". $LqH", ps, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-CimInstance", ps, StringComparison.Ordinal); // a separate small call (RemoteScripts.WmiCheck)
    }

    [Fact]
    public void TheRustCheckCanNeverInstallAToolchain()
    {
        var m = T.Linux();
        m.Capabilities = ["rust", "go"];
        Assert.Contains("RUSTUP_AUTO_INSTALL=0 cargo --version", RemoteScripts.Doctor(m), StringComparison.Ordinal);
        Assert.Contains("GOTOOLCHAIN=local go version", RemoteScripts.Doctor(m), StringComparison.Ordinal);
        var w = T.Windows();
        w.Capabilities = ["rust", "go"];
        Assert.Contains("$env:RUSTUP_AUTO_INSTALL = ''0''; cargo --version", RemoteScripts.Doctor(w), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowsDoctorScriptWithEveryCheckFitsTheCommandLine()
    {
        var m = T.Windows();
        m.Capabilities = [.. RemoteScripts.ToolChecks.Keys];
        m.Env["NODE_OPTIONS"] = "--max-old-space-size=2048";
        m.PathPrepend.Add("C:/Users/build/.cargo/bin");
        Assert.NotEmpty(RemoteScripts.Wrap(m, RemoteScripts.Doctor(m)));
    }

    private static void MakeTool(string bin, string name, string sh, string cmd)
    {
        File.WriteAllText(Path.Combine(bin, name + ".cmd"), cmd + (char)13 + (char)10);
        var file = Path.Combine(bin, name);
        File.WriteAllText(file, "#!/bin/sh" + (char)10 + sh + (char)10);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [ShFact]
    public void TheRealShScriptStopsAHangingToolCheck() => TheRealScriptStopsAHangingToolCheck("linux");

    /// <summary>
    /// A tool that ignores SIGTERM outlives timeout's TERM and is killed by its -k KILL, which makes timeout exit 137, not 124. That is still a
    /// check that ran out of time. (Under load Git for Windows sh delivers the emulated TERM late enough that the hanging-tool test above saw
    /// exactly this and reported cap.dotnet=137.) A short check limit keeps the test quick; the assertion is on the line, never on timing.
    /// </summary>
    [ShFact]
    public void TheRealShScriptReportsATermIgnoringToolAsTimedOut()
    {
        using var worker = new LocalWorker();
        var bin = Path.Combine(worker.Root, "fake-bin");
        Directory.CreateDirectory(bin);
        MakeTool(bin, "dotnet", "trap '' TERM; exec sleep 600 >/dev/null 2>&1", "@ping -n 600 127.0.0.1 >nul");
        var dir = bin.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
        {
            dir = "/" + char.ToLowerInvariant(dir[0]) + dir[2..];
        }

        var m = worker.Machine;
        m.Os = MachineOs.Linux;
        m.Capabilities = ["dotnet"];
        m.PathPrepend = [dir];
        m.Transfer = TransferMode.Archive;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lines = StaleSlotTests.RunScript("linux", RemoteScripts.Doctor(m, checkSeconds: 3)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5), "took " + watch.Elapsed);
        Assert.Contains("cap.dotnet=timeout|", lines);
    }

    [WindowsFact]
    public void TheRealPowerShellScriptStopsAHangingToolCheck() => TheRealScriptStopsAHangingToolCheck("windows");

    private static void TheRealScriptStopsAHangingToolCheck(string shell)
    {
        using var worker = new LocalWorker();
        var bin = Path.Combine(worker.Root, "fake-bin");
        Directory.CreateDirectory(bin);
        // The hang lasts ten minutes and each check may take 20 s (under load, starting a check, a fresh PowerShell, can itself take seconds).
        // The hanging tool records its pid (and start time) with its output detached, and the proof is that this process is gone afterwards.
        var hung = Path.Combine(worker.Root, "hung.pid");
        MakeTool(
            bin,
            "dotnet",
            "echo $$ > '" + hung.Replace('\\', '/') + "'; exec sleep 600 >/dev/null 2>&1",
            "@powershell -NoProfile -Command \"" + Recorded.PowerShellWrite(hung) + "; Start-Sleep -Seconds 600\" >nul 2>nul");
        MakeTool(bin, "npm", "echo 9.9.9-fake", "@echo 9.9.9-fake");
        var dir = bin.Replace('\\', '/');
        if (shell == "linux" && OperatingSystem.IsWindows())
        {
            dir = "/" + char.ToLowerInvariant(dir[0]) + dir[2..]; // Git for Windows sh: a PATH entry cannot contain "C:"
        }

        var m = worker.Machine;
        m.Os = shell == "windows" ? MachineOs.Windows : MachineOs.Linux;
        m.Capabilities = ["dotnet", "npm"];
        m.PathPrepend = [dir];
        m.Transfer = TransferMode.Archive;
        if (shell == "windows")
        {
            // A worker that has run a job has the wrapper's helper; with it the check runs in a job object (no WMI needed to stop its tree).
            StaleSlotTests.RunScript(shell, RemoteScripts.EnsureIncoming(m));
            Assert.True(File.Exists(RemoteScripts.WindowsHelperPath(m)));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lines = StaleSlotTests.RunScript(shell, RemoteScripts.Doctor(m, checkSeconds: 20)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5), "took " + watch.Elapsed);
        Assert.Contains("cap.dotnet=timeout|", lines);
        Assert.Contains("cap.npm=0|9.9.9-fake", lines);
        // The kill is proved by the recorded process being gone for PowerShell and for a real Linux sh. The Git for Windows sh variant does NOT
        // prove the kill: its $$ is an emulated pid, not a Windows one, so it only checks the timeout line and the npm result. The Linux leg
        // (CI and the sdk container) runs the sh script with the proof.
        if (shell == "windows" || OperatingSystem.IsLinux())
        {
            Assert.True(File.Exists(hung), "the hanging tool never started");
            Assert.True(Recorded.Gone(hung, TimeSpan.FromSeconds(60)), "the hanging tool survived its check's limit");
        }
    }

    /// <summary>known_hosts lookups use the HostKeyAlias (bare, as ssh does) and the global known_hosts files from ssh -G.</summary>
    [Fact]
    public async Task KnownHostsHonoursHostKeyAliasAndTheGlobalFiles()
    {
        const string Global = "/etc/ssh/ssh_known_hosts";
        var host = Host(
            exec: line => line switch
            {
                "ssh -G user@build-01.lan" => new SshResult(0,
                    "hostname 192.0.2.10\nport 2222\nhostkeyalias build01-key\nuserknownhostsfile ~/.ssh/known_hosts\nglobalknownhostsfile /etc/ssh/ssh_known_hosts /etc/ssh/ssh_known_hosts2\n"),
                "ssh-keygen -F build01-key -f " + Global => new SshResult(0, "# Host build01-key found: line 1\nbuild01-key ssh-ed25519 AAAA"),
                _ when line.StartsWith("ssh-keygen -F build01-key", StringComparison.Ordinal) || line.StartsWith("ssh-keygen -F [192.0.2.10]", StringComparison.Ordinal) => new SshResult(1, ""),
                _ => null,
            },
            exists: path => path is KnownHosts or Global or "/keys/build01");
        var (code, o) = await Doctor(host);

        Assert.Equal(0, code);
        Assert.Contains(Lines(o, "OK"), l => l.Contains("build-01", StringComparison.Ordinal) && l.Contains("build01-key", StringComparison.Ordinal));
        Assert.DoesNotContain(_ran, l => l.Contains("[192.0.2.10]:2222", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hostkeyalias none\n")]
    [InlineData("")]
    public async Task NoHostKeyAliasMeansTheHostNameAndPort(string alias)
    {
        var host = Host(exec: line => line == "ssh -G user@build-01.lan" ? new SshResult(0, "hostname 192.0.2.10\nport 2222\n" + alias) : null);
        await Doctor(host);
        Assert.Contains("ssh-keygen -F [192.0.2.10]:2222", _ran);
    }

    /// <summary>The exact text doctor printed, with the per-test state folder written as &lt;home&gt; and the line ends as \n.</summary>
    private string Golden(string output) =>
        output.Replace(_home, "<home>", StringComparison.Ordinal).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

    /// <summary>
    /// lanq doctor's text, pinned byte for byte (written on the 0.6.0 code before doctor --json): a healthy local check, a remote run with a
    /// HOLD machine (and OK, WARN, SKIP and FAIL remote lines), local FAIL lines, a config FAIL, and an unreachable machine. Only the state
    /// folder (a temporary path per test) is replaced, and the platform's line end is written as \n. The one change since: the logs line
    /// after the config line (0.7.0, job logs pruning, section L of the 0.7.0 design).
    /// </summary>
    [Fact]
    public async Task DoctorTextIsByteForByteUnchanged()
    {
        // 1. Healthy, local only.
        var (code, o) = await Doctor(Host());
        Assert.Equal(0, code);
        Assert.Equal(
            """
            OK    ssh: OpenSSH_9.6p1, OpenSSL 3.0.13
            OK    scp: found
            OK    git: git version 2.47.0
            OK    ssh-keygen: found
            OK    state folder <home> is writable
            OK    config: 2 machine(s)
            OK    logs: 0 files, 0.0 MB (kept 30 days)
            OK    build-01: known_hosts has build-01.lan
            OK    build-01: key file named by LANQ_KEY_BUILD01 found
            OK    build-01: work_root /srv/lanq (linux, transfer: git)
            OK    build-02: known_hosts has build-02.lan
            OK    build-02: work_root D:/lanq (windows, transfer: git)

            """.Replace("\r", "", StringComparison.Ordinal),
            Golden(o));

        // 2. --remote at noon: build-02 is outside its allowed hours (HOLD, only probed); build-01 is checked: OK, WARN, FAIL and SKIP lines.
        var config = T.Config();
        config.Machines[0].Capabilities = ["dotnet", "node", "gpu"];
        var noon = new FakeClock(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        var ssh = new FakeSsh
        {
            Handler = (_, cmd) => IsProbe(cmd)
                ? new SshResult(0, HealthyProbe + "local_time=2026-01-05T12:00:00\n")
                : new SshResult(0, "root=ok\ntool=ok\ngit_version=git version 2.25.1\ncap.dotnet=0|10.0.100\ncap.node=127|sh: 1: node: not found\n"),
        };
        (code, o) = await Doctor(Host(), ssh, () => config, remote: true, clock: noon);
        Assert.Equal(1, code);
        Assert.Equal(
            """
            OK    ssh: OpenSSH_9.6p1, OpenSSL 3.0.13
            OK    scp: found
            OK    git: git version 2.47.0
            OK    ssh-keygen: found
            OK    state folder <home> is writable
            OK    config: 2 machine(s)
            OK    logs: 0 files, 0.0 MB (kept 30 days)
            OK    build-01: known_hosts has build-01.lan
            OK    build-01: key file named by LANQ_KEY_BUILD01 found
            OK    build-01: work_root /srv/lanq (linux, transfer: git)
            OK    build-02: known_hosts has build-02.lan
            OK    build-02: work_root D:/lanq (windows, transfer: git)
            OK    build-01 (remote): work_root /srv/lanq is writable
            OK    build-01 (remote): git (transfer: git)
            WARN  build-01 (remote): git older than 2.32: global config not isolated (git 2.25 ignores GIT_CONFIG_GLOBAL, so a filter or attributes file in the ssh user's ~/.gitconfig can still run during a job's checkout; update git on that machine)
            OK    build-01 (remote): dotnet: 10.0.100
            FAIL  build-01 (remote): node: 'node --version' failed in the job environment (sh: 1: node: not found). If the tool is installed, a non-login ssh session does not see it: add its folder to path_prepend for this machine
            SKIP  build-01 (remote): gpu: not checked (no known tool command)
            HOLD  build-02 (remote): outside allowed hours; not checked. Add --force to check it anyway

            """.Replace("\r", "", StringComparison.Ordinal),
            Golden(o));

        // 3. Local FAIL lines: git missing, no known_hosts entry (non-standard port), key_env unset; and a key without a passphrase (WARN).
        (code, o) = await Doctor(Host(
            exec: line => line switch
            {
                _ when line.StartsWith("git ", StringComparison.Ordinal) => new SshResult(255, "Could not start git: not found"),
                "ssh -G user@build-02.lan" => new SshResult(0, "hostname 192.0.2.20\nport 2222\nuserknownhostsfile ~/.ssh/known_hosts\n"),
                _ when line.StartsWith("ssh-keygen -F [192.0.2.20]:2222", StringComparison.Ordinal) => new SshResult(1, ""),
                _ => null,
            },
            env: k => k == "HOME" ? "/home/me" : null));
        Assert.Equal(1, code);
        Assert.Equal(
            """
            OK    ssh: OpenSSH_9.6p1, OpenSSL 3.0.13
            OK    scp: found
            FAIL  git: not found on PATH
            OK    ssh-keygen: found
            OK    state folder <home> is writable
            OK    config: 2 machine(s)
            OK    logs: 0 files, 0.0 MB (kept 30 days)
            OK    build-01: known_hosts has build-01.lan
            FAIL  build-01: key_env LANQ_KEY_BUILD01 is not set
            OK    build-01: work_root /srv/lanq (linux, transfer: git)
            FAIL  build-02: no known_hosts entry for [192.0.2.20]:2222: connect once with ssh user@build-02.lan, check the fingerprint with the machine's owner, then accept it
            OK    build-02: work_root D:/lanq (windows, transfer: git)

            """.Replace("\r", "", StringComparison.Ordinal),
            Golden(o));

        // 4. A config that does not load: one FAIL line after the local tools, nothing per machine.
        (code, o) = await Doctor(Host(), config: () => throw new ConfigException("Machine 'x': ssh target must be user@host"));
        Assert.Equal(1, code);
        Assert.Equal(
            """
            OK    ssh: OpenSSH_9.6p1, OpenSSL 3.0.13
            OK    scp: found
            OK    git: git version 2.47.0
            OK    ssh-keygen: found
            OK    state folder <home> is writable
            FAIL  config: Machine 'x': ssh target must be user@host

            """.Replace("\r", "", StringComparison.Ordinal),
            Golden(o));

        // 5. --remote --force: build-01's work_root is missing and it has no tar; build-02 does not answer ssh.
        config = T.Config();
        config.Machines[0].Transfer = TransferMode.Archive;
        config.Machines[0].Capabilities = [];
        ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name == "build-01"
                ? new SshResult(0, "root=missing\ntool=missing\n")
                : new SshResult(255, "ssh: connect to host build-02.lan port 22: Connection timed out"),
        };
        (code, o) = await Doctor(Host(), ssh, () => config, remote: true, force: true);
        Assert.Equal(1, code);
        var remote = Golden(o).Split('\n').SkipWhile(l => !l.Contains("(remote)", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            [
                "WARN  build-01 (remote): work_root /srv/lanq does not exist yet (the first job creates it)",
                "FAIL  build-01 (remote): no usable tar for transfer: archive (see docs/WORKERS.md)",
                "FAIL  build-02 (remote): unreachable: ssh: connect to host build-02.lan port 22: Connection timed out",
                "",
            ],
            remote);
        Assert.Contains("OK    build-01: work_root /srv/lanq (linux, transfer: archive)\n", Golden(o), StringComparison.Ordinal);
    }

    private static string[] Lines(string output, string level) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith(level, StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task AHealthySetupIsAllOkAndExitsZeroWithoutAnySsh()
    {
        var ssh = new FakeSsh();
        var (code, o) = await Doctor(Host(), ssh);

        Assert.Equal(0, code);
        Assert.Empty(Lines(o, "FAIL"));
        Assert.Empty(Lines(o, "WARN"));
        Assert.Contains("OpenSSH_9.6p1", o, StringComparison.Ordinal);
        Assert.Contains("git version 2.47.0", o, StringComparison.Ordinal);
        Assert.Contains(Lines(o, "OK"), l => l.Contains("build-01", StringComparison.Ordinal) && l.Contains("known_hosts", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "OK"), l => l.Contains("state folder", StringComparison.Ordinal));
        Assert.Empty(ssh.Calls); // local only by default: no network
        Assert.Contains("ssh-keygen -F build-01.lan", _ran);
    }

    [Fact]
    public async Task AMissingKnownHostsEntryFailsAndANonStandardPortIsLookedUpBracketed()
    {
        var host = Host(exec: line => line switch
        {
            "ssh -G user@build-01.lan" => new SshResult(0, "hostname 192.0.2.10\nport 2222\nuserknownhostsfile ~/.ssh/known_hosts\n"),
            _ when line.StartsWith("ssh-keygen -F [192.0.2.10]:2222", StringComparison.Ordinal) => new SshResult(1, ""),
            _ => null,
        });
        var (code, o) = await Doctor(host);

        Assert.Equal(1, code);
        Assert.Contains("ssh-keygen -F [192.0.2.10]:2222 -f " + KnownHosts, _ran);
        var fail = Assert.Single(Lines(o, "FAIL"));
        Assert.Contains("build-01", fail, StringComparison.Ordinal);
        Assert.Contains("[192.0.2.10]:2222", fail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLocalToolsFail()
    {
        var host = Host(exec: line => line.StartsWith("git ", StringComparison.Ordinal) ? new SshResult(255, "Could not start git: not found") : null);
        var (code, o) = await Doctor(host);
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConfigErrorIsReportedWithTheLoadersMessageAndMachinesAreSkipped()
    {
        var (code, o) = await Doctor(Host(), config: () => throw new ConfigException("Machine 'x': ssh target must be user@host"));
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("ssh target must be user@host", StringComparison.Ordinal));
        Assert.DoesNotContain("build-01", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyEnvProblemsAreReported()
    {
        var (code, o) = await Doctor(Host(env: k => k == "HOME" ? "/home/me" : null));
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("LANQ_KEY_BUILD01", StringComparison.Ordinal));

        (code, o) = await Doctor(Host(exists: p => p == KnownHosts));
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("LANQ_KEY_BUILD01", StringComparison.Ordinal) && l.Contains("not found", StringComparison.Ordinal));

        Directory.CreateDirectory(_home);
        var key = Path.Combine(_home, "id_test");
        File.WriteAllText(key, "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");
        (code, o) = await Doctor(Host(
            env: k => k switch { "HOME" => "/home/me", "LANQ_KEY_BUILD01" => key, _ => null },
            exists: p => p == KnownHosts || p == key,
            unencrypted: true));
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "WARN"), l => l.Contains("passphrase", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnwritableStateFolderFails()
    {
        Directory.CreateDirectory(_home);
        var blocker = Path.Combine(_home, "file");
        File.WriteAllText(blocker, "x"); // a file where the state folder's parent should be
        var (code, o) = await Doctor(Host(home: Path.Combine(blocker, "lanq")));
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("state folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteChecksEachMachineOnceAndExplainsAToolMissingFromTheNonLoginShell()
    {
        var config = T.Config();
        config.Machines[0].Capabilities = ["dotnet", "npm", "gpu"];
        config.Machines[0].PathPrepend = ["/opt/node/bin"];
        var ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name == "build-01"
                ? new SshResult(0, "root=ok\ntool=ok\ncap.dotnet=0|10.0.100\ncap.npm=127|sh: 1: npm: not found\n")
                : new SshResult(255, "ssh: connect to host build-02.lan port 22: Connection timed out"),
        };
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);

        Assert.Equal(1, code);
        Assert.Equal(2, ssh.Calls.Count(c => !IsProbe(c.Command))); // one doctor call per machine
        var linux = ssh.Calls.Single(c => c.Machine == "build-01" && !IsProbe(c.Command)).Command;
        Assert.Contains("npm --version", linux, StringComparison.Ordinal);
        Assert.Contains("PATH=/opt/node/bin", linux, StringComparison.Ordinal); // run in the job environment
        Assert.DoesNotContain("gpu", linux, StringComparison.Ordinal);

        Assert.Contains(Lines(o, "OK"), l => l.Contains("dotnet", StringComparison.Ordinal) && l.Contains("10.0.100", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("npm", StringComparison.Ordinal) && l.Contains("path_prepend", StringComparison.Ordinal));
        Assert.Contains(o.Split('\n'), l => l.Contains("gpu", StringComparison.Ordinal) && l.Contains("not checked", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("build-02", StringComparison.Ordinal) && l.Contains("Connection timed out", StringComparison.Ordinal));
    }

    /// <summary>GIT_CONFIG_GLOBAL needs git 2.32: an older worker git still reads the user's global config, and doctor says so.</summary>
    [Theory]
    [InlineData("git version 2.25.1", true)]
    [InlineData("git version 2.31.9.windows.1", true)]
    [InlineData("git version 2.32.0", false)]
    [InlineData("git version 2.48.1.windows.1", false)]
    [InlineData("git version 3.0.0", false)]
    public async Task RemoteWarnsAboutAGitTooOldToIgnoreTheGlobalConfig(string version, bool warns)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, "root=ok\ntool=ok\ngit_version=" + version + "\ncap.dotnet=0|10.0.100\ncap.node=0|v22\n") };
        var config = T.Config();
        config.Machines.RemoveAt(1);
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);
        Assert.Equal(warns ? 1 : 0, code);
        Assert.Equal(warns, Lines(o, "WARN").Any(l => l.Contains("git older than 2.32: global config not isolated", StringComparison.Ordinal)));
        Assert.Contains("git_version=", ssh.Calls.Single(c => c.Command.Contains("cap.", StringComparison.Ordinal)).Command, StringComparison.Ordinal);
    }

    /// <summary>A machine whose doctor script is over the Windows command-line cap gets one FAIL line that says why; doctor never throws.</summary>
    [Fact]
    public async Task RemoteReportsADoctorScriptTooLongForTheMachineAsAFailLine()
    {
        // Today's doctor script is always shorter than the job script for the same env:, so a too-long one is stood in for: the job
        // script of this machine fits (the config loads), only the doctor script is over the cap.
        var config = T.Config();
        static string Oversized(Machine m) => m.Os == MachineOs.Windows ? RemoteScripts.Doctor(m) + "\n#" + new string('x', 3500) : RemoteScripts.Doctor(m);
        var ssh = new FakeSsh { Handler = (_, cmd) => IsProbe(cmd) ? new SshResult(0, HealthyProbe + "local_time=2026-01-05T23:00:00\n") : new SshResult(0, "root=ok\ntool=ok\n") };
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true, force: true, doctorScript: Oversized);

        Assert.Equal(1, code);
        var fail = Assert.Single(Lines(o, "FAIL"), l => l.Contains("build-02 (remote)", StringComparison.Ordinal));
        Assert.Contains("env:", fail, StringComparison.Ordinal);
        Assert.Contains("path_prepend:", fail, StringComparison.Ordinal);
        Assert.Matches("[0-9]+ characters once encoded, [0-9]+ over the 8000", fail);
        Assert.DoesNotContain(ssh.Calls, c => c.Machine == "build-02" && !IsProbe(c.Command));
    }

    [Fact]
    public async Task RemoteReportsAReadOnlyWorkRootAndAMissingTransferTool()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, "root=readonly\ntool=missing\n") };
        var config = T.Config();
        config.Machines.RemoveAt(1);
        var (code, o) = await Doctor(Host(), ssh, () => config, remote: true);
        Assert.Equal(1, code);
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("work_root", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("git", StringComparison.Ordinal));
    }

    [ShFact]
    public void TheRealShScriptRunsToolsWithPathPrependLikeAJob() => TheRealScriptRunsToolsWithPathPrependLikeAJob("linux");

    [WindowsFact] // the Windows script's PATH handling and .cmd shims are Windows behaviour, not something pwsh on Linux has
    public void TheRealPowerShellScriptRunsToolsWithPathPrependLikeAJob() => TheRealScriptRunsToolsWithPathPrependLikeAJob("windows");

    private static void TheRealScriptRunsToolsWithPathPrependLikeAJob(string shell)
    {
        using var worker = new LocalWorker();
        var bin = Path.Combine(worker.Root, "fake-bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "npm.cmd"), "@echo 9.9.9-fake" + (char)13 + (char)10);
        var npm = Path.Combine(bin, "npm");
        File.WriteAllText(npm, "#!/bin/sh" + (char)10 + "echo 9.9.9-fake" + (char)10);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(npm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var dir = bin.Replace('\\', '/');
        if (shell == "linux" && OperatingSystem.IsWindows())
        {
            dir = "/" + char.ToLowerInvariant(dir[0]) + dir[2..]; // Git for Windows sh: a PATH entry cannot contain "C:"
        }

        var m = worker.Machine;
        m.Os = shell == "windows" ? MachineOs.Windows : MachineOs.Linux;
        m.Capabilities = ["npm", "rust"];
        m.PathPrepend = [dir];
        m.Env = new Dictionary<string, string> { ["CARGO_HOME"] = Path.Combine(worker.Root, "no-cargo") };
        m.Transfer = TransferMode.Archive;
        var output = StaleSlotTests.RunScript(shell, RemoteScripts.Doctor(m));
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.Contains("root=ok", lines);
        Assert.Contains(lines, l => l.StartsWith("tool=", StringComparison.Ordinal));
        Assert.Contains("cap.npm=0|9.9.9-fake", lines);
        Assert.Contains(lines, l => l.StartsWith("cap.rust=", StringComparison.Ordinal) && !l.StartsWith("cap.rust=0|", StringComparison.Ordinal) || l.StartsWith("cap.rust=0|cargo", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith(".lanq-doctor", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(worker.Root)); // the probe file was removed again
    }
}
