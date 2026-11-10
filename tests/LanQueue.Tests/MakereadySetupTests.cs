using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq makeready --ssh user@host --os windows|linux --setup: the worker's one-time setup, printed; lanq connects to nothing.</summary>
public sealed class MakereadySetupTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "lanq-setup-" + Guid.NewGuid().ToString("N"));

    public MakereadySetupTests() => Directory.CreateDirectory(Path.Combine(_home, ".ssh"));

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>
    /// An obviously fake public key made for the test: a well-formed ssh-ed25519 blob around 32 random bytes (nobody holds its private half).
    /// </summary>
    internal static string FakeKey(string comment = "test-only-key", string type = "ssh-ed25519")
    {
        var name = Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + 4 + 32];
        BinaryPrimitives.WriteInt32BigEndian(blob, name.Length);
        name.CopyTo(blob, 4);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + name.Length), 32);
        RandomNumberGenerator.Fill(blob.AsSpan(8 + name.Length));
        return $"{type} {Convert.ToBase64String(blob)}" + (comment.Length > 0 ? " " + comment : "");
    }

    /// <summary>What a private key file looks like (fake: random text between the markers), to prove it is never read or printed.</summary>
    private static readonly string FakePrivateKey =
        "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(96)) + "\n-----END OPENSSH PRIVATE KEY-----\n";

    private MakereadyHost Host(string? agent = null, Dictionary<string, string>? env = null, Action? onAgent = null) => new(
        name => name == "HOME" ? _home : env?.GetValueOrDefault(name),
        Windows: false,
        () =>
        {
            onAgent?.Invoke();
            return agent;
        },
        Confirm: null);

    private static async Task<(int Code, string Out, string Err, FakeSsh Ssh)> Setup(MakereadyHost host, params string[] args)
    {
        var ssh = new FakeSsh();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Makeready(
            ParsedArgs.Parse(["makeready", .. args, "--setup"]), () => throw new InvalidOperationException("--setup must not load the config"), ssh,
            new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None, host);
        return (code, o.ToString(), e.ToString(), ssh);
    }

    [Fact]
    public async Task SetupMakesNoConnection()
    {
        var key = FakeKey();
        var asked = 0;
        var (code, o, e, ssh) = await Setup(Host(key + "\n", onAgent: () => asked++), "--ssh", "build@10.20.30.21", "--os", "windows");
        Assert.Equal(0, code);
        Assert.Empty(e);
        Assert.Empty(ssh.Calls);
        Assert.Empty(ssh.InputRuns);
        Assert.Empty(ssh.Uploads);
        Assert.Equal(1, asked); // ssh-add -L once, a local agent question: nothing goes to the worker
        Assert.Contains("lanq connected to nothing", o, StringComparison.Ordinal);
        Assert.Contains(key, o, StringComparison.Ordinal);
        Assert.Contains("lanq makeready --ssh build@10.20.30.21 --os windows --add --name NAME", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupIsTextOnlyAndRefusesJsonAndOtherModes()
    {
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", "build@10.20.30.21", "--os", "linux", "--setup", "--json"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--ssh", "build@10.20.30.21", "--os", "linux", "--setup", "--add", "--name", "b"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["makeready", "--all", "--setup"]));

        // Through the whole CLI, --json still gets its one object on stdout.
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["makeready", "--ssh", "build@10.20.30.21", "--os", "linux", "--setup", "--json"], o, new StringWriter(), CancellationToken.None));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());

        // No --ssh or --os: usage, exit 2.
        var (code, _, e, _) = await Setup(Host(FakeKey()), "--os", "linux");
        Assert.Equal(2, code);
        Assert.Contains("Usage", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupPrintsOnlyThePublicKey()
    {
        // ~/.ssh/lanq_ed25519 (fake private key) beside its .pub; no agent.
        var pub = FakeKey("lanq");
        await File.WriteAllTextAsync(Path.Combine(_home, ".ssh", "lanq_ed25519"), FakePrivateKey);
        await File.WriteAllTextAsync(Path.Combine(_home, ".ssh", "lanq_ed25519.pub"), pub + "\n");
        var (code, o, _, _) = await Setup(Host(), "--ssh", "build@10.20.30.31", "--os", "linux");
        Assert.Equal(0, code);
        Assert.Contains(pub, o, StringComparison.Ordinal);
        Assert.Contains("from ~/.ssh/lanq_ed25519.pub", o, StringComparison.Ordinal);
        Assert.Contains("ssh-add $HOME/.ssh/lanq_ed25519", o, StringComparison.Ordinal); // not in the agent: say how to load it
        Assert.Contains("-i $HOME/.ssh/lanq_ed25519 build@10.20.30.31 exit", o, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", o, StringComparison.Ordinal);

        // --key-env names the private key file: only "<that path>.pub" is read.
        var keyFile = Path.Combine(_home, "keys", "worker_key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        var other = FakeKey("other");
        await File.WriteAllTextAsync(keyFile, FakePrivateKey);
        await File.WriteAllTextAsync(keyFile + ".pub", other + "\n");
        var agentKey = FakeKey("agent");
        (code, o, _, _) = await Setup(Host(agentKey, new() { ["LANQ_KEY"] = keyFile }), "--ssh", "build@10.20.30.31", "--os", "windows", "--key-env", "LANQ_KEY");
        Assert.Equal(0, code);
        Assert.Contains(other, o, StringComparison.Ordinal); // the --key-env key, not the agent's: lanq connects with that key alone
        Assert.DoesNotContain(agentKey, o, StringComparison.Ordinal);
        Assert.Contains("-i \"$env:LANQ_KEY\" build@10.20.30.31 exit", o, StringComparison.Ordinal);
        Assert.Contains("-i \"$LANQ_KEY\" build@10.20.30.31 exit", o, StringComparison.Ordinal);
        Assert.Contains("--add --name NAME --key-env LANQ_KEY", o, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", o, StringComparison.Ordinal);

        // A key file without a .pub: a placeholder, never the private key.
        File.Delete(keyFile + ".pub");
        (code, o, _, _) = await Setup(Host(FakeKey("agent"), new() { ["LANQ_KEY"] = keyFile }), "--ssh", "build@10.20.30.31", "--os", "linux", "--key-env", "LANQ_KEY");
        Assert.Equal(0, code);
        Assert.Contains("<your public key", o, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", o, StringComparison.Ordinal);
        Assert.DoesNotContain("ssh-ed25519 AAAA", o, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyPubFilesAreEverOpened()
    {
        // A .pub that holds a private key is still not printed (it does not validate), and a path without .pub is never opened.
        var both = Path.Combine(_home, ".ssh", "lanq_ed25519.pub");
        File.WriteAllText(both, FakePrivateKey);
        var key = Cli.FindSetupKey(null, Host());
        Assert.Null(key.Line);
        Assert.Equal(Cli.KeySource.None, key.Source);

        File.WriteAllText(Path.Combine(_home, ".ssh", "id_ed25519.pub"), FakeKey("id") + "\n");
        key = Cli.FindSetupKey(null, Host());
        Assert.Equal(Cli.KeySource.IdFile, key.Source);
        Assert.EndsWith(" id", key.Line, StringComparison.Ordinal);

        // An agent key comes first.
        var agent = FakeKey("agent");
        Assert.Equal(agent, Cli.FindSetupKey(null, Host("The agent has no identities.\n" + agent + "\n")).Line);
    }

    [Theory]
    [InlineData("x'; rm -rf ~ #")]
    [InlineData("$(reboot)")]
    [InlineData("a\"b")]
    [InlineData("two words")]
    [InlineData("`whoami`")]
    public async Task SetupDropsAnUnsafeKeyComment(string comment)
    {
        var key = FakeKey(comment);
        var bare = string.Join(' ', key.Split(' ').Take(2));
        var (code, o, _, _) = await Setup(Host(key + "\n"), "--ssh", "build@10.20.30.31", "--os", "linux");
        Assert.Equal(0, code);
        Assert.Contains($"echo '{bare}' | sudo tee /home/build/.ssh/authorized_keys", o, StringComparison.Ordinal);
        Assert.DoesNotContain(comment, o, StringComparison.Ordinal);

        // A plain comment is kept.
        var plain = FakeKey("dev@build-pc");
        Assert.Equal(plain, Cli.PublicKeyLine(plain));
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 !!!notbase64!!!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP1/U4EddRIpUt9KnC7s5Of2EbdSPO9EAMMeP4C2USZpRV1AIlH7WT2NWPq/xfW6MPbLm1Vs14E7")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public async Task SetupFallsBackToAPlaceholderForAMalformedPub(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_home, ".ssh", "lanq_ed25519.pub"), content + "\n");
        var (code, o, _, _) = await Setup(Host(), "--ssh", "build@10.20.30.31", "--os", "windows");
        Assert.Equal(0, code);
        Assert.Contains("No public key found", o, StringComparison.Ordinal);
        Assert.Contains("ssh-keygen -t ed25519 -f $HOME/.ssh/lanq_ed25519 -C lanq", o, StringComparison.Ordinal);
        Assert.Contains("-Value '<your public key", o, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", o, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyWhoseBlobIsAnotherTypeIsRefused()
    {
        var ed = FakeKey("x");
        Assert.NotNull(Cli.PublicKeyLine(ed));
        Assert.Null(Cli.PublicKeyLine("ssh-rsa " + ed.Split(' ')[1])); // the blob says ssh-ed25519
        Assert.Null(Cli.PublicKeyLine("ssh-ed25519-cert-v01@openssh.com " + ed.Split(' ')[1])); // certificates are not offered
    }

    [Fact]
    public async Task SetupWindowsCommandsUseSids()
    {
        var key = FakeKey();
        var (code, o, _, _) = await Setup(Host(key + "\n"), "--ssh", "build@10.20.30.21", "--os", "windows");
        Assert.Equal(0, code);
        Assert.Contains("Add-LocalGroupMember -SID S-1-5-32-545 -Member build", o, StringComparison.Ordinal);
        Assert.Contains("$sid = (Get-LocalUser build).SID.Value", o, StringComparison.Ordinal);
        Assert.Contains($"Set-Content -Path \"$p\\.ssh\\authorized_keys\" -Value '{key}' -Encoding ascii", o, StringComparison.Ordinal);

        // Every icacls grant is by SID (works on a Windows in any language), and inheritance is cut on the key and the work folder.
        var icacls = o.Split('\n').Where(l => l.StartsWith("icacls ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, icacls.Count);
        Assert.All(icacls, l => Assert.Contains("/inheritance:r", l, StringComparison.Ordinal));
        foreach (var grant in icacls.SelectMany(l => l.Split("/grant ").Skip(1)))
        {
            Assert.Matches(@"^['""]\*(S-1-5-18|S-1-5-32-544|\$\{sid\}):", grant);
        }

        Assert.Contains("-Profile Private -RemoteAddress LocalSubnet", o, StringComparison.Ordinal);
        Assert.Contains("@('PasswordAuthentication no')", o, StringComparison.Ordinal);
        Assert.Contains("administrators_authorized_keys", o, StringComparison.Ordinal); // why the build user is not an administrator
        Assert.Contains("ssh-keygen -lf C:\\ProgramData\\ssh\\ssh_host_ed25519_key.pub", o, StringComparison.Ordinal);
        Assert.Contains("compare the fingerprint before answering yes", o, StringComparison.Ordinal);
        Assert.Contains("ssh -o PasswordAuthentication=no build@10.20.30.21 exit", o, StringComparison.Ordinal);
        Assert.Contains("New-Item -ItemType Directory -Force C:\\lq", o, StringComparison.Ordinal); // the work_root lanq makeready suggests
        Assert.DoesNotContain("sudo", o, StringComparison.Ordinal);

        // Passwords go off only after the key login worked: that step comes after the first connection.
        Assert.True(o.IndexOf("PasswordAuthentication no'", StringComparison.Ordinal) > o.IndexOf("ssh -o PasswordAuthentication=no", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetupUsesTheUserFromTheTarget()
    {
        var key = FakeKey();
        var (code, o, _, _) = await Setup(Host(key + "\n"), "--ssh", "me@10.20.30.31", "--os", "linux");
        Assert.Equal(0, code);
        Assert.Contains("sudo useradd -m -s /bin/bash me", o, StringComparison.Ordinal);
        Assert.Contains("sudo install -d -m 700 -o me -g me /home/me/.ssh", o, StringComparison.Ordinal);
        Assert.Contains($"echo '{key}' | sudo tee /home/me/.ssh/authorized_keys >/dev/null", o, StringComparison.Ordinal);
        Assert.Contains("sudo chmod 600 /home/me/.ssh/authorized_keys", o, StringComparison.Ordinal);
        Assert.Contains("sudo install -d -o me -g me /srv/lq", o, StringComparison.Ordinal);
        Assert.Contains("sudo ufw allow from 10.20.30.0/24 to any port 22 proto tcp", o, StringComparison.Ordinal);
        Assert.Contains("PasswordAuthentication no", o, StringComparison.Ordinal);
        Assert.Contains("ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub", o, StringComparison.Ordinal);
        Assert.Contains("compare the fingerprint before answering yes", o, StringComparison.Ordinal);
        Assert.Contains("ssh -o PasswordAuthentication=no me@10.20.30.31 exit", o, StringComparison.Ordinal); // an agent key: no -i
        Assert.DoesNotContain("lanqbuild", o, StringComparison.Ordinal);

        // A host name: the firewall range is a placeholder to fill in.
        (code, o, _, _) = await Setup(Host(key + "\n"), "--ssh", "me@build-01.lan", "--os", "linux");
        Assert.Equal(0, code);
        Assert.Contains("sudo ufw allow from <your-LAN> to any port 22", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.20.30.31")] // no user to create
    [InlineData("root@10.20.30.31")]
    [InlineData("Administrator@10.20.30.31")]
    [InlineData("a@b@10.20.30.31")] // the user part holds an @
    [InlineData("averyveryverylongusername@10.20.30.31")]
    public async Task SetupNeedsANewBuildUser(string target)
    {
        var (code, o, e, _) = await Setup(Host(FakeKey()), "--ssh", target, "--os", "linux");
        Assert.Equal(2, code);
        Assert.Empty(o);
        Assert.NotEmpty(e);
    }

    /// <summary>The deferred phase-D row: when the worker refused the key or its host key is unknown, the next steps add --setup (for the developer).</summary>
    [Theory]
    [InlineData("me@10.20.30.31: Permission denied (publickey).", true)]
    [InlineData("Host key verification failed.", true)]
    [InlineData("No ED25519 host key is known for 10.20.30.31 and you have requested strict checking.", true)]
    [InlineData("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\nHost key verification failed.", false)]
    [InlineData("ssh: connect to host 10.20.30.31 port 22: Connection refused", false)]
    public async Task AKeyOrHostKeyFailureSuggestsTheSetup(string sshError, bool suggested)
    {
        Assert.Equal(suggested, SshErrors.SetupWouldHelp(sshError));
        foreach (var json in new[] { false, true })
        {
            var ssh = new FakeSsh { InputHandler = (_, _, _) => new SshResult(255, "") { ErrorOutput = sshError } };
            var o = new StringWriter();
            var e = new StringWriter();
            string[] args = ["makeready", "--ssh", "me@10.20.30.31", "--os", "linux", "--key-env", "LANQ_KEY", .. json ? new[] { "--json" } : []];
            var code = await Cli.Makeready(ParsedArgs.Parse(args), T.Config, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None, Host());
            Assert.Equal(70, code);
            var line = "Next: (for the developer) if the PC is not set up for your key yet, print its one-time setup";
            Assert.Equal(suggested, e.ToString().Contains(line, StringComparison.Ordinal));
            if (suggested)
            {
                Assert.Contains(": lanq makeready --ssh me@10.20.30.31 --os linux --setup --key-env LANQ_KEY", e.ToString(), StringComparison.Ordinal);
            }

            if (json)
            {
                using var doc = JsonDocument.Parse(o.ToString());
                var steps = doc.RootElement.GetProperty("nextSteps").EnumerateArray().ToList();
                var setup = steps.Where(s => s.TryGetProperty("argv", out var a) && a.EnumerateArray().Any(t => t.GetString() == "--setup")).ToList();
                Assert.Equal(suggested ? 1 : 0, setup.Count);
                Assert.All(setup, s => Assert.False(s.GetProperty("safeToRunUnattended").GetBoolean()));
            }
        }

        // A configured machine never gets it (--setup takes --ssh user@host).
        var configured = new FakeSsh { Handler = (_, _) => new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n"), InputHandler = (_, _, _) => new SshResult(255, "") { ErrorOutput = sshError } };
        var err = new StringWriter();
        Assert.Equal(70, await Cli.Makeready(ParsedArgs.Parse(["makeready", "build-01"]), T.Config, configured, new HealthProbe(configured, TimeProvider.System), new StringWriter(), err, CancellationToken.None, Host()));
        Assert.DoesNotContain("--setup", err.ToString(), StringComparison.Ordinal);
    }
}
