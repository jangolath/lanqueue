using LanQueue.Fetch;

namespace LanQueue.Results;

/// <summary>The test results format lanq counts (0.11.0): a VSTest TRX file or a JUnit XML file.</summary>
public enum ResultsFormat
{
    Trx,
    Junit,
}

/// <summary>
/// lanq run --results FORMAT:PATTERN (lanq.yml results: { format:, path: }, 0.11.0): the results files a job writes, as a fetch pattern. The
/// pattern is also fetched (merged into --fetch, see <see cref="FetchPatterns.Validate"/>); lanq counts the tests in the fetched files that
/// match it. Information only: the status and exit code never change because of it.
/// </summary>
/// <param name="Path">A valid fetch pattern (<see cref="FetchPatterns.Problem"/>), relative to the job's --dir.</param>
public sealed record ResultsSpec(ResultsFormat Format, string Path)
{
    /// <summary>The refusal of a value that is not FORMAT:PATTERN.</summary>
    internal const string Usage = "--results must be trx:PATTERN or junit:PATTERN (for example --results trx:TestResults/*.trx).";

    /// <summary>
    /// Reads "trx:PATTERN" or "junit:PATTERN". Split at the first ':' (a fetch pattern may not contain one, so nothing is lost); the pattern
    /// is judged by the --fetch rules.
    /// </summary>
    /// <exception cref="ArgumentException">Not FORMAT:PATTERN, or the pattern is not a valid fetch pattern.</exception>
    public static ResultsSpec Parse(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new ArgumentException(Usage);
        }

        var format = value[..colon] switch
        {
            "trx" => ResultsFormat.Trx,
            "junit" => ResultsFormat.Junit,
            _ => throw new ArgumentException(Usage),
        };
        var path = value[(colon + 1)..];
        return FetchPatterns.Problem(path) is { } why
            ? throw new ArgumentException($"--results pattern '{path}' {why}.")
            : new ResultsSpec(format, path);
    }

    /// <summary>trx or junit, as written on the command line and in JSON.</summary>
    public string FormatName => Name(Format);

    /// <summary>FORMAT:PATTERN, as --results takes it.</summary>
    public string Text => FormatName + ":" + Path;

    /// <summary>trx or junit.</summary>
    public static string Name(ResultsFormat format) => format == ResultsFormat.Trx ? "trx" : "junit";
}
