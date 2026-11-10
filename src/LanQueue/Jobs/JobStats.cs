using System.Globalization;

namespace LanQueue.Jobs;

/// <summary>One machine in <see cref="JobStatsReport"/>.</summary>
/// <param name="Machine">The machine's name, "-" for a job that never got one.</param>
/// <param name="Other">Jobs that neither completed nor failed (rejected, timed out, infrastructure error, ...).</param>
/// <param name="MedianSeconds">Median wall-clock seconds of its Completed jobs (nearest rank, one decimal); null when none completed.</param>
/// <param name="P90Seconds">90th percentile of the same (nearest rank, one decimal); null when none completed.</param>
public sealed record MachineStats(
    string Machine, int Jobs, int Completed, int Failed, int Other, double WallMinutes, double? MedianSeconds, double? P90Seconds, DateTimeOffset LastRunUtc);

/// <summary>One command in <see cref="JobStatsReport"/>: every job with the same (key, dir), or for a line written before 0.7.0 the same recorded command.</summary>
/// <param name="Command">The key (<see cref="JobHistory.Key"/>), or the recorded command of a line without one.</param>
/// <param name="Dir">The normalised --dir; null for lines without a key.</param>
/// <param name="Keyed">Whether these lines carry a key (0.7.0 or later); only those can ever give timing.</param>
/// <param name="Runs">Distinct runIds (every shard of one --shards run is one run); a job without a runId is a run of its own.</param>
/// <param name="Jobs">Every job (each shard, each retried attempt).</param>
/// <param name="LastShards">The shard count N of the newest job (1 for a single run); null for lines without a key.</param>
public sealed record CommandStats(
    string Command, string? Dir, bool Keyed, int Runs, int Jobs, int Completed, int Failed, int Other, double? MedianSeconds, double? P90Seconds,
    DateTimeOffset LastRunUtc, int? LastShards);

/// <summary>What <see cref="JobStats.Of"/> found in the job log.</summary>
/// <param name="Machines">Ordinal by name.</param>
/// <param name="Commands">Newest first (by the start of their newest job), then ordinal by command and dir.</param>
public sealed record JobStatsReport(
    int Jobs, double WallMinutes, DateTimeOffset? OldestUtc, DateTimeOffset? NewestUtc, IReadOnlyList<MachineStats> Machines, IReadOnlyList<CommandStats> Commands);

/// <summary>
/// lanq stats, without any I/O: totals, per machine and per command, from the records of jobs.jsonl (oldest first). Only Completed jobs feed the
/// median and p90; failed and other jobs are counted, never timed.
/// </summary>
public static class JobStats
{
    public static JobStatsReport Of(IReadOnlyList<JobRecord> records)
    {
        var machines = records
            .GroupBy(r => r.Machine ?? "-", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var (completed, failed, other) = Count(g);
                var times = CompletedSeconds(g);
                return new MachineStats(
                    g.Key, g.Count(), completed, failed, other, Math.Round(g.Sum(r => r.WallSeconds) / 60, 1),
                    Percentile(times, 50), Percentile(times, 90), g.Max(r => r.StartedUtc));
            })
            .ToList();

        var commands = records
            .GroupBy(r => (Keyed: r.Key is not null, Command: r.Key ?? r.Command, Dir: r.Key is null ? null : r.Dir))
            .Select(g =>
            {
                var (completed, failed, other) = Count(g);
                var times = CompletedSeconds(g);
                var runs = g.Where(r => r.RunId is not null).Select(r => r.RunId).Distinct(StringComparer.Ordinal).Count() + g.Count(r => r.RunId is null);

                // The newest job: the latest start, and of equal starts the one written last.
                var newest = g.Select((r, i) => (r, i)).MaxBy(x => (x.r.StartedUtc, x.i)).r;
                return new CommandStats(
                    g.Key.Command, g.Key.Dir, g.Key.Keyed, runs, g.Count(), completed, failed, other, Percentile(times, 50), Percentile(times, 90),
                    newest.StartedUtc, g.Key.Keyed ? newest.Shards ?? 1 : null);
            })
            .OrderByDescending(c => c.LastRunUtc)
            .ThenBy(c => c.Command, StringComparer.Ordinal)
            .ThenBy(c => c.Dir, StringComparer.Ordinal)
            .ToList();

        return new JobStatsReport(
            records.Count,
            Math.Round(records.Sum(r => r.WallSeconds) / 60, 1),
            records.Count == 0 ? null : records.Min(r => r.StartedUtc),
            records.Count == 0 ? null : records.Max(r => r.StartedUtc),
            machines,
            commands);
    }

    /// <summary>
    /// The nearest-rank <paramref name="percent"/> percentile of <paramref name="values"/>: sorted[ceil(percent/100 * n) - 1] (so the median of
    /// an even count is the lower middle value), rounded to one decimal; null for no values.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> values, int percent)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();

        // Whole numbers only, so 90% of 10 is rank 9 exactly (0.9 * 10 in floating point is not).
        var rank = Math.Max(1, ((percent * sorted.Count) + 99) / 100);
        return Math.Round(sorted[rank - 1], 1);
    }

    /// <summary>A duration for people: "42 s", "3.5 min" or "2.1 h".</summary>
    public static string Duration(double seconds) => seconds switch
    {
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s"),
        < 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:0.#} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.#} h"),
    };

    private static (int Completed, int Failed, int Other) Count(IEnumerable<JobRecord> records)
    {
        int completed = 0, failed = 0, other = 0;
        foreach (var r in records)
        {
            switch (r.Status)
            {
                case nameof(JobStatus.Completed):
                    completed++;
                    break;
                case nameof(JobStatus.Failed):
                    failed++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return (completed, failed, other);
    }

    private static List<double> CompletedSeconds(IEnumerable<JobRecord> records) =>
        [.. records.Where(r => r.Status == nameof(JobStatus.Completed)).Select(r => r.WallSeconds)];
}
