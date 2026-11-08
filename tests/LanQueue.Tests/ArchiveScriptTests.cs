using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

public class ArchiveScriptTests
{
    private static Machine Linux()
    {
        var m = T.Linux();
        m.Transfer = TransferMode.Archive;
        return m;
    }

    private static Machine Windows()
    {
        var m = T.Windows();
        m.Transfer = TransferMode.Archive;
        return m;
    }

    [Fact]
    public void IncomingNameDependsOnTransfer()
    {
        Assert.EndsWith("/incoming/j.bundle", RemoteScripts.Incoming(T.Linux(), "j"), StringComparison.Ordinal);
        Assert.EndsWith("/incoming/j.tar.gz", RemoteScripts.Incoming(Linux(), "j"), StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPrepareListsBeforeExtractingAndRefusesUnsafeEntries()
    {
        var s = RemoteScripts.Prepare(Linux(), "j");
        var list = s.IndexOf("tar -tzf", StringComparison.Ordinal);
        var extract = s.IndexOf("tar -xzf", StringComparison.Ordinal);
        Assert.True(list >= 0 && extract > list, "listing check must precede extraction");
        Assert.Contains("--no-same-owner", s, StringComparison.Ordinal);
        Assert.Contains("--no-same-permissions", s, StringComparison.Ordinal);
        Assert.Contains("../*", s, StringComparison.Ordinal);
        Assert.Contains("exit 1", s, StringComparison.Ordinal);
        Assert.DoesNotContain("git", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPrepareUsesBuiltInTarWithAListingCheckAndFitsTheCommandLimit()
    {
        var s = RemoteScripts.Prepare(Windows(), "20260101-120000-abcdef");
        Assert.True(s.IndexOf("-tzf", StringComparison.Ordinal) < s.IndexOf("-xzf", StringComparison.Ordinal));
        Assert.DoesNotContain("git", s, StringComparison.Ordinal);
        _ = RemoteScripts.Wrap(Windows(), s); // throws if over the Windows command-line cap
    }

    [Fact]
    public void ExecuteIsSharedSoDirectoryChecksAndProcessGroupApplyToArchiveJobs()
    {
        Assert.Equal(
            RemoteScripts.Execute(T.Linux(), "j", "src", ["dotnet", "test"]),
            RemoteScripts.Execute(Linux(), "j", "src", ["dotnet", "test"]));
        Assert.Contains("cd -P", RemoteScripts.Execute(Linux(), "j", "src", ["dotnet", "test"]), StringComparison.Ordinal);
        Assert.Contains("Resolve-Path", RemoteScripts.Execute(Windows(), "j", "src", ["dotnet", "test"]), StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupKillsTheJobThenRemovesFolderArchiveAndSlotWithoutGit()
    {
        var l = RemoteScripts.Cleanup(Linux(), "j", "1");
        Assert.DoesNotContain("git", l, StringComparison.Ordinal);
        Assert.True(l.IndexOf("kill -s KILL", StringComparison.Ordinal) < l.IndexOf("rm -rf", StringComparison.Ordinal));
        Assert.Contains("incoming/$ID.tar.gz", l, StringComparison.Ordinal);
        Assert.Contains("locks/slot-1", l, StringComparison.Ordinal);

        var w = RemoteScripts.Cleanup(Windows(), "j", "1");
        Assert.DoesNotContain("git", w, StringComparison.Ordinal);
        Assert.True(w.IndexOf("taskkill", StringComparison.Ordinal) < w.IndexOf("rd /s", StringComparison.Ordinal));
        Assert.Contains("incoming/$id.tar.gz", w, StringComparison.Ordinal);
    }
}
