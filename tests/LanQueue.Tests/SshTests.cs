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
}
