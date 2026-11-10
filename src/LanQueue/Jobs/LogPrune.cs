using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LanQueue.Jobs;

/// <summary>What one <see cref="LogPrune.Run"/> did: whether it looked at all, how many logs it deleted, and whether it got through them all.</summary>
public sealed record LogPruneResult(bool Ran, int Deleted, bool Finished);

/// <summary>
/// Deletes lanq's own old per-job logs (state folder logs/, see <see cref="JobLog.LogPathFor"/>) after a run: those older than
/// policy.keep_logs_days, then the oldest beyond <see cref="MaxFiles"/>. Only regular files directly in logs/ whose whole name is a lanq job id
/// plus ".log" are ever deleted; links and reparse points are skipped (and a logs/ that is itself a link is left alone), a file modified within
/// the last hour is never deleted, and nothing outside logs/ is touched. Runs at most once per <see cref="Interval"/> (the mtime of the marker
/// file logs/.pruned) and stops after <see cref="TimeBudget"/> or <see cref="MaxDeletes"/> deletions, carrying on after the next run.
/// </summary>
public static partial class LogPrune
{
    /// <summary>policy.keep_logs_days when the config does not set it.</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>The most lanq job logs kept, whatever their age (fixed in 0.7).</summary>
    public const int MaxFiles = 5000;

    /// <summary>The most logs one prune deletes; the rest wait for the next one.</summary>
    public const int MaxDeletes = 10_000;

    /// <summary>The name of the marker in logs/ whose mtime is the last prune.</summary>
    public const string MarkerName = ".pruned";

    /// <summary>How long one prune may take before it stops and leaves the rest for the next run.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);

    /// <summary>Pruning runs at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>A log modified this recently is never deleted (its job may still be running, or the clock may be wrong).</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromHours(1);

    /// <summary>A log lanq wrote: a job id exactly as JobRunner.NewId makes it (yyyyMMdd-HHmmss-6 lowercase hex digits), then ".log".</summary>
    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}-[0-9a-f]{6}\.log\z", RegexOptions.CultureInvariant, 1000)]
    internal static partial Regex LogName();

    /// <summary>
    /// The prune after a lanq run (single or --shards): never throws and never changes the run's result; anything that goes wrong is one
    /// warning line.
    /// </summary>
    public static void AfterRun(string logsFolder, int keepDays, Action<string> warn)
    {
        try
        {
            Run(logsFolder, keepDays, DateTime.UtcNow, warn);
        }
        catch (Exception ex)
        {
            try
            {
                warn("[lanq] warning: could not prune old job logs: " + JobRunner.SafeLine(ex.Message));
            }
            catch (Exception)
            {
                // The warning could not be written either; the run's result stands.
            }
        }
    }

    /// <summary>One prune of <paramref name="logsFolder"/> at <paramref name="nowUtc"/> (see the class summary). May throw; <see cref="AfterRun"/> does not.</summary>
    /// <param name="maxFiles">The count cap (<see cref="MaxFiles"/>; smaller only in tests).</param>
    /// <param name="budget">The time budget (<see cref="TimeBudget"/> when null).</param>
    public static LogPruneResult Run(string logsFolder, int keepDays, DateTime nowUtc, Action<string>? warn = null, int maxFiles = MaxFiles, TimeSpan? budget = null)
    {
        var dir = new DirectoryInfo(logsFolder);
        if (keepDays <= 0 || !dir.Exists || IsLink(dir))
        {
            return new LogPruneResult(false, 0, true);
        }

        var marker = new FileInfo(Path.Combine(logsFolder, MarkerName));
        if (IsLink(marker))
        {
            warn?.Invoke($"[lanq] warning: {JobRunner.SafeLine(marker.FullName)} is a link; old job logs are not pruned while it is there");
            return new LogPruneResult(false, 0, true);
        }

        // A marker from the future (a clock that was wrong) does not stop pruning for ever: only a recent past one counts.
        if (marker.Exists && marker.LastWriteTimeUtc <= nowUtc && nowUtc - marker.LastWriteTimeUtc < Interval)
        {
            return new LogPruneResult(false, 0, true);
        }

        // Claim this prune first (a second lanq finishing now skips it); an interrupted prune hands the claim back below.
        using (new FileStream(marker.FullName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
        }

        File.SetLastWriteTimeUtc(marker.FullName, nowUtc);

        var clock = Stopwatch.StartNew();
        var limit = budget ?? TimeBudget;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        var protect = nowUtc - Recent;
        var tooOld = nowUtc - TimeSpan.FromDays(keepDays);
        var logs = new List<(string Path, DateTime Modified)>();
        var finished = true;
        foreach (var f in dir.EnumerateFiles("*.log", options))
        {
            if (clock.Elapsed > limit)
            {
                finished = false;
                break;
            }

            // The listing's own attributes (no extra call per file): links were skipped by AttributesToSkip already, and are again here.
            if (LogName().IsMatch(f.Name) && !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                logs.Add((f.FullName, f.LastWriteTimeUtc));
            }
        }

        // Too old first, then the oldest beyond the count cap; never one modified within the last hour (or in the future).
        var newestFirst = logs.OrderByDescending(l => l.Modified).ThenByDescending(l => l.Path, StringComparer.Ordinal).ToList();
        var doomed = newestFirst
            .Select((l, i) => (l.Path, l.Modified, Index: i))
            .Where(l => l.Modified < protect && (l.Modified < tooOld || l.Index >= maxFiles))
            .OrderBy(l => l.Modified)
            .ToList();

        var deleted = 0;
        var failed = 0;
        foreach (var (path, _, _) in doomed)
        {
            if (!finished || deleted >= MaxDeletes || clock.Elapsed > limit)
            {
                finished = false;
                break;
            }

            try
            {
                // Checked again just before the delete: the name was a plain file when listed.
                var f = new FileInfo(path);
                if (!f.Exists || IsLink(f))
                {
                    continue;
                }

                f.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            warn?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[lanq] warning: could not delete {failed} old job log(s) in {JobRunner.SafeLine(logsFolder)}"));
        }

        if (!finished)
        {
            // Cut short: the next run carries on instead of waiting a day.
            File.SetLastWriteTimeUtc(marker.FullName, nowUtc - Interval - TimeSpan.FromMinutes(1));
        }

        return new LogPruneResult(true, deleted, finished);
    }

    /// <summary>A symbolic link, junction or other reparse point (a dangling link included).</summary>
    private static bool IsLink(FileSystemInfo f) =>
        f.LinkTarget is not null || (f.Exists && f.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
