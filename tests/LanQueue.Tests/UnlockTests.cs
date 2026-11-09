using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq unlock: which machine, which slots, and the --all --yes confirmation.</summary>
public class UnlockTests
{
    private static async Task<(int Code, string Out, string Err, FakeSsh Ssh)> Unlock(string output, params string[] args)
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(0, output) };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Unlock(ParsedArgs.Parse(["unlock", .. args]), T.Config(), ssh, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString(), ssh);
    }

    [Fact]
    public async Task ReportsWhatWasRemovedAndWhatWasKept()
    {
        var (code, o, _, ssh) = await Unlock("REMOVED=slot-1 crashed-job\nKEPT=slot-2 live\nKEPT=slot-3 nodeadline\n", "build-01");

        Assert.Equal(0, code);
        Assert.Single(ssh.Calls);
        Assert.Contains("removed slot-1 (job crashed-job", o, StringComparison.Ordinal);
        Assert.Contains("kept slot-2", o, StringComparison.Ordinal);
        Assert.Contains("--all --yes", o, StringComparison.Ordinal); // how to clear a slot without a deadline
    }

    [Fact]
    public async Task NothingToUnlockSaysSo()
    {
        var (code, o, _, _) = await Unlock("", "build-01");
        Assert.Equal(0, code);
        Assert.Contains("nothing to unlock", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobIdFromTheWorkerIsSanitised()
    {
        var (_, o, _, _) = await Unlock("REMOVED=slot-1 evil\u001b]0;pwned\u0007\n", "build-01");
        Assert.DoesNotContain('\u001b', o);
        Assert.DoesNotContain('\u0007', o);
    }

    [Fact]
    public async Task AllWithoutYesIsRefusedBeforeAnySsh()
    {
        var (code, _, e, ssh) = await Unlock("", "build-01", "--all");
        Assert.Equal(2, code);
        Assert.Contains("--yes", e, StringComparison.Ordinal);
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task AllWithYesWarnsAndRemovesEverything()
    {
        var (code, o, e, ssh) = await Unlock("REMOVED=slot-1 a\nREMOVED=slot-2 \n", "build-01", "--all", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("running job", e, StringComparison.Ordinal);
        Assert.Contains("# unlock --all", ssh.Calls[0].Command, StringComparison.Ordinal);
        Assert.Contains("removed slot-2", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("no-such-machine")]
    public async Task UnknownOrMissingMachineIsExit2(params string[] args)
    {
        var (code, _, e, ssh) = await Unlock("", args);
        Assert.Equal(2, code);
        Assert.Contains("build-01", e, StringComparison.Ordinal); // lists the configured names
        Assert.Empty(ssh.Calls);
    }

    [Fact]
    public async Task SshFailureIsExit70()
    {
        var ssh = new FakeSsh { Handler = (_, _) => new SshResult(255, "ssh: connect to host build-01.lan port 22: Connection refused") };
        var e = new StringWriter();
        var code = await Cli.Unlock(ParsedArgs.Parse(["unlock", "build-01"]), T.Config(), ssh, new StringWriter(), e, CancellationToken.None);
        Assert.Equal(70, code);
        Assert.Contains("Connection refused", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptsTakeTheConfiguredWorkRootQuoted()
    {
        Assert.Contains("'/srv/lanq'", Jobs.RemoteScripts.Unlock(T.Linux(), all: false), StringComparison.Ordinal);
        Assert.Contains("'D:/lanq'", Jobs.RemoteScripts.Unlock(T.Windows(), all: true), StringComparison.Ordinal);
    }
}
