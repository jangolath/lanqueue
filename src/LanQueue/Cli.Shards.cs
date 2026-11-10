using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Jobs;
using LanQueue.Output;

namespace LanQueue;

/// <summary>lanq run --shards N|auto [--shard-retries R] [--fail-fast]: see docs/SHARDING.md.</summary>
public static partial class Cli
{
    /// <summary>Collects the shard options while ParsedArgs.Parse walks the command line, then validates them together.</summary>
    internal sealed class ShardArgs
    {
        private string? _count;
        private string? _retries;

        public bool FailFast { get; set; }

        public void Set(string option, string value)
        {
            if (option == "--shards")
            {
                _count = value;
            }
            else
            {
                _retries = value;
            }
        }

        /// <summary>Null without --shards; refuses --shard-retries or --fail-fast without it, a count outside 1 to 64 (or auto) and retries outside 0 to 3.</summary>
        public ShardOptions? Build()
        {
            if (_count is null)
            {
                return _retries is not null || FailFast
                    ? throw new ArgumentException("--shard-retries and --fail-fast need --shards.")
                    : null;
            }

            int? count = null;
            if (_count != "auto")
            {
                count = int.TryParse(_count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= ShardInfo.MaxShards
                    ? n
                    : throw new ArgumentException($"--shards must be a whole number from 1 to {ShardInfo.MaxShards}, or auto.");
            }

            var retries = 0;
            if (_retries is not null && (!int.TryParse(_retries, NumberStyles.None, CultureInfo.InvariantCulture, out retries) || retries > ShardInfo.MaxRetries))
            {
                throw new ArgumentException($"--shard-retries must be a whole number from 0 to {ShardInfo.MaxRetries}.");
            }

            return new ShardOptions(count, retries, FailFast);
        }
    }

    /// <summary>
    /// lanq run --shards. Each shard's lines stream prefixed [i/N machine]; at the end a summary table (shard, machine, status, exit code,
    /// duration) goes where the job output went, and the status line to stderr. With --json stdout carries exactly one <see cref="ShardRunJson"/>.
    /// </summary>
    internal static async Task<int> RunShards(
        ParsedArgs p, ShardOptions options, JobRunner runner, string repoDir, FetchRequest? fetch, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var jobOutput = p.Json ? stderr : stdout;
        var started = System.Diagnostics.Stopwatch.StartNew();
        ShardRunResult result;
        try
        {
            result = await runner.RunShardsAsync(
                new JobRequest(p.Needs, p.Argv, p.Directory, repoDir, TimeSpan.FromSeconds(p.WaitSeconds), TimeSpan.FromMinutes(p.TimeoutMinutes), p.MemGb, fetch) { NoWake = p.NoWake },
                options,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (p.Json && ct.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("[lanq] Cancelled").ConfigureAwait(false);
            await WriteShardsJsonError(JobRunner.Cancelled, 130, "cancelled (Ctrl+C) before any shard started", stdout, NextSteps.Nothing("cancelled (Ctrl+C)"), started.Elapsed)
                .ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (p.Json && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            var message = JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            var steps = NextSteps.ForRun(p, new NextSteps.RunOutcome("InfraError", 70, message, null, InfraKind.Unexpected, false, null, null, null, null));
            await NextSteps.WriteAsync(stderr, steps, "[lanq] ").ConfigureAwait(false);
            await stderr.WriteLineAsync("[lanq] InfraError: " + message).ConfigureAwait(false);
            await WriteShardsJsonError("InfraError", 70, message, stdout, steps, started.Elapsed).ConfigureAwait(false);
            return 70;
        }

        if (result.Shards.Count > 0)
        {
            await jobOutput.WriteAsync(ShardTable(result)).ConfigureAwait(false);
        }

        // What to do next, then the logs of the shards that did not complete (--json carries every shard's logPath); all before the status
        // line, which stays last.
        var next = NextSteps.ForShards(p, result);
        await NextSteps.WriteAsync(stderr, next, "[lanq] ").ConfigureAwait(false);
        if (!p.Json)
        {
            foreach (var s in result.Shards.Where(s => s.Status != nameof(JobStatus.Completed) && s.LogPath is not null))
            {
                await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"[lanq] log: {JobRunner.SafeLine(s.LogPath!)} (shard {s.Index}/{result.Count})")).ConfigureAwait(false);
            }
        }

        await stderr.WriteLineAsync($"[lanq] {result.Status}: {JobRunner.SafeLine(result.Message)}").ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(ShardJson(result, started.Elapsed, fetch is not null, p.LogTail, next), LanqJson.Default.ShardRunJson)).ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    internal static ShardRunJson ShardJson(ShardRunResult result, TimeSpan elapsed, bool fetch, int? logTail = null, IReadOnlyList<NextStepJson>? nextSteps = null) => new(
        result.Status,
        result.ExitCode,
        result.Count,
        Math.Round(elapsed.TotalSeconds, 1),
        JobRunner.SafeLine(result.Message),
        [.. result.Shards.Select(s => WithFetch(
            WithLog(
                new RunJson(s.Status, s.ExitCode, s.Machine, s.JobId, Math.Round(s.Duration.TotalSeconds, 1), JobRunner.SafeLine(s.Message)) { Shard = s.Index, Attempts = s.Attempts },
                s.LogPath,
                logTail),
            fetch,
            s.Fetch))])
    {
        Retryable = result.Status == nameof(JobStatus.NoCapacity) ? result.Retryable ?? true : null,
        Excluded = result.Status == nameof(JobStatus.NoCapacity) ? ExcludedJsons(result.Excluded) : null,
        NextSteps = nextSteps ?? [],
    };

    /// <summary>The summary table: one line per shard, in index order.</summary>
    internal static string ShardTable(ShardRunResult result)
    {
        var sb = new StringBuilder();
        sb.Append("[lanq] shard   machine          status        exit  duration\n");
        foreach (var s in result.Shards)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"[lanq] {s.Index + "/" + result.Count,-7} {JobRunner.SafeLine(s.Machine ?? "-"),-16} {s.Status,-12} {s.ExitCode,5}  {s.Duration.TotalSeconds,7:0.0}s\n"));
        }

        return sb.ToString();
    }

    /// <summary>The one --shards --json object when no shard result exists (bad arguments, config, cancelled before the start, unexpected error).</summary>
    private static Task WriteShardsJsonError(string status, int exitCode, string message, TextWriter stdout, IReadOnlyList<NextStepJson> steps, TimeSpan? elapsed = null) =>
        stdout.WriteLineAsync(JsonSerializer.Serialize(
            new ShardRunJson(status, exitCode, 0, Math.Round((elapsed ?? TimeSpan.Zero).TotalSeconds, 1), message, []) { NextSteps = steps }, LanqJson.Default.ShardRunJson));
}
