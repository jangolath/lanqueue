using System.Diagnostics;
using System.Globalization;
using LanQueue.Policy;

namespace LanQueue.Payload;

/// <summary>
/// The secret scan of uncommitted files git shows as binary (0.9.2). The overlay diff prints only "Binary files ... differ" for them, so:
/// <list type="number">
/// <item>the binary paths come from <c>git diff --cached --numstat -z --no-renames HEAD</c> ("-&lt;TAB&gt;-");</item>
/// <item>each is looked up in <c>git diff --cached --raw -z --full-index --no-abbrev --no-renames HEAD</c> (--full-index alone leaves the
/// raw format's ids abbreviated) for its modes, object ids and status; a binary
/// path missing there fails closed;</item>
/// <item>the blobs are read by object id, one <c>git cat-file --batch-check</c> for the sizes, then one <c>--batch</c> process asked for
/// one blob at a time (never with --filters or --textconv, so no attribute driver runs), and an object lanq asked for that is missing
/// fails closed;</item>
/// <item>their content is scanned by <see cref="BlobScan"/> under its caps; a file over a cap is listed in <see cref="JobPayload.ScanSkipped"/>.</item>
/// </list>
/// Only new content is scanned in a modified file: the lines (or strings) the old blob does not have. Content is never written anywhere
/// and never echoed.
/// </summary>
public sealed partial class GitPayloadBuilder
{
    /// <summary>One <c>--raw</c> entry: the old and new modes and object ids and the status letter (A, M, T, D; no renames).</summary>
    internal sealed record RawEntry(string OldMode, string NewMode, string OldOid, string NewOid, char Status);

    /// <summary>A file to read: its new blob, and the old one when it is modified (null for an added file).</summary>
    internal sealed record BlobTarget(string Path, string NewOid, string? OldOid);

    /// <param name="BytesRead">Content bytes read from git (whole blobs, plus the first bytes of the ones only looked at).</param>
    internal sealed record BinaryScanResult(IReadOnlyList<SecretFinding> Findings, IReadOnlyList<ScanSkip> Skipped, int BlobsRead, long BytesRead = 0)
    {
        public static readonly BinaryScanResult None = new([], [], 0);
    }

    /// <summary>The binary scan of the snapshot's temporary index (<paramref name="env"/>) against HEAD.</summary>
    private static async Task<BinaryScanResult> ScanBinaryChangesAsync(string top, Dictionary<string, string> env, CancellationToken ct)
    {
        string[] diff = ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames"];
        var binary = BinaryPaths(await Git(top, env, ct, [.. diff, "--numstat", "-z", "HEAD"]).ConfigureAwait(false));
        if (binary.Count == 0)
        {
            return BinaryScanResult.None;
        }

        var raw = ParseRaw(await Git(top, env, ct, [.. diff, "--raw", "-z", "--full-index", "--no-abbrev", "HEAD"]).ConfigureAwait(false));
        var (targets, incomplete) = PlanReads(raw, binary);
        var read = await ScanBlobsAsync(top, targets, ct).ConfigureAwait(false);
        return read with { Findings = [.. incomplete, .. read.Findings] };
    }

    /// <summary>The paths <c>--numstat -z</c> shows as binary ("-", "-"), in its order.</summary>
    internal static List<string> BinaryPaths(string numstat) =>
        [.. numstat.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(r => r.StartsWith("-\t-\t", StringComparison.Ordinal))
            .Select(r => r[4..])];

    /// <summary>The <c>--raw -z</c> entries by path: ":oldmode newmode oldoid newoid status" NUL path NUL.</summary>
    internal static Dictionary<string, RawEntry> ParseRaw(string raw)
    {
        var map = new Dictionary<string, RawEntry>(StringComparer.Ordinal);
        var parts = raw.Split('\0');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (!parts[i].StartsWith(':'))
            {
                continue;
            }

            var f = parts[i][1..].Split(' ');
            if (f.Length == 5 && f[4].Length > 0)
            {
                map[parts[i + 1]] = new RawEntry(f[0], f[1], f[2], f[3], f[4][0]);
            }

            i++;
        }

        return map;
    }

    /// <summary>
    /// Which blobs to read. Nothing is read for a deleted file, a submodule (160000), a symlink (120000: its target is in the diff already) or
    /// a mode-only change (the same object id). A binary path with no raw entry, an unknown status or an unexpected mode is a scan-incomplete
    /// finding (fails closed). A modified file keeps its old blob when that was a regular file, so only new content is scanned.
    /// </summary>
    internal static (List<BlobTarget> Targets, List<SecretFinding> Incomplete) PlanReads(IReadOnlyDictionary<string, RawEntry> raw, IEnumerable<string> binaryPaths)
    {
        var targets = new List<BlobTarget>();
        var incomplete = new List<SecretFinding>();
        foreach (var path in binaryPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!raw.TryGetValue(path, out var e))
            {
                incomplete.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(path)));
                continue;
            }

            if (e.Status == 'D' || e.NewMode is "160000" or "120000" || string.Equals(e.OldOid, e.NewOid, StringComparison.Ordinal))
            {
                continue;
            }

            if (e.Status is not ('A' or 'M' or 'T') || !ArchiveWriter.IsRegularMode(e.NewMode) || !IsObjectId(e.NewOid))
            {
                incomplete.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(path)));
                continue;
            }

            var added = e.Status == 'A' || !ArchiveWriter.IsRegularMode(e.OldMode) || !IsObjectId(e.OldOid) || e.OldOid.All(c => c == '0');
            targets.Add(new BlobTarget(path, e.NewOid, added ? null : e.OldOid));
        }

        return (targets, incomplete);
    }

    /// <summary>
    /// Reads and scans <paramref name="targets"/> (in path order) under <see cref="BlobScan"/>'s caps. Sizes come from one
    /// <c>cat-file --batch-check</c>, so the caps are decided before any content is read: a blob that may be over a cap (over
    /// <see cref="BlobScan.BinaryFileCap"/>, or past the binary or text total) only has its first <see cref="BlobScan.ClassifyBytes"/> bytes
    /// read to tell text from binary, and is listed when that kind is over its cap. The rest is read whole, on demand, from one
    /// <c>cat-file --batch</c> process. Any object missing fails closed.
    /// </summary>
    /// <param name="caps">The caps; null: <see cref="BlobScan.Caps.Default"/> (tests use smaller ones).</param>
    internal static async Task<BinaryScanResult> ScanBlobsAsync(string top, IReadOnlyList<BlobTarget> targets, CancellationToken ct, BlobScan.Caps? caps = null)
    {
        var cap = caps ?? BlobScan.Caps.Default;
        if (targets.Count == 0)
        {
            return BinaryScanResult.None;
        }

        var findings = new List<SecretFinding>();
        var skipped = new List<ScanSkip>();
        var sizes = await BatchCheckAsync(top, [.. targets.SelectMany(t => t.OldOid is null ? [t.NewOid] : new[] { t.OldOid, t.NewOid }).Distinct(StringComparer.Ordinal)], ct)
            .ConfigureAwait(false);
        long? Size(string oid) => sizes.TryGetValue(oid, out var s) ? s : null;

        var blobsRead = 0;
        long binaryBytes = 0, textBytes = 0, bytesRead = 0;
        await using var reader = BatchReader.Start(top, WorkTreeTimeout, ct);
        foreach (var t in targets)
        {
            if (Size(t.NewOid) is not { } size || (t.OldOid is { } o && Size(o) is null))
            {
                findings.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(t.Path)));
                continue;
            }

            // Which caps it could be over, whatever it is: then look at its start first, and read it whole only when its kind fits.
            var overAsBinary = size > cap.BinaryFile ? BlobScan.BinaryOverFileCap : binaryBytes + size > cap.BinaryTotal ? BlobScan.BinaryOverTotalCap : null;
            var overAsText = size > cap.Text ? BlobScan.MaskedTextOverCap : textBytes + size > cap.TextTotal ? BlobScan.MaskedTextOverTotalCap : null;
            if (overAsBinary is not null || overAsText is not null)
            {
                var head = await PeekAsync(top, t.NewOid, BlobScan.ClassifyBytes, ct).ConfigureAwait(false);
                blobsRead++;
                bytesRead += head?.Length ?? 0;
                if (head is null)
                {
                    findings.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(t.Path)));
                    continue;
                }

                if ((BlobScan.Classify(head) == BlobScan.Kind.Binary ? overAsBinary : overAsText) is { } reason)
                {
                    skipped.Add(new ScanSkip(t.Path, size, reason));
                    continue;
                }
            }

            var data = await reader.NextAsync(t.NewOid).ConfigureAwait(false);
            blobsRead++;
            bytesRead += data?.Length ?? 0;
            if (data is null)
            {
                findings.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(t.Path)));
                continue;
            }

            var kind = BlobScan.Classify(data);
            if (kind == BlobScan.Kind.Binary)
            {
                binaryBytes += data.Length;
            }
            else
            {
                textBytes += data.Length;
            }

            var lines = BlobScan.Lines(data, kind);

            // Only new content: what the old version does not have (as the diff's added lines). An old blob over the new one's cap is not
            // read, and the whole new blob is scanned.
            if (t.OldOid is { } oldOid && Size(oldOid) <= (kind == BlobScan.Kind.Binary ? cap.BinaryFile : cap.Text))
            {
                var oldData = await reader.NextAsync(oldOid).ConfigureAwait(false);
                blobsRead++;
                bytesRead += oldData?.Length ?? 0;
                if (oldData is null)
                {
                    findings.Add(new SecretFinding(BlobScan.IncompleteRule, BlobScan.Where(t.Path)));
                    continue;
                }

                var oldKind = kind == BlobScan.Kind.Binary ? kind : BlobScan.Classify(oldData) == BlobScan.Kind.Utf16Text ? BlobScan.Kind.Utf16Text : BlobScan.Kind.MaskedText;
                var known = new HashSet<string>(BlobScan.Lines(oldData, oldKind), StringComparer.Ordinal);
                lines = [.. lines.Where(l => !known.Contains(l))];
            }

            findings.AddRange(BlobScan.ScanLines(lines, BlobScan.Where(t.Path)));
        }

        await reader.FinishAsync().ConfigureAwait(false);
        return new BinaryScanResult(findings, skipped, blobsRead, bytesRead);
    }

    /// <summary>
    /// The size of each object by id from one <c>git cat-file --batch-check</c>; an id git reports missing (or as anything but a blob) is left
    /// out.
    /// </summary>
    private static async Task<Dictionary<string, long>> BatchCheckAsync(string top, IReadOnlyList<string> oids, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync("git", top, null, MetadataTimeout, ct, ["cat-file", "--batch-check"], string.Concat(oids.Select(o => o + "\n")))
            .ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException("git cat-file failed: " + error.Trim());
        }

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(' ');
            if (f.Length == 3 && f[1] == "blob" && long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                sizes[f[0]] = size;
            }
        }

        return sizes;
    }

    /// <summary>The first <paramref name="count"/> bytes of blob <paramref name="oid"/> (<c>git cat-file blob</c>, stopped once they are read), or null when git cannot read it.</summary>
    private static async Task<byte[]?> PeekAsync(string top, string oid, int count, CancellationToken ct)
    {
        var psi = NonInteractive(new ProcessStartInfo("git")
        {
            WorkingDirectory = top,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        foreach (var a in new[] { "cat-file", "blob", oid })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MetadataTimeout);
        var err = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            var buffer = new byte[count];
            var got = 0;
            while (got < count)
            {
                var n = await p.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(got), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                got += n;
            }

            if (got == 0)
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return p.ExitCode == 0 ? [] : null;
            }

            return buffer[..got];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitTimeoutException("cat-file", MetadataTimeout);
        }
        finally
        {
            Kill(p);
            WaitBriefly(p);
            try
            {
                await err.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // the pipe closed with the killed process
            }
        }
    }

    /// <summary>
    /// One <c>git cat-file --batch</c> process, asked for one blob at a time (git answers each request before reading the next, so nothing is
    /// read that lanq does not want), started on the first request. Each blob is read whole. The whole conversation is bounded by its
    /// timeout (then git is killed and <see cref="GitTimeoutException"/> is thrown).
    /// </summary>
    private sealed class BatchReader : IAsyncDisposable
    {
        private readonly string _top;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _outer;
        private readonly TimeSpan _limit;
        private Process? _p;
        private Task<string>? _err;

        private BatchReader(string top, TimeSpan limit, CancellationToken ct)
        {
            _top = top;
            _outer = ct;
            _limit = limit;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts.CancelAfter(limit);
        }

        public static BatchReader Start(string top, TimeSpan limit, CancellationToken ct) => new(top, limit, ct);

        private Process Process()
        {
            if (_p is not null)
            {
                return _p;
            }

            var psi = NonInteractive(new ProcessStartInfo("git")
            {
                WorkingDirectory = _top,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("--batch");
            _p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
            _err = _p.StandardError.ReadToEndAsync(_cts.Token);
            return _p;
        }

        /// <summary>The content of blob <paramref name="oid"/>; null when git reports it missing.</summary>
        public async Task<byte[]?> NextAsync(string oid)
        {
            try
            {
                var p = Process();
                await p.StandardInput.BaseStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(oid + "\n"), _cts.Token).ConfigureAwait(false);
                await p.StandardInput.BaseStream.FlushAsync(_cts.Token).ConfigureAwait(false);
                var stdout = p.StandardOutput.BaseStream;
                var header = await ReadLine(stdout, _cts.Token).ConfigureAwait(false);
                var f = header.Split(' ');
                if (f.Length == 2 && f[0] == oid && f[1] == "missing")
                {
                    return null;
                }

                if (f.Length != 3 || f[0] != oid || f[1] != "blob"
                    || !long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > BlobScan.TextCap)
                {
                    throw new InvalidOperationException("git cat-file returned an unexpected header while reading a file git shows as binary.");
                }

                var data = new byte[size];
                await stdout.ReadExactlyAsync(data, _cts.Token).ConfigureAwait(false);
                await ReadLine(stdout, _cts.Token).ConfigureAwait(false); // the LF after the content
                return data;
            }
            catch (OperationCanceledException) when (!_outer.IsCancellationRequested)
            {
                throw new GitTimeoutException("cat-file", _limit);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidOperationException("git cat-file ended before the whole file was read.");
            }
        }

        public async Task FinishAsync()
        {
            if (_p is null)
            {
                return;
            }

            try
            {
                _p.StandardInput.Close();
                await _p.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
                if (_p.ExitCode != 0)
                {
                    throw new InvalidOperationException("git cat-file failed: " + (await _err!.ConfigureAwait(false)).Trim());
                }
            }
            catch (OperationCanceledException) when (!_outer.IsCancellationRequested)
            {
                throw new GitTimeoutException("cat-file", _limit);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_p is not null)
            {
                Kill(_p);
                WaitBriefly(_p);
                try
                {
                    await _err!.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // cancelled with the conversation, or the pipe closed with the killed process
                }

                _p.Dispose();
            }

            _cts.Dispose();
        }
    }
}
