namespace LanQueue.Policy;

public static class ShellQuote
{
    /// <summary>Every character PowerShell's tokenizer accepts as a single quote: ' and the typographic ‘ ’ ‚ ‛.</summary>
    private static readonly char[] PowerShellSingleQuotes = [(char)0x27, (char)0x2018, (char)0x2019, (char)0x201A, (char)0x201B];

    public static string Posix(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// A PowerShell single-quoted literal. Inside one, the only special characters are the five single quotes, and each is escaped
    /// by doubling it; missing any of them lets an argument close the string early and run code.
    /// </summary>
    public static string PowerShell(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            sb.Append(c);
            if (Array.IndexOf(PowerShellSingleQuotes, c) >= 0)
            {
                sb.Append(c);
            }
        }

        return sb.Append('\'').ToString();
    }
}
