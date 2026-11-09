using System.Security.Cryptography;
using System.Text;
using LanQueue.Fetch;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The local side of --fetch against a hostile worker: every frame, path, size, count and hash is checked again, partial files are deleted,
/// and nothing is ever written outside out/jobid.
/// </summary>
public sealed class FetchReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-fetchrx-" + Guid.NewGuid().ToString("N"));

    public FetchReceiverTests() => Directory.CreateDirectory(_root);

    /// <summary>Removes the links the tests plant (never what they point at), then the rest.</summary>
    public void Dispose() => TestTree.Delete(_root);

    private string JobDir => Path.Combine(_root, "out", "job1");

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Bytes(int n, int seed = 0) => Enumerable.Range(0, n).Select(i => (byte)((i + seed) % 256)).ToArray();

    /// <summary>One file frame: header (with the size given, or the real one), base64 lines of <paramref name="chunk"/> bytes, END (the hash given, or the real one).</summary>
    private static IEnumerable<string> Frame(string path, byte[] data, long? announced = null, string? hash = null, int chunk = 57)
    {
        yield return $"LQF1 FILE {announced ?? data.Length} {B64(path)}";
        for (var i = 0; i < data.Length; i += chunk)
        {
            yield return Convert.ToBase64String(data, i, Math.Min(chunk, data.Length - i));
        }

        yield return "LQF1 END " + (hash ?? Hash(data));
    }

    private FetchReport Receive(IEnumerable<string> lines, string[]? patterns = null, FetchLimits? limits = null, bool done = true, bool ignoreCase = false)
    {
        using var rx = new FetchReceiver(JobDir, patterns ?? ["**"], limits ?? FetchLimits.Default, ignoreCase);
        foreach (var l in lines)
        {
            rx.OnLine(l);
        }

        if (done)
        {
            rx.OnLine("LQF1 DONE");
        }

        return rx.Finish();
    }

    private static string Reason(FetchReport r, string path) => Assert.Single(r.Skipped, s => s.Path == path).Reason;

    [Fact]
    public void FilesAreWrittenBelowTheJobFolderWithTheirHashes()
    {
        var a = Bytes(1000);
        var b = Bytes(3, 7);
        var r = Receive(Frame("TestResults/run/a.trx", a).Concat(Frame("b.bin", b)).Concat(Frame("empty.txt", [])));

        Assert.Null(r.Error);
        Assert.Empty(r.Skipped);
        Assert.Equal(["TestResults/run/a.trx", "b.bin", "empty.txt"], r.Fetched.Select(f => f.Path));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(JobDir, "TestResults", "run", "a.trx")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(JobDir, "b.bin")));
        Assert.Equal(Hash(a), r.Fetched[0].Sha256);
        Assert.Equal(1003, r.Bytes);
        Assert.Equal(Path.GetFullPath(JobDir), r.Directory);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..")]
    [InlineData("./evil.txt")]
    [InlineData("a//evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("evil.txt:stream")]
    public void APathThatLeavesTheJobFolderIsRefused(string path)
    {
        var r = Receive(Frame(path, Bytes(10)));

        Assert.Empty(r.Fetched);
        Assert.StartsWith("unsafe path", Reason(r, path), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "out", "evil.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ANameWithANewlineIsRefusedAndShownSanitised()
    {
        var r = Receive(Frame("a\nb.txt", Bytes(10)).Concat(Frame("c\u202e.txt", Bytes(10))));

        Assert.Empty(r.Fetched);
        Assert.Equal(2, r.Skipped.Count);
        Assert.Equal("a?b.txt", r.Skipped[0].Path); // never a raw newline or bidi control on the terminal
        Assert.Equal("c?.txt", r.Skipped[1].Path);
        Assert.All(r.Skipped, s => Assert.Contains("control", s.Reason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("logs/COM1.log")]
    [InlineData("Aux")]
    [InlineData("LPT9")]
    [InlineData("conin$")]
    [InlineData("COM\u00b9.txt")]
    [InlineData("NUL .txt")]
    [InlineData("logs/COM1 .log")]
    [InlineData("CON  .txt")]
    [InlineData("conout$ .log")]
    [InlineData("LPT² .txt")]
    [InlineData("trailing.")]
    [InlineData("dir./x.txt")]
    [InlineData("space ")]
    [InlineData("a<b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("a\"b.txt")]
    public void AWindowsReservedOrUnstorableNameIsRefused(string path)
    {
        var r = Receive(Frame(path, Bytes(10)));
        Assert.Empty(r.Fetched);
        Assert.StartsWith("unsafe path", Reason(r, path), StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongNamesAreRefused()
    {
        var segment = new string('a', 256);
        var path = string.Join('/', Enumerable.Repeat("abcdefghij", 27)); // 296 characters
        var r = Receive(Frame(segment, Bytes(1)).Concat(Frame(path, Bytes(1))));
        Assert.Empty(r.Fetched);
        Assert.Equal(2, r.Skipped.Count);
    }

    [Fact]
    public void ACaseInsensitiveCollisionIsRefusedAndTheFirstFileKept()
    {
        var first = Bytes(10);
        var r = Receive(Frame("Report.txt", first).Concat(Frame("report.TXT", Bytes(20, 1)))
            .Concat(Frame("Dir/a.txt", Bytes(5))).Concat(Frame("dir/b.txt", Bytes(5)))
            .Concat(Frame("x", Bytes(5))).Concat(Frame("X/y.txt", Bytes(5)))
            .Concat(Frame("Dir/a.txt", Bytes(5))));

        Assert.Equal(["Report.txt", "Dir/a.txt", "x"], r.Fetched.Select(f => f.Path));
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(JobDir, "Report.txt")));
        Assert.Contains("collides", Reason(r, "report.TXT"), StringComparison.Ordinal);
        Assert.Contains("collides", Reason(r, "dir/b.txt"), StringComparison.Ordinal);
        Assert.Contains("collides", Reason(r, "X/y.txt"), StringComparison.Ordinal);
        Assert.Contains("collides", Reason(r, "Dir/a.txt"), StringComparison.Ordinal); // an exact duplicate too
    }

    [Fact]
    public void MoreBytesThanTheHeaderSaidFailsTheFileAndDeletesIt()
    {
        var r = Receive(Frame("lie.bin", Bytes(20), announced: 10, chunk: 5).Concat(Frame("ok.bin", Bytes(4))));

        Assert.Equal(["ok.bin"], r.Fetched.Select(f => f.Path));
        Assert.Contains("more than the 10 bytes", Reason(r, "lie.bin"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(JobDir, "lie.bin")));
    }

    [Fact]
    public void FewerBytesThanTheHeaderSaidFailsTheFile()
    {
        var r = Receive(Frame("short.bin", Bytes(10), announced: 20));
        Assert.Empty(r.Fetched);
        Assert.Contains("size mismatch", Reason(r, "short.bin"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(JobDir, "short.bin")));
    }

    [Fact]
    public void ABadHashFailsTheFileAndDeletesIt()
    {
        var r = Receive(Frame("bad.bin", Bytes(100), hash: new string('0', 64)).Concat(Frame("nohash.bin", Bytes(5), hash: "")));

        Assert.Empty(r.Fetched);
        Assert.Contains("SHA-256", Reason(r, "bad.bin"), StringComparison.Ordinal);
        Assert.Contains("SHA-256", Reason(r, "nohash.bin"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(JobDir, "bad.bin")));
        Assert.False(File.Exists(Path.Combine(JobDir, "nohash.bin")));
    }

    [Fact]
    public void TheTotalCapIsEnforcedOnTheBytesReceivedWhateverTheWorkerSays()
    {
        var limits = new FetchLimits(500, 100, 100);
        var r = Receive(
            Frame("a.bin", Bytes(60)).Concat(Frame("b.bin", Bytes(60))).Concat(Frame("c.bin", Bytes(30)))
                .Concat(Frame("big.bin", Bytes(101))),
            limits: limits);

        Assert.Equal(["a.bin", "c.bin"], r.Fetched.Select(f => f.Path));
        Assert.Equal("over the total size cap", Reason(r, "b.bin"));
        Assert.Equal("larger than the per-file cap", Reason(r, "big.bin"));
        Assert.False(File.Exists(Path.Combine(JobDir, "b.bin")));
        Assert.True(r.Bytes <= 100);
    }

    /// <summary>A header that claims a small size cannot smuggle more than the total cap: the bytes are counted as they arrive.</summary>
    [Fact]
    public void ASizeLieCannotGetPastTheTotalCap()
    {
        var limits = new FetchLimits(500, 50, 50);
        var r = Receive(Frame("a.bin", Bytes(40)).Concat(Frame("b.bin", Bytes(40), announced: 5, chunk: 5)), limits: limits);
        Assert.Equal(["a.bin"], r.Fetched.Select(f => f.Path));
        Assert.Single(r.Skipped, s => s.Path == "b.bin");
        Assert.False(File.Exists(Path.Combine(JobDir, "b.bin")));
    }

    [Fact]
    public void TheCountCapIsEnforced()
    {
        var r = Receive(Enumerable.Range(1, 4).SelectMany(i => Frame($"f{i}.txt", Bytes(3))), limits: new FetchLimits(2, 1000, 1000));
        Assert.Equal(["f1.txt", "f2.txt"], r.Fetched.Select(f => f.Path));
        Assert.Equal("over the file count cap", Reason(r, "f3.txt"));
        Assert.Equal("over the file count cap", Reason(r, "f4.txt"));
    }

    [Fact]
    public void AFileNoPatternAskedForIsRefused()
    {
        var r = Receive(Frame("TestResults/a.trx", Bytes(5)).Concat(Frame("secrets.txt", Bytes(5))).Concat(Frame("TestResults/sub/b.trx", Bytes(5))), ["TestResults/*.trx"]);
        Assert.Equal(["TestResults/a.trx"], r.Fetched.Select(f => f.Path));
        Assert.Equal("not matched by any --fetch pattern", Reason(r, "secrets.txt"));
        Assert.Equal("not matched by any --fetch pattern", Reason(r, "TestResults/sub/b.trx")); // * never crosses a folder
    }

    [Fact]
    public void PatternsMatchCaseInsensitivelyOnlyForAWindowsWorker()
    {
        Assert.Single(Receive(Frame("testresults/A.TRX", Bytes(5)), ["TestResults/*.trx"], ignoreCase: true).Fetched);
        Directory.Delete(JobDir, recursive: true);
        Assert.Empty(Receive(Frame("testresults/A.TRX", Bytes(5)), ["TestResults/*.trx"]).Fetched);
    }

    [Fact]
    public void AFrameErrorDeletesThePartialFileAndTheNextFileStillArrives()
    {
        var lines = new List<string> { $"LQF1 FILE 100 {B64("cut.bin")}", Convert.ToBase64String(Bytes(57)), "not base64 at all!" };
        lines.AddRange(Frame("next.bin", Bytes(10)));
        lines.Add($"LQF1 FILE 100 {B64("cut2.bin")}");
        lines.Add(Convert.ToBase64String(Bytes(57)));
        lines.AddRange(Frame("next2.bin", Bytes(10))); // a new header without END before it

        var r = Receive(lines);

        Assert.Equal(["next.bin", "next2.bin"], r.Fetched.Select(f => f.Path));
        Assert.Contains("frame error", Reason(r, "cut.bin"), StringComparison.Ordinal);
        Assert.Contains("frame error", Reason(r, "cut2.bin"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(JobDir, "cut.bin")));
        Assert.False(File.Exists(Path.Combine(JobDir, "cut2.bin")));
    }

    [Fact]
    public void AStreamThatEndsInsideAFileDeletesItAndReportsAnIncompleteFetch()
    {
        var r = Receive([$"LQF1 FILE 100 {B64("cut.bin")}", Convert.ToBase64String(Bytes(57))], done: false);
        Assert.Empty(r.Fetched);
        Assert.Contains("ended inside", Reason(r, "cut.bin"), StringComparison.Ordinal);
        Assert.Contains("incomplete", r.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(JobDir, "cut.bin")));
    }

    [Fact]
    public void BadHeadersOverlongDataLinesAndLinesAfterDoneAreRefused()
    {
        var lines = new List<string>
        {
            "LQF1 FILE ten " + B64("a.txt"),
            "LQF1 END " + Hash([]),
            "LQF1 FILE 5 not-base64!",
            "LQF1 END " + Hash([]),
            $"LQF1 FILE 4000 {B64("long.bin")}",
            Convert.ToBase64String(Bytes(3075)), // 4100 characters: over the line limit
            "LQF1 END " + Hash(Bytes(3075)),
            "some worker warning between frames",
            "LQF1 DONE",
        };
        lines.AddRange(Frame("after-done.txt", Bytes(5)));

        var r = Receive(lines, done: false);

        Assert.Empty(r.Fetched);
        Assert.Null(r.Error);
        Assert.Equal(3, r.Skipped.Count);
        Assert.All(r.Skipped, s => Assert.Contains("frame error", s.Reason, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(JobDir, "after-done.txt")));
    }

    [Fact]
    public void WorkerSkipsAndErrorsAreReportedWithKnownReasonsOnly()
    {
        var r = Receive([$"LQF1 SKIP link {B64("TestResults/passwd")}", $"LQF1 SKIP \u001b[2Jevil {B64("x")}", "LQF1 ERROR tool-base64"]);
        Assert.Equal("symbolic link, junction or reparse point (never followed)", Reason(r, "TestResults/passwd"));
        Assert.Equal("skipped by the worker", Reason(r, "x"));
        Assert.Equal("the worker could not list the files: tool-base64", r.Error);
    }

    [Fact]
    public void AnExistingFileIsNeverOverwritten()
    {
        Directory.CreateDirectory(JobDir);
        File.WriteAllText(Path.Combine(JobDir, "a.txt"), "mine");
        var r = Receive(Frame("a.txt", Bytes(10)));
        Assert.Empty(r.Fetched);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(JobDir, "a.txt")));
    }

    [Fact]
    public void ALinkInTheTargetPathIsNeverWrittenThrough()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(JobDir);
        MakeDirLink(Path.Combine(JobDir, "sub"), outside);

        var r = Receive(Frame("sub/x.txt", Bytes(10)).Concat(Frame("sub/deeper/y.txt", Bytes(10))));

        Assert.Empty(r.Fetched);
        Assert.Contains("link", Reason(r, "sub/x.txt"), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void AnOutputFolderThatIsALinkIsNeverWrittenThrough()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        MakeDirLink(JobDir, outside);

        var r = Receive(Frame("x.txt", Bytes(10)));

        Assert.Empty(r.Fetched);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    /// <summary>A pattern that would make a backtracking engine explode against a long name is matched in linear time, never timing out.</summary>
    [Fact]
    public void AManyStarPatternAgainstALongNameCannotHangTheMatcher()
    {
        var pattern = string.Concat(Enumerable.Repeat("*a", 40)) + "*b";
        var name = new string('a', 255);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = Receive(Frame(name, Bytes(3)).Concat(Frame("ok" + new string('a', 40) + "b", Bytes(3))), [pattern]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "took " + watch.Elapsed);
        Assert.Equal("not matched by any --fetch pattern", Reason(r, name));
        Assert.Single(r.Fetched);
    }

    /// <summary>The Windows script sends 3072-byte chunks: exactly 4096 base64 characters per line, the most a data line may hold.</summary>
    [Fact]
    public void AnExactly4096CharacterDataLineIsAccepted()
    {
        var data = Bytes(3072 * 3 + 5);
        var frame = Frame("w.bin", data, chunk: 3072).ToList();
        Assert.Equal(4096, frame[1].Length);
        var r = Receive(frame);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(JobDir, "w.bin")));
        Assert.Single(r.Fetched);
    }

    /// <summary>The file is written as data lines arrive, not held until END: more than the write buffer is on disk before the frame ends.</summary>
    [Fact]
    public void FilesAreStreamedToDisk()
    {
        var data = Bytes(300_000);
        using var rx = new FetchReceiver(JobDir, ["**"], FetchLimits.Default, ignoreCase: false);
        var frame = Frame("big.bin", data, chunk: 3072).ToList();
        foreach (var l in frame.Take(frame.Count - 1))
        {
            rx.OnLine(l);
        }

        Assert.True(new FileInfo(Path.Combine(JobDir, "big.bin")).Length >= 200_000);
        rx.OnLine(frame[^1]);
        rx.OnLine("LQF1 DONE");
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(JobDir, "big.bin")));
        Assert.Single(rx.Finish().Fetched);
    }

    [Fact]
    public void AnEmptyOutputFolderIsRemovedAgain()
    {
        var r = Receive([]);
        Assert.Empty(r.Fetched);
        Assert.False(Directory.Exists(JobDir));
    }

    /// <summary>A directory symlink (Linux, macOS) or a junction (Windows: needs no privilege).</summary>
    internal static void MakeDirLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var (code, output) = Shells.Exec("cmd", ["/c", "mklink", "/J", link.Replace('/', '\\'), target.Replace('/', '\\')]);
            Assert.True(code == 0, output);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
