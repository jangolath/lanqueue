using System.Globalization;
using LanQueue.Fetch;

namespace LanQueue.Plan;

/// <summary>
/// Fetch limits and the fetch folder of a plan (0.10.0). lanq.yml's fetch_max and fetch_max_files may only LOWER the defaults (200 MB, 500 files);
/// there is no fetch_dir key. Only the developer's command line may raise the limits (lanq run --plan --fetch-max / --fetch-max-files, up to the
/// 2 GB / 5,000-file ceiling, replacing the file's values) or pick the folder (--fetch-dir), for every job that fetches. There is no plan-wide
/// total: the worst case is the number of fetching jobs times the per-job limit (32 jobs at the defaults: 6.4 GB). Pure.
/// </summary>
public static class PlanFetch
{
    /// <summary>The refusal of a fetch limit in lanq.yml above the defaults.</summary>
    public static readonly string RaiseRefused = string.Create(
        CultureInfo.InvariantCulture,
        $"lanq.yml may lower fetch limits (at most {FetchLimits.DefaultMaxTotalBytes / (1024 * 1024)}M / {FetchLimits.DefaultMaxFiles} files); a larger limit is the developer's choice: lanq run --plan --fetch-max 1G");

    /// <summary>The refusal of --fetch-dir, --fetch-max or --fetch-max-files when no selected job fetches.</summary>
    public const string NoFetchingJob =
        "--fetch-dir, --fetch-max and --fetch-max-files with --plan set where and how much the plan's jobs fetch, but none of the selected jobs has fetch: in lanq.yml; leave them out";

    /// <summary>Whether any of the jobs fetches files (fetch: in lanq.yml).</summary>
    public static bool AnyFetches(IEnumerable<PlanJob> jobs) => jobs.Any(j => j.Args.Fetch.Count > 0);

    /// <summary>
    /// One job with the command line's fetch options: a job that fetches gets --fetch-dir <paramref name="fetchDir"/> (a full path) and the
    /// command line's --fetch-max / --fetch-max-files in place of its own; a job that fetches nothing, or a command line without them, is
    /// returned unchanged.
    /// </summary>
    public static PlanJob Apply(PlanJob job, string? fetchDir, long? maxBytes, int? maxFiles)
    {
        if (job.Args.Fetch.Count == 0 || (fetchDir is null && maxBytes is null && maxFiles is null))
        {
            return job;
        }

        var args = job.RunArgs.ToList();
        foreach (var (option, value) in new[]
        {
            ("--fetch-dir", fetchDir),
            ("--fetch-max", maxBytes is { } b ? SizeText(b) : null),
            ("--fetch-max-files", maxFiles?.ToString(CultureInfo.InvariantCulture)),
        })
        {
            if (value is not null)
            {
                args = PlanNarrowing.WithOptions(args, [option], [option, value]);
            }
        }

        return PlanNarrowing.Reparsed(job, args);
    }

    /// <summary>A byte count as --fetch-max takes it: 1G, 500M, 64K, or plain bytes.</summary>
    public static string SizeText(long bytes) => bytes switch
    {
        _ when bytes % (1024L * 1024 * 1024) == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024L * 1024 * 1024)}G"),
        _ when bytes % (1024L * 1024) == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024L * 1024)}M"),
        _ when bytes % 1024 == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024}K"),
        _ => bytes.ToString(CultureInfo.InvariantCulture),
    };
}
