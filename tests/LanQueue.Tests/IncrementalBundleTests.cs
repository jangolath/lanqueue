using LanQueue.Jobs;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Incremental bundles against a real repository and a real worker cache (the generated scripts run by the local shell).</summary>
public sealed class IncrementalBundleTests : IDisposable
{
    private readonly TempRepo _repo = new();

    private readonly LocalWorker _worker = new();

    private readonly GitPayloadBuilder _builder = new();

    public void Dispose()
    {
        _repo.Dispose();
        _worker.Dispose();
    }

    /// <summary>The "-sha" prerequisite lines of a bundle's header.</summary>
    private static List<string> Prerequisites(string bundle)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(bundle);
        string? line;
        while ((line = reader.ReadLine()) is { Length: > 0 })
        {
            if (line[0] == '-')
            {
                lines.Add(line[1..].Split(' ')[0]);
            }
        }

        return lines;
    }

    private async Task<(JobPayload Payload, BundleBuild Bundle)> Snapshot(string job, string? baseSha)
    {
        var p = await _builder.BuildAsync(job, _repo.Dir, _repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        return (p, await _builder.BuildBundleAsync(job, _repo.Dir, _repo.Out, p.Sha, baseSha, CancellationToken.None));
    }

    private (int Code, string Output) Prepare(string job, JobPayload p, BundleBuild b)
    {
        File.Copy(b.Path, Path.Combine(_worker.Root, "incoming", job + ".bundle"), overwrite: true);
        return _worker.Run(RemoteScripts.Prepare(_worker.Machine, job, p.RepoId));
    }

    private string CacheGit(JobPayload p, params string[] args) =>
        _repo.Git(["-C", Path.Combine(_worker.Root, "repos", p.RepoId + ".git"), .. args]);

    [Fact]
    public async Task FirstRunIsFullThenTheNextIsIncrementalSmallerAndListsItsPrerequisite()
    {
        for (var i = 0; i < 20; i++)
        {
            _repo.Commit($"f{i}.txt", new string((char)('a' + i), 2000) + i, "c" + i);
        }

        var (p1, full) = await Snapshot("j1", null);
        Assert.Null(full.Base);
        Assert.Empty(Prerequisites(full.Path));
        var (code, output) = Prepare("j1", p1, full);
        Assert.True(code == 0, output);
        Assert.Equal(p1.Sha, CacheGit(p1, "rev-parse", "refs/lanq/base").Trim());
        Assert.Equal(0, _worker.Run(RemoteScripts.Cleanup(_worker.Machine, "j1", "1", p1.RepoId)).Code);

        _repo.Commit("next.txt", "next", "next");
        File.WriteAllText(Path.Combine(_repo.Dir, "dirty.txt"), "uncommitted");
        var (p2, inc) = await Snapshot("j2", p1.Head);

        Assert.Equal(p1.Head, inc.Base);
        Assert.Equal([p1.Head!], Prerequisites(inc.Path));
        var fullAgain = await _builder.BuildBundleAsync("j2", _repo.Dir, _repo.Out, p2.Sha, null, CancellationToken.None);
        Assert.True(new FileInfo(inc.Path).Length < new FileInfo(fullAgain.Path).Length / 2, "the incremental bundle must be much smaller");

        (code, output) = Prepare("j2", p2, inc);
        Assert.True(code == 0, output);
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(_worker.Root, "jobs", "j2", "dirty.txt")));
        Assert.Equal("next", File.ReadAllText(Path.Combine(_worker.Root, "jobs", "j2", "next.txt")));
        Assert.Equal(p2.Sha, CacheGit(p2, "rev-parse", "refs/lanq/base").Trim());
    }

    [Fact]
    public async Task UnchangedCleanRepositoryShipsOnlyTheLastCommit()
    {
        var (p1, full) = await Snapshot("j1", null);
        _repo.Commit("b.txt", "two", "second");
        var (p2, _) = await Snapshot("j2", null);
        var same = await _builder.BuildBundleAsync("j3", _repo.Dir, _repo.Out, p2.Sha, p2.Head, CancellationToken.None);

        Assert.Equal(p2.Sha, p2.Head); // clean tree: the snapshot is HEAD itself
        Assert.Equal(p2.Head, same.Base);
        Assert.Equal([p1.Sha], Prerequisites(same.Path)); // git refuses an empty bundle, so the base's parent is the prerequisite
        Assert.Contains("refs/lanq/j3", _repo.Git("bundle", "list-heads", same.Path), StringComparison.Ordinal);
        Assert.Null(full.Base);
    }

    [Fact]
    public async Task WorkerThatLostItsCacheRefusesTheIncrementalBundleAndTakesTheFullOne()
    {
        var (p1, full) = await Snapshot("j1", null);
        Assert.Equal(0, Prepare("j1", p1, full).Code);
        Assert.Equal(0, _worker.Run(RemoteScripts.Cleanup(_worker.Machine, "j1", "1", p1.RepoId)).Code);
        _repo.Commit("b.txt", "two", "second");
        var (p2, inc) = await Snapshot("j2", p1.Head);
        Assert.NotNull(inc.Base);

        foreach (var f in Directory.EnumerateFiles(Path.Combine(_worker.Root, "repos"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(Path.Combine(_worker.Root, "repos"), recursive: true);
        var (code, output) = Prepare("j2", p2, inc);
        Assert.NotEqual(0, code);
        Assert.Contains(RemoteScripts.BundleNotUsable, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_worker.Root, "jobs", "j2")));

        var retry = await _builder.BuildBundleAsync("j2", _repo.Dir, _repo.Out, p2.Sha, null, CancellationToken.None);
        (code, output) = Prepare("j2", p2, retry);
        Assert.True(code == 0, output);
        Assert.Equal("two", File.ReadAllText(Path.Combine(_worker.Root, "jobs", "j2", "b.txt")));
    }

    [Fact]
    public async Task RewrittenHistoryFallsBackToAFullBundle()
    {
        _repo.Commit("b.txt", "two", "second");
        var (p1, _) = await Snapshot("j1", null);
        _repo.Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--amend", "-m", "rewritten");

        var (_, b) = await Snapshot("j2", p1.Head);

        Assert.Null(b.Base);
        Assert.Empty(Prerequisites(b.Path));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("--output=/tmp/x")]
    [InlineData("HEAD")]
    [InlineData("")]
    public async Task UnknownOrMalformedBaseFallsBackToAFullBundle(string baseSha)
    {
        _repo.Commit("b.txt", "two", "second");
        var (_, b) = await Snapshot("j1", baseSha);
        Assert.Null(b.Base);
        Assert.Empty(Prerequisites(b.Path));
        Assert.Empty(_repo.Git("for-each-ref", "refs/lanq"));
    }
}
