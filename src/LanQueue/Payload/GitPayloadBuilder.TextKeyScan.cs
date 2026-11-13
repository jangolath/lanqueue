using System.Globalization;
using LanQueue.Policy;

namespace LanQueue.Payload;

/// <summary>
/// The text key-block scan (0.10.1): a private key replaced in place in an ordinary text file. The overlay diff (<c>-U0</c>) shows only the
/// changed lines, so a key whose body changed while its BEGIN line stayed the same never shows its BEGIN line to the scan. So:
/// <list type="number">
/// <item>the rename-aware (<c>-M</c>, like the overlay diff and its file list) <c>--numstat -z</c> and <c>--raw -z</c> of the snapshot's
/// temporary index against HEAD say which files are text, modified (or renamed or copied) with added lines, and their old and new object
/// ids;</item>
/// <item><c>git grep --cached -I -l -z -i -F -e -----BEGIN</c> under the same index, given only those files, keeps the ones whose new
/// version has a BEGIN line at all (a BEGIN line only the old version has is a removed line, which the diff scan sees already); one of
/// them that cannot be looked up fails closed (<see cref="BlobScan.TextIncompleteRule"/>);</item>
/// <item>their sizes come from one <c>git cat-file --batch-check</c>, so a file over the caps is listed (<see cref="BlobScan.TextOverCap"/>)
/// before anything is read; the rest are read by object id from one <c>git cat-file --batch</c> (no filters, no textconv);</item>
/// <item>only the unchanged BEGIN lines whose block has a new line are scanned (<see cref="BlobScan.ChangedBlockStarts"/>): every other new
/// line is in the diff already.</item>
/// </list>
/// Added files are not read (every line is in the diff), nor deleted files, files git shows as binary (the binary scan's), symlinks and
/// submodules. Content is never written anywhere and never echoed.
/// </summary>
public sealed partial class GitPayloadBuilder
{
    /// <summary>The text key-block scan's findings (scan-incomplete included), the files over its caps, and how many blobs it read.</summary>
    internal sealed record TextKeyScanResult(IReadOnlyList<SecretFinding> Findings, IReadOnlyList<ScanSkip> Skipped, int BlobsRead)
    {
        public static readonly TextKeyScanResult None = new([], [], 0);
    }

    /// <summary>The diff options of the text path: the same rename detection for the overlay diff, its file list, numstat and raw (0.10.1).</summary>
    private static readonly string[] TextDiff = ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "-M"];

    /// <summary>The text key-block scan of the snapshot's temporary index (<paramref name="env"/>) against HEAD.</summary>
    private static async Task<TextKeyScanResult> ScanTextKeyBlocksAsync(string top, Dictionary<string, string> env, CancellationToken ct)
    {
        var numstat = ParseNumstat(await Git(top, env, ct, [.. TextDiff, "--numstat", "-z", "HEAD"]).ConfigureAwait(false));
        if (!numstat.Values.Any(a => a != 0 && a is not null))
        {
            return TextKeyScanResult.None; // no text file has an added line
        }

        var raw = ParseRaw(await Git(top, env, ct, [.. TextDiff, "--raw", "-z", "--full-index", "--no-abbrev", "HEAD"]).ConfigureAwait(false));

        // Only files that can need a read are grepped: changed (not added or deleted) text files with added lines, or with an unreadable count.
        var changed = raw.Where(r => r.Value.Status is not ('A' or 'D') && (!numstat.TryGetValue(r.Key, out var a) || (a is not null && a != 0)))
            .Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        var keyed = await KeyBearingPathsAsync(top, env, changed, ct).ConfigureAwait(false);
        if (keyed.Count == 0)
        {
            return TextKeyScanResult.None;
        }

        var (targets, incomplete) = PlanTextReads(raw, numstat, keyed);
        var read = await ScanTextBlobsAsync(top, targets, ct).ConfigureAwait(false);
        return read with { Findings = [.. incomplete, .. read.Findings] };
    }

    /// <summary>The most characters of paths given to one git grep (Windows limits a command line to 32,767).</summary>
    private const int GrepPathChars = 16_000;

    /// <summary>
    /// Which of <paramref name="paths"/> are text files in the snapshot's index (git's own text test, <c>-I</c>) holding <c>-----BEGIN</c> in
    /// any case: a superset of the files with a line <see cref="SecretScanner.StartsASecretBlock"/> matches. The paths go as literal pathspecs,
    /// in batches of at most <see cref="GrepPathChars"/> characters. No match is git grep's exit code 1.
    /// </summary>
    private static async Task<HashSet<string>> KeyBearingPathsAsync(string top, Dictionary<string, string> env, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var keyed = new HashSet<string>(StringComparer.Ordinal);
        string[] grep = ["grep", "--cached", "-I", "-l", "-z", "-i", "-F", "--full-name", "--no-color", "--no-textconv", "--no-recurse-submodules", "-e", "-----BEGIN", "--"];
        for (var i = 0; i < paths.Count;)
        {
            var batch = new List<string>();
            for (var chars = 0; i < paths.Count && (batch.Count == 0 || chars + paths[i].Length <= GrepPathChars); i++)
            {
                batch.Add(":(literal)" + paths[i]);
                chars += paths[i].Length + 12;
            }

            var (code, output, error) = await GitRun(top, env, ct, [.. grep, .. batch]).ConfigureAwait(false);
            if (code is not (0 or 1))
            {
                throw new InvalidOperationException("git grep failed: " + error.Trim());
            }

            keyed.UnionWith(output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        return keyed;
    }

    /// <summary>
    /// The <c>--numstat -z</c> entries by (new) path: the added line count, or null for a file git shows as binary ("-"). A rename or copy is
    /// "added TAB deleted TAB" NUL old path NUL new path NUL.
    /// </summary>
    internal static Dictionary<string, long?> ParseNumstat(string numstat)
    {
        var map = new Dictionary<string, long?>(StringComparer.Ordinal);
        var parts = numstat.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var f = parts[i].Split('\t', 3);
            if (f.Length != 3)
            {
                continue;
            }

            string path;
            if (f[2].Length == 0)
            {
                if (i + 2 >= parts.Length)
                {
                    break;
                }

                path = parts[i + 2];
                i += 2;
            }
            else
            {
                path = f[2];
            }

            map[path] = f[0] == "-" ? null : long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) ? added : -1;
        }

        return map;
    }

    /// <summary>
    /// Which key-bearing files to compare: those the rename-aware raw shows modified, renamed, copied or type-changed from a regular file, that
    /// numstat shows as text with added lines. Nothing is read for an unchanged, added or deleted file, a submodule or symlink, a file git
    /// shows as binary, or one with only removed lines. A changed key-bearing file missing from numstat, an unreadable added count, an unknown
    /// status or an unexpected mode is a scan-incomplete finding (fails closed).
    /// </summary>
    internal static (List<BlobTarget> Targets, List<SecretFinding> Incomplete) PlanTextReads(
        IReadOnlyDictionary<string, RawEntry> raw, IReadOnlyDictionary<string, long?> numstat, IEnumerable<string> keyed)
    {
        var targets = new List<BlobTarget>();
        var incomplete = new List<SecretFinding>();
        foreach (var path in keyed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!raw.TryGetValue(path, out var e))
            {
                continue; // unchanged: nothing new in it
            }

            if (e.Status is 'A' or 'D' || e.NewMode is "160000" or "120000" || string.Equals(e.OldOid, e.NewOid, StringComparison.Ordinal))
            {
                continue; // every line of an added file is in the diff
            }

            if (!numstat.TryGetValue(path, out var added) || added < 0)
            {
                incomplete.Add(new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere(path)));
                continue;
            }

            if (added is null or 0)
            {
                continue; // binary (the binary scan's), or only removed lines
            }

            if (e.Status is not ('M' or 'T' or 'R' or 'C') || !ArchiveWriter.IsRegularMode(e.NewMode) || !IsObjectId(e.NewOid))
            {
                incomplete.Add(new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere(path)));
                continue;
            }

            if (!ArchiveWriter.IsRegularMode(e.OldMode) || !IsObjectId(e.OldOid) || e.OldOid.All(c => c == '0'))
            {
                continue; // was a symlink: every line is in the diff, as for an added file
            }

            targets.Add(new BlobTarget(path, e.NewOid, e.OldOid));
        }

        return (targets, incomplete);
    }

    /// <summary>
    /// Compares the key blocks of <paramref name="targets"/> (each with an old blob), smallest first. Sizes come first from one
    /// <c>cat-file --batch-check</c>: a file whose old or new version is over <see cref="BlobScan.Caps.Text"/>, or that would take the bytes
    /// read past <see cref="BlobScan.Caps.TextTotal"/>, is listed (<see cref="BlobScan.TextOverCap"/>, with the larger size) and not read.
    /// Any object missing fails closed.
    /// </summary>
    /// <param name="caps">The caps; null: <see cref="BlobScan.Caps.Default"/> (tests use smaller ones).</param>
    internal static async Task<TextKeyScanResult> ScanTextBlobsAsync(string top, IReadOnlyList<BlobTarget> targets, CancellationToken ct, BlobScan.Caps? caps = null)
    {
        var cap = caps ?? BlobScan.Caps.Default;
        if (targets.Count == 0)
        {
            return TextKeyScanResult.None;
        }

        var findings = new List<SecretFinding>();
        var skipped = new List<ScanSkip>();
        var sizes = await BatchCheckAsync(top, [.. targets.SelectMany(t => new[] { t.NewOid, t.OldOid! }).Distinct(StringComparer.Ordinal)], ct).ConfigureAwait(false);
        long? Size(string oid) => sizes.TryGetValue(oid, out var s) ? s : null;

        var blobsRead = 0;
        long textBytes = 0;
        await using var reader = BatchReader.Start(top, WorkTreeTimeout, ct);
        foreach (var t in targets.OrderBy(t => Size(t.NewOid) ?? -1).ThenBy(t => t.Path, StringComparer.Ordinal))
        {
            if (Size(t.NewOid) is not { } size || Size(t.OldOid!) is not { } oldSize)
            {
                findings.Add(new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere(t.Path)));
                continue;
            }

            // BatchReader refuses a blob over BlobScan.TextCap, so the caps are checked before anything is read.
            if (size > cap.Text || oldSize > cap.Text || textBytes + size + oldSize > cap.TextTotal)
            {
                skipped.Add(new ScanSkip(t.Path, Math.Max(size, oldSize), BlobScan.TextOverCap));
                continue;
            }

            var data = await reader.NextAsync(t.NewOid).ConfigureAwait(false);
            blobsRead++;
            var oldData = data is null ? null : await reader.NextAsync(t.OldOid!).ConfigureAwait(false);
            blobsRead += data is null ? 0 : 1;
            if (data is null || oldData is null)
            {
                findings.Add(new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere(t.Path)));
                continue;
            }

            textBytes += data.Length + oldData.Length;
            var starts = BlobScan.ChangedBlockStarts(BlobScan.TextLines(data), BlobScan.TextLines(oldData));
            if (starts.Count > 0)
            {
                findings.AddRange(BlobScan.ScanLines(starts, BlobScan.TextWhere(t.Path)));
            }
        }

        await reader.FinishAsync().ConfigureAwait(false);
        return new TextKeyScanResult(findings, skipped, blobsRead);
    }
}
