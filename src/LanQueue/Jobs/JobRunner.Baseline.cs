using System.Globalization;
using LanQueue.Config;
using LanQueue.Payload;

namespace LanQueue.Jobs;

/// <summary>The verdicts of a lanq run --baseline rerun (JSON baseline.verdict). Values may be added.</summary>
public static class BaselineVerdict
{
    /// <summary>The same command Completed at the base commit: the failure is new.</summary>
    public const string New = "new";

    /// <summary>The same command Failed at the base commit too (any exit code): the failure was there before.</summary>
    public const string Preexisting = "preexisting";

    /// <summary>Anything else: the base timed out, hit an infrastructure error, found no machine, was cancelled, or could not be prepared or found.</summary>
    public const string Inconclusive = "inconclusive";

    /// <summary>The snapshot is the base commit itself (on it, nothing uncommitted): nothing to compare, nothing ran.</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// The rerun of a failed job (shard, plan job) at the base commit (lanq run --baseline). Information only: it never changes the job's status
/// or exit code.
/// </summary>
/// <param name="Ref">What the base came from: the default branch found (origin/main) or --baseline-ref as given.</param>
/// <param name="Sha">The base commit's full id, or null when it could not be found.</param>
/// <param name="Verdict">A <see cref="BaselineVerdict"/>.</param>
/// <param name="Message">One line for people, without the "[lanq] baseline: " lead (for example "at 1a2b3c4 (merge-base with origin/main) the same command Completed on build-01 in 81.0s: this failure is new").</param>
public sealed record BaselineResult(string Ref, string? Sha, string Verdict, string Message)
{
    /// <summary>The base rerun's own status (Completed, Failed, TimedOut, ..., Cancelled), or null when it never started.</summary>
    public string? Status { get; init; }

    /// <summary>The base rerun's exit code as lanq run would exit with it, or null when it never started.</summary>
    public int? ExitCode { get; init; }

    public string? Machine { get; init; }

    public string? JobId { get; init; }

    public string? LogPath { get; init; }

    /// <summary>How long the base rerun's command ran (as <see cref="JobResult.RunSeconds"/>), or null when it never started.</summary>
    public double? DurationSeconds { get; init; }
}

/// <summary>lanq run --baseline: resolving the base commit and rerunning failed jobs there (see Item 3 of the 0.9.0 design).</summary>
public sealed partial class JobRunner
{
    /// <summary>One failed job (or shard) to rerun at <paramref name="Base"/>: its request as it ran, its job id and the machine it failed on.</summary>
    private sealed record BaselineTarget(JobRequest Request, BaseCommit Base, string FailedJobId, string? Machine);

    /// <summary>
    /// lanq run --baseline: the base commit (<see cref="IPayloadBuilder.ResolveBaseAsync"/>), local git only, before anything is built or sent.
    /// (null, null) without --baseline; a bad --baseline-ref (or no default branch, unrelated histories) is Rejected (2, Arguments).
    /// </summary>
    private async Task<(BaseCommit? Base, JobResult? Refused)> ResolveBaseAsync(string jobId, JobRequest req, CancellationToken ct)
    {
        if (!req.Baseline)
        {
            return (null, null);
        }

        BaseResolution resolved;
        try
        {
            resolved = await payloads.ResolveBaseAsync(req.RepoDir, req.BaselineRef, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            resolved = new BaseResolution(null, "--baseline: could not read the repository: " + ex.Message);
        }

        return resolved.Base is { } b
            ? (b, null)
            : (null, new JobResult(jobId, JobStatus.Rejected, 2, null, SafeLine(resolved.Error ?? "--baseline: no base commit")) { RejectKind = Jobs.RejectKind.Arguments });
    }

    /// <summary>"at 1a2b3c4 (merge-base with origin/main)".</summary>
    internal static string At(BaseCommit b) => $"at {b.Short} ({SafeLine(b.Label)})";

    /// <summary>What a dry run says about --baseline: the base a failure would be rerun at, or that it would be skipped.</summary>
    internal static BaselineResult PreviewBaseline(BaseCommit b, string snapshotSha) =>
        b.Sha is null
            ? new BaselineResult(b.Ref, null, BaselineVerdict.Inconclusive, $"on failure: inconclusive: {b.Problem}")
            : string.Equals(b.Sha, snapshotSha, StringComparison.Ordinal)
                ? new BaselineResult(b.Ref, b.Sha, BaselineVerdict.Skipped, $"skipped: the snapshot is the base commit {b.Short} ({SafeLine(b.Label)}), nothing to compare")
                : new BaselineResult(b.Ref, b.Sha, "", $"on failure, rerun {At(b)}");

    /// <summary>
    /// Reruns each failed job at its base commit, all at once (each waits for a slot as a job does; --wait counts, fresh, only while none of
    /// them holds a slot), and prefers the machine it failed on when that one can take it. The base payload (a clean tree of the base commit,
    /// secret-scanned like a snapshot) is built once per base, only here. Never throws for a rerun's own outcome: Ctrl+C cancels the reruns
    /// (each is cleaned up) and makes them inconclusive. Results are in <paramref name="targets"/> order.
    /// </summary>
    private async Task<List<BaselineResult>> RunBaselinesAsync(IReadOnlyList<BaselineTarget> targets, JobPayload snapshot, CancellationToken ct)
    {
        var gate = new PlanProgress();
        var anyGit = targets.Any(t => Candidates(t.Request).Any(m => m.Transfer == TransferMode.Git));
        var built = new Dictionary<string, Task<(PreparedPayload? Prepared, string? Failure)>>(StringComparer.Ordinal);
        var builtGate = new Lock();
        Task<(PreparedPayload? Prepared, string? Failure)> BaseOf(BaseCommit b, string repoDir)
        {
            lock (builtGate)
            {
                if (!built.TryGetValue(b.Sha!, out var task))
                {
                    task = PrepareBaseAsync(b, repoDir, anyGit, ct);
                    built[b.Sha!] = task;
                }

                return task;
            }
        }

        try
        {
            return [.. await Task.WhenAll(targets.Select(t => Task.Run(() => OneBaselineAsync(t, snapshot, gate, BaseOf, ct), CancellationToken.None))).ConfigureAwait(false)];
        }
        finally
        {
            foreach (var task in built.Values)
            {
                try
                {
                    if ((await task.ConfigureAwait(false)).Prepared is { } p)
                    {
                        await p.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
                {
                    // nothing was built
                }
            }
        }
    }

    /// <summary>The payload of the base commit (<see cref="IPayloadBuilder.BuildCommitAsync"/>) after the same secret scans a snapshot gets, or why not.</summary>
    private async Task<(PreparedPayload? Prepared, string? Failure)> PrepareBaseAsync(BaseCommit b, string repoDir, bool anyGit, CancellationToken ct)
    {
        var payloadId = NewId();
        JobPayload payload;
        try
        {
            payload = await payloads.BuildCommitAsync(payloadId, repoDir, scratchDir, b.Sha!, anyGit ? PayloadKinds.Bundle : PayloadKinds.None, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return (null, "the base commit could not be read: " + ex.Message);
        }

        if (ScanPayload(payloadId, payload) is { } refused)
        {
            return (null, "the base commit was not sent: " + refused.Message);
        }

        var builds = new SemaphoreSlim(1, 1);
        return (new PreparedPayload(
            payloadId,
            payload,
            new LazyArchive(payloads, payloadId, repoDir, scratchDir, payload.Sha, output, builds),
            new LazyBundles(payloads, payloadId, repoDir, scratchDir, payload.Sha, builds),
            builds), null);
    }

    private async Task<BaselineResult> OneBaselineAsync(
        BaselineTarget t, JobPayload snapshot, PlanProgress gate, Func<BaseCommit, string, Task<(PreparedPayload? Prepared, string? Failure)>> baseOf, CancellationToken ct)
    {
        var b = t.Base;
        BaselineResult Result(string verdict, string message) => new(b.Ref, b.Sha, verdict, SafeLine(message));
        if (b.Sha is null)
        {
            return Result(BaselineVerdict.Inconclusive, $"inconclusive: {b.Problem}");
        }

        var at = At(b);
        if (string.Equals(b.Sha, snapshot.Sha, StringComparison.Ordinal))
        {
            return Result(BaselineVerdict.Skipped, $"skipped: the snapshot is the base commit {b.Short} ({b.Label}), nothing to compare");
        }

        if (ct.IsCancellationRequested)
        {
            return Result(BaselineVerdict.Inconclusive, $"{at}: inconclusive: cancelled (Ctrl+C) before it started");
        }

        var name = "baseline" + (t.Request.PlanJob is { } job ? " " + job : "") + (t.Request.Shard is { } shard ? " " + shard : "");
        var req = t.Request with
        {
            Fetch = null,
            Progress = gate,
            Placed = null,
            RunId = null,
            Baseline = false,
            BaselineRef = null,
            BaselineOf = t.FailedJobId,
            Commit = b.Short,
            Label = name,
        };
        Say(req)($"[lanq] the job failed; running the same command {at} to see whether the failure is new");
        var jobId = NewId();
        string? machine = null;
        try
        {
            var (prepared, failure) = await baseOf(b, t.Request.RepoDir).ConfigureAwait(false);
            if (prepared is null)
            {
                return Result(BaselineVerdict.Inconclusive, $"{at}: inconclusive: {failure}");
            }

            var r = await Dispatch(jobId, req, prepared, ct, m => machine = m, prefer: t.Machine).ConfigureAwait(false);
            var on = r.Machine is { } m ? $" on {m}" : "";
            var took = r.RunSeconds is { } s ? string.Create(CultureInfo.InvariantCulture, $" in {s:0.0}s") : "";
            var (verdict, message) = r.Status switch
            {
                JobStatus.Completed => (BaselineVerdict.New, $"{at} the same command Completed{on}{took}: this failure is new"),
                JobStatus.Failed => (BaselineVerdict.Preexisting, string.Create(CultureInfo.InvariantCulture, $"{at} the same command Failed (exit {r.ExitCode}){on}{took}: this failure is pre-existing")),
                _ => (BaselineVerdict.Inconclusive, $"{at} the same command ended {r.Status}{on}: inconclusive ({r.Message})"),
            };
            return Result(verdict, message) with
            {
                Status = r.Status.ToString(),
                ExitCode = r.CliExitCode,
                Machine = r.Machine,
                JobId = r.Machine is null && r.LogPath is null ? null : r.JobId,
                LogPath = r.LogPath,
                DurationSeconds = r.RunSeconds,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var logPath = machine is null ? null : log.LogPathFor(jobId) is var path && File.Exists(path) ? Path.GetFullPath(path) : null;
            return Result(BaselineVerdict.Inconclusive, $"{at}: inconclusive: cancelled (Ctrl+C)" + (machine is null ? " before it started" : "; its cleanup ran")) with
            {
                Status = Cancelled,
                ExitCode = 130,
                Machine = machine,
                JobId = machine is null ? null : jobId,
                LogPath = logPath,
            };
        }
        catch (Exception ex)
        {
            return Result(BaselineVerdict.Inconclusive, $"{at}: inconclusive: unexpected error: {ex.GetType().Name}: {ex.Message}") with
            {
                Status = nameof(JobStatus.InfraError),
                ExitCode = 70,
                Machine = machine,
            };
        }
    }
}
