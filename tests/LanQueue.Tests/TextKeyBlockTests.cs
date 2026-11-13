using System.Diagnostics;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.10.1: a private key replaced in place in an ordinary text file (same BEGIN line, new body) is found
/// (Payload/GitPayloadBuilder.TextKeyScan.cs, <see cref="BlobScan.ChangedBlockStarts"/>). The overlay diff shows only the changed body lines,
/// so the BEGIN line is compared from the old and new blobs of the files a git grep finds a BEGIN line in.
/// </summary>
public sealed class TextKeyBlockTests : IDisposable
{
    private const int TestTimeout = 120_000;

    /// <summary>"-----BEGIN ", split from the key type so no scanner flags this file.</summary>
    private const string Begin = "-----BEGIN ";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "lanq-textkey-" + Guid.NewGuid().ToString("N"));

    public TextKeyBlockTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".out/\n");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Commit();
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        TestTree.Delete(_repo);
    }

    private void Commit()
    {
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "c");
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

    private void Write(string name, string text)
    {
        var path = Path.Combine(_repo, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<JobPayload> Snapshot() =>
        new GitPayloadBuilder().BuildAsync("j" + Guid.NewGuid().ToString("N")[..8], _repo, Path.Combine(_repo, ".out"), PayloadKinds.None, CancellationToken.None);

    private static bool Has(JobPayload p, string path) =>
        p.TextKeyFindings.Any(f => f.Rule == "private-key-block" && f.Where == $"uncommitted file {path} (key block changed in place)");

    private static string Show(JobPayload p) => string.Join("; ", p.TextKeyFindings);

    /// <summary>What the diff scan alone finds (the gap: nothing, for a key replaced in place).</summary>
    private static IReadOnlyList<SecretFinding> DiffFindings(JobPayload p) => SecretScanner.ScanText(p.OverlayDiff, "uncommitted changes");

    private static string Body(char c, int changedLine = -1, string indent = "", string eol = "\n") =>
        string.Concat(Enumerable.Range(0, 4).Select(k => indent + new string(k == changedLine ? (char)(c + 1) : c, 64) + eol));

    private static string Pem(char body, string tail = "tail", string type = "RSA PRIVATE KEY", string eol = "\n") =>
        "config" + eol + Begin + type + "-----" + eol + Body(body, eol: eol) + "-----END " + type + "-----" + eol + tail + eol;

    [Fact(Timeout = TestTimeout)]
    public async Task KeyReplacedInPlaceInATextFileIsFound()
    {
        static string Block(string begin, string head, string body, string end) => "config\n" + begin + "\n" + head + body + end + "\ntail\n";
        (string Name, string Old, string New)[] cases =
        [
            ("pem.txt", Pem('A'), Pem('B')),
            ("openssh.txt", Block(Begin + "OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n", Body('C'), "-----END OPENSSH PRIVATE KEY-----"),
                Block(Begin + "OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n", Body('D'), "-----END OPENSSH PRIVATE KEY-----")),
            ("pgp.asc.txt", Block(Begin + "PGP PRIVATE KEY BLOCK-----", "Version: GnuPG v2\n\n", Body('E'), "-----END PGP PRIVATE KEY BLOCK-----"),
                Block(Begin + "PGP PRIVATE KEY BLOCK-----", "Version: GnuPG v2\n\n", Body('F'), "-----END PGP PRIVATE KEY BLOCK-----")),
            ("enc.txt", Block(Begin + "RSA PRIVATE KEY-----", "Proc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0123456789ABCDEF0123456789ABCDEF\n\n", Body('G'), "-----END RSA PRIVATE KEY-----"),
                Block(Begin + "RSA PRIVATE KEY-----", "Proc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0123456789ABCDEF0123456789ABCDEF\n\n", Body('H'), "-----END RSA PRIVATE KEY-----")),
            ("middle.txt", Block(Begin + "EC PRIVATE KEY-----", "", Body('J'), "-----END EC PRIVATE KEY-----"),
                Block(Begin + "EC PRIVATE KEY-----", "", Body('J', changedLine: 2), "-----END EC PRIVATE KEY-----")),
        ];
        foreach (var (name, old, _) in cases)
        {
            Write(name, old);
        }

        Commit();
        foreach (var (name, _, replaced) in cases)
        {
            Write(name, replaced);
        }

        var p = await Snapshot();

        Assert.Empty(DiffFindings(p)); // the gap: the diff shows only the new body lines
        Assert.All(cases, c => Assert.True(Has(p, c.Name), c.Name + ": " + Show(p)));
        Assert.Equal(cases.Length * 2, p.TextBlobsRead);
        Assert.Empty(p.ScanSkipped);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task UnchangedKeyWithUnrelatedEditIsNotFlagged()
    {
        Write("deploy.txt", Pem('A'));
        Commit();
        Write("deploy.txt", Pem('A', tail: "tail changed"));

        var p = await Snapshot();

        Assert.Empty(p.TextKeyFindings);
        Assert.Empty(DiffFindings(p));
        Assert.Equal(2, p.TextBlobsRead); // the grep gate matched: both versions compared
    }

    /// <summary>An indented key (a YAML block scalar) ends at its indented END line; one replaced in place is still found.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task IndentedTextKeyEndsAtItsEndLine()
    {
        static string Yaml(char body, string tail) =>
            "deploy:\n  key: |\n    " + Begin + "RSA PRIVATE KEY-----\n" + Body(body, indent: "    ") + "    -----END RSA PRIVATE KEY-----\nother: " + tail + "\n";
        Write("values.yml", Yaml('A', "one"));
        Commit();

        Write("values.yml", Yaml('A', "two"));
        Assert.Empty((await Snapshot()).TextKeyFindings); // an unchanged key, an unrelated edit after it

        Write("values.yml", Yaml('B', "one"));
        var p = await Snapshot();
        Assert.True(Has(p, "values.yml"), Show(p));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CrlfKeyReplacedInPlaceIsFound()
    {
        Write("deploy.txt", Pem('A', eol: "\r\n"));
        Commit();

        Write("deploy.txt", Pem('A', tail: "tail changed", eol: "\r\n"));
        Assert.Empty((await Snapshot()).TextKeyFindings);

        Write("deploy.txt", Pem('B', eol: "\r\n"));
        var p = await Snapshot();
        Assert.Empty(DiffFindings(p));
        Assert.True(Has(p, "deploy.txt"), Show(p));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task KeyAtFileStartIsFoundAndUnterminatedKeyIsNot()
    {
        static string Start(char c) => Begin + "RSA PRIVATE KEY-----\n" + Body(c) + "-----END RSA PRIVATE KEY-----\n";
        static string Unterminated(char c) => "config\n" + Begin + "EC PRIVATE KEY-----\n" + Body(c).TrimEnd('\n');
        Write("start.txt", Start('A'));
        Write("end.txt", Unterminated('C'));
        Commit();

        Write("start.txt", Start('B'));
        Write("end.txt", Unterminated('D'));
        var p = await Snapshot();

        Assert.True(Has(p, "start.txt"), Show(p));
        Assert.False(Has(p, "end.txt"), Show(p)); // no END line: not rescanned in a text file (documented limit)
        Assert.Single(p.TextKeyFindings);
    }

    /// <summary>A block whose END line is more than 512 lines down is not a key block: a change inside it is not rescanned (documented).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task ChangeBeyondBlockCapIsNotFlagged()
    {
        static string Long(string last) =>
            Begin + "RSA PRIVATE KEY-----\n" + string.Concat(Enumerable.Range(0, 600).Select(i => $"line {i}\n")) + last + "\n-----END RSA PRIVATE KEY-----\n";
        Write("notes.txt", Long("end"));
        Commit();

        Write("notes.txt", Long("end changed"));
        var p = await Snapshot();

        Assert.Empty(p.TextKeyFindings);
        Assert.Equal(2, p.TextBlobsRead);
    }

    /// <summary>A renamed file is compared with its old version even when diff.renames is off: lanq pins -M on the text path.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task RenamedFileWithReplacedKeyIsFound()
    {
        Git("config", "diff.renames", "false");
        var filler = string.Concat(Enumerable.Range(0, 40).Select(i => $"setting{i}: value {i}\n"));
        Write("old/deploy.txt", filler + Pem('A'));
        Commit();
        File.Delete(Path.Combine(_repo, "old", "deploy.txt"));
        Write("new/deploy.txt", filler + Pem('B'));

        var p = await Snapshot();

        Assert.Empty(DiffFindings(p)); // with -M the diff shows only the replaced body
        Assert.Contains("rename from old/deploy.txt", p.OverlayDiff, StringComparison.Ordinal);
        Assert.Contains("new/deploy.txt", p.ChangedFiles);
        Assert.True(Has(p, "new/deploy.txt"), Show(p));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TextFileWithoutBeginLineReadsNoBlobs()
    {
        Write("deploy.txt", Pem('A'));
        Commit();
        Write("a.txt", "one\ntwo\n");

        var p = await Snapshot();

        Assert.Equal((0, 0), (p.TextBlobsRead, p.TextKeyFindings.Count));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AddedTextFileReadsNoBlobs()
    {
        Write("fresh.txt", Pem('A'));

        var p = await Snapshot();

        Assert.Contains(DiffFindings(p), f => f.Rule == "private-key-block"); // every line of an added file is in the diff
        Assert.Equal((0, 0), (p.TextBlobsRead, p.TextKeyFindings.Count));
    }

    /// <summary>A key added under a new BEGIN line is the diff's: the text scan reads the file but reports nothing more.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task DiffFileKeyIsNotCountedTwice()
    {
        Write("deploy.txt", Pem('A'));
        Commit();
        Write("deploy.txt", Pem('A') + Pem('B', type: "OPENSSH PRIVATE KEY"));

        var p = await Snapshot();

        Assert.Single(DiffFindings(p), f => f.Rule == "private-key-block");
        Assert.Empty(p.TextKeyFindings);
        Assert.Equal(2, p.TextBlobsRead);
    }

    /// <summary>
    /// Lines are matched by content: a second key added under a BEGIN line identical to an old one is reported by the diff and by the text
    /// scan. Two findings for one key are harmless (either refuses); this pins that it is never zero.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task SameHeaderAddedKeyMayBeCountedTwice()
    {
        Write("deploy.txt", Pem('A'));
        Commit();
        Write("deploy.txt", Pem('B') + Pem('A'));

        var p = await Snapshot();

        Assert.Single(DiffFindings(p), f => f.Rule == "private-key-block");
        Assert.True(Has(p, "deploy.txt"), Show(p));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TextFindingsNeverEchoKeyContent()
    {
        Write("deploy.txt", Pem('A'));
        Commit();
        Write("deploy.txt", Pem('Q'));

        var p = await Snapshot();
        var r = JobRunner.SecretRefusal("j1", p.TextKeyFindings);

        Assert.Equal((JobStatus.Rejected, 2, RejectKind.Secret), (r.Status, r.CliExitCode, r.RejectKind));
        Assert.Contains("private-key-block in uncommitted file deploy.txt (key block changed in place)", r.Message, StringComparison.Ordinal);
        Assert.Contains("unchanged BEGIN line", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("QQQQ", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RSA PRIVATE", r.Message, StringComparison.Ordinal);
        Assert.All(p.TextKeyFindings, f => Assert.DoesNotContain("QQQQ", f.Rule + f.Where, StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedBlockStartsReturnsOnlyRescannedBeginLines()
    {
        var begin = Begin + "RSA PRIVATE KEY-----";
        var other = Begin + "EC PRIVATE KEY-----";
        string[] old = ["config", begin, "AAAA", "AAAA2", "-----END RSA PRIVATE KEY-----", other, "CCCC", "-----END EC PRIVATE KEY-----", "tail"];
        string[] changed = ["config", begin, "BBBB", "AAAA2", "-----END RSA PRIVATE KEY-----", other, "CCCC", "-----END EC PRIVATE KEY-----", "tail 2"];

        Assert.Equal([begin], BlobScan.ChangedBlockStarts(changed, old));

        // NewContent (the binary scan's) is the new lines plus exactly these BEGIN lines.
        Assert.Equal([begin, "BBBB", "tail 2"], BlobScan.NewContent(changed, old));
        Assert.Empty(BlobScan.ChangedBlockStarts(old, old));

        // A new BEGIN line is the diff's, not this scan's; a one-line key holding its END line has no block.
        string[] added = ["config", Begin + "OPENSSH PRIVATE KEY-----", "DDDD", "-----END OPENSSH PRIVATE KEY-----"];
        Assert.Empty(BlobScan.ChangedBlockStarts(added, ["config"]));
        var oneLine = "{\"k\": \"" + begin + "\\nMII\\n-----END RSA PRIVATE KEY-----\"}";
        Assert.Empty(BlobScan.ChangedBlockStarts([oneLine, "x2"], [oneLine, "x1"]));

        // Only terminated blocks: a BEGIN with no END line is never rescanned; a changed END line is inside the block.
        Assert.Empty(BlobScan.ChangedBlockStarts([begin, "BBBB", "tail"], [begin, "AAAA", "tail"]));
        Assert.Equal([begin], BlobScan.ChangedBlockStarts([begin, "AAAA", "-----END EC PRIVATE KEY-----"], [begin, "AAAA", "-----END RSA PRIVATE KEY-----"]));
    }

    /// <summary>A key-header string in code (a parser, a scanner, a test) has no END line: editing a few lines below it is never refused.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task LoneBeginLiteralInCodeIsNotFlagged()
    {
        static string Code(string body) =>
            "namespace Demo;\n\npublic static class Keys\n{\n    public const string Header = \"" + Begin + "OPENSSH PRIVATE KEY-----\";\n\n"
            + "    public static bool Looks(string s) => s.StartsWith(Header);\n\n    public static int Size() => " + body + ";\n}\n";
        Write("Keys.cs", Code("1"));
        Commit();
        Write("Keys.cs", Code("2"));

        var p = await Snapshot();

        Assert.Empty(DiffFindings(p));
        Assert.Empty(p.TextKeyFindings);
        Assert.Equal(2, p.TextBlobsRead); // compared, and the lone header is no block
    }

    [Fact]
    public void ParseRawReadsRenameEntries()
    {
        var (a, b, c, d) = (new string('a', 40), new string('b', 40), new string('c', 40), new string('d', 40));
        var raw = GitPayloadBuilder.ParseRaw(
            $":100644 100644 {a} {b} R086\0old/x.txt\0new/x.txt\0:100644 100644 {c} {d} M\0m.txt\0:100644 100644 {a} {c} C075\0src.txt\0copy.txt\0");

        Assert.Equal(new GitPayloadBuilder.RawEntry("100644", "100644", a, b, 'R', "old/x.txt"), raw["new/x.txt"]);
        Assert.Equal(new GitPayloadBuilder.RawEntry("100644", "100644", c, d, 'M'), raw["m.txt"]);
        Assert.Equal(new GitPayloadBuilder.RawEntry("100644", "100644", a, c, 'C', "src.txt"), raw["copy.txt"]);
        Assert.False(raw.ContainsKey("old/x.txt"));
        Assert.False(raw.ContainsKey("src.txt"));
    }

    [Fact]
    public void ParseNumstatReadsRenamesAndBinaries()
    {
        var n = GitPayloadBuilder.ParseNumstat("3\t1\ta b.txt\0" + "-\t-\tlogo.png\0" + "2\t2\t\0old.txt\0new.txt\0" + "0\t4\tshrunk.txt\0");

        Assert.Equal(3, n["a b.txt"]);
        Assert.Null(n["logo.png"]);
        Assert.Equal(2, n["new.txt"]);
        Assert.False(n.ContainsKey("old.txt"));
        Assert.Equal(0, n["shrunk.txt"]);
    }

    [Fact]
    public void PlanTextReadsSkipsWhatTheDiffShowsAndFailsClosed()
    {
        var zero = new string('0', 40);
        var (a, b, c) = (new string('a', 40), new string('b', 40), new string('c', 40));
        var raw = GitPayloadBuilder.ParseRaw(
            $":100644 100644 {a} {b} M\0mod.txt\0:000000 100644 {zero} {b} A\0add.txt\0:100644 100644 {a} {c} R090\0was.txt\0moved.txt\0"
            + $":100644 100644 {a} {b} M\0bin.dat\0:100644 100644 {a} {b} M\0shrunk.txt\0:100644 100644 {a} {b} M\0ghost.txt\0"
            + $":120000 100644 {a} {b} T\0link.txt\0:100644 100644 {a} {b} X\0odd.txt\0");
        var numstat = new Dictionary<string, long?>
        {
            ["mod.txt"] = 1, ["add.txt"] = 9, ["moved.txt"] = 2, ["bin.dat"] = null, ["shrunk.txt"] = 0, ["link.txt"] = 5, ["odd.txt"] = 1,
        };

        var (targets, incomplete) = GitPayloadBuilder.PlanTextReads(
            raw, numstat, ["mod.txt", "add.txt", "moved.txt", "bin.dat", "shrunk.txt", "ghost.txt", "link.txt", "odd.txt", "unchanged.txt"]);

        Assert.Equal([new GitPayloadBuilder.BlobTarget("mod.txt", b, a), new GitPayloadBuilder.BlobTarget("moved.txt", c, a)], targets);
        Assert.Equal(
            [new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere("ghost.txt")), new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere("odd.txt"))],
            incomplete);
    }

    /// <summary>Sizes are checked before anything is read (git cat-file --batch refuses nothing, lanq's reader refuses over 32 MB).</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TextOverCapIsListedNotRead()
    {
        Write("small.txt", Pem('A'));
        Write("small2.txt", Pem('B'));
        Write("big.txt", Pem('C') + new string('x', 4096) + "\n");
        var small = Git("hash-object", "-w", "small.txt").Trim();
        var small2 = Git("hash-object", "-w", "small2.txt").Trim();
        var big = Git("hash-object", "-w", "big.txt").Trim();
        var caps = new BlobScan.Caps(BlobScan.BinaryFileCap, BlobScan.BinaryTotalCap, 2048, 1024 * 1024);

        var r = await GitPayloadBuilder.ScanTextBlobsAsync(
            _repo, [new GitPayloadBuilder.BlobTarget("grown.txt", big, small), new GitPayloadBuilder.BlobTarget("shrank.txt", small, big), new GitPayloadBuilder.BlobTarget("ok.txt", small2, small)],
            CancellationToken.None, caps);

        var bigSize = new FileInfo(Path.Combine(_repo, "big.txt")).Length;
        Assert.Equal([new ScanSkip("grown.txt", bigSize, BlobScan.TextOverCap), new ScanSkip("shrank.txt", bigSize, BlobScan.TextOverCap)], r.Skipped.OrderBy(s => s.Path, StringComparer.Ordinal));
        Assert.Equal(2, r.BlobsRead); // only ok.txt
        Assert.Single(r.Findings, f => f.Where == BlobScan.TextWhere("ok.txt"));

        // The total: past it the rest is listed too.
        var tight = caps with { TextTotal = new FileInfo(Path.Combine(_repo, "small.txt")).Length * 2 };
        var t = await GitPayloadBuilder.ScanTextBlobsAsync(
            _repo, [new GitPayloadBuilder.BlobTarget("one.txt", small2, small), new GitPayloadBuilder.BlobTarget("two.txt", small, small2)], CancellationToken.None, tight);
        Assert.Single(t.Skipped);
        Assert.Equal(BlobScan.TextOverCap, t.Skipped[0].Reason);
        Assert.Equal(2, t.BlobsRead);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MissingTextObjectFailsClosed()
    {
        var ghost = new string('e', 40);
        var real = Git("hash-object", "-w", "a.txt").Trim();

        var r = await GitPayloadBuilder.ScanTextBlobsAsync(
            _repo, [new GitPayloadBuilder.BlobTarget("half.txt", real, ghost), new GitPayloadBuilder.BlobTarget("gone.txt", ghost, real)], CancellationToken.None);

        Assert.Equal(
            [new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere("gone.txt")), new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere("half.txt"))],
            r.Findings.OrderBy(f => f.Where, StringComparer.Ordinal));
        var refusal = JobRunner.SecretRefusal("j1", r.Findings);
        Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("changed text file with a private-key block", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("take the key out", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Text key findings refuse the run like any secret, before anything is sent.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task TextKeyFindingRefusesTheRun()
    {
        var ssh = new PoolSsh();
        var payloads = new TextKeyPayloads(new RunnerTests.FakePayloads(""));
        var runner = new JobRunner(
            ConfigLoader.Parse(ShardTests.PoolYaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_repo, ".out", "state")),
            Path.Combine(_repo, ".out", "tmp"), _ => { });

        var r = await runner.RunAsync(new JobRequest(["node"], ["npx", "jest"], ".", _repo, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal((JobStatus.Rejected, RejectKind.Secret), (r.Status, r.RejectKind));
        Assert.Contains("private-key-block in uncommitted file x.txt (key block changed in place)", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Uploads);
    }

    private sealed class TextKeyPayloads(RunnerTests.FakePayloads inner) : IPayloadBuilder
    {
        public async Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) =>
            await inner.BuildAsync(jobId, repoDir, outputDir, kinds, ct) with { TextKeyFindings = [new SecretFinding("private-key-block", BlobScan.TextWhere("x.txt"))] };

        public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => inner.BuildArchiveAsync(jobId, repoDir, outputDir, sha, ct);

        public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
            inner.BuildBundleAsync(jobId, repoDir, outputDir, sha, baseSha, ct);
    }

    /// <summary>A text file over the cap reaches scanSkipped with its own documented reason.</summary>
    [Fact]
    public void TextOverCapReasonIsDocumented()
    {
        var json = Assert.Single(Cli.ScanSkippedJson([new ScanSkip("keys.txt", 40L * 1024 * 1024, BlobScan.TextOverCap)])!);
        Assert.Equal("text_over_cap", json.Reason);
        var doc = File.ReadAllText(Path.Combine(T.RepoRoot, "docs", "JSON.md"));
        Assert.Contains("`text_over_cap`", doc, StringComparison.Ordinal);
    }
}
