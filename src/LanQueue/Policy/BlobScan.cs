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

    /// <summary>How many leading bytes decide text or binary: the same 8000 git looks at.</summary>
    internal const int ClassifyBytes = 8000;

    /// <summary>A chunk is cut at a line end once it reaches about this many characters.</summary>
    internal const int ChunkChars = 1024 * 1024;

    /// <summary>Each chunk starts with up to this many whole lines of the previous one.</summary>
    internal const int OverlapLines = 64;

    internal const string BinaryOverFileCap = "binary_over_file_cap";

    internal const string BinaryOverTotalCap = "binary_over_total_cap";

    internal const string MaskedTextOverCap = "masked_text_over_cap";

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

    /// <summary>The cap that applies to a file of <paramref name="kind"/>.</summary>
    internal static long CapFor(Kind kind) => kind == Kind.Binary ? BinaryFileCap : TextCap;

    /// <summary>The lines scanned for <paramref name="data"/> of <paramref name="kind"/>: text lines (a trailing CR dropped) or strings runs.</summary>
    internal static List<string> Lines(byte[] data, Kind kind) => kind switch
    {
        Kind.Utf16Text => SplitLines(data[1] == 0xFE
            ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
            : Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)),
        Kind.MaskedText => SplitLines(Encoding.UTF8.GetString(data)),
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
    /// characters, each followed by a zero byte, at either byte alignment, like <c>strings -el</c>).
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

        return runs;
    }

    /// <summary>
    /// Scans <paramref name="lines"/> with every secret rule in line-cut chunks of about <see cref="ChunkChars"/> characters. Each chunk
    /// after the first starts with the last <see cref="OverlapLines"/> whole lines of the previous one (at most about one more chunk of
    /// them), so a match that spans lines is still seen; a line is never cut, and a line over the chunk size is a chunk of its own (with
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

                // The next chunk starts with the last whole lines of this one: at least one, at most OverlapLines, about ChunkChars at most.
                var o = i;
                long overlap = 0;
                while (o > start && i - o < OverlapLines && (o == i || overlap + lines[o - 1].Length + 1 <= ChunkChars))
                {
                    o--;
                    overlap += lines[o].Length + 1;
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
