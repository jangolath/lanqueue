namespace LanQueue.Policy;

public static class ShellQuote
{
    public static string Posix(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string PowerShell(string s) =>
        "'" + s.Replace("'", "''", StringComparison.Ordinal)
               .Replace("‘", "‘‘", StringComparison.Ordinal)
               .Replace("’", "’’", StringComparison.Ordinal) + "'";
}
