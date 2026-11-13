using System.Text;
using LanQueue.Output;
using LanQueue.Policy;
using LanQueue.Scheduling;

namespace LanQueue.Jobs;

/// <summary>
/// What identifies "the same command" across runs in jobs.jsonl: the pair (<see cref="Key"/>, <see cref="Dir"/>). --needs and --mem are not
/// part of it (they choose a machine, not what runs).
/// </summary>
public static class JobHistory
{
    /// <summary>Keys longer than this are cut and end in a hash of the whole key.</summary>
    public const int MaxKeyChars = 1000;

    /// <summary>How much of a long key is kept before "#" and the hash.</summary>
    internal const int LongKeyPrefixChars = 960;

    /// <summary>--target-minutes reads the jobs of the last this many days (by their start).</summary>
    public const int HistoryDays = 90;

    /// <summary>--target-minutes uses at most this many runs, the newest.</summary>
    public const int MaxRuns = 10;

    /// <summary>
    /// The command's identity, from its argv (for a shard: the TEMPLATE argv, so {shard} and {shards} stay literal): each token bare when it is
    /// made only of plain characters (<see cref="CommandLine.IsBare"/>), otherwise POSIX single-quoted, joined by one space. Token boundaries
    /// therefore survive ("a b" and "a" "b" differ). A key over <see cref="MaxKeyChars"/> characters keeps its first
    /// <see cref="LongKeyPrefixChars"/> characters, then "#" and the first 16 hex digits of the SHA-256 of the whole key.
    /// </summary>
    public static string Key(IReadOnlyList<string> argv)
    {
        var key = string.Join(' ', argv.Select(t => CommandLine.IsBare(t) ? t : ShellQuote.Posix(t)));
        if (key.Length <= MaxKeyChars)
        {
            return key;
        }

        var hash = Convert.ToHexStringLower(Util.Sha256.Hash(Encoding.UTF8.GetBytes(key)))[..16];
        var cut = char.IsHighSurrogate(key[LongKeyPrefixChars - 1]) ? LongKeyPrefixChars - 1 : LongKeyPrefixChars;
        return key[..cut] + "#" + hash;
    }

    /// <summary>The job's --dir as one spelling: backslashes become "/", leading "./" and trailing "/" are dropped, and nothing left is ".".</summary>
    public static string Dir(string directory)
    {
        var d = directory.Replace('\\', '/');
        while (d.StartsWith("./", StringComparison.Ordinal))
        {
            d = d[2..];
        }

        d = d.TrimEnd('/');
        return d.Length == 0 ? "." : d;
    }

    /// <summary>
    /// What --target-minutes knows about the command (<paramref name="key"/>, <paramref name="dir"/>) from jobs.jsonl (both files): see
    /// <see cref="From"/>. A job log that cannot be read is no history (the run goes on; plain --shards auto decides).
    /// </summary>
    public static TimingHistory For(JobLog log, string key, string dir, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            return From(log.ReadHistory(ct).Records, key, dir, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TimingHistory.None(key, dir);
        }
    }

    /// <summary>
    /// The runs of the command (<paramref name="key"/>, <paramref name="dir"/>) that timing may use, newest first, from <paramref name="records"/>
    /// (oldest first, as <see cref="JobLog.Read"/> returns them): only Completed jobs with a runSeconds that started in the last
    /// <see cref="HistoryDays"/> days. Jobs are grouped by runId (a job without one is a run of its own, N = 1); within a run each shard index
    /// counts once (its last Completed attempt). A run counts when at least ceil(N/2) of its N shards completed. At most
    /// <see cref="MaxRuns"/> runs, the newest (by their newest job). The machines' speeds come from every command (<see cref="ShardTiming.Speeds"/>).
    /// </summary>
    public static TimingHistory From(IReadOnlyList<JobRecord> records, string key, string dir, DateTimeOffset now) =>
        From(records, key, dir, now, ShardTiming.Speeds(records, now));

    /// <param name="speeds">Every machine's speed (<see cref="ShardTiming.Speeds"/> of the same records), computed once for many commands.</param>
    public static TimingHistory From(IReadOnlyList<JobRecord> records, string key, string dir, DateTimeOffset now, IReadOnlyDictionary<string, MachineSpeed> speeds)
    {
        var since = now.AddDays(-HistoryDays);
        var runs = records
            .Where(r => r.Key == key && r.Dir == dir && r.Status == nameof(JobStatus.Completed) && r.RunSeconds is not null && r.Machine is not null
                && r.StartedUtc >= since)
            .GroupBy(r => r.RunId is { } id ? "run:" + id : "job:" + r.Id, StringComparer.Ordinal)
            .Select(g =>
            {
                var n = g.First().RunId is null ? 1 : Math.Max(1, g.Max(r => r.Shards ?? 1));

                // The last Completed attempt of each index (a retried shard counts once).
                List<TimingShard> shards = [.. g
                    .GroupBy(r => r.RunId is null ? 1 : r.Shard ?? 1)
                    .Select(s => s.Last())
                    .OrderBy(r => r.RunId is null ? 1 : r.Shard ?? 1)
                    .Select(r => new TimingShard(r.RunId is null ? 1 : r.Shard ?? 1, r.Machine!, r.RunSeconds!.Value, r.WallSeconds))];
                return new TimingRun(n, g.Max(r => r.StartedUtc), shards);
            })
            .Where(r => r.Completed.Count >= (r.Shards + 1) / 2)
            .OrderByDescending(r => r.LastUtc)
            .Take(MaxRuns)
            .ToList();
        return new TimingHistory(key, dir, runs, speeds);
    }
}
