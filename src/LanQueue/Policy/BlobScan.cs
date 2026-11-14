using System.Text;
using System.Text.RegularExpressions;

namespace LanQueue.Policy;

/// <summary>
/// An uncommitted file whose content the secret scan did not read in full: it was over a cap. Reported, never refused. <see cref="Reason"/>
/// is <see cref="BlobScan.BinaryOverFileCap"/>, <see cref="BlobScan.BinaryOverTotalCap"/>, <see cref="BlobScan.MaskedTextOverCap"/> or
/// <see cref="BlobScan.MaskedTextOverTotalCap"/> for a file git shows as binary (0.9.2), and <see cref="BlobScan.TextOverCap"/> for a
/// changed text file that holds a private-key BEGIN line (0.10.1).
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

    /// <summary>
    /// A changed text file with a private-key BEGIN line whose old or new version is over <see cref="TextCap"/>, or that would take the text
    /// key scan past <see cref="TextTotalCap"/> (0.10.1): its key blocks were not compared.
    /// </summary>
    internal const string TextOverCap = "text_over_cap";

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

    /// <summary>
    /// The finding when a changed text file with a private-key BEGIN line could not be looked up or read for the key-block scan (0.10.1; fails
    /// closed: the run is refused).
    /// </summary>
    internal const string TextIncompleteRule = "scan-incomplete (refusing: a changed text file with a private-key block could not be read)";

    /// <summary>The "where" of a finding of the text key-block scan (0.10.1); the path is sanitised.</summary>
    internal static string TextWhere(string path) => $"uncommitted file {Jobs.JobRunner.SafeLine(path)}{TextWhereSuffix}";

    internal const string TextWhereSuffix = " (key block changed in place)";

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

    /// <summary>The lines of an ordinary text file (UTF-8, invalid bytes replaced; a trailing CR dropped, as on both sides of a comparison).</summary>
    internal static List<string> TextLines(byte[] data) => SplitLines(Encoding.UTF8.GetString(data));

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
    /// scanned again when any line of its block (up to the next line holding <c>-----END</c>) is new, so a key replaced in place (same header, new
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
    /// The text key-block scan (0.10.1): only the old BEGIN lines that are unchanged themselves but start a changed key block
    /// (<see cref="TextKeyBlockChanged"/>). Every other new line is a line the overlay diff already shows as added (and scans), so it is left
    /// out here. Lines are matched by content (set membership), as in <see cref="NewContent"/>: a new key added under a BEGIN line identical
    /// to an old one can be reported twice, once by the diff and once here (harmless: either refuses).
    /// </summary>
    internal static List<string> ChangedBlockStarts(IReadOnlyList<string> lines, IReadOnlyList<string> oldLines)
    {
        var known = new HashSet<string>(oldLines, StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("-----", StringComparison.Ordinal) && known.Contains(line) && SecretScanner.StartsASecretBlock(line) && TextKeyBlockChanged(lines, i, known))
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>The longest block the text key-block scan looks for an END line in (0.10.1).</summary>
    internal const int TextMaxBlockLines = 8192;

    /// <summary>
    /// Whether the block that starts at <paramref name="begin"/> in a text file is a key block with a new line (0.10.1). It counts only when
    /// it is terminated, a line holding <c>-----END</c> (any case) within <see cref="TextMaxBlockLines"/> lines, and every line strictly
    /// between BEGIN and END looks like key material (<see cref="KeyLine"/>), at least one of them a base64 line of
    /// <see cref="MinBodyChars"/> or more characters. The new line must be one of the lines strictly between BEGIN and END: an edit to the
    /// END line alone does not count. A real key replaced in place keeps its END line and its base64 body; header and footer strings in code
    /// (a parser, a scanner, a test), adjacent or with code between them, are no key, so ordinary edits near them are never refused. A BEGIN
    /// line that itself holds <c>-----END</c> (a one-line JSON or escaped key) has no block.
    /// </summary>
    private static bool TextKeyBlockChanged(IReadOnlyList<string> lines, int begin, HashSet<string> known)
    {
        if (lines[begin].Contains("-----END", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool changed = false, body = false;
        for (var j = begin + 1; j < lines.Count && j <= begin + TextMaxBlockLines; j++)
        {
            if (lines[j].Contains("-----END", StringComparison.OrdinalIgnoreCase))
            {
                return changed && body;
            }

            var length = KeyLine(lines[j], out var strong);
            if (length < 0)
            {
                return false;
            }

            body |= length >= MinBodyChars && strong;
            changed |= !known.Contains(lines[j]);
        }

        return false; // no END line within the cap: not a key block
    }

    /// <summary>
    /// A text key block needs at least one base64 body line this long (after stripping) that also holds a digit, <c>+</c> or <c>/</c>, so
    /// header and footer strings alone, banners (<c># =====</c>) and long identifiers are no key.
    /// </summary>
    internal const int MinBodyChars = 16;

    /// <summary>A line longer than this is never key material (a one-line PEM body of a 4096-bit key is about 3.2 KB).</summary>
    internal const int MaxKeyLineChars = 16 * 1024;

    /// <summary>Whether a line can sit inside an armored key (0.10.1): <see cref="KeyLine"/> is not -1.</summary>
    internal static bool LooksLikeKeyLine(string line) => KeyLine(line, out _) >= 0;

    /// <summary>Single-character markers stripped, as runs, from the start of a key line: whitespace, quotes, concatenation, comments, a YAML dash.</summary>
    private static readonly char[] KeyLineLead = [' ', '\t', '\r', '\f', '\v', '"', '\'', '+', '.', '&', ',', ';', '#', '*', '>', '|', '-', '/'];

    /// <summary>Single-character markers stripped, as runs, from the end of a key line: whitespace, quotes, concatenation, a continuation.</summary>
    private static readonly char[] KeyLineTrail = [' ', '\t', '\r', '\f', '\v', '"', '\'', '+', '.', '&', ',', ';', '_', '\\', '|'];

    /// <summary>Escapes and character references stripped from the end of a key line (with or without the reference's ';').</summary>
    private static readonly string[] KeyLineSuffixes =
        [@"\r\n", @"\n", "&#xD;", "&#xd;", "&#13;", "&#xA;", "&#xa;", "&#10;", "&#xD", "&#xd", "&#13", "&#xA", "&#xa", "&#10"];

    /// <summary>
    /// What a line inside an armored key is (0.10.1): -1 when it cannot be key material, 0 for a blank line or an armor header
    /// (<c>Name: value</c>, such as Proc-Type, DEK-Info, Comment, Version), else the length of its base64 (<c>[A-Za-z0-9+/=]</c>). First,
    /// repeatedly, whitespace and the syntax a key is quoted or commented in are stripped: quotes and a one-letter string prefix or suffix
    /// (<c>b"</c>, <c>r"</c>, <c>"u8</c>), concatenation (<c>+</c>, <c>.</c>, <c>..</c>, <c>&amp;</c>, VB's <c>&amp; _</c>, <c>||</c>, <c>,</c>,
    /// <c>;</c>), a trailing <c>\n</c> or <c>\r\n</c> escape, a trailing <c>\</c> continuation, XML character references for CR and LF, comment
    /// and quote markers (<c>#</c>, <c>//</c>, <c>*</c>, <c>&gt;</c>) and a YAML sequence dash. Runs of single-character markers go in one
    /// step. A line over <see cref="MaxKeyLineChars"/> is -1. <paramref name="strong"/>: the base64 holds a digit, <c>+</c> or <c>/</c>.
    /// </summary>
    internal static int KeyLine(string line, out bool strong)
    {
        strong = false;
        if (line.Length > MaxKeyLineChars)
        {
            return -1;
        }

        var t = line.AsSpan();
        while (true)
        {
            var before = t.Length;
            t = t.TrimStart(KeyLineLead);
            for (var stripped = true; stripped;)
            {
                stripped = false;
                t = t.TrimEnd(KeyLineTrail);
                foreach (var s in KeyLineSuffixes)
                {
                    if (t.EndsWith(s, StringComparison.Ordinal))
                    {
                        t = t[..^s.Length];
                        stripped = true;
                        break;
                    }
                }
            }

            if (t.Length >= 2 && char.IsAsciiLetter(t[0]) && t[1] is '"' or '\'')
            {
                t = t[2..]; // b"..., r'...
            }

            for (var k = 1; k <= 2; k++)
            {
                if (t.Length > k && t[^(k + 1)] is '"' or '\'' && IsAsciiLettersOrDigits(t[^k..]))
                {
                    t = t[..^k]; // ..."u8, ..."L
                    break;
                }
            }

            if (t.Length == before)
            {
                break;
            }
        }

        if (t.IsEmpty)
        {
            return 0;
        }

        var base64 = true;
        var mixed = false;
        foreach (var c in t)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                base64 = false;
                break;
            }

            mixed |= char.IsAsciiDigit(c) || c is '+' or '/';
        }

        if (base64)
        {
            strong = mixed;
            return t.Length;
        }

        var colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0)
        {
            return -1;
        }

        foreach (var c in t[..colon])
        {
            if (!(char.IsAsciiLetter(c) || c == '-'))
            {
                return -1;
            }
        }

        return 0;
    }

    private static bool IsAsciiLettersOrDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The longest block <see cref="BlockChanged"/> looks at: a key with no END line within it is judged on these lines only.</summary>
    private const int MaxBlockLines = 512;

    /// <summary>
    /// Whether any line of the block that starts at <paramref name="begin"/> is new: an OpenSSH key's first body line is the same for every
    /// key of a type, a PGP block starts with headers and a blank line, an encrypted PEM with Proc-Type and DEK-Info, and a change may be only
    /// in the middle. The block ends at the first line that holds <c>-----END</c> anywhere (an indented key in a YAML block scalar too), and
    /// after at most <see cref="MaxBlockLines"/> lines; a BEGIN line that itself holds <c>-----END</c> (a one-line JSON or escaped key) has
    /// no block, so an unclosed BEGIN can never reach a far-away edit.
    /// </summary>
    private static bool BlockChanged(IReadOnlyList<string> lines, int begin, HashSet<string> known)
    {
        if (lines[begin].Contains("-----END", StringComparison.Ordinal))
        {
            return false;
        }

        for (var j = begin + 1; j < lines.Count && j <= begin + MaxBlockLines && !lines[j].Contains("-----END", StringComparison.Ordinal); j++)
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
