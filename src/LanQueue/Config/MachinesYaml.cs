using System.Text.RegularExpressions;

namespace LanQueue.Config;

/// <summary>Why lanq makeready --add will not insert into a machines.yml by itself (the entry is then printed to paste by hand).</summary>
internal sealed class InsertRefusedException(string message) : Exception(message);

/// <summary>
/// Adds one machine to the text of a machines.yml without re-serialising the YAML, so every comment, quote and blank line stays byte for
/// byte: the item's lines go after the last non-blank, non-comment line of the top-level <c>machines:</c> block (commented examples after it
/// stay after it), re-indented to the dash column of the block's first item, with the file's own line ending. Anything unusual is refused
/// rather than guessed at: flow style (<c>machines: [...]</c>), several YAML documents, a missing or repeated <c>machines:</c>, tabs in the
/// block, or a block with no item to line up with. The caller still checks that the result loads and holds exactly one more machine.
/// </summary>
internal static partial class MachinesYaml
{
    /// <summary>The result: the whole new text, the 1-based line the item follows, and the item's lines exactly as inserted.</summary>
    internal sealed record Insertion(string Text, int AfterLine, IReadOnlyList<string> Lines);

    [GeneratedRegex(@"^machines[ \t]*:(?<rest>.*)\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(---|\.\.\.)([ \t].*)?\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DocumentMarkerRegex();

    /// <param name="text">The file's text (without a BOM).</param>
    /// <param name="itemLines">The item as <c>  - key: value</c> (the dash at column 2) and <c>    key: value</c> lines.</param>
    /// <exception cref="InsertRefusedException">The file is not one lanq edits by itself.</exception>
    public static Insertion Insert(string text, IReadOnlyList<string> itemLines)
    {
        if (itemLines.Count == 0 || itemLines.Any(l => !l.StartsWith("  ", StringComparison.Ordinal) || l.Contains('\n', StringComparison.Ordinal) || l.Contains('\r', StringComparison.Ordinal)))
        {
            throw new ArgumentException("item lines must start with two spaces and hold no line break", nameof(itemLines));
        }

        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text);
        string Line(int k) => text[lines[k].Start..lines[k].End];
        static bool Blank(string l) => l.Trim().Length == 0;
        static bool Comment(string l) => l.TrimStart().StartsWith('#');

        var content = false;
        var markers = 0;
        for (var k = 0; k < lines.Count; k++)
        {
            var l = Line(k);
            if (DocumentMarkerRegex().IsMatch(l))
            {
                if (l.StartsWith("...", StringComparison.Ordinal) || content || ++markers > 1)
                {
                    throw new InsertRefusedException("the file holds more than one YAML document (a --- or ... line)");
                }
            }
            else if (!Blank(l) && !Comment(l) && !l.StartsWith('%'))
            {
                content = true;
            }
        }

        var headers = Enumerable.Range(0, lines.Count).Where(k => HeaderRegex().IsMatch(Line(k))).ToList();
        if (headers.Count != 1)
        {
            throw new InsertRefusedException(headers.Count == 0 ? "there is no top-level machines: line" : "machines: appears more than once");
        }

        var header = headers[0];
        var rest = HeaderRegex().Match(Line(header)).Groups["rest"].Value.Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new InsertRefusedException(rest.StartsWith('[') || rest.StartsWith('{')
                ? "machines: is written in flow style (machines: [...])"
                : "machines: has a value on its own line (an anchor, alias or tag)");
        }

        // The block: every line up to the next top-level key (a column-0 line that is not blank, a comment or a "-" item).
        var end = header + 1;
        while (end < lines.Count && (Blank(Line(end)) || Comment(Line(end)) || Line(end)[0] is ' ' or '-'))
        {
            end++;
        }

        if (Enumerable.Range(header, end - header).Any(k => Line(k).Contains('\t', StringComparison.Ordinal)))
        {
            throw new InsertRefusedException("the machines: block contains a tab");
        }

        var items = Enumerable.Range(header + 1, end - header - 1).Where(k => !Comment(Line(k)) && !Blank(Line(k))).ToList();
        if (items.Count == 0 || !Line(items[0]).TrimStart().StartsWith('-'))
        {
            throw new InsertRefusedException("the machines: block has no machine to line the new one up with");
        }

        var dash = Line(items[0]).IndexOf('-', StringComparison.Ordinal);
        var last = items[^1];
        if (Line(last).Length - Line(last).TrimStart().Length < dash)
        {
            throw new InsertRefusedException("the machines: block's last line is indented less than its first machine");
        }

        var indent = new string(' ', dash);
        var shown = itemLines.Select(l => indent + l[2..]).ToList();
        var block = string.Concat(shown.Select(l => l + nl));
        var at = lines[last].Next;
        if (lines[last].Next == lines[last].End)
        {
            block = nl + block; // the last line had no line break (end of file)
        }

        return new Insertion(text[..at] + block + text[at..], last + 1, shown);
    }

    /// <summary>Each line's start, its end (before \r\n or \n) and where the next line starts.</summary>
    private static List<(int Start, int End, int Next)> SplitLines(string text)
    {
        var lines = new List<(int, int, int)>();
        var i = 0;
        while (i < text.Length)
        {
            var j = text.IndexOf('\n', i);
            if (j < 0)
            {
                lines.Add((i, text.Length, text.Length));
                break;
            }

            lines.Add((i, j > i && text[j - 1] == '\r' ? j - 1 : j, j + 1));
            i = j + 1;
        }

        return lines;
    }
}
