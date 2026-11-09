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
        var p = await new GitPayloadBuilder().BuildAsync("j1", _repo, Path.Combine(_repo, ".out"), PayloadKinds.Bundle, CancellationToken.None);
        Assert.Empty(p.ChangedFiles);
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), p.Sha);
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), p.Head);
        var b = await new GitPayloadBuilder().BuildBundleAsync("j1", _repo, Path.Combine(_repo, ".out"), p.Sha, null, CancellationToken.None);
        Assert.True(File.Exists(b.Path));
        Assert.Null(b.Base);
    }

    [Fact]
    public async Task OverlayIncludesModifiedAndUntrackedFilesWithoutTouchingIndexOrRefs()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "fresh");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".out/\n");
        var statusBefore = Git("status", "--porcelain");

        var p = await new GitPayloadBuilder().BuildAsync("j2", _repo, Path.Combine(_repo, ".out"), PayloadKinds.Bundle, CancellationToken.None);

        Assert.Contains("a.txt", p.ChangedFiles);
        Assert.Contains("new.txt", p.ChangedFiles);
        Assert.Contains("+fresh", p.OverlayDiff, StringComparison.Ordinal);
        Assert.NotEqual(Git("rev-parse", "HEAD").Trim(), p.Sha);
        Assert.Equal(statusBefore, Git("status", "--porcelain"));
        Assert.Empty(Git("for-each-ref", "refs/lanq"));
        var b = await new GitPayloadBuilder().BuildBundleAsync("j2", _repo, Path.Combine(_repo, ".out"), p.Sha, null, CancellationToken.None);
        Assert.Contains("lanq/j2", Git("bundle", "list-heads", b.Path), StringComparison.Ordinal);
        Assert.Empty(Git("for-each-ref", "refs/lanq"));
    }

    [Fact]
    public async Task LanqStateFolderIsNeverShippedEvenWhenNotIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".lanq", "logs"));
        File.WriteAllText(Path.Combine(_repo, ".lanq", "jobs.jsonl"), "log");
        Directory.CreateDirectory(Path.Combine(_repo, "sub", ".lanq"));
        File.WriteAllText(Path.Combine(_repo, "sub", ".lanq", "x.log"), "log");
        File.WriteAllText(Path.Combine(_repo, "real.txt"), "keep");
        var p = await new GitPayloadBuilder().BuildAsync("j4", _repo, Path.Combine(_repo, ".out"), PayloadKinds.Bundle, CancellationToken.None);
        Assert.Contains("real.txt", p.ChangedFiles);
        Assert.DoesNotContain(p.ChangedFiles, f => f.Contains(".lanq", StringComparison.Ordinal));
        Assert.DoesNotContain("log", p.OverlayDiff.Replace("+keep", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitignoredFilesAreNotShipped()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "secret.txt\n.out/\n");
        File.WriteAllText(Path.Combine(_repo, "secret.txt"), "nope");
        var p = await new GitPayloadBuilder().BuildAsync("j3", _repo, Path.Combine(_repo, ".out"), PayloadKinds.Bundle, CancellationToken.None);
        Assert.DoesNotContain("secret.txt", p.ChangedFiles);
    }

    [Fact]
    public async Task ArchiveHoldsTheSameSnapshotAsTheBundle()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "fresh");
        File.WriteAllText(Path.Combine(_repo, "secret.txt"), "nope");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "secret.txt\n.out/\n");
        Directory.CreateDirectory(Path.Combine(_repo, ".lanq"));
        File.WriteAllText(Path.Combine(_repo, ".lanq", "jobs.jsonl"), "log");
        Directory.CreateDirectory(Path.Combine(_repo, "sub", "deep"));
        File.WriteAllText(Path.Combine(_repo, "sub", "deep", "x.txt"), "deep");
        var p = await new GitPayloadBuilder().BuildAsync("j5", _repo, Path.Combine(_repo, ".out"), PayloadKinds.Archive, CancellationToken.None);

        Assert.NotNull(p.ArchivePath);
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(p.ArchivePath), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gz);
        var files = new Dictionary<string, string>();
        System.Formats.Tar.TarEntry? e;
        while ((e = reader.GetNextEntry()) is not null)
        {
            if (e.EntryType == System.Formats.Tar.TarEntryType.RegularFile)
            {
                files[e.Name] = new StreamReader(e.DataStream!).ReadToEnd();
            }
        }

        Assert.Equal("two", files["a.txt"]);
        Assert.Equal("fresh", files["new.txt"]);
        Assert.Equal("deep", files["sub/deep/x.txt"]);
        Assert.Contains(".gitignore", files.Keys);
        Assert.DoesNotContain("secret.txt", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.Contains(".lanq", StringComparison.Ordinal) || k.StartsWith(".out", StringComparison.Ordinal));
        Assert.Contains("new.txt", p.ChangedFiles);
        Assert.Empty(Git("for-each-ref", "refs/lanq"));
    }

    [Fact]
    public async Task LazyArchiveStreamsLargeFilesInOrderAndCountsSkippedSymlinksAndSubmodules()
    {
        var big = new byte[24 * 1024 * 1024];
        new Random(7).NextBytes(big);
        File.WriteAllBytes(Path.Combine(_repo, "a.big"), big);
        Directory.CreateDirectory(Path.Combine(_repo, "a"));
        File.WriteAllText(Path.Combine(_repo, "a", "b.txt"), "inner");
        File.WriteAllText(Path.Combine(_repo, "empty.txt"), "");
        File.WriteAllText(Path.Combine(_repo, "z.txt"), "last");
        Git("add", "-A");
        var blob = Git("hash-object", "-w", "a.txt").Trim();
        Git("update-index", "--add", "--cacheinfo", $"120000,{blob},link1");
        Git("update-index", "--add", "--cacheinfo", $"120000,{blob},link2");
        Git("update-index", "--add", "--cacheinfo", $"160000,{Git("rev-parse", "HEAD").Trim()},sub");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "more");
        // Commit straight from the index (symlink and submodule entries have no working-tree files, so a snapshot's "add -A" would drop them).
        var builder = new GitPayloadBuilder();
        var built = await builder.BuildArchiveAsync("j6", _repo, Path.Combine(_repo, ".out"), Git("rev-parse", "HEAD").Trim(), CancellationToken.None);

        Assert.Equal(2, built.SkippedSymlinks);
        Assert.Equal(1, built.SkippedSubmodules);
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(built.Path), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gz);
        var names = new List<string>();
        System.Formats.Tar.TarEntry? e;
        while ((e = reader.GetNextEntry()) is not null)
        {
            names.Add(e.Name);
            if (e.EntryType != System.Formats.Tar.TarEntryType.RegularFile)
            {
                continue;
            }

            using var ms = new MemoryStream();
            e.DataStream?.CopyTo(ms);
            switch (e.Name)
            {
                case "a.big":
                    Assert.True(big.AsSpan().SequenceEqual(ms.ToArray()), "large file content must survive streaming");
                    break;
                case "empty.txt":
                    Assert.Equal(0, ms.Length);
                    break;
                case "z.txt":
                    Assert.Equal("last", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
                    break;
                default:
                    break;
            }
        }

        Assert.DoesNotContain("link1", names);
        Assert.DoesNotContain("sub", names);
        Assert.Contains("a/b.txt", names);
        Assert.Contains("z.txt", names);
        Assert.Empty(Git("for-each-ref", "refs/lanq"));
    }
    [Fact]
    public async Task CommittedFilesListTheWholeHeadTreeButNotTheOverlay()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "deploy", "with space"));
        File.WriteAllText(Path.Combine(_repo, "deploy", "with space", "prod.pem"), "x");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "cert");
        File.WriteAllText(Path.Combine(_repo, "untracked.txt"), "new");

        var p = await new GitPayloadBuilder().BuildAsync("j7", _repo, Path.Combine(_repo, ".out"), PayloadKinds.None, CancellationToken.None);

        Assert.Equal(["a.txt", "deploy/with space/prod.pem"], p.CommittedFiles!.Order(StringComparer.Ordinal));
    }
}
