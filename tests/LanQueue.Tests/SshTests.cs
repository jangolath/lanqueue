using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public class SshTests
{
    [Fact]
    public void SshIsKeyOnlyAndNonInteractive()
    {
        var args = SshArgs.Ssh(T.Linux(), "/keys/id", "true");
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("PasswordAuthentication=no", args);
        Assert.Contains("KbdInteractiveAuthentication=no", args);
        Assert.Contains("PreferredAuthentications=publickey", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("/keys/id", args);
    }

    [Fact]
    public void ScpIsAlsoBatchModeAndKeyOnly()
    {
        var args = SshArgs.Scp(T.Linux(), null, "a.bundle", "/srv/x.bundle");
        Assert.Contains("-B", args);
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("PasswordAuthentication=no", args);
        Assert.Equal("user@build-01.lan:/srv/x.bundle", args[^1]);
    }

    [Fact]
    public void TargetCannotBeParsedAsAnOption()
    {
        var m = T.Linux();
        m.Ssh = "-oProxyCommand=evil";
        Assert.Throws<ArgumentException>(() => SshArgs.Ssh(m, null, "true"));
    }

    [Fact]
    public void NoArgumentEverEnablesPasswordAuth()
    {
        var all = SshArgs.Ssh(T.Linux(), "k", "x").Concat(SshArgs.Scp(T.Linux(), "k", "a", "b"));
        Assert.DoesNotContain(all, a => a.Contains("PasswordAuthentication=yes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WithoutAKeyNoIdentityOptionIsPassedAndAgentAuthStaysHardened()
    {
        foreach (var args in new[] { SshArgs.Ssh(T.Windows(), null, "true"), SshArgs.Scp(T.Windows(), null, "a", "b") })
        {
            Assert.DoesNotContain("-i", args);
            Assert.DoesNotContain("IdentitiesOnly=yes", args);
            Assert.Contains("BatchMode=yes", args);
            Assert.Contains("PasswordAuthentication=no", args);
            Assert.Contains("PreferredAuthentications=publickey", args);
            Assert.Contains("StrictHostKeyChecking=yes", args);
        }
    }

    [Fact]
    public void WithAKeyIdentityIsPinned()
    {
        var args = SshArgs.Ssh(T.Linux(), "/keys/id", "true");
        Assert.Contains("IdentitiesOnly=yes", args);
        Assert.Equal("/keys/id", args[args.IndexOf("-i") + 1]);
    }

    [Fact]
    public void MachineWithoutKeyEnvResolvesToNoKeyAndMissingEnvIsAnError()
    {
        var client = new ProcessSshClient(_ => null);
        Assert.Null(client.ResolveKey(T.Windows()));
        Assert.Throws<InvalidOperationException>(() => client.ResolveKey(T.Linux()));
    }

    [Theory]
    [InlineData("ForwardAgent=no")]
    [InlineData("ClearAllForwardings=yes")]
    [InlineData("PermitLocalCommand=no")]
    public void SshAndScpDisableAgentForwardingPortForwardsAndLocalCommands(string option)
    {
        foreach (var args in new[] { SshArgs.Ssh(T.Linux(), "/keys/id", "true"), SshArgs.Scp(T.Linux(), "/keys/id", "a", "b") })
        {
            var at = args.IndexOf(option);
            Assert.True(at > 0, option + " missing");
            Assert.Equal("-o", args[at - 1]);
            Assert.True(at < args.IndexOf("--"), "options must come before the target");
        }
    }
}
