using System.Text;

namespace Sample.Core;

/// <summary>Splits text into lower-case words (letters, digits and inner apostrophes), and counts them.</summary>
public static class Tokenizer
{
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || (c == '\'' && current.Length > 0))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else
            {
                Flush(words, current);
            }
        }

        Flush(words, current);
        return words;
    }

    private static void Flush(List<string> words, StringBuilder current)
    {
        var word = current.ToString().TrimEnd('\'');
        if (word.Length > 0)
        {
            words.Add(word);
        }

        current.Clear();
    }

    public static IReadOnlyDictionary<string, int> Counts(string text) =>
        Words(text).GroupBy(w => w, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
