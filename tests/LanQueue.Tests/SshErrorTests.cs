using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>The first-run failures say what to do next, and still quote ssh's own line.</summary>
public class SshErrorTests
{
    private const string Target = "build@10.20.30.11";

    [Fact]
    public void AnUnknownHostKeySaysToConnectOnceWithSsh()
    {
        var text = SshErrors.Explain(
            "No ED25519 host key is known for 10.20.30.11 and you have requested strict checking.\nHost key verification failed.", Target);
        Assert.Contains("known_hosts", text, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=yes", text, StringComparison.Ordinal);
        Assert.Contains("ssh build@10.20.30.11", text, StringComparison.Ordinal);
        Assert.Contains("fingerprint", text, StringComparison.Ordinal);
        Assert.Contains("Host key verification failed.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedHostKeyIsNotMistakenForAnUnknownOne()
    {
        var text = SshErrors.Explain(
            "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\n"
            + "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\nHost key verification failed.", Target);
        Assert.Contains("CHANGED", text, StringComparison.Ordinal);
        Assert.Contains("ssh-keygen -R 10.20.30.11", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@@@", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build@10.20.30.11: Permission denied (publickey).")]
    [InlineData("build@10.20.30.11: Permission denied (publickey,keyboard-interactive).")]
    public void PermissionDeniedPointsAtTheAgentAndAuthorizedKeys(string line)
    {
        var text = SshErrors.Explain(line, Target);
        Assert.Contains("ssh-add", text, StringComparison.Ordinal);
        Assert.Contains("authorized_keys", text, StringComparison.Ordinal);
        Assert.Contains("administrators_authorized_keys", text, StringComparison.Ordinal);
        Assert.Contains("ssh -o BatchMode=yes build@10.20.30.11 true", text, StringComparison.Ordinal);
        Assert.Contains(line, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnprotectedKeyFileSaysToFixItsPermissions()
    {
        var text = SshErrors.Explain(
            "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@         WARNING: UNPROTECTED PRIVATE KEY FILE!          @\n"
            + "Permissions for '/home/me/.ssh/id_ed25519' are too open.\nbuild@10.20.30.11: Permission denied (publickey).", Target);
        Assert.Contains("chmod 600", text, StringComparison.Ordinal);
        Assert.Contains("are too open", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinuxUnprotectedKeyLineWithItsModeAlsoSaysToFixItsPermissions()
    {
        // Upstream OpenSSH (Linux, macOS) prints the octal mode; Win32-OpenSSH does not.
        var text = SshErrors.Explain(
            "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@         WARNING: UNPROTECTED PRIVATE KEY FILE!          @\n"
            + "Permissions 0644 for '/home/me/.ssh/id_ed25519' are too open.\nbuild@10.20.30.11: Permission denied (publickey).", Target);
        Assert.Contains("chmod 600", text, StringComparison.Ordinal);
        Assert.Contains("Permissions 0644 for", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownHostNameAndARefusedConnectionAreExplained()
    {
        var name = SshErrors.Explain("ssh: Could not resolve hostname old-pc.lan: Name or service not known", "build@old-pc.lan");
        Assert.Contains("old-pc.lan", name, StringComparison.Ordinal);
        Assert.Contains("IP address", name, StringComparison.Ordinal);

        var refused = SshErrors.Explain("ssh: connect to host 10.20.30.11 port 22: Connection refused", Target);
        Assert.Contains("sshd", refused, StringComparison.Ordinal);
        Assert.Contains("Connection refused", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSshClientIsExplained() =>
        Assert.Contains("OpenSSH client", SshErrors.Explain("Could not start ssh: No such file or directory", Target), StringComparison.Ordinal);

    [Fact]
    public void AnythingElseIsSsh_sOwnFirstLine() =>
        Assert.Equal(
            "ssh: connect to host 10.20.30.11 port 22: No route to host",
            SshErrors.Explain("\nssh: connect to host 10.20.30.11 port 22: No route to host\nmore", Target));

    /// <summary>Only ssh's own lines count: text a remote command printed elsewhere on a line, or with another exit code, explains nothing.</summary>
    [Theory]
    [InlineData("echo: Host key verification failed.")]
    [InlineData("job says: build@10.20.30.11: Permission denied (publickey).")]
    [InlineData("log: ssh: connect to host 10.20.30.11 port 22: Connection refused")]
    [InlineData("x Could not resolve hostname old-pc.lan")]
    [InlineData("the file's Permissions for 'x' are too open.")]
    public void TextThatIsNotSsh_sOwnLineIsNotExplained(string line) =>
        Assert.Equal(line, SshErrors.Explain(line, Target));

    [Fact]
    public async Task HealthExplainsOnlyAnSshFailure()
    {
        // Exit 1 is the remote script's own failure, never ssh's: its output is shown as it is.
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(1, "Host key verification failed.") };
        var h = await new HealthProbe(ssh, TimeProvider.System).ProbeAsync(T.Windows(), T.Config().Policy, CancellationToken.None);
        Assert.False(h.Reachable);
        Assert.Equal("Host key verification failed.", h.Error);
    }

    [Fact]
    public async Task DoctorExplainsOnlyAnSshFailure()
    {
        var host = new DoctorHost((_, _) => Task.FromResult(new SshResult(0, "")), _ => null, _ => true, _ => Task.FromResult(false), Path.GetTempPath());
        var config = T.Config();
        config.Machines.RemoveAt(0);
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(124, "Host key verification failed.", TimedOut: true) };
        var o = new StringWriter();
        await Cli.Doctor(ParsedArgs.Parse(["doctor", "--remote", "--force"]), () => config, host, ssh, new HealthProbe(ssh, TimeProvider.System), o, CancellationToken.None);
        Assert.Contains("unreachable: Host key verification failed.", o.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("StrictHostKeyChecking", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthShowsTheExplanationForAMachineThatRefusesTheKey()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(255, "user@build-02.lan: Permission denied (publickey).") };
        var h = await new HealthProbe(ssh, TimeProvider.System).ProbeAsync(T.Windows(), T.Config().Policy, CancellationToken.None);
        Assert.False(h.Reachable);
        Assert.Contains("authorized_keys", h.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakereadyShowsTheExplanationForAnUnknownHostKey()
    {
        var ssh = new FakeSsh { InputHandler = (_, _, _) => new SshResult(255, "") { ErrorOutput = "Host key verification failed." } };
        var e = new StringWriter();
        var code = await Cli.Makeready(
            ParsedArgs.Parse(["makeready", "--ssh", Target, "--os", "linux"]), T.Config, ssh, new HealthProbe(ssh, TimeProvider.System), new StringWriter(), e, CancellationToken.None);
        Assert.Equal(70, code);
        Assert.Contains("ssh build@10.20.30.11", e.ToString(), StringComparison.Ordinal);
        Assert.Contains("known_hosts", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyEnvThatIsNotSetSaysHowToSetItOrUseTheAgent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProcessSshClient(_ => null).ResolveKey(T.Linux()));
        Assert.Contains("LANQ_KEY_BUILD01 is not set", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ssh-agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyEnvPointingNowhereNamesThePath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProcessSshClient(_ => "/nowhere/id_ed25519").ResolveKey(T.Linux()));
        Assert.Contains("/nowhere/id_ed25519", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConfigPointsAtLanqInit()
    {
        var none = Assert.Throws<ConfigException>(() => Cli.ResolveConfig(null, n => n == "HOME" ? "/home/me" : null, windows: false, exists: _ => false));
        Assert.Contains("lanq init --config " + Path.Combine("/home/me", ".config", "lanq", "machines.yml"), none.Message, StringComparison.Ordinal);

        var given = Assert.Throws<ConfigException>(() => Cli.ResolveConfig("fleet.yml", _ => null, windows: false, exists: _ => false));
        Assert.Contains("lanq init --config fleet.yml", given.Message, StringComparison.Ordinal);
    }
}
