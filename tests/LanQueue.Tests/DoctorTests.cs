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
        var o = new StringWriter();
        var p = ParsedArgs.Parse([.. (string[])["doctor"], .. remote ? ["--remote"] : (string[])[], .. force ? ["--force"] : (string[])[]]);
        ssh ??= new FakeSsh();
        var code = await Cli.Doctor(p, config ?? T.Config, host, ssh, new HealthProbe(ssh, clock ?? TimeProvider.System), o, CancellationToken.None, doctorScript);
        return (code, o.ToString());
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
        Assert.Equal(2, ssh.Calls.Count);
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
        Assert.Contains("if timeout -k 5 1 true >/dev/null 2>&1; then T=\"timeout -k 5 7\"; fi", sh, StringComparison.Ordinal);
        Assert.Contains("$T /bin/sh -c 'dotnet --version'", sh, StringComparison.Ordinal);
        Assert.Contains("=timeout|", sh, StringComparison.Ordinal);

        var windows = T.Windows();
        windows.Capabilities = ["dotnet", "rust"];
        var ps = RemoteScripts.Doctor(windows, checkSeconds: 7);
        Assert.Contains("WaitForExit(7000)", ps, StringComparison.Ordinal);
        Assert.Contains("taskkill /PID $p.Id /T /F", ps, StringComparison.Ordinal);
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

    [WindowsFact]
    public void TheRealPowerShellScriptStopsAHangingToolCheck() => TheRealScriptStopsAHangingToolCheck("windows");

    private static void TheRealScriptStopsAHangingToolCheck(string shell)
    {
        using var worker = new LocalWorker();
        var bin = Path.Combine(worker.Root, "fake-bin");
        Directory.CreateDirectory(bin);
        // The hang lasts ten minutes and each check may take 20 s. Under load, starting a check (a fresh PowerShell) can itself take seconds,
        // so the old 3 s limit made the quick npm check time out too. Finishing well under ten minutes still proves the hang was stopped.
        MakeTool(bin, "dotnet", "sleep 600", "@ping -n 600 127.0.0.1 >nul");
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
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lines = StaleSlotTests.RunScript(shell, RemoteScripts.Doctor(m, checkSeconds: 20)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5), "took " + watch.Elapsed);
        Assert.Contains("cap.dotnet=timeout|", lines);
        Assert.Contains("cap.npm=0|9.9.9-fake", lines);
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
