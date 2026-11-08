using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

public sealed class ArchiveWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-arch-" + Guid.NewGuid().ToString("N"));

    public ArchiveWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ArchiveFile F(string path, string text, bool exec = false) => new(path, exec, Encoding.UTF8.GetBytes(text));

    private static List<TarEntry> Read(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        var list = new List<TarEntry>();
        TarEntry? e;
        while ((e = reader.GetNextEntry(copyData: true)) is not null)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public async Task WritesSortedRegularFilesAndDirectoriesOnly()
    {
        var path = Path.Combine(_dir, "a.tar.gz");
        await ArchiveWriter.CreateAsync(path, [F("src/b.cs", "b"), F("a.txt", "a"), F("src/deep/c.cs", "c", exec: true)], CancellationToken.None);
        var entries = Read(path);
        Assert.All(entries, e => Assert.True(e.EntryType is TarEntryType.RegularFile or TarEntryType.Directory, e.EntryType.ToString()));
        var names = entries.Select(e => e.Name.TrimEnd('/')).ToList();
        Assert.Equal(["a.txt", "src", "src/b.cs", "src/deep", "src/deep/c.cs"], names);
        Assert.Equal((UnixFileMode)0b111_101_101, entries.Single(e => e.Name == "src/deep/c.cs").Mode);
        Assert.Equal((UnixFileMode)0b110_100_100, entries.Single(e => e.Name == "a.txt").Mode);
        Assert.Equal("c", new StreamReader(entries.Single(e => e.Name == "src/deep/c.cs").DataStream!).ReadToEnd());
    }

    [Fact]
    public async Task OutputIsDeterministicRegardlessOfInputOrder()
    {
        var p1 = Path.Combine(_dir, "1.tar.gz");
        var p2 = Path.Combine(_dir, "2.tar.gz");
        await ArchiveWriter.CreateAsync(p1, [F("b.txt", "b"), F("a/long-" + new string('x', 150) + ".txt", "a")], CancellationToken.None);
        await Task.Delay(1100);
        await ArchiveWriter.CreateAsync(p2, [F("a/long-" + new string('x', 150) + ".txt", "a"), F("b.txt", "b")], CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(p1), File.ReadAllBytes(p2));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a/..")]
    [InlineData(@"a\b")]
    [InlineData("C:/x")]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a\nb")]
    [InlineData("...")]
    [InlineData("x/.. /y")]
    [InlineData("x/ ")]
    [InlineData("dir./file")]
    [InlineData("trail ing /x")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("src/NUL.txt")]
    [InlineData("Aux.tar.gz")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("a/com5/b")]
    [InlineData("CON .txt")]
    public async Task RefusesUnsafeEntryPaths(string bad)
    {
        var path = Path.Combine(_dir, "bad.tar.gz");
        await Assert.ThrowsAsync<ArgumentException>(() => ArchiveWriter.CreateAsync(path, [F(bad, "x")], CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("a/CONSOLE.txt")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("a.b/c.d")]
    [InlineData(".gitignore")]
    [InlineData("dir/.hidden")]
    [InlineData("name with space.txt")]
    public void ValidatePathAcceptsOrdinaryNames(string ok) => ArchiveWriter.ValidatePath(ok);

    [Theory]
    [InlineData("a/CON", "windows-reserved-device-name")]
    [InlineData("x/...", "windows-trailing-dot-or-space")]
    [InlineData("a:b", "windows-forbidden-character")]
    [InlineData("../x", "empty-dot-or-dotdot-segment")]
    public void RefusalNamesTheRule(string bad, string rule)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArchiveWriter.ValidatePath(bad));
        Assert.Contains("rule: " + rule, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("100644", true)]
    [InlineData("100755", true)]
    [InlineData("120000", false)]
    [InlineData("160000", false)]
    public void OnlyRegularFileModesAreArchived(string mode, bool expected) =>
        Assert.Equal(expected, ArchiveWriter.IsRegularMode(mode));

    [Theory]
    [InlineData("a -> b.txt")]
    [InlineData("dir/x link to y")]
    public void NamesThatLookLikeLinkListingsAreRefusedLocallyWithANamedRule(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => ArchiveWriter.ValidatePath(path));
        Assert.Contains("name-looks-like-link-listing", ex.Message, StringComparison.Ordinal);
    }
}
