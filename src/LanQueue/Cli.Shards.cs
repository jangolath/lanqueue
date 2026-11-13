using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Scheduling;

namespace LanQueue;

/// <summary>lanq run --shards N|auto [--shard-retries R] [--fail-fast]: see docs/SHARDING.md.</summary>
public static partial class Cli
{
    /// <summary>Collects the shard options while ParsedArgs.Parse walks the command line, then validates them together.</summary>
    internal sealed class ShardArgs
    {
        private string? _count;
        private string? _retries;
        private int? _target;

        public bool FailFast { get; set; }

        /// <summary>--target-minutes, already checked to be 1 to 1440 (<see cref="ParseTargetMinutes"/>).</summary>
        public void Target(int minutes) => _target = minutes;

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

        /// <summary>
        /// Null without --shards; refuses --shard-retries or --fail-fast without it, --target-minutes without --shards auto, a count outside
        /// 1 to 64 (or auto) and retries outside 0 to 3.
        /// </summary>
        public ShardOptions? Build()
        {
            if (_target is not null && _count != "auto")
            {
                throw new ArgumentException(TargetMinutesNeedsAuto);
            }

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

            return new ShardOptions(count, retries, FailFast, _target);
        }
    }

    /// <summary>Why --target-minutes was refused without --shards auto.</summary>
    internal const string TargetMinutesNeedsAuto = "--target-minutes goes with --shards auto (it picks N from earlier runs of the same command)";

    /// <summary>--target-minutes M: whole minutes from 1 to 1440 (one day).</summary>
    internal static int ParseTargetMinutes(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= ShardTiming.MaxTargetMinutes
            ? m
            : throw new ArgumentException($"--target-minutes must be a whole number of minutes from 1 to {ShardTiming.MaxTargetMinutes}.");

    /// <summary>
    /// lanq run --shards. Each shard's lines stream prefixed [i/N machine]; at the end a summary table (shard, machine, status, exit code,
    /// duration) goes where the job output went, and the status line to stderr. With --json stdout carries exactly one <see cref="ShardRunJson"/>.
    /// </summary>
    internal static async Task<int> RunShards(
        ParsedArgs p, ShardOptions options, JobRunner runner, string repoDir, FetchRequest? fetch, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        Action? finished = null)
    {
        var jobOutput = p.Json ? stderr : stdout;
        var started = System.Diagnostics.Stopwatch.StartNew();
        ShardRunResult result;
        try
        {
            result = await runner.RunShardsAsync(
                RequestOf(p, repoDir, fetch),
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

        // After the result is decided, before the Next and status lines (see Cli.PruneLogs).
        finished?.Invoke();
        if (result.Shards.Count > 0)
        {
            await jobOutput.WriteAsync(ShardTable(result)).ConfigureAwait(false);
        }

        // What to do next, then the logs of the shards that did not complete (--json carries every shard's logPath); all before the status
        // line, which stays last.
        var next = NextSteps.ForShards(p, result);
        var merged = MergedResults(result);
        if (NoTestsRan(result.Status, merged) is { } noTests)
        {
            next = NextSteps.WithNoTests(next, "the job");
            await stderr.WriteLineAsync(NoTestsLine(null, noTests)).ConfigureAwait(false);
        }

        await WriteBaselineLines(stderr, result.Shards.Where(s => s.Baseline is not null).Select(s => (string.Create(CultureInfo.InvariantCulture, $"shard {s.Index}/{result.Count} "), s.Baseline!)))
            .ConfigureAwait(false);
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
            await stdout.WriteLineAsync(JsonSerializer.Serialize(ShardJson(result, started.Elapsed, fetch is not null, p.LogTail, next) with { Env = EnvNames(p.Env), Exclusive = Flag(p.Exclusive), ScanSkipped = ScanSkippedJson(runner.ScanSkipped) }, LanqJson.Default.ShardRunJson)).ConfigureAwait(false);
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
                new RunJson(s.Status, s.ExitCode, s.Machine, s.JobId, Math.Round(s.Duration.TotalSeconds, 1), JobRunner.SafeLine(s.Message))
                {
                    Shard = s.Index,
                    Attempts = s.Attempts,
                    Baseline = BaselineJsonOf(s.Baseline),
                    Results = ResultsJsonOf(s.Results),
                },
                s.LogPath,
                logTail),
            fetch,
            s.Fetch))])
    {
        Retryable = result.Status == nameof(JobStatus.NoCapacity) ? result.Retryable ?? true : null,
        Excluded = result.Status == nameof(JobStatus.NoCapacity) ? ExcludedJsons(result.Excluded) : null,
        ShardTiming = TimingJson(result.Timing),
        NextSteps = nextSteps ?? [],
        Results = ResultsJsonOf(MergedResults(result)),
    };

    /// <summary>The shardTiming object of --shards auto --target-minutes (sanitised: the key, dir and machine come from the job log); null without it.</summary>
    internal static ShardTimingJson? TimingJson(ShardTimingResult? t) => t is null ? null : new(
        t.TargetMinutes, JobRunner.SafeLine(t.Command), JobRunner.SafeLine(t.Dir), t.Basis, t.RunsUsed, t.LastRunUtc?.UtcDateTime, t.WorkMinutes, t.OverheadSeconds,
        t.SlowestMachine is { } m ? JobRunner.SafeLine(m) : null, t.SpeedFactor, t.Wanted, t.FreeSlots, t.Capped, t.Picked, t.ExpectedShardMinutes,
        JobRunner.SafeLine(t.Why));

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
