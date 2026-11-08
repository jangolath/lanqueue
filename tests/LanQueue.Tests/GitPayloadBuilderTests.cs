using System.Diagnostics;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

public sealed class GitPayloadBuilderTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "lanq-repo-" + Guid.NewGuid().ToString("N"));

    public GitPayloadBuilderTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(_repo, recursive: true);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return o;
    }

    [Fact]
    public async Task CleanTreeBundlesHeadWithNoOverlay()
    {
        var p = await new GitPayloadBuilder().BuildAsync("j1", _repo, Path.Combine(_repo, ".out"), CancellationToken.None);
        Assert.Empty(p.ChangedFiles);
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), p.Sha);
        Assert.True(File.Exists(p.BundlePath));
    }

    [Fact]
    public async Task OverlayIncludesModifiedAndUntrackedFilesWithoutTouchingIndexOrRefs()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "fresh");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".out/\n");
        var statusBefore = Git("status", "--porcelain");

        var p = await new GitPayloadBuilder().BuildAsync("j2", _repo, Path.Combine(_repo, ".out"), CancellationToken.None);

        Assert.Contains("a.txt", p.ChangedFiles);
        Assert.Contains("new.txt", p.ChangedFiles);
        Assert.Contains("+fresh", p.OverlayDiff, StringComparison.Ordinal);
        Assert.NotEqual(Git("rev-parse", "HEAD").Trim(), p.Sha);
        Assert.Equal(statusBefore, Git("status", "--porcelain"));
        Assert.Empty(Git("for-each-ref", "refs/lanq"));
        Assert.Contains("lanq/j2", Git("bundle", "list-heads", p.BundlePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitignoredFilesAreNotShipped()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "secret.txt\n.out/\n");
        File.WriteAllText(Path.Combine(_repo, "secret.txt"), "nope");
        var p = await new GitPayloadBuilder().BuildAsync("j3", _repo, Path.Combine(_repo, ".out"), CancellationToken.None);
        Assert.DoesNotContain("secret.txt", p.ChangedFiles);
    }
}
