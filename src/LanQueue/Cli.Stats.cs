using System.Globalization;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Output;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>The most commands lanq stats --json lists; the rest are counted in commandsMore.</summary>
    internal const int MaxStatsCommands = 500;

    /// <summary>The most commands lanq stats prints as text (newest first); --json lists them all.</summary>
    internal const int StatsTextCommands = 20;

    /// <summary>The longest command lanq stats prints as text; longer ones are cut and end in "...".</summary>
    private const int StatsTextCommandChars = 120;

    /// <summary>
    /// policy.keep_logs_days for the logs line of lanq stats, read from the config when it loads. stats never needs the config, so one that is
    /// missing or broken gives the default instead of an error.
    /// </summary>
    private static int StatsKeepDays(ParsedArgs p, Func<FleetConfig>? loadConfig)
    {
        try
        {
            return (loadConfig?.Invoke() ?? ConfigLoader.Load(ResolveConfig(p.ConfigPath, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists)))
                .Policy.KeepLogsDays;
        }
        catch (Exception)
        {
            return LogPrune.DefaultKeepDays;
        }
    }

    /// <summary>
    /// lanq stats: what the local job log (jobs.1.jsonl, then jobs.jsonl) says. Text: the totals line and one line per machine (exactly as
    /// before 0.7.0), then a table of machines (median and p90 of completed jobs), the newest <see cref="StatsTextCommands"/> commands and the
    /// size of logs/. --json: one <see cref="StatsJson"/>. Exit 0 (no job log is 0 jobs), 70 the job log could not be read, 130 Ctrl+C (with
    /// --json; without it Ctrl+C ends lanq as everywhere else). Unreadable lines are skipped with one warning on stderr.
    /// </summary>
    /// <param name="keepDays">policy.keep_logs_days (0 = never pruned), for the logs line.</param>
    /// <param name="zone">The time zone of the text's times (local when null); JSON is always UTC.</param>
    internal static async Task<int> Stats(
        ParsedArgs p, JobLog log, int keepDays, TextWriter stdout, TextWriter stderr, CancellationToken ct, TimeZoneInfo? zone = null)
    {
        JobLogRead read;
        try
        {
            ct.ThrowIfCancellationRequested();
            read = log.Read(ct);
        }
        catch (OperationCanceledException) when (p.Json && ct.IsCancellationRequested)
        {
            return await WriteStatsError("Cancelled", 130, "cancelled (Ctrl+C)", keepDays, stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await WriteStatsError("InfraError", 70, JobRunner.SafeLine("the job log could not be read: " + ex.Message), keepDays, stdout, stderr, text: !p.Json)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (p.Json && ex is not OperationCanceledException)
        {
            return await WriteStatsError("InfraError", 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), keepDays, stdout, stderr)
                .ConfigureAwait(false);
        }

        if (read.Skipped > 0)
        {
            await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"[lanq] stats: {read.Skipped} unreadable line(s) in jobs.jsonl skipped"))
                .ConfigureAwait(false);
        }

        (int Files, long Bytes) logs;
        try
        {
            logs = LogPrune.Measure(log.LogsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync("[lanq] stats: " + JobRunner.SafeLine($"{log.LogsFolder} could not be read: {ex.Message}")).ConfigureAwait(false);
            logs = (0, 0);
        }

        var report = JobStats.Of(read.Records);
        if (p.Json)
        {
            var message = string.Create(CultureInfo.InvariantCulture, $"{report.Jobs} jobs");
            var json = new StatsJson(
                "Ok", 0, message, [.. read.Files.Select(JobRunner.SafeLine)], report.Jobs, report.WallMinutes, read.Skipped, report.OldestUtc?.UtcDateTime,
                report.NewestUtc?.UtcDateTime, LogsJson(logs, keepDays),
                [.. report.Machines.Select(m => new StatsMachineJson(
                    JobRunner.SafeLine(m.Machine), m.Jobs, m.Completed, m.Failed, m.Other, m.WallMinutes, m.MedianSeconds, m.P90Seconds, m.LastRunUtc.UtcDateTime, null, null))],
                [.. report.Commands.Take(MaxStatsCommands).Select(c => new StatsCommandJson(
                    JobRunner.SafeLine(c.Command), c.Dir is null ? null : JobRunner.SafeLine(c.Dir), c.Runs, c.Jobs, c.Completed, c.Failed, c.Other, c.MedianSeconds,
                    c.P90Seconds, c.LastRunUtc.UtcDateTime, c.LastShards, null))])
            {
                CommandsMore = report.Commands.Count > MaxStatsCommands ? report.Commands.Count - MaxStatsCommands : null,
                NextSteps = NextSteps.ForStats("Ok", message),
            };
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.StatsJson)).ConfigureAwait(false);
            return 0;
        }

        // Exactly the lines of 0.6 first (from the unrounded sums), names sanitised.
        var all = read.Records;
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{all.Count} jobs, {all.Sum(r => r.WallSeconds) / 60:0.#} min total wall-clock"))
            .ConfigureAwait(false);
        foreach (var g in all.GroupBy(r => r.Machine ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {JobRunner.SafeLine(g.Key)}: {g.Count()} jobs, {g.Sum(r => r.WallSeconds) / 60:0.#} min"))
                .ConfigureAwait(false);
        }

        if (report.Jobs > 0)
        {
            zone ??= TimeZoneInfo.Local;
            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync("Machines (median and p90 of completed jobs):").ConfigureAwait(false);
            foreach (var line in Table(
                [
                    ["machine", "jobs", "pass", "fail", "other", "median", "p90", "last run"],
                    .. report.Machines.Select(m => new[]
                    {
                        JobRunner.SafeLine(m.Machine), Num(m.Jobs), Num(m.Completed), Num(m.Failed), Num(m.Other), Seconds(m.MedianSeconds), Seconds(m.P90Seconds),
                        Local(m.LastRunUtc, zone),
                    }),
                ]))
            {
                await stdout.WriteLineAsync("  " + line).ConfigureAwait(false);
            }

            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync("Commands (newest first; each shard is one job):").ConfigureAwait(false);
            foreach (var c in report.Commands.Take(StatsTextCommands))
            {
                var command = JobRunner.SafeLine(c.Command);
                if (command.Length > StatsTextCommandChars)
                {
                    command = command[..(char.IsHighSurrogate(command[StatsTextCommandChars - 4]) ? StatsTextCommandChars - 4 : StatsTextCommandChars - 3)] + "...";
                }

                var where = !c.Keyed ? " (recorded before 0.7.0)" : c.Dir is null or "." ? "" : " (--dir " + JobRunner.SafeLine(c.Dir) + ")";
                await stdout.WriteLineAsync("  " + command + where).ConfigureAwait(false);
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {Plural(c.Runs, "run")}, {Plural(c.Jobs, "job")} ({c.Completed} passed, {c.Failed} failed, {c.Other} other); median {Seconds(c.MedianSeconds)}, "
                    + $"p90 {Seconds(c.P90Seconds)}; last run {Local(c.LastRunUtc, zone)}{(c.LastShards is > 1 ? $" ({c.LastShards} shards)" : "")}")).ConfigureAwait(false);
                if (!c.Keyed)
                {
                    await stdout.WriteLineAsync("    timing: none (no key)").ConfigureAwait(false);
                }
            }

            if (report.Commands.Count > StatsTextCommands)
            {
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture, $"  and {report.Commands.Count - StatsTextCommands} more (lanq stats --json lists them all)")).ConfigureAwait(false);
            }

            await stdout.WriteLineAsync().ConfigureAwait(false);
        }

        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"logs: {logs.Files} files, {logs.Bytes / (1024.0 * 1024):0.0} MB ({(keepDays == 0 ? "kept for ever: policy.keep_logs_days is 0" : $"kept {keepDays} days")})"))
            .ConfigureAwait(false);
        return 0;

        static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        static string Seconds(double? s) => s is { } v ? JobStats.Duration(v) : "-";
        static string Plural(int n, string word) => string.Create(CultureInfo.InvariantCulture, $"{n} {word}{(n == 1 ? "" : "s")}");
        static string Local(DateTimeOffset utc, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The logs object of lanq stats --json: count, megabytes (one decimal) and policy.keep_logs_days.</summary>
    private static StatsLogsJson LogsJson((int Files, long Bytes) logs, int keepDays) =>
        new(logs.Files, Math.Round(logs.Bytes / (1024.0 * 1024), 1), keepDays);

    /// <summary>A lanq stats --json object without results (zeros and empty lists): Rejected, InfraError or Cancelled.</summary>
    internal static StatsJson StatsError(string status, int exitCode, string message, int keepDays, IReadOnlyList<NextStepJson> steps) =>
        new(status, exitCode, message, [], 0, 0, 0, null, null, LogsJson((0, 0), keepDays), [], []) { NextSteps = steps };

    /// <summary>
    /// A lanq stats failure: the message (in text mode, or always for InfraError) and the Next lines on stderr; with --json also the object on
    /// stdout.
    /// </summary>
    private static async Task<int> WriteStatsError(string status, int exitCode, string message, int keepDays, TextWriter stdout, TextWriter stderr, bool text = false)
    {
        var steps = NextSteps.ForStats(status, message);
        if (status != "Cancelled")
        {
            await stderr.WriteLineAsync("[lanq] stats: " + message).ConfigureAwait(false);
        }

        await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
        if (!text)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(StatsError(status, exitCode, message, keepDays, steps), LanqJson.Default.StatsJson)).ConfigureAwait(false);
        }

        return exitCode;
    }

    /// <summary>Rows as columns: the first column left-aligned, the last left-aligned, the others right-aligned, two spaces apart.</summary>
    private static IEnumerable<string> Table(IReadOnlyList<string[]> rows)
    {
        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        foreach (var row in rows)
        {
            yield return string.Join(
                "  ",
                row.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : i == row.Length - 1 ? cell : cell.PadLeft(widths[i])));
        }
    }
}
