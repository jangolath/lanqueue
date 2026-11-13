using System.Text;
using System.Text.RegularExpressions;

namespace LanQueue.Policy;

/// <summary>
/// An uncommitted file git shows as binary whose content the secret scan did not read in full (0.9.2): it was over a cap. Reported, never
/// refused. <see cref="Reason"/> is <see cref="BlobScan.BinaryOverFileCap"/>, <see cref="BlobScan.BinaryOverTotalCap"/> or
/// <see cref="BlobScan.MaskedTextOverCap"/>.
/// </summary>
/// <param name="Path">The file's path in the repository, as git gives it (unsanitised; sanitised wherever it is shown).</param>
/// <param name="Bytes">The file's size.</param>
public sealed record ScanSkip(string Path, long Bytes, string Reason);

/// <summary>
/// The secret scan of the content of uncommitted files git shows as binary (0.9.2): files a .gitattributes entry (-diff, binary) hides from
/// diffs, UTF-16 text, files with a NUL byte and files over core.bigFileThreshold. Their text never appears in the overlay diff, so their blobs
/// are read (by <see cref="Payload.GitPayloadBuilder"/>, without filters or textconv) and turned into lines here:
/// <list type="bullet">
/// <item>a UTF-16 byte order mark: decoded as UTF-16 text;</item>
/// <item>no NUL byte in the first <see cref="ClassifyBytes"/> bytes: masked text, decoded as UTF-8 (invalid bytes replaced);</item>
/// <item>otherwise a real binary: a strings projection, runs of 4 or more printable ASCII characters or tabs, and runs of 4 or more such
/// characters each followed by a zero byte (UTF-16LE without a mark), each run on its own line.</item>
/// </list>
/// The lines are scanned in line-cut chunks by <see cref="ScanLines"/>. Nothing here executes anything; values are never echoed.
/// </summary>
internal static class BlobScan
{
    /// <summary>Largest real binary whose strings are scanned.</summary>
    internal const long BinaryFileCap = 8L * 1024 * 1024;

    /// <summary>The most real-binary bytes scanned in one snapshot; files past it are listed, not read.</summary>
    internal const long BinaryTotalCap = 64L * 1024 * 1024;

    /// <summary>Largest masked (attribute-hidden) or UTF-16 text file scanned.</summary>
    internal const long TextCap = 32L * 1024 * 1024;

    /// <summary>The most masked or UTF-16 text bytes scanned in one snapshot; files past it are listed, not read.</summary>
    internal const long TextTotalCap = 256L * 1024 * 1024;

    /// <summary>How many leading bytes decide text or binary: the same 8000 git looks at.</summary>
    internal const int ClassifyBytes = 8000;

    /// <summary>A chunk is cut at a line end once it reaches about this many characters.</summary>
    internal const int ChunkChars = 1024 * 1024;

    /// <summary>
    /// Each chunk starts with the previous chunk's last lines holding up to this many non-blank lines (blank and whitespace-only lines
    /// between them come along without counting), at most about <see cref="ChunkChars"/> characters.
    /// </summary>
    internal const int OverlapLines = 64;

    internal const string BinaryOverFileCap = "binary_over_file_cap";

    internal const string BinaryOverTotalCap = "binary_over_total_cap";

    internal const string MaskedTextOverCap = "masked_text_over_cap";

    internal const string MaskedTextOverTotalCap = "masked_text_over_total_cap";

    /// <summary>The finding when a file git shows as binary could not be looked up or read (fails closed: the run is refused).</summary>
    internal const string IncompleteRule = "scan-incomplete (refusing: a file git shows as binary could not be read)";

    internal enum Kind
    {
        Utf16Text,
        MaskedText,
        Binary,
    }

    /// <summary>The "where" of a finding in a file git shows as binary; the path is sanitised.</summary>
    internal static string Where(string path) => $"uncommitted file {Jobs.JobRunner.SafeLine(path)}{WhereSuffix}";

    internal const string WhereSuffix = " (binary per git)";

    /// <summary>UTF-16 (a byte order mark), masked text (no NUL in the first <see cref="ClassifyBytes"/> bytes) or a real binary.</summary>
    internal static Kind Classify(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
        {
            return Kind.Utf16Text;
        }

        return data[..Math.Min(data.Length, ClassifyBytes)].IndexOf((byte)0) < 0 ? Kind.MaskedText : Kind.Binary;
    }

    /// <summary>The caps of one snapshot's scan: per file and in total, for real binaries and for masked or UTF-16 text.</summary>
    internal sealed record Caps(long BinaryFile, long BinaryTotal, long Text, long TextTotal)
    {
        public static readonly Caps Default = new(BinaryFileCap, BinaryTotalCap, TextCap, TextTotalCap);
    }

    /// <summary>
    /// The lines scanned for <paramref name="data"/> of <paramref name="kind"/>: text lines (a trailing CR dropped) or strings runs. Text
    /// with a byte order mark or any NUL byte also gets the strings runs, so a mark on a file that is not UTF-16, or UTF-16 or UTF-32 text
    /// after a NUL-free start, cannot hide what the decoding missed.
    /// </summary>
    internal static List<string> Lines(byte[] data, Kind kind) => kind switch
    {
        Kind.Utf16Text => [.. SplitLines(data[1] == 0xFE
            ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
            : Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)), .. Strings(data)],
        Kind.MaskedText => Array.IndexOf(data, (byte)0) < 0
            ? SplitLines(Encoding.UTF8.GetString(data))
            : [.. SplitLines(Encoding.UTF8.GetString(data)), .. Strings(data)],
        _ => Strings(data),
    };

    private static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return [.. lines];
    }

    private static bool Printable(byte b) => b == 0x09 || (b >= 0x20 && b <= 0x7E);

    /// <summary>
    /// The strings projection of a binary: 8-bit runs of 4 or more printable ASCII characters or tabs, then UTF-16LE runs (4 or more such
    /// characters, each followed by a zero byte, at either byte alignment, like <c>strings -el</c>; this also finds UTF-16BE), then UTF-32LE
    /// runs (each followed by three zero bytes, at any of the four alignments; this also finds UTF-32BE).
    /// </summary>
    internal static List<string> Strings(ReadOnlySpan<byte> data)
    {
        var runs = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 4)
            {
                runs.Add(sb.ToString());
            }

            sb.Clear();
        }

        foreach (var b in data)
        {
            if (Printable(b))
            {
                sb.Append((char)b);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        for (var align = 0; align < 2; align++)
        {
            for (var i = align; i + 1 < data.Length; i += 2)
            {
                if (Printable(data[i]) && data[i + 1] == 0)
                {
                    sb.Append((char)data[i]);
                }
                else
                {
                    Flush();
                }
            }

            Flush();
        }

        for (var align = 0; align < 4; align++)
        {
            for (var i = align; i + 3 < data.Length; i += 4)
            {
                if (Printable(data[i]) && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 0)
                {
                    sb.Append((char)data[i]);
                }
                else
                {
                    Flush();
                }
            }

            Flush();
        }

        return runs;
    }

    /// <summary>
    /// The lines of a modified file's new version to scan: those its old version does not have, as the diff's added lines. One exception:
    /// an old line that starts a secret block (<c>-----BEGIN ... PRIVATE KEY</c>, <see cref="SecretScanner.StartsASecretBlock"/>) is
    /// scanned again when any line of its block (up to the next <c>-----END</c> line) is new, so a key replaced in place (same header, new
    /// body) is found. An unchanged block or single-line secret stays known and is not re-flagged.
    /// </summary>
    internal static List<string> NewContent(IReadOnlyList<string> lines, IReadOnlyList<string> oldLines)
    {
        var known = new HashSet<string>(oldLines, StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!known.Contains(line) || (SecretScanner.StartsASecretBlock(line) && BlockChanged(lines, i, known)))
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether any line of the block that starts at <paramref name="begin"/> (up to the next line starting with <c>-----END</c>, or the end)
    /// is new: an OpenSSH key's first body line is the same for every key of a type, a PGP block starts with headers and a blank line, an
    /// encrypted PEM with Proc-Type and DEK-Info, and a change may be only in the middle.
    /// </summary>
    private static bool BlockChanged(IReadOnlyList<string> lines, int begin, HashSet<string> known)
    {
        for (var j = begin + 1; j < lines.Count && !lines[j].StartsWith("-----END", StringComparison.Ordinal); j++)
        {
            if (!known.Contains(lines[j]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans <paramref name="lines"/> with every secret rule in line-cut chunks of about <see cref="ChunkChars"/> characters. Each chunk
    /// after the first starts with the previous one's last whole lines holding <see cref="OverlapLines"/> non-blank lines (blank lines
    /// between them do not count; at most about one more chunk of them), so a match that spans lines is still seen; a line is never cut, and a line over the chunk size is a chunk of its own (with
    /// that overlap). Each rule keeps its 2 s timeout per chunk, and a timeout fails closed (<see cref="SecretScanner.TimeoutRule"/>).
    /// One finding per rule.
    /// </summary>
    internal static IReadOnlyList<SecretFinding> ScanLines(IReadOnlyList<string> lines, string where) => ScanLines(lines, where, null);

    internal static IReadOnlyList<SecretFinding> ScanLines(IReadOnlyList<string> lines, string where, IEnumerable<(string Name, Regex Pattern)>? rules)
    {
        var found = new Dictionary<string, SecretFinding>(StringComparer.Ordinal);
        void Scan(int from, int to)
        {
            var text = string.Join('\n', lines.Skip(from).Take(to - from));
            var hits = rules is null ? SecretScanner.ScanText(text, where) : SecretScanner.ScanText(text, where, rules);
            foreach (var f in hits)
            {
                found.TryAdd(f.Rule, f);
            }
        }

        int start = 0, fresh = 0;
        long length = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var size = lines[i].Length + 1L;
            if (i > fresh && length + size > ChunkChars)
            {
                Scan(start, i);

                // The next chunk starts with the last whole lines of this one: at least one, up to OverlapLines non-blank ones (blank lines,
                // which a pattern's whitespace can span, do not count), about ChunkChars characters at most.
                var o = i;
                long overlap = 0;
                var counted = 0;
                while (o > start && counted < OverlapLines && (o == i || overlap + lines[o - 1].Length + 1 <= ChunkChars))
                {
                    o--;
                    overlap += lines[o].Length + 1;
                    if (!string.IsNullOrWhiteSpace(lines[o]))
                    {
                        counted++;
                    }
                }

                (start, fresh, length) = (o, i, overlap);
            }

            length += size;
        }

        if (fresh < lines.Count)
        {
            Scan(start, lines.Count);
        }

        return [.. found.Values];
    }
}
