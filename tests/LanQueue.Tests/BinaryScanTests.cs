using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Payload;
using LanQueue.Policy;
using Xunit;
using Xunit.Abstractions;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.2: the secret scan reads the content of uncommitted files git shows as binary (Payload/GitPayloadBuilder.BinaryScan.cs,
/// Policy/BlobScan.cs), under caps, and lists what it did not read in full in scanSkipped.
/// </summary>
public sealed class BinaryScanTests : IDisposable
{
    private const int TestTimeout = 120_000;

    /// <summary>An AWS-style access key id (AKIA + 16 upper-case letters or digits); fake, built so no scanner flags this file.</summary>
    private static readonly string AwsKey = "AKIA" + "QWERTYUIOPASDFGH";

    /// <summary>A GitHub-style token (ghp_ + 36 characters); fake.</summary>
    private static readonly string GitHubToken = "ghp" + "_" + new string('a', 36);

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "lanq-binscan-" + Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _out;

    public BinaryScanTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_repo, ".gitattributes"), "*.dat -diff\n*.blob binary\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".out/\n");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Commit();
    }

    public void Dispose()
    {
        // git writes its objects read-only.
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

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_repo, name), text);

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_repo, name), bytes);

    private Task<JobPayload> Snapshot() =>
        new GitPayloadBuilder().BuildAsync("j" + Guid.NewGuid().ToString("N")[..8], _repo, Path.Combine(_repo, ".out"), PayloadKinds.None, CancellationToken.None);

    /// <summary>Bytes that git and lanq both take for a real binary (a NUL early on), random but repeatable, with no printable run by chance.</summary>
    private static byte[] Binary(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(data[i] & 0x0F); // control bytes only: no 8-bit or UTF-16 run of printable characters
        }

        data[0] = 0;
        return data;
    }

    private static byte[] WithText(byte[] data, int at, string text)
    {
        Encoding.ASCII.GetBytes(text).CopyTo(data, at);
        return data;
    }

    private static bool Has(JobPayload p, string rule, string path) =>
        p.BinaryFindings.Any(f => f.Rule == rule && f.Where == $"uncommitted file {path} (binary per git)");

    [Theory(Timeout = TestTimeout)]
    [InlineData("secret.dat")] // -diff
    [InlineData("secret.blob")] // binary
    public async Task AttributeMaskedTextIsScanned(string name)
    {
        Write(name, "config\nkey = " + AwsKey + "\n");

        var p = await Snapshot();

        Assert.DoesNotContain(AwsKey, p.OverlayDiff, StringComparison.Ordinal); // the gap: the diff only says "Binary files ... differ"
        Assert.True(Has(p, "aws-access-key", name), string.Join("; ", p.BinaryFindings));
        Assert.Empty(p.ScanSkipped);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Utf16WithBomIsScanned()
    {
        File.WriteAllText(Path.Combine(_repo, "notes.txt"), "line one\r\ntoken " + GitHubToken + "\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var p = await Snapshot();

        Assert.True(Has(p, "github-token", "notes.txt"), string.Join("; ", p.BinaryFindings));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Utf16LeWithoutBomIsFoundInBinaryPass()
    {
        var data = Binary(4096);
        Encoding.Unicode.GetBytes("aws_key " + AwsKey).CopyTo(data, 1001); // odd offset: the second alignment
        Write("app.exe", data);

        var p = await Snapshot();

        Assert.True(Has(p, "aws-access-key", "app.exe"), string.Join("; ", p.BinaryFindings));
        Assert.Equal(BlobScan.Kind.Binary, BlobScan.Classify(data));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SecretInRealBinaryUnderCapIsFound()
    {
        Write("lib.so", WithText(Binary(2 * 1024 * 1024), 700_000, "-----BEGIN RSA PRIVATE KEY-----"));

        var p = await Snapshot();

        Assert.True(Has(p, "private-key-block", "lib.so"), string.Join("; ", p.BinaryFindings));
        Assert.Empty(p.ScanSkipped);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BinaryOverFileCapIsSkippedAndListed()
    {
        var nine = WithText(Binary((int)BlobScan.BinaryFileCap + 1024), 100, "-----BEGIN RSA PRIVATE KEY-----");
        var huge = WithText(Binary((int)BlobScan.TextCap + 1024, seed: 2), 100, "-----BEGIN RSA PRIVATE KEY-----"); // over every cap: only peeked
        Write("big.bin", nine);
        Write("huge.bin", huge);

        var p = await Snapshot();

        Assert.Empty(p.BinaryFindings);
        Assert.Equal(
            [new ScanSkip("big.bin", nine.Length, BlobScan.BinaryOverFileCap), new ScanSkip("huge.bin", huge.Length, BlobScan.BinaryOverFileCap)],
            p.ScanSkipped);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TotalCapListsTheRest()
    {
        // 9 binaries of 7.5 MB: the first 8 (60 MB) are scanned, the 9th would pass 64 MB and is listed. The secret is in the 9th only.
        const int Size = 7 * 1024 * 1024 + 512 * 1024;
        for (var i = 1; i <= 9; i++)
        {
            Write($"b{i}.bin", i == 9 ? WithText(Binary(Size, i), 10, "-----BEGIN RSA PRIVATE KEY-----") : Binary(Size, i));
        }

        var p = await Snapshot();

        Assert.Empty(p.BinaryFindings);
        Assert.Equal([new ScanSkip("b9.bin", Size, BlobScan.BinaryOverTotalCap)], p.ScanSkipped);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MaskedTextOverCapIsListed()
    {
        var sb = new StringBuilder();
        var line = new string('x', 1023) + "\n";
        while (sb.Length <= BlobScan.TextCap)
        {
            sb.Append(line);
        }

        sb.Append("key = ").Append(AwsKey).Append('\n');
        Write("data.dat", sb.ToString());

        // Under the text cap, a masked file is scanned whole (no binary cap applies): 10 MB of text.
        Write("mid.dat", new string('y', 10 * 1024 * 1024) + "\nkey = " + AwsKey + "\n");

        var p = await Snapshot();

        Assert.Equal([new ScanSkip("data.dat", sb.Length, BlobScan.MaskedTextOverCap)], p.ScanSkipped);
        Assert.True(Has(p, "aws-access-key", "mid.dat"));
        Assert.False(Has(p, "aws-access-key", "data.dat"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ModifiedFileScansOnlyNewLines()
    {
        Write("keys.dat", "header\nkey = " + AwsKey + "\n");
        Commit(); // HEAD already holds this line: it is not re-flagged

        Write("keys.dat", "header\nkey = " + AwsKey + "\nanother harmless line\n");
        var unchanged = await Snapshot();
        Assert.Empty(unchanged.BinaryFindings);
        Assert.Equal(2, unchanged.BlobsRead); // the old blob and the new one

        Write("keys.dat", "header\nkey = " + AwsKey + "\ntoken = " + GitHubToken + "\n");
        var added = await Snapshot();
        Assert.True(Has(added, "github-token", "keys.dat"));
        Assert.False(Has(added, "aws-access-key", "keys.dat"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ModifiedBinaryScansOnlyNewStrings()
    {
        Write("tool.bin", WithText(Binary(64 * 1024), 100, "aws " + AwsKey));
        Commit();

        Write("tool.bin", WithText(WithText(Binary(64 * 1024), 100, "aws " + AwsKey), 9000, "plain words"));
        Assert.Empty((await Snapshot()).BinaryFindings);

        Write("tool.bin", WithText(WithText(Binary(64 * 1024), 100, "aws " + AwsKey), 9000, "tok " + GitHubToken));
        var p = await Snapshot();
        Assert.True(Has(p, "github-token", "tool.bin"));
        Assert.False(Has(p, "aws-access-key", "tool.bin"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RenamedMaskedFileIsScanned()
    {
        Write("old.dat", "key = " + AwsKey + "\n");
        Commit();
        File.Move(Path.Combine(_repo, "old.dat"), Path.Combine(_repo, "new.dat"));

        var p = await Snapshot();

        // --no-renames: a delete of old.dat (nothing to read) and an add of new.dat (read whole), so the moved secret is found.
        Assert.True(Has(p, "aws-access-key", "new.dat"), string.Join("; ", p.BinaryFindings));
        Assert.False(Has(p, "aws-access-key", "old.dat"));
    }

    [Fact]
    public void ModeOnlyChangeReadsNothing()
    {
        var oid = new string('a', 40);
        var raw = GitPayloadBuilder.ParseRaw($":100644 100755 {oid} {oid} M\0tool.bin\0");

        var (targets, incomplete) = GitPayloadBuilder.PlanReads(raw, ["tool.bin"]);

        Assert.Empty(targets);
        Assert.Empty(incomplete);
    }

    [Fact]
    public void SymlinkAndGitlinkSkipped()
    {
        var zero = new string('0', 40);
        var (a, b, c, d) = (new string('a', 40), new string('b', 40), new string('c', 40), new string('d', 40));
        var raw = GitPayloadBuilder.ParseRaw(
            $":000000 120000 {zero} {a} A\0link.bin\0:160000 160000 {b} {c} M\0sub\0:100644 000000 {d} {zero} D\0gone.bin\0:000000 100644 {zero} {d} A\0new.bin\0");

        var (targets, incomplete) = GitPayloadBuilder.PlanReads(raw, ["link.bin", "sub", "gone.bin", "new.bin"]);

        Assert.Equal([new GitPayloadBuilder.BlobTarget("new.bin", d, null)], targets);
        Assert.Empty(incomplete);
    }

    [Fact]
    public void ModifiedFromASymlinkReadsTheNewBlobWhole()
    {
        var (a, b) = (new string('a', 40), new string('b', 40));
        var raw = GitPayloadBuilder.ParseRaw($":120000 100644 {a} {b} T\0x.bin\0:100644 100644 {a} {b} M\0y.bin\0");

        var (targets, _) = GitPayloadBuilder.PlanReads(raw, ["x.bin", "y.bin"]);

        Assert.Equal([new GitPayloadBuilder.BlobTarget("x.bin", b, null), new GitPayloadBuilder.BlobTarget("y.bin", b, a)], targets);
    }

    [Fact]
    public void BinaryPathMissingFromRawFailsClosed()
    {
        var (targets, incomplete) = GitPayloadBuilder.PlanReads(GitPayloadBuilder.ParseRaw(""), ["ghost.bin"]);

        Assert.Empty(targets);
        Assert.Equal([new SecretFinding(BlobScan.IncompleteRule, "uncommitted file ghost.bin (binary per git)")], incomplete);
        Assert.Equal(["a b.bin", "c.bin"], GitPayloadBuilder.BinaryPaths("-\t-\ta b.bin\0" + "3\t1\ttext.txt\0" + "-\t-\tc.bin\0"));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LfsPointerStaysOnTheTextPath()
    {
        Write(".gitattributes", "*.dat -diff\n*.blob binary\n*.psd filter=lfs diff=lfs merge=lfs -text\n");
        Commit();
        Write("art.psd", "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('4', 64) + "\nsize 12345\n");

        var p = await Snapshot();

        Assert.Contains("+version https://git-lfs.github.com/spec/v1", p.OverlayDiff, StringComparison.Ordinal);
        Assert.Equal(0, p.BlobsRead);
        Assert.Empty(p.BinaryFindings);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task UnexpectedMissingFailsClosed()
    {
        // An object id lanq asks for that the object store does not have: never "clean".
        var ghost = new string('e', 40);
        var real = Git("hash-object", "-w", "a.txt").Trim();

        var added = await GitPayloadBuilder.ScanBlobsAsync(_repo, [new GitPayloadBuilder.BlobTarget("ghost.bin", ghost, null)], CancellationToken.None);
        var modified = await GitPayloadBuilder.ScanBlobsAsync(_repo, [new GitPayloadBuilder.BlobTarget("half.bin", real, ghost)], CancellationToken.None);

        Assert.Equal([new SecretFinding(BlobScan.IncompleteRule, "uncommitted file ghost.bin (binary per git)")], added.Findings);
        Assert.Equal([new SecretFinding(BlobScan.IncompleteRule, "uncommitted file half.bin (binary per git)")], modified.Findings);

        // And through the run: a scan-incomplete finding refuses it like any secret.
        var ssh = new PoolSsh();
        var payloads = new RunnerTests.FakePayloads("");
        var runner = new JobRunner(
            ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new IncompletePayloads(payloads), new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(_repo, ".out", "state")),
            Path.Combine(_repo, ".out", "tmp"), _ => { });
        var r = await runner.RunAsync(new JobRequest(["node"], ["npx", "jest"], ".", _repo, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal((JobStatus.Rejected, RejectKind.Secret), (r.Status, r.RejectKind));
        Assert.Contains("scan-incomplete", r.Message, StringComparison.Ordinal);
        Assert.Contains("git fsck", r.Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Uploads);
    }

    private sealed class IncompletePayloads(RunnerTests.FakePayloads inner) : IPayloadBuilder
    {
        public async Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct) =>
            await inner.BuildAsync(jobId, repoDir, outputDir, kinds, ct) with { BinaryFindings = [new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where("x.bin"))] };

        public Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct) => inner.BuildArchiveAsync(jobId, repoDir, outputDir, sha, ct);

        public Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct) =>
            inner.BuildBundleAsync(jobId, repoDir, outputDir, sha, baseSha, ct);
    }

    [Fact]
    public void SecretSpanningAChunkBoundaryIsFound()
    {
        // Filler fills the first chunk; "password =" is its last line and the quoted value is the first new line of the next chunk
        // (quoted-credential's \s* spans the line break). The overlap carries the key line into the second chunk.
        var lines = new List<string>();
        long length = 0;
        var filler = new string('f', 1000);
        while (length + filler.Length + 1 + 11 <= BlobScan.ChunkChars)
        {
            lines.Add(filler);
            length += filler.Length + 1;
        }

        lines.Add("password =");
        lines.Add("\"" + new string('v', 700) + "\"");
        Assert.True(length + 11 + 703 > BlobScan.ChunkChars); // the value line starts a new chunk

        var found = BlobScan.ScanLines(lines, "uncommitted file x.dat (binary per git)");

        Assert.Contains(found, f => f.Rule == "quoted-credential");
    }

    [Fact]
    public void LongValueInAOneMegabyteLineIsFound()
    {
        List<string> lines = [.. Enumerable.Repeat(new string('f', 1000), 900), "secret_value: \"" + new string('k', 1536 * 1024) + "\"", "after"];

        var found = BlobScan.ScanLines(lines, "uncommitted file x.dat (binary per git)");

        Assert.Contains(found, f => f.Rule == "quoted-credential");
    }

    [Fact]
    public void TimeoutInAChunkFailsClosed()
    {
        var slow = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(20));
        List<string> lines = [.. Enumerable.Repeat(new string('f', 1000), 2000), new string('a', 5000) + "!"];

        var found = BlobScan.ScanLines(lines, "uncommitted file x.dat (binary per git)", [("slow", slow)]);

        Assert.Equal([new SecretFinding(SecretScanner.TimeoutRule, "uncommitted file x.dat (binary per git)")], found);
    }

    /// <summary>A UTF-16 byte order mark on a file that is not UTF-16 (ASCII after it, and a NUL): the strings are scanned too.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task BomFollowedByAsciiIsStillScanned()
    {
        Write("fake.bin", [0xFF, 0xFE, .. Encoding.ASCII.GetBytes("key = " + AwsKey + "\ntoken = " + GitHubToken + "\n"), 0]);

        var p = await Snapshot();

        Assert.True(Has(p, "aws-access-key", "fake.bin"), string.Join("; ", p.BinaryFindings));
        Assert.True(Has(p, "github-token", "fake.bin"));
        Assert.Empty(p.ScanSkipped);
    }

    /// <summary>A masked file whose first 8000 bytes have no NUL is text; UTF-16LE further on is still found through its strings.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Utf16AfterANulFreeStartIsScanned()
    {
        Write("notes.dat", [.. Encoding.ASCII.GetBytes(new string('x', 9000) + "\n"), .. Encoding.Unicode.GetBytes("aws_key " + AwsKey + "\n")]);

        var p = await Snapshot();

        Assert.True(Has(p, "aws-access-key", "notes.dat"), string.Join("; ", p.BinaryFindings));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Utf32InABinaryIsFound()
    {
        var data = Binary(4096);
        Encoding.UTF32.GetBytes("aws_key " + AwsKey).CopyTo(data, 1003); // little-endian, an odd offset
        Write("tool.bin", data);

        var p = await Snapshot();

        Assert.True(Has(p, "aws-access-key", "tool.bin"), string.Join("; ", p.BinaryFindings));
    }

    /// <summary>quoted-credential's \s* spans blank lines: 100 of them between the key at a chunk's end and the value must not split the match.</summary>
    [Fact]
    public void OverlapSpansBlankLines()
    {
        var lines = new List<string>();
        long length = 0;
        var filler = new string('f', 1000);
        while (length + filler.Length + 1 + 11 + 100 <= BlobScan.ChunkChars)
        {
            lines.Add(filler);
            length += filler.Length + 1;
        }

        lines.Add("password =");
        lines.AddRange(Enumerable.Repeat("", 100));
        lines.Add("\"" + new string('v', 700) + "\"");
        Assert.True(length + 11 + 100 + 703 > BlobScan.ChunkChars); // the value line starts a new chunk

        Assert.Contains(BlobScan.ScanLines(lines, "uncommitted file x.dat (binary per git)"), f => f.Rule == "quoted-credential");
    }

    /// <summary>Over-cap decisions come from the sizes and a look at the first bytes: a binary over a cap is never read whole.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task OverCapBlobsAreNotReadWhole()
    {
        var caps = new BlobScan.Caps(BinaryFile: 64 * 1024, BinaryTotal: 100 * 1024, Text: 64 * 1024, TextTotal: 100 * 1024);
        var targets = new List<GitPayloadBuilder.BlobTarget>();
        void Add(string name, byte[] data)
        {
            Write(name, data);
            targets.Add(new GitPayloadBuilder.BlobTarget(name, Git("hash-object", "-w", name).Trim(), null));
        }

        Add("a1.bin", Binary(60 * 1024, 1)); // scanned: 60 KB of binaries so far
        Add("a2.bin", Binary(60 * 1024, 2)); // past the 100 KB total: looked at, listed
        Add("a3.bin", Binary(200 * 1024, 3)); // over the file cap: looked at, listed
        Add("t1.dat", Encoding.ASCII.GetBytes(new string('t', 60 * 1024))); // text: its own totals, scanned
        Add("t2.dat", Encoding.ASCII.GetBytes(new string('u', 60 * 1024))); // past the 100 KB text total: listed
        Add("t3.dat", Encoding.ASCII.GetBytes(new string('w', 200 * 1024))); // over the text cap: listed

        var r = await GitPayloadBuilder.ScanBlobsAsync(_repo, targets, CancellationToken.None, caps);

        Assert.Equal(
            [
                new ScanSkip("a2.bin", 60 * 1024, BlobScan.BinaryOverTotalCap), // smallest first, then by path
                new ScanSkip("t2.dat", 60 * 1024, BlobScan.MaskedTextOverTotalCap),
                new ScanSkip("a3.bin", 200 * 1024, BlobScan.BinaryOverFileCap),
                new ScanSkip("t3.dat", 200 * 1024, BlobScan.MaskedTextOverCap),
            ],
            r.Skipped);
        Assert.Equal((60 * 1024 * 2) + (5 * BlobScan.ClassifyBytes), r.BytesRead); // two whole files, five first parts (t1 might have been over the binary total)
    }

    /// <summary>Blobs are read smallest first: large files earlier in path order cannot use up the total before a small secret-bearing one.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task SmallFilesAreReadBeforeLargeOnes()
    {
        var caps = new BlobScan.Caps(BinaryFile: 64 * 1024, BinaryTotal: 100 * 1024, Text: 64 * 1024, TextTotal: 100 * 1024);
        var targets = new List<GitPayloadBuilder.BlobTarget>();
        void Add(string name, byte[] data)
        {
            Write(name, data);
            targets.Add(new GitPayloadBuilder.BlobTarget(name, Git("hash-object", "-w", name).Trim(), null));
        }

        Add("a1.bin", Binary(60 * 1024, 1));
        Add("a2.bin", Binary(60 * 1024, 2));
        Add("z.bin", WithText(Binary(2048, 3), 100, "aws " + AwsKey)); // last by path, first by size

        var r = await GitPayloadBuilder.ScanBlobsAsync(_repo, targets, CancellationToken.None, caps);

        Assert.Contains(r.Findings, f => f.Rule == "aws-access-key" && f.Where == "uncommitted file z.bin (binary per git)");
        Assert.Equal([new ScanSkip("a2.bin", 60 * 1024, BlobScan.BinaryOverTotalCap)], r.Skipped);
    }

    /// <summary>A private key replaced in place in a -diff file (same BEGIN line, new body) is found; an unchanged one is not re-flagged.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task KeyReplacedInPlaceInADiffFileIsFound()
    {
        static string Pem(char body) => "config\n-----BEGIN RSA PRIVATE KEY-----\n" + string.Concat(Enumerable.Repeat(new string(body, 64) + "\n", 4)) + "-----END RSA PRIVATE KEY-----\ntail\n";
        Write("deploy.dat", Pem('A'));
        Commit();

        Write("deploy.dat", Pem('A').Replace("tail", "tail changed", StringComparison.Ordinal));
        Assert.Empty((await Snapshot()).BinaryFindings); // the same key, something else changed

        Write("deploy.dat", Pem('B'));
        var p = await Snapshot();
        Assert.True(Has(p, "private-key-block", "deploy.dat"), string.Join("; ", p.BinaryFindings));
    }

    /// <summary>A file lanq could not read from git: refused with a git fsck hint, not the "take the secret out" one.</summary>
    [Fact]
    public void ScanIncompleteHasItsOwnHint()
    {
        var r = JobRunner.SecretRefusal("j1", [new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where("x.bin"))]);

        Assert.Equal((JobStatus.Rejected, 2, RejectKind.Secret), (r.Status, r.CliExitCode, r.RejectKind));
        Assert.Contains("git fsck", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("take the secret out", r.Message, StringComparison.Ordinal);

        var both = JobRunner.SecretRefusal("j1", [new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where("x.bin")), new SecretFinding("aws-access-key", BlobScan.Where("y.bin"))]);
        Assert.Contains("git fsck", both.Message, StringComparison.Ordinal);
        Assert.Contains("take the secret out", both.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingAndScanSkippedPathsAreSanitised()
    {
        var path = "a‮b​\u001B[31m.bin";

        Assert.Equal("uncommitted file a?b??[31m.bin (binary per git)", BlobScan.Where(path));
        var json = Assert.Single(Cli.ScanSkippedJson([new ScanSkip(path, 9 * 1024 * 1024, BlobScan.BinaryOverFileCap)])!);
        Assert.Equal("a?b??[31m.bin", json.Path);
        Assert.Equal(
            "[lanq] secret scan: 1 file(s) not read in full: a?b??[31m.bin (9 MB) (over the scan's caps; listed in scanSkipped)",
            JobRunner.ScanSkippedLine([new ScanSkip(path, 9 * 1024 * 1024, BlobScan.BinaryOverFileCap)]));
        var six = Enumerable.Range(1, 6).Select(i => new ScanSkip($"f{i}.bin", 1024 * 1024, BlobScan.BinaryOverTotalCap)).ToList();
        Assert.EndsWith("f5.bin (1 MB), and 1 more (over the scan's caps; listed in scanSkipped)", JobRunner.ScanSkippedLine(six), StringComparison.Ordinal);
        Assert.Null(Cli.ScanSkippedJson([]));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CleanTreeReadsNoBlobs()
    {
        Write("logo.bin", Binary(4096));
        Commit();

        var p = await Snapshot();

        Assert.Equal((0, 0, 0), (p.BlobsRead, p.BinaryFindings.Count, p.ScanSkipped.Count));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ScanSkippedJsonOnEveryPath()
    {
        var dir = Path.Combine(_repo, ".out", "cli");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lanq.yml"), "version: 1\njobs:\n  unit:\n    command: npx jest\n    needs: [node]\n");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        ScanSkip[] skipped = [new("assets/big.bin", 52 * 1024 * 1024, BlobScan.BinaryOverFileCap)];
        string[][] paths =
        [
            ["run", "--json", "--needs", "node", "--", "npx", "jest"],
            ["run", "--json", "--needs", "node", "--shards", "2", "--", "npx", "jest"],
            ["run", "--json", "--plan"],
            ["run", "--json", "--dry-run", "--needs", "node", "--", "npx", "jest"],
            ["run", "--json", "--dry-run", "--plan"],
        ];
        foreach (var args in paths)
        {
            foreach (var withSkips in new[] { true, false })
            {
                var ssh = new PoolSsh();
                var payloads = new RunnerTests.FakePayloads("") { ScanSkips = withSkips ? skipped : [] };
                var o = new StringWriter();
                var e = new StringWriter();
                var code = await Cli.Run(
                    ParsedArgs.Parse(args), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, payloads, new HealthProbe(ssh, TimeProvider.System), new JobLog(Path.Combine(dir, "state")),
                    Path.Combine(dir, "home"), dir, o, e, CancellationToken.None);

                var label = string.Join(' ', args);
                Assert.True(code == 0, label + ": " + e);
                using var doc = JsonDocument.Parse(o.ToString());
                if (!withSkips)
                {
                    Assert.False(doc.RootElement.TryGetProperty("scanSkipped", out _), label);
                    continue;
                }

                var item = Assert.Single(doc.RootElement.GetProperty("scanSkipped").EnumerateArray());
                Assert.Equal(("assets/big.bin", 52L * 1024 * 1024, "binary_over_file_cap"), (item.GetProperty("path").GetString(), item.GetProperty("bytes").GetInt64(), item.GetProperty("reason").GetString()));
                var stderr = e.ToString();
                if (args.Contains("--dry-run"))
                {
                    Assert.Contains("1 file(s) git shows as binary not read in full (scanSkipped)", stderr, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains("[lanq] secret scan: 1 file(s) not read in full: assets/big.bin (52 MB)", stderr, StringComparison.Ordinal);
                }

                Assert.StartsWith("[lanq] ", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1], StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The design's soft budget: 64 MB of random binaries (8 x 8 MB, exactly the caps) scanned in under 10 s on CI. Logged; the hard limit is generous.</summary>
    [Fact(Timeout = 300_000)]
    public async Task PerfSixtyFourMegabytesOfRandomBinary()
    {
        var targets = new List<GitPayloadBuilder.BlobTarget>();
        for (var i = 0; i < 8; i++)
        {
            var data = new byte[(int)BlobScan.BinaryFileCap];
            new Random(100 + i).NextBytes(data);
            data[0] = 0;
            var name = $"r{i}.bin";
            Write(name, data);
            targets.Add(new GitPayloadBuilder.BlobTarget(name, Git("hash-object", "-w", name).Trim(), null));
        }

        var watch = Stopwatch.StartNew();
        var r = await GitPayloadBuilder.ScanBlobsAsync(_repo, targets, CancellationToken.None);
        watch.Stop();

        _out.WriteLine($"binary scan of 64 MB random data: {watch.Elapsed.TotalSeconds:0.00} s (soft budget 10 s); {r.Findings.Count} finding(s)");
        Assert.Empty(r.Skipped);
        Assert.Equal(8, r.BlobsRead);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), $"took {watch.Elapsed}");
    }
}
