using System.Globalization;
using LanQueue.Policy;

namespace LanQueue.Payload;

/// <summary>
/// The text key-block scan (0.10.1): a private key replaced in place in an ordinary text file. The overlay diff (<c>-U0</c>) shows only the
/// changed lines, so a key whose body changed while its BEGIN line stayed the same never shows its BEGIN line to the scan. So:
/// <list type="number">
/// <item>one rename-aware (<c>-M</c>, like the overlay diff and its file list) <c>--raw --numstat -z</c> of the snapshot's temporary index
/// against HEAD says which files are text, modified (or renamed or copied) with added lines, and their old and new object ids;</item>
/// <item><c>git grep --cached -l -z -i -F -e -----BEGIN</c> under the same index, given only those files, keeps the ones whose new
/// version has a BEGIN line at all (a BEGIN line only the old version has is a removed line, which the diff scan sees already); one of
/// them that cannot be looked up, or whose path git gave in bytes that are not UTF-8, fails closed
/// (<see cref="BlobScan.TextIncompleteRule"/>);</item>
/// <item>their sizes come from one <c>git cat-file --batch-check</c>, so a file over the caps is listed (<see cref="BlobScan.TextOverCap"/>)
/// before anything is read; the rest are read by object id from one <c>git cat-file --batch</c> (no filters, no textconv);</item>
/// <item>only the unchanged BEGIN lines of a changed key block are scanned (<see cref="BlobScan.ChangedBlockStarts"/>: terminated within
/// 8192 lines, key material between BEGIN and END): every other new line is in the diff already, and a header string in code is no key.</item>
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
        var (raw, numstat, undecodable) = ParseRawNumstat(
            await Git(top, env, ct, [.. TextDiff, "--raw", "--numstat", "-z", "--full-index", "--no-abbrev", "HEAD"]).ConfigureAwait(false));
        var changed = GrepCandidates(raw, numstat);
        var keyed = await KeyBearingPathsAsync(top, env, changed, ct).ConfigureAwait(false);
        if (keyed.Count == 0)
        {
            return undecodable.Count == 0 ? TextKeyScanResult.None : new TextKeyScanResult(undecodable, [], 0);
        }

        var (targets, incomplete) = PlanTextReads(raw, numstat, keyed);
        var read = await ScanTextBlobsAsync(top, targets, ct).ConfigureAwait(false);
        return read with { Findings = [.. undecodable, .. incomplete, .. read.Findings] };
    }

    /// <summary>
    /// The files that can need a read, to grep: changed (not added or deleted) text files with added lines, or with an unreadable count. A
    /// path holding U+FFFD (bytes that are not UTF-8) cannot be named back to git and is left out: <see cref="ParseRawNumstat"/> has already
    /// made it a scan-incomplete finding.
    /// </summary>
    internal static List<string> GrepCandidates(IReadOnlyDictionary<string, RawEntry> raw, IReadOnlyDictionary<string, long?> numstat) =>
        [.. raw.Where(r => r.Value.Status is not ('A' or 'D')
                && !(numstat.TryGetValue(r.Key, out var added) && added is null or 0)
                && !r.Key.Contains('\uFFFD', StringComparison.Ordinal))
            .Select(r => r.Key)
            .Order(StringComparer.Ordinal)];

    /// <summary>The most characters of paths given to one git grep (Windows limits a command line to 32,767).</summary>
    private const int GrepPathChars = 16_000;

    /// <summary>
    /// Which of <paramref name="paths"/> (text files per numstat; no <c>-I</c>, so git grep's own binary test can never drop one) hold
    /// <c>-----BEGIN</c> in the snapshot's index, in any case: a superset of the files with a line <see cref="SecretScanner.StartsASecretBlock"/> matches. The paths go as literal pathspecs,
    /// in batches of at most <see cref="GrepPathChars"/> characters. No match is git grep's exit code 1.
    /// </summary>
    private static async Task<HashSet<string>> KeyBearingPathsAsync(string top, Dictionary<string, string> env, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var keyed = new HashSet<string>(StringComparer.Ordinal);
        string[] grep = ["grep", "--cached", "-l", "-z", "-i", "-F", "--full-name", "--no-color", "--no-textconv", "--no-recurse-submodules", "-e", "-----BEGIN", "--"];
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
    /// One <c>--raw --numstat -z</c> output, walked token by token: a token starting with ':' is a raw record (":oldmode newmode oldoid newoid
    /// status", then its path, or for R and C its old and new paths); any other token is a numstat record ("added TAB deleted TAB path", or
    /// "added TAB deleted TAB" then the old and new paths). A record's paths are always taken as its next tokens, so a path that starts with
    /// ':' or holds a tab is never read as a record. Both maps are keyed by the new path; a numstat count is null for a file git shows as
    /// binary ("-") and -1 when it cannot be read. Scan-incomplete findings (fail closed), once per path: every new path seen twice in raw or
    /// in numstat, whatever the file (two names git gave in bytes that are not UTF-8 can decode to the same string, which would hide one of
    /// them from this scan and from the binary scan alike), and every text candidate (modified, renamed, copied or type-changed, text, with
    /// added lines) whose path holds U+FFFD: it cannot be named back to git to be grepped. Other such files (binary, added, deleted) pass.
    /// </summary>
    internal static (Dictionary<string, RawEntry> Raw, Dictionary<string, long?> Numstat, List<SecretFinding> Undecodable) ParseRawNumstat(string output)
    {
        var raw = new Dictionary<string, RawEntry>(StringComparer.Ordinal);
        var numstat = new Dictionary<string, long?>(StringComparer.Ordinal);
        var flagged = new SortedSet<string>(StringComparer.Ordinal);
        void Check(string path, bool duplicate)
        {
            if (duplicate)
            {
                flagged.Add(path);
            }
        }

        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (token.Length == 0)
            {
                continue;
            }

            if (token[0] == ':')
            {
                var f = token[1..].Split(' ');
                var status = f.Length == 5 && f[4].Length > 0 ? f[4][0] : '?';
                var pair = status is 'R' or 'C';
                var at = i + (pair ? 2 : 1);
                if (at >= parts.Length)
                {
                    break;
                }

                if (status != '?')
                {
                    Check(parts[at], raw.ContainsKey(parts[at]));
                    raw[parts[at]] = new RawEntry(f[0], f[1], f[2], f[3], status, pair ? parts[i + 1] : null);
                }

                i = at;
                continue;
            }

            var n = token.Split('\t', 3);
            if (n.Length != 3)
            {
                continue;
            }

            string path;
            if (n[2].Length == 0)
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
                path = n[2];
            }

            Check(path, numstat.ContainsKey(path));
            numstat[path] = n[0] == "-" ? null : long.TryParse(n[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) ? added : -1;
        }

        foreach (var (path, e) in raw)
        {
            if (e.Status is 'M' or 'R' or 'C' or 'T' && !(numstat.TryGetValue(path, out var added) && added is null or 0)
                && path.Contains('\uFFFD', StringComparison.Ordinal))
            {
                flagged.Add(path);
            }
        }

        return (raw, numstat, [.. flagged.Select(p => new SecretFinding(BlobScan.TextIncompleteRule, BlobScan.TextWhere(p)))]);
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
