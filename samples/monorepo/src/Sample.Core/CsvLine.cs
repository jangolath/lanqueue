using System.Text;

namespace Sample.Core;

/// <summary>Parses and formats one RFC 4180 line: commas, double-quoted fields, "" inside quotes.</summary>
public static class CsvLine
{
    public static IReadOnlyList<string> Parse(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            throw new FormatException("Unterminated quoted field.");
        }

        fields.Add(field.ToString());
        return fields;
    }

    public static string Format(IEnumerable<string> fields) =>
        string.Join(',', fields.Select(f => f.AsSpan().IndexOfAny(",\"\n") >= 0 ? "\"" + f.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : f));
}
