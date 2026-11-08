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
        Assert.True(File.Exists(p.BundlePath));
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
        Assert.Contains("lanq/j2", Git("bundle", "list-heads", p.BundlePath!), StringComparison.Ordinal);
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
        Assert.Null(p.BundlePath);
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
}
