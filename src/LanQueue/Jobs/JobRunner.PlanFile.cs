using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;

namespace LanQueue.Jobs;

/// <summary>
/// The wait gate every job of one lanq run --plan shares: which of its jobs (or shards) hold a slot now, and since when none has. A plan job
/// waiting for a slot counts --wait only while no job of the plan holds one, so it waits behind its siblings even with --wait 0 and ends
/// NoCapacity only when nothing of the plan runs (a job every machine rules out for good still ends at once).
/// </summary>
public sealed class PlanProgress
{
    private readonly Lock _gate = new();
    private int _holding;
    private long _idleSince = Stopwatch.GetTimestamp();
    private long _releases;
    private long _lastRelease;
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while at least one job of the plan holds a slot.</summary>
    public bool AnyHolding
    {
        get
        {
            lock (_gate)
            {
                return _holding > 0;
            }
        }
    }

    /// <summary>How long no job of the plan has held a slot (zero while one does): since the plan started, or since the last slot was given back.</summary>
    public TimeSpan IdleFor
    {
        get
        {
            lock (_gate)
            {
                return _holding > 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_idleSince);
            }
        }
    }

    /// <summary>Completes the next time a job of the plan gives back a slot (a job waiting for one looks again at once).</summary>
    public Task Released
    {
        get
        {
            lock (_gate)
            {
                return _released.Task;
            }
        }
    }

    /// <summary>
    /// The plan's latest slot release (a count, 1 for the first) when it happened at or after <paramref name="timestamp"/> (a
    /// <see cref="Stopwatch"/> timestamp), else 0. A job that saw every slot full in a look that began before that release has not seen the
    /// free slot yet: it looks once more before it may give up (<see cref="JobRunner.RoundStart"/>).
    /// </summary>
    public long ReleaseSince(long timestamp)
    {
        lock (_gate)
        {
            return _releases > 0 && _lastRelease >= timestamp ? _releases : 0;
        }
    }

    /// <summary>A job (or shard) of the plan now holds a slot.</summary>
    public void Hold()
    {
        lock (_gate)
        {
            _holding++;
        }
    }

    /// <summary>A job (or shard) of the plan gave its slot back (after its cleanup).</summary>
    public void Release()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            _holding = Math.Max(0, _holding - 1);
            _idleSince = Stopwatch.GetTimestamp();
            _lastRelease = _idleSince;
            _releases++;
            done = _released;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        done.TrySetResult();
    }
}

/// <summary>One job of lanq run --plan, ready to run: its lanq.yml name, the request its lanq run line makes, --shards (or null), and the jobs it starts after.</summary>
/// <param name="After">The selected jobs it waits for (edges to jobs left out by --only are already dropped).</param>
public sealed record PlanJobSpec(string Name, JobRequest Request, ShardOptions? Shards, IReadOnlyList<string> After);

/// <summary>
/// How one plan job ended. <see cref="Status"/> is a run status (Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed),
/// Cancelled, or <see cref="PlanJobOutcome.Skipped"/> (never started: a job it starts after did not complete, --fail-fast, or Ctrl+C).
/// </summary>
/// <param name="ExitCode">The job's lanq run exit code (Skipped: 0, written as null).</param>
public sealed record PlanJobOutcome(string Name, string Status, int ExitCode, string Message, IReadOnlyList<string> After)
{
    /// <summary>A plan job that never started.</summary>
    public const string Skipped = "Skipped";

    /// <summary>A single job's machine (null for a sharded job, or when it never ran).</summary>
    public string? Machine { get; init; }

    /// <summary>A single job's job id once it held a slot (null for a sharded job, or when it never ran).</summary>
    public string? JobId { get; init; }

    /// <summary>A single job's log once it held a slot (a sharded job has one per shard), else null.</summary>
    public string? LogPath { get; init; }

    /// <summary>Seconds from the plan's start to this job's start; null when it never started.</summary>
    public double? StartedSeconds { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The single job's own result (fetch, logs), or null.</summary>
    public JobResult? Single { get; init; }

    /// <summary>The sharded job's own result (every shard), or null.</summary>
    public ShardRunResult? Sharded { get; init; }

    /// <summary>NoCapacity only: see <see cref="JobResult.Retryable"/>.</summary>
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: see <see cref="JobResult.Excluded"/>.</summary>
    public IReadOnlyList<ExcludedMachine>? Excluded { get; init; }

    public RejectKind? RejectKind { get; init; }

    public InfraKind? InfraKind { get; init; }

    /// <summary>The job (or one of its shards) may still be running on its machine: the connection dropped while it ran and its cleanup did not finish.</summary>
    public bool MayStillRun { get; init; }
}

/// <summary>
/// A whole lanq run --plan: the overall status and exit code (<see cref="JobRunner.AggregatePlan"/>) and every selected job in launch order
/// (empty when the plan was refused before anything ran). <see cref="Decided"/> names the job that decided the status, if one did.
/// </summary>
public sealed record PlanRunResult(string Status, int ExitCode, string Message, IReadOnlyList<PlanJobOutcome> Jobs)
{
    public string? Decided { get; init; }

    /// <summary>NoCapacity only: from the deciding job.</summary>
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: from the deciding job.</summary>
    public IReadOnlyList<ExcludedMachine>? Excluded { get; init; }

    public RejectKind? RejectKind { get; init; }

    public InfraKind? InfraKind { get; init; }
}

public sealed partial class JobRunner
{
    /// <summary>How long the next ready job of a plan waits at most for the one started before it to hold a slot or begin waiting.</summary>
    internal static readonly TimeSpan PlanStagger = TimeSpan.FromSeconds(30);

    /// <summary>
    /// lanq run --plan. Every job's command line (and every shard's, for a fixed --shards N) is checked before any network or git work, all or
    /// nothing: one refusal refuses the plan (Rejected, 2), and a job no machine in the config can ever take ends the whole plan at once
    /// (NoCapacity, 75, not retryable). Then the repository is snapshotted and scanned once, and every job runs on that one payload: jobs with no
    /// after: start in <paramref name="ordered"/> order, each once the one before it holds its first slot(s) or begins waiting (at most
    /// <see cref="PlanStagger"/>); a job with after: starts when every job it waits for has Completed, and is Skipped otherwise. --wait counts
    /// only while no job of the plan holds a slot (<see cref="PlanProgress"/>). One job failing never stops the others unless
    /// <paramref name="failFast"/>: then it cancels the running jobs (each is cleaned up) and skips the rest. Ctrl+C cancels and cleans up every
    /// started job and returns what ran so far.
    /// </summary>
    public async Task<PlanRunResult> RunPlanAsync(IReadOnlyList<PlanJobSpec> ordered, bool failFast, CancellationToken ct)
    {
        if (PrecheckPlan(ordered) is { } ended)
        {
            return ended;
        }

        var planRun = NewId();
        var anyGit = ordered.SelectMany(j => Candidates(j.Request)).Any(m => m.Transfer == TransferMode.Git);
        var (prepared, failed) = await PrepareAsync(planRun, ordered[0].Request, anyGit, output, ct).ConfigureAwait(false);
        if (prepared is null)
        {
            return new PlanRunResult(failed!.Status.ToString(), failed.CliExitCode, failed.Message, [])
            {
                RejectKind = failed.RejectKind,
                InfraKind = failed.InfraKind,
            };
        }

        await using (prepared.ConfigureAwait(false))
        {
            // DispatchPlan returns only once every job it started has finished (and cleaned up), so the payload outlives them all.
            return await DispatchPlan(ordered, failFast, prepared, planRun, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The checks of every plan job before any network or git work (<see cref="Precheck"/>, and <see cref="PrecheckShards"/> for a fixed N): the
    /// first refusal in launch order refuses the plan ("job 'unit': ..."); else a job the config rules out on every candidate ends the plan as
    /// NoCapacity (not retryable), the other jobs Skipped. Null when every job may run.
    /// </summary>
    private PlanRunResult? PrecheckPlan(IReadOnlyList<PlanJobSpec> ordered)
    {
        var never = new Dictionary<string, JobResult>(StringComparer.Ordinal);
        foreach (var spec in ordered)
        {
            var check = PrecheckPlanJob(spec);
            if (check is { Status: JobStatus.NoCapacity } none)
            {
                never[spec.Name] = none;
            }
            else if (check is { } refused)
            {
                return new PlanRunResult(refused.Status.ToString(), refused.CliExitCode, $"job '{spec.Name}': {refused.Message}", [])
                {
                    Decided = spec.Name,
                    RejectKind = refused.RejectKind,
                    InfraKind = refused.InfraKind,
                };
            }
        }

        if (never.Count == 0)
        {
            return null;
        }

        var first = ordered.First(j => never.ContainsKey(j.Name)).Name;
        List<PlanJobOutcome> jobs = [.. ordered.Select(j => never.TryGetValue(j.Name, out var r)
            ? new PlanJobOutcome(j.Name, r.Status.ToString(), r.CliExitCode, r.Message, j.After) { Retryable = r.Retryable, Excluded = r.Excluded }
            : SkippedJob(j, $"not started: job '{first}' can never run as configured, so the plan did not start"))];
        return AggregatePlan(jobs, cancelled: false);
    }

    /// <summary>One plan job's command-line checks: as lanq run (single), or as lanq run --shards (shard 1, then every shard of a fixed N).</summary>
    private JobResult? PrecheckPlanJob(PlanJobSpec spec)
    {
        var req = spec.Request;
        if (spec.Shards is not { } options)
        {
            return Precheck("plan", req);
        }

        if (Precheck("shard", req with { Argv = new ShardInfo(1, 1).Substitute(req.Argv) }) is { } first)
        {
            return first.Status == JobStatus.NoCapacity ? first : first with { Message = "shard 1: " + first.Message };
        }

        if (options.Count is { } n && PrecheckShards(req, [.. Enumerable.Range(1, n).Select(i => new ShardInfo(i, n))]) is { } refusal)
        {
            return refusal.Refused with { Message = refusal.Message };
        }

        return null;
    }

    private static PlanJobOutcome SkippedJob(PlanJobSpec spec, string why) => new(spec.Name, PlanJobOutcome.Skipped, 0, why, spec.After);

    private async Task<PlanRunResult> DispatchPlan(IReadOnlyList<PlanJobSpec> ordered, bool failFast, PreparedPayload prepared, string planRun, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new PlanProgress();
        var sinceStart = Stopwatch.StartNew();
        var outcomes = new Dictionary<string, PlanJobOutcome>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        var running = new Dictionary<Task<PlanJobOutcome>, string>();
        string? failedFast = null;
        Exception? loopError = null;
        Task? gate = null;

        try
        {
            while (true)
            {
                foreach (var task in running.Keys.Where(t => t.IsCompleted).ToList())
                {
                    running.Remove(task);
                    var o = await task.ConfigureAwait(false);
                    outcomes[o.Name] = o;
                    if (failFast && failedFast is null && o.Status != nameof(JobStatus.Completed) && !stop.IsCancellationRequested)
                    {
                        failedFast = $"{o.Name} ended {o.Status}";
                        output($"[lanq] --fail-fast: job {o.Name} {o.Status}; cancelling the other jobs");
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                }

                if (stop.IsCancellationRequested)
                {
                    break;
                }

                // The first job in launch order that is not started and whose after: jobs have all ended: it starts if they all completed,
                // else it is skipped (which may settle the jobs after it in turn, so look again).
                PlanJobSpec? next = null;
                var skipped = false;
                foreach (var spec in ordered.Where(j => !started.Contains(j.Name) && !outcomes.ContainsKey(j.Name)))
                {
                    if (!spec.After.All(outcomes.ContainsKey))
                    {
                        continue;
                    }

                    if (spec.After.FirstOrDefault(d => outcomes[d].Status != nameof(JobStatus.Completed)) is { } bad)
                    {
                        outcomes[spec.Name] = SkippedJob(spec, $"not started: {bad} ended {outcomes[bad].Status}");
                        output($"[lanq] {spec.Name}: not started ({bad} ended {outcomes[bad].Status})");
                        skipped = true;
                        break;
                    }

                    next = spec;
                    break;
                }

                if (skipped)
                {
                    continue;
                }

                if (next is not null && (gate is null || gate.IsCompleted))
                {
                    started.Add(next.Name);
                    var placed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var job = RunPlanJob(next, prepared, progress, placed, planRun, sinceStart, stop.Token);
                    running[job] = next.Name;
                    gate = Task.WhenAny(placed.Task, job, delay(PlanStagger, stop.Token));
                    continue;
                }

                if (running.Count == 0 && next is null)
                {
                    break; // every job has ended (or was skipped)
                }

                var waits = new List<Task>(running.Keys);
                if (next is not null)
                {
                    waits.Add(gate!);
                }

                await Task.WhenAny(waits).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            // The plan's own loop broke: stop the jobs still running and wait for them below.
            loopError = ex;
            output(SafeLine($"[lanq] unexpected error: {ex.GetType().Name}: {ex.Message}; stopping the other jobs"));
            await stop.CancelAsync().ConfigureAwait(false);
        }

        // Cancelled (Ctrl+C, --fail-fast or an error above): wait for every started job to finish its cleanup. Each job task catches
        // everything itself, so none of these awaits throws.
        foreach (var task in running.Keys)
        {
            var o = await task.ConfigureAwait(false);
            outcomes[o.Name] = failedFast is not null && o.Status == Cancelled ? o with { Message = $"cancelled by --fail-fast ({failedFast}); " + o.Message } : o;
        }

        var userCancelled = ct.IsCancellationRequested;
        List<PlanJobOutcome> jobs = [.. ordered.Select(j => outcomes.TryGetValue(j.Name, out var o)
            ? o
            : SkippedJob(j, failedFast is not null ? $"not started: --fail-fast after {failedFast}"
                : loopError is not null ? SafeLine($"not started: unexpected error: {loopError.GetType().Name}: {loopError.Message}")
                : userCancelled ? "not started: cancelled (Ctrl+C)"
                : "not started"))];
        var result = AggregatePlan(jobs, userCancelled && jobs.Any(j => j.Status is Cancelled or PlanJobOutcome.Skipped));
        return loopError is not null && result.Status != nameof(JobStatus.Failed)
            ? result with { Status = nameof(JobStatus.InfraError), ExitCode = 70, InfraKind = Jobs.InfraKind.Unexpected, Message = SafeLine($"{result.Message}; unexpected error: {loopError.GetType().Name}: {loopError.Message}") }
            : result;
    }

    /// <summary>Runs one plan job (single or sharded) on the shared payload; never throws (an error or Ctrl+C is the job's outcome).</summary>
    private Task<PlanJobOutcome> RunPlanJob(
        PlanJobSpec spec, PreparedPayload prepared, PlanProgress progress, TaskCompletionSource placed, string planRun, Stopwatch sinceStart, CancellationToken ct) => Task.Run(async () =>
    {
        var startedAt = Math.Round(sinceStart.Elapsed.TotalSeconds, 1);
        var clock = Stopwatch.StartNew();
        var req = spec.Request with { PlanJob = spec.Name, PlanRun = planRun, Progress = progress, Placed = () => placed.TrySetResult() };
        var jobId = NewId();
        string? machine = null;
        try
        {
            if (spec.Shards is { } options)
            {
                var r = await RunShardsPreparedAsync(req, options, prepared, ct).ConfigureAwait(false);
                return new PlanJobOutcome(spec.Name, r.Status, r.ExitCode, r.Message, spec.After)
                {
                    StartedSeconds = startedAt,
                    Duration = clock.Elapsed,
                    Sharded = r,
                    Retryable = r.Retryable,
                    Excluded = r.Excluded,
                    RejectKind = r.RejectKind,
                    InfraKind = r.InfraKind,
                    MayStillRun = r.Shards.Any(s => s.MayStillRun),
                };
            }

            var one = await Dispatch(jobId, req, prepared, ct, m => machine = m).ConfigureAwait(false);
            return new PlanJobOutcome(spec.Name, one.Status.ToString(), one.CliExitCode, SafeLine(one.Message), spec.After)
            {
                Machine = one.Machine,
                JobId = one.LogPath is null && one.Machine is null ? null : one.JobId,
                LogPath = one.LogPath,
                StartedSeconds = startedAt,
                Duration = clock.Elapsed,
                Single = one,
                Retryable = one.Status == JobStatus.NoCapacity ? one.Retryable ?? true : null,
                Excluded = one.Status == JobStatus.NoCapacity ? one.Excluded ?? [] : null,
                RejectKind = one.RejectKind,
                InfraKind = one.InfraKind,
                MayStillRun = one.MayStillRun && !one.CleanedUp,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var logPath = machine is null ? null : log.LogPathFor(jobId) is var path && File.Exists(path) ? Path.GetFullPath(path) : null;
            return new PlanJobOutcome(spec.Name, Cancelled, 130, machine is null ? "cancelled before it started" : "cancelled; its cleanup ran", spec.After)
            {
                Machine = machine,
                JobId = machine is null ? null : jobId,
                LogPath = logPath,
                StartedSeconds = startedAt,
                Duration = clock.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new PlanJobOutcome(spec.Name, nameof(JobStatus.InfraError), 70, SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), spec.After)
            {
                Machine = machine,
                StartedSeconds = startedAt,
                Duration = clock.Elapsed,
                InfraKind = Jobs.InfraKind.Unexpected,
            };
        }
        finally
        {
            placed.TrySetResult();
        }
    }, CancellationToken.None);

    /// <summary>
    /// The overall result of a plan. Every job completed: Completed, 0. Cancelled by the caller (Ctrl+C) while jobs were unfinished: Cancelled,
    /// 130. Otherwise the job with the most telling status decides, earliest in launch order first: Failed (its own exit code, as for shards)
    /// before Rejected (2), TimedOut and WindowClosed (124), InfraError (70), NoCapacity (75), Cancelled by --fail-fast (130) and Skipped.
    /// </summary>
    internal static PlanRunResult AggregatePlan(IReadOnlyList<PlanJobOutcome> jobs, bool cancelled)
    {
        var done = jobs.Count(j => j.Status == nameof(JobStatus.Completed));
        var summary = string.Create(CultureInfo.InvariantCulture, $"{done} of {jobs.Count} job(s) completed");
        if (cancelled)
        {
            return new PlanRunResult(Cancelled, 130, summary + "; cancelled (Ctrl+C), every started job was cleaned up", jobs);
        }

        var worst = jobs.Select((j, i) => (j, i)).Where(x => x.j.Status != nameof(JobStatus.Completed)).OrderBy(x => PlanRank(x.j.Status)).ThenBy(x => x.i).Select(x => x.j).FirstOrDefault();
        if (worst is null)
        {
            return new PlanRunResult(nameof(JobStatus.Completed), 0, summary, jobs);
        }

        var noCapacity = worst.Status == nameof(JobStatus.NoCapacity);

        // Skipped never decides on its own (a job is skipped only after another did not complete); 1 keeps a failure a failure if it ever did.
        var exit = worst.Status == PlanJobOutcome.Skipped ? 1 : worst.ExitCode;
        return new PlanRunResult(worst.Status, exit, $"{summary}; {worst.Name}: {worst.Status}: {worst.Message}", jobs)
        {
            Decided = worst.Name,
            Retryable = noCapacity ? worst.Retryable ?? true : null,
            Excluded = noCapacity ? worst.Excluded ?? [] : null,
            RejectKind = worst.RejectKind,
            InfraKind = worst.InfraKind,
        };

        static int PlanRank(string status) => status switch
        {
            nameof(JobStatus.Failed) => 0,
            nameof(JobStatus.Rejected) => 1,
            nameof(JobStatus.TimedOut) => 2,
            nameof(JobStatus.WindowClosed) => 3,
            nameof(JobStatus.InfraError) => 4,
            nameof(JobStatus.NoCapacity) => 5,
            Cancelled => 6,
            _ => 7,
        };
    }
}
