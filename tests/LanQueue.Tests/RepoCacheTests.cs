using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

public sealed class RepoCacheTests : IDisposable
{
    private const string Job = "20260101-120000-aaaaaa";

    private readonly TempRepo _repo = new();

    private readonly LocalWorker _worker = new();

    public void Dispose()
    {
        _repo.Dispose();
        _worker.Dispose();
    }

    [Fact]
    public async Task RepoIdIsTheFirst16HexOfTheSmallestRootCommit()
    {
        var firstRoot = _repo.Git("rev-parse", "HEAD").Trim();
        _repo.Git("checkout", "-q", "--orphan", "other");
        var otherRoot = _repo.Commit("b.txt", "two", "second root");
        _repo.Git("checkout", "-q", "main");
        _repo.Git("-c", "user.name=t", "-c", "user.email=t@t", "merge", "-q", "--allow-unrelated-histories", "-m", "join", "other");

        var p = await new GitPayloadBuilder().BuildAsync("j1", _repo.Dir, _repo.Out, PayloadKinds.Bundle, CancellationToken.None);

        var expected = string.CompareOrdinal(firstRoot, otherRoot) < 0 ? firstRoot : otherRoot;
        Assert.Equal(expected[..16], p.RepoId);
    }

    [Fact]
    public async Task RepoIdIsStableAcrossCommitsAndOverlays()
    {
        var first = await new GitPayloadBuilder().BuildAsync("j1", _repo.Dir, _repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        _repo.Commit("c.txt", "three", "more");
        File.WriteAllText(Path.Combine(_repo.Dir, "dirty.txt"), "x");
        var second = await new GitPayloadBuilder().BuildAsync("j2", _repo.Dir, _repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        Assert.NotNull(first.RepoId);
        Assert.Equal(first.RepoId, second.RepoId);
    }

    [Fact]
    public void GitScriptsUseThePerRepositoryCacheNeverTheSharedOne()
    {
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            var prepare = RemoteScripts.Prepare(m, "j", T.RepoId);
            var cleanup = RemoteScripts.Cleanup(m, "j", "1", T.RepoId);
            Assert.Contains("repos/" + T.RepoId + ".git", prepare, StringComparison.Ordinal);
            Assert.Contains("repos/" + T.RepoId + ".git", cleanup, StringComparison.Ordinal);
            Assert.DoesNotContain("repo.git", prepare, StringComparison.Ordinal);
            Assert.DoesNotContain("repo.git", cleanup, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcde")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789abcd'x")]
    public void GitScriptsRefuseAnythingButA16HexRepoId(string? repoId)
    {
        Assert.Throws<ArgumentException>(() => RemoteScripts.Prepare(T.Linux(), "j", repoId));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Prepare(T.Windows(), "j", repoId));
        if (repoId is not null)
        {
            Assert.Throws<ArgumentException>(() => RemoteScripts.Cleanup(T.Linux(), "j", "1", repoId)); // null: cleanup without the cache steps
        }
    }

    [Fact]
    public async Task RealPrepareCreatesThePerRepositoryCacheAndCleanupRemovesTheWorktree()
    {
        var p = await new GitPayloadBuilder().BuildAsync(Job, _repo.Dir, _repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        File.Copy((await new GitPayloadBuilder().BuildBundleAsync(Job, _repo.Dir, _repo.Out, p.Sha, null, CancellationToken.None)).Path, Path.Combine(_worker.Root, "incoming", Job + ".bundle"));

        var (code, output) = _worker.Run(RemoteScripts.Prepare(_worker.Machine, Job, p.RepoId));

        Assert.True(code == 0, output);
        Assert.True(Directory.Exists(Path.Combine(_worker.Root, "repos", p.RepoId + ".git")));
        Assert.False(Directory.Exists(Path.Combine(_worker.Root, "repo.git")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(_worker.Root, "jobs", Job, "a.txt")));

        (code, output) = _worker.Run(RemoteScripts.Cleanup(_worker.Machine, Job, "1", p.RepoId));
        Assert.True(code == 0, output);
        Assert.False(Directory.Exists(Path.Combine(_worker.Root, "jobs", Job)));
        Assert.False(File.Exists(Path.Combine(_worker.Root, "incoming", Job + ".bundle")));
    }
}
