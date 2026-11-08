using System.Text;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

public class ScriptTests
{
    [Fact]
    public void LinuxExecuteQuotesEveryArgumentAndStaysInWorktree()
    {
        var s = RemoteScripts.Execute(T.Linux(), "job1", "src", ["dotnet", "test", "--filter", "a b; rm -rf /"]);
        Assert.Contains("cd '/srv/lanq/jobs/job1/src'", s, StringComparison.Ordinal);
        Assert.Contains("'a b; rm -rf /'", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsExecuteUsesCallOperatorWithQuotedArgs()
    {
        var s = RemoteScripts.Execute(T.Windows(), "job1", ".", ["dotnet", "test"]);
        Assert.Contains("& 'dotnet' 'test'", s, StringComparison.Ordinal);
        Assert.Contains("'D:/lanq/jobs/job1'", s, StringComparison.Ordinal);
        Assert.EndsWith("exit $LASTEXITCODE", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsWrapIsEncodedCommand()
    {
        var w = RemoteScripts.Wrap(T.Windows(), "Write-Output hi");
        var b64 = w[(w.LastIndexOf(' ') + 1)..];
        Assert.Contains("Write-Output hi", Encoding.Unicode.GetString(Convert.FromBase64String(b64)), StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxWrapSingleQuotes() =>
        Assert.StartsWith("sh -c '", RemoteScripts.Wrap(T.Linux(), "echo 'x'"), StringComparison.Ordinal);

    [Fact]
    public void SlotUsesExclusiveCreateAndHonoursMaxJobs()
    {
        Assert.Contains("set -C", RemoteScripts.AcquireSlot(T.Linux(), "j"), StringComparison.Ordinal);
        Assert.Contains("-le 2", RemoteScripts.AcquireSlot(T.Linux(), "j"), StringComparison.Ordinal);
        Assert.Contains("CreateNew", RemoteScripts.AcquireSlot(T.Windows(), "j"), StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupRemovesWorktreeBundleAndSlot()
    {
        var s = RemoteScripts.Cleanup(T.Linux(), "j", "2");
        Assert.Contains("worktree remove --force", s, StringComparison.Ordinal);
        Assert.Contains("slot-2", s, StringComparison.Ordinal);
        Assert.Contains(".bundle", s, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupRejectsNonNumericSlot() =>
        Assert.Throws<ArgumentException>(() => RemoteScripts.Cleanup(T.Linux(), "j", "1; rm -rf /"));

    [Theory]
    [InlineData("SLOT=2\n", "2")]
    [InlineData("noise\nSLOT=1\r\n", "1")]
    [InlineData("SLOT=FULL\n", null)]
    [InlineData("", null)]
    public void ParsesSlot(string output, string? expected) => Assert.Equal(expected, JobRunner.ParseSlot(output));
}
