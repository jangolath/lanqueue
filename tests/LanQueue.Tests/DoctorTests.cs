using LanQueue.Config;
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

    private static async Task<(int Code, string Out)> Doctor(DoctorHost host, ISshClient? ssh = null, Func<FleetConfig>? config = null, bool remote = false)
    {
        var o = new StringWriter();
        var p = ParsedArgs.Parse(remote ? ["doctor", "--remote"] : ["doctor"]);
        var code = await Cli.Doctor(p, config ?? T.Config, host, ssh ?? new FakeSsh(), o, CancellationToken.None);
        return (code, o.ToString());
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
        Assert.Equal(2, ssh.Calls.Count); // one call per machine
        var linux = ssh.Calls.Single(c => c.Machine == "build-01").Command;
        Assert.Contains("npm --version", linux, StringComparison.Ordinal);
        Assert.Contains("PATH=/opt/node/bin", linux, StringComparison.Ordinal); // run in the job environment
        Assert.DoesNotContain("gpu", linux, StringComparison.Ordinal);

        Assert.Contains(Lines(o, "OK"), l => l.Contains("dotnet", StringComparison.Ordinal) && l.Contains("10.0.100", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("npm", StringComparison.Ordinal) && l.Contains("path_prepend", StringComparison.Ordinal));
        Assert.Contains(o.Split('\n'), l => l.Contains("gpu", StringComparison.Ordinal) && l.Contains("not checked", StringComparison.Ordinal));
        Assert.Contains(Lines(o, "FAIL"), l => l.Contains("build-02", StringComparison.Ordinal) && l.Contains("Connection timed out", StringComparison.Ordinal));
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
