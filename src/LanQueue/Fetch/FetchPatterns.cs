using System.Text;
using System.Text.RegularExpressions;

namespace LanQueue.Fetch;

/// <summary>
/// lanq run --fetch patterns: relative to the job's working folder, forward slashes, and only <c>*</c> (any characters within one name),
/// <c>?</c> (one character within one name) and <c>**</c> (a whole segment: zero or more folders) as wildcards. Everything else in a pattern
/// is a literal from a small portable set, so a pattern can be turned into a regular expression without a single escape character.
/// </summary>
public static partial class FetchPatterns
{
    public const int MaxPatterns = 16;

    public const int MaxPatternLength = 200;

    /// <summary>
    /// The characters a pattern may use. Excludes, among others, backslash, ':' (drive letters, alternate data streams), quotes, '$', '[' and
    /// every control character, so nothing in a pattern means anything to sh, PowerShell, a regex engine or the Windows file system.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._+@=,~ /*?-]{1,200}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AllowedChars();

    [GeneratedRegex(@"^-", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LeadingDash();

    /// <summary>Absolute (leading /), a drive letter or ':' anywhere, a backslash: each refused with its own message.</summary>
    [GeneratedRegex(@"^/|^[A-Za-z]:|:|\\", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AbsoluteOrDrive();

    /// <summary>An empty, '.' or '..' segment (also a trailing slash).</summary>
    [GeneratedRegex(@"(^|/)\.{0,2}(/|\z)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BadSegment();

    /// <summary>'**' that is not a whole segment, or more than two stars.</summary>
    [GeneratedRegex(@"\*\*\*|[^/]\*\*|\*\*[^/]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BadDoubleStar();

    [GeneratedRegex(@"[\x00-\x1f\x7f-\x9f]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Control();

    /// <summary>Why <paramref name="pattern"/> is refused, or null when it is a valid fetch pattern.</summary>
    public static string? Problem(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return "is empty";
        }

        if (Control().IsMatch(pattern))
        {
            return "contains a control character";
        }

        if (pattern.Length > MaxPatternLength)
        {
            return $"is longer than {MaxPatternLength} characters";
        }

        if (LeadingDash().IsMatch(pattern))
        {
            return "starts with '-'";
        }

        if (AbsoluteOrDrive().IsMatch(pattern))
        {
            return "must be a relative path with forward slashes (no leading '/', drive letter, ':' or backslash)";
        }

        if (BadSegment().IsMatch(pattern))
        {
            return "has an empty, '.' or '..' segment";
        }

        if (BadDoubleStar().IsMatch(pattern))
        {
            return "uses '**' other than as a whole segment";
        }

        return AllowedChars().IsMatch(pattern)
            ? null
            : "may use only letters, digits, space, '.', '_', '+', '@', '=', ',', '~', '-', '/' and the wildcards '*', '?' and '**'";
    }

    /// <summary>Validates a list of patterns; throws <see cref="ArgumentException"/> naming the first bad one (sanitised by the caller).</summary>
    /// <param name="extra">
    /// --results' pattern (0.11.0, already valid): added at the end unless a pattern equals it (ordinal); it counts toward
    /// <see cref="MaxPatterns"/>.
    /// </param>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string> patterns, string? extra = null)
    {
        var adds = extra is not null && !patterns.Contains(extra, StringComparer.Ordinal);
        if (patterns.Count + (adds ? 1 : 0) > MaxPatterns)
        {
            throw new ArgumentException(extra is null
                ? $"--fetch can be given at most {MaxPatterns} times."
                : $"--fetch can be given at most {MaxPatterns} times (--results adds its path unless a --fetch names it).");
        }

        if (adds)
        {
            patterns = [.. patterns, extra!];
        }

        foreach (var p in patterns)
        {
            if (Problem(p) is { } why)
            {
                throw new ArgumentException($"--fetch pattern '{p}' {why}.");
            }
        }

        return patterns.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One anchored-free regular expression body for a validated pattern, valid both as a POSIX ERE (awk) and as a .NET regex: literals are
    /// bracketed ([.] and [+]), groups are plain parentheses, and there is no backslash anywhere.
    /// </summary>
    internal static string ToRegexBody(string pattern)
    {
        var segments = pattern.Split('/');
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            var s = segments[i];
            if (s == "**")
            {
                // Not last: zero or more folders. Last: everything below (at least one name).
                sb.Append(last ? "[^/]+(/[^/]+)*" : "([^/]+/)*");
                continue;
            }

            foreach (var c in s)
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    '.' => "[.]",
                    '+' => "[+]",
                    _ => c.ToString(),
                });
            }

            if (!last)
            {
                sb.Append('/');
            }
        }

        return sb.ToString();
    }

    /// <summary>All patterns as one alternation, for a POSIX ERE (anchored with ^ and $; worker paths never contain a newline there).</summary>
    internal static string ToEre(IReadOnlyList<string> patterns) => "^(" + string.Join('|', patterns.Select(p => "(" + ToRegexBody(p) + ")")) + ")$";

    /// <summary>All patterns as one .NET alternation, anchored with \z (a trailing newline never matches).</summary>
    internal static string ToDotNet(IReadOnlyList<string> patterns) => "^(" + string.Join('|', patterns.Select(p => "(" + ToRegexBody(p) + ")")) + @")\z";

    /// <summary>
    /// The local matcher: a path the worker sends must match one of the requested patterns (case-insensitively for a Windows worker). The
    /// non-backtracking engine matches in time linear in the name, so a many-star pattern cannot be made to explode by a crafted name.
    /// </summary>
    internal static Regex Matcher(IReadOnlyList<string> patterns, bool ignoreCase) =>
        new(
            ToDotNet(patterns),
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromSeconds(1));

    /// <summary>
    /// The folders the worker walks: each pattern's leading literal folders (stopping at the first wildcard segment, never the last segment),
    /// with a folder below another one dropped. "." means the whole working folder.
    /// </summary>
    internal static IReadOnlyList<string> WalkRoots(IReadOnlyList<string> patterns)
    {
        var roots = new List<string>();
        foreach (var p in patterns)
        {
            var segments = p.Split('/');
            var literal = segments.Take(segments.Length - 1).TakeWhile(s => !s.Contains('*', StringComparison.Ordinal) && !s.Contains('?', StringComparison.Ordinal)).ToList();
            roots.Add(literal.Count == 0 ? "." : string.Join('/', literal));
        }

        if (roots.Contains("."))
        {
            return ["."];
        }

        var sorted = roots.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
        return sorted.Where(r => !sorted.Any(o => o != r && r.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
    }
}
