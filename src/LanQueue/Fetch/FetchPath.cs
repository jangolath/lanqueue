using System.Text.RegularExpressions;
using LanQueue.Jobs;

namespace LanQueue.Fetch;

/// <summary>
/// The local rules for a relative path a worker sends back: checked again here, whatever the worker script did. A path that passes can be
/// written below the output folder on Windows, macOS and Linux alike, and names exactly one file there.
/// </summary>
internal static partial class FetchPath
{
    internal const int MaxPathChars = 260;

    internal const int MaxSegmentChars = 255;

    /// <summary>
    /// Names Windows reserves for devices, with or without an extension, and with any spaces before it (CON, NUL.txt, "NUL .txt", COM1,
    /// LPT9.log, COM¹, CONIN$...): Windows strips those spaces and opens the device.
    /// </summary>
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³]) *(\..*)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Reserved();

    /// <summary>Characters Windows cannot store in a name (':' would also open an alternate data stream), plus the backslash.</summary>
    [GeneratedRegex(@"[<>:""|?*\\]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WindowsInvalid();

    /// <summary>Why <paramref name="rel"/> must not be written, or null when it is safe.</summary>
    public static string? Problem(string rel)
    {
        if (rel.Length == 0)
        {
            return "empty path";
        }

        if (rel.Length > MaxPathChars)
        {
            return $"path longer than {MaxPathChars} characters";
        }

        if (JobRunner.SafeLine(rel) != rel)
        {
            return "control or invisible characters in the name";
        }

        if (WindowsInvalid().IsMatch(rel))
        {
            return "a character Windows cannot store (< > : \" | ? * or backslash)";
        }

        if (rel.StartsWith('/'))
        {
            return "absolute path";
        }

        if (rel.EndsWith(FetchReceiver.PartialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return "a name ending in " + FetchReceiver.PartialSuffix + " (reserved for files lanq is still receiving)";
        }

        foreach (var s in rel.Split('/'))
        {
            if (s.Length == 0 || s is "." or "..")
            {
                return "empty, '.' or '..' path segment";
            }

            if (s.Length > MaxSegmentChars)
            {
                return $"a name longer than {MaxSegmentChars} characters";
            }

            if (s.EndsWith('.') || s.EndsWith(' '))
            {
                return "a name ending in a dot or space";
            }

            if (Reserved().IsMatch(s))
            {
                return "a Windows reserved device name";
            }
        }

        return null;
    }
}
