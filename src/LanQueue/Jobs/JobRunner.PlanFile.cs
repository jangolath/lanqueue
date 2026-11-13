using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Payload;
using LanQueue.Ssh;

namespace LanQueue.Jobs;

/// <summary>
/// The wait gate every job of one lanq run --plan shares: which of its jobs (or shards) hold a slot now, and since when none has. A plan job
/// waiting for a slot counts --wait only while no job of the plan holds one, so it waits behind its siblings even with --wait 0 and ends
/// NoCapacity only when nothing of the plan runs (a job every machine rules out for good still ends at once).
/// </summary>
/// <param name="holders">Who holds the slots a waiting job waits behind, in its waiting line ("this plan's other jobs hold some").</param>
public sealed class PlanProgress(string holders = "this plan's other jobs")
{
    /// <summary>Who holds the slots a waiting job waits behind (see the constructor).</summary>
    public string Holders => holders;

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

/// <summary>
/// lanq run --plan --plan-timeout M (lanq.yml plan_timeout: M, 0.10.0): the whole plan's limit, shared by every job and baseline of one plan. It
/// starts after the precheck and the snapshot. On expiry <see cref="Expired"/> is set BEFORE the plan's jobs are cancelled, so each one that
/// stops can tell a plan timeout from Ctrl+C or --fail-fast: a running job is cleaned up and ends TimedOut (124), a job waiting for a slot ends
/// TimedOut, a job not started yet is Skipped, and a baseline not finished is inconclusive.
/// </summary>
/// <remarks>
/// One state, changed atomically, decides why the plan stopped, so records and the plan's status agree: Open, Expired (the limit came first),
/// Held (--fail-fast or an error stopped the jobs first), HeldDue (held, and the limit came meanwhile: <see cref="Release"/> applies it,
/// synchronously, before any baseline starts) or Closed (the plan ended first: the limit can no longer be reached).
/// </remarks>
public sealed class PlanDeadline(int minutes)
{
    private const int Open = 0;
    private const int ExpiredState = 1;
    private const int Closed = 2;
    private const int Held = 3;
    private const int HeldDue = 4;

    private int _state;
    private volatile bool _cameDuringHold;
    private int _running;

    /// <summary>The limit, in minutes (1 to 10080).</summary>
    public int Minutes => minutes;

    /// <summary>True once the limit was reached first (set before anything is cancelled); never after --fail-fast stopped the jobs or the plan ended.</summary>
    public bool Expired => Volatile.Read(ref _state) == ExpiredState;

    /// <summary>The limit is reached: true when it came first (the caller then says so and cancels the plan), false when the plan was already stopped or ended.</summary>
    internal bool TryExpire() => Interlocked.CompareExchange(ref _state, ExpiredState, Open) == Open;

    /// <summary>--fail-fast (or an error) stops the plan's jobs: true when it came first, so the jobs it stops are not TimedOut; false when the limit did.</summary>
    internal bool Hold() => Interlocked.CompareExchange(ref _state, Held, Open) == Open;

    /// <summary>The limit came while the plan was held: true when it was held (the limit is now due, for <see cref="Release"/> to apply).</summary>
    internal bool MarkDue()
    {
        if (Interlocked.CompareExchange(ref _state, HeldDue, Held) != Held)
        {
            return false;
        }

        _cameDuringHold = true;
        return true;
    }

    /// <summary>True once the limit came while --fail-fast (or an error) held the plan: the plan is then flagged planTimedOut.</summary>
    internal bool CameDuringHold => _cameDuringHold;

    /// <summary>Tests only: called with the state the timer is about to act on, before its compare-and-swap (to interleave a Release or a Hold).</summary>
    internal Action<string>? BeforeReach { get; set; }

    /// <summary>
    /// The limit is due: one compare-and-swap loop on the state, so a Hold or Release in between is never lost. Open: Expired (the caller says
    /// so and cancels the plan). Held: HeldDue (the caller says so; <see cref="Release"/> applies it). Expired or Closed: nothing. A failed swap
    /// (the state changed meanwhile) looks again.
    /// </summary>
    internal PlanReach Reach()
    {
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Open:
                    BeforeReach?.Invoke("Open");
                    if (TryExpire())
                    {
                        return PlanReach.Expired;
                    }

                    break;
                case Held:
                    BeforeReach?.Invoke("Held");
                    if (MarkDue())
                    {
                        return PlanReach.HeldDue;
                    }

                    break;
                default:
                    return PlanReach.None;
            }
        }
    }

    /// <summary>
    /// The jobs have all ended after a hold, and the baselines may start: the limit applies again. True when it already came during the hold:
    /// the deadline is now expired, and the caller cancels the plan BEFORE any baseline starts.
    /// </summary>
    internal bool Release()
    {
        if (Interlocked.CompareExchange(ref _state, ExpiredState, HeldDue) == HeldDue)
        {
            return true;
        }

        Interlocked.CompareExchange(ref _state, Open, Held);
        return false;
    }

    /// <summary>The plan ended: the limit can no longer be reached. False when it already was.</summary>
    internal bool Close()
    {
        foreach (var from in new[] { Open, Held, HeldDue })
        {
            if (Interlocked.CompareExchange(ref _state, Closed, from) == from)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many jobs of the plan are started and not ended (for the line printed on expiry).</summary>
    public int Running
    {
        get => Volatile.Read(ref _running);
        set => Volatile.Write(ref _running, value);
    }

    /// <summary>A job that held a slot and was stopped by it.</summary>
    public string RunningMessage => string.Create(CultureInfo.InvariantCulture, $"plan_timeout {minutes} min reached; cancelled and cleaned up");

    /// <summary>A job (or shard) still waiting for a slot when it was reached: nothing was claimed, nothing to clean up.</summary>
    public const string WaitingMessage = "plan_timeout reached while waiting for a slot";

    /// <summary>A job that had not started (behind after: or the stagger).</summary>
    public const string NotStartedMessage = "not started: plan_timeout reached";

    /// <summary>The line lanq prints when the limit comes while --fail-fast (or an error) is already stopping the jobs.</summary>
    public string HeldLine => string.Create(
        CultureInfo.InvariantCulture, $"[lanq] plan_timeout: {minutes} min reached while the jobs were already being stopped (--fail-fast or an error); no baseline rerun will start");

    /// <summary>The line lanq prints when the limit is reached (started jobs: some may still be waiting for a slot).</summary>
    public string ReachedLine => string.Create(CultureInfo.InvariantCulture, $"[lanq] plan_timeout: {minutes} min reached; stopping {Running} started job(s)");
}

/// <summary>What reaching the plan's limit did (<see cref="PlanDeadline.Reach"/>).</summary>
internal enum PlanReach
{
    /// <summary>The plan had ended (or the limit was reached already): nothing.</summary>
    None,

    /// <summary>The limit came first: the plan is to be stopped now.</summary>
    Expired,

    /// <summary>--fail-fast (or an error) was stopping the jobs: the limit is due when the hold is released.</summary>
    HeldDue,
}

/// <summary>One job of lanq run --plan, ready to run: its lanq.yml name, the request its lanq run line makes, --shards (or null), and the jobs it starts after.</summary>
/// <param name="After">The selected jobs it waits for (edges to jobs left out by --only are already dropped).</param>
public sealed record PlanJobSpec(string Name, JobRequest Request, ShardOptions? Shards, IReadOnlyList<string> After)
{
    /// <summary>The job's place in lanq.yml (0 for the first job in the file): breaks ties in <see cref="JobRunner.AggregatePlan"/>.</summary>
    public int FileOrder { get; init; }

    /// <summary>Where the job is defined, for refusals found once the repository is read ("lanq.yml: job 'x' (line 14)"); null means "job 'x'".</summary>
    public string? Where { get; init; }
}

/// <summary>
/// How one plan job ended. <see cref="Status"/> is a run status (Completed, Failed, Rejected, NoCapacity, InfraError, TimedOut, WindowClosed),
/// Cancelled, or <see cref="PlanJobOutcome.Skipped"/> (never started: a job it starts after did not complete, --fail-fast, or Ctrl+C).
/// </summary>
/// <param name="ExitCode">The job's lanq run exit code (Skipped: 0, written as null).</param>
public sealed record PlanJobOutcome(string Name, string Status, int ExitCode, string Message, IReadOnlyList<string> After)
{
    /// <summary>A plan job that never started.</summary>
    public const string Skipped = "Skipped";

    /// <summary>The job's place in lanq.yml (<see cref="PlanJobSpec.FileOrder"/>).</summary>
    public int FileOrder { get; init; }

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

    /// <summary>baseline: true, a single job that ended Failed: its rerun at the base commit (a sharded job has one per failed shard); else null.</summary>
    public BaselineResult? Baseline { get; init; }
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

    /// <summary>
    /// NoCapacity from --on / --size / --min-size narrowing only (0.10.0): the selected jobs the narrowing still leaves a machine and that do
    /// not start after (directly or not) a job it leaves none, in file order (the suggested rerun's --only, which replaces any --only typed);
    /// null otherwise.
    /// </summary>
    public IReadOnlyList<string>? Runnable { get; init; }

    /// <summary>--plan-timeout: the limit in minutes the plan ran under, or null without one.</summary>
    public int? PlanTimeoutMinutes { get; init; }

    /// <summary>--plan-timeout: true when the limit was reached before the plan ended (jobs and baselines included).</summary>
    public bool PlanTimedOut { get; init; }
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
    /// <param name="planTimeoutMinutes">
    /// --plan-timeout (lanq.yml plan_timeout:): the whole plan's limit, from after the snapshot to the last baseline (<see cref="PlanDeadline"/>);
    /// on expiry the plan is TimedOut (124) unless a Failed or Rejected job decides it. Null for none.
    /// </param>
    public async Task<PlanRunResult> RunPlanAsync(IReadOnlyList<PlanJobSpec> ordered, bool failFast, CancellationToken ct, int? planTimeoutMinutes = null)
    {
        var result = await RunPlanCoreAsync(ordered, failFast, ct, planTimeoutMinutes is { } m ? new PlanDeadline(m) : null).ConfigureAwait(false);
        return result with { PlanTimeoutMinutes = planTimeoutMinutes };
    }

    private async Task<PlanRunResult> RunPlanCoreAsync(IReadOnlyList<PlanJobSpec> ordered, bool failFast, CancellationToken ct, PlanDeadline? deadline)
    {
        if (PrecheckPlan(ordered) is { } ended)
        {
            return ended;
        }

        var planRun = NewId();
        var (bases, badBase) = await ResolvePlanBasesAsync(ordered, ct).ConfigureAwait(false);
        if (badBase is not null)
        {
            return badBase;
        }

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
            // --plan-timeout starts now, after the precheck and the snapshot, and covers the waits, the jobs and the baselines. The plan's token
            // is cancelled by Ctrl+C or by the deadline; the timer stops at Ctrl+C (which then wins) and when the plan ends.
            using var plan = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (deadline is not null)
            {
                ordered = [.. ordered.Select(s => s with { Request = s.Request with { PlanTimeout = deadline } })];
            }

            var timer = deadline is null ? Task.CompletedTask : PlanTimerAsync(deadline, plan, timerStop.Token, ct);
            try
            {
                // DispatchPlan returns only once every job it started has finished (and cleaned up), so the payload outlives them all.
                var result = await DispatchPlan(ordered, failFast, prepared, planRun, ct, plan.Token, deadline, closeAtEnd: bases.Count == 0).ConfigureAwait(false);
                if (bases.Count > 0)
                {
                    // After --fail-fast the limit applies again to the baselines. One that came during the hold is applied here, before any
                    // baseline starts (unless Ctrl+C came: then the plan is the user's, not the limit's).
                    if (ct.IsCancellationRequested)
                    {
                        deadline?.Close();
                    }
                    else if (deadline?.Release() == true)
                    {
                        await plan.CancelAsync().ConfigureAwait(false);
                    }

                    result = await WithPlanBaselinesAsync(ordered, result, bases, planRun, prepared.Payload, plan.Token, deadline).ConfigureAwait(false);
                }

                deadline?.Close();
                if (deadline is { CameDuringHold: true })
                {
                    // The limit came due before the plan finished its work (while --fail-fast stopped the jobs): flagged, whether or not a
                    // baseline was left to cut short. The status stays the deciding job's.
                    result = result with { PlanTimedOut = true };
                }
                else if (deadline is { Expired: true } && !result.PlanTimedOut)
                {
                    // The limit came while the last job was only cleaning up: the "stopping" line above stopped nothing; say so.
                    output("[lanq] plan_timeout: nothing was stopped; every job had already ended");
                }

                return result;
            }
            finally
            {
                await timerStop.CancelAsync().ConfigureAwait(false);
                await timer.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits out the plan's limit; then marks <paramref name="deadline"/> expired, says so, and only then cancels the plan's token. Returns quietly
    /// when stopped first (the plan ended, or Ctrl+C came first). Never throws.
    /// </summary>
    private async Task PlanTimerAsync(PlanDeadline deadline, CancellationTokenSource plan, CancellationToken stop, CancellationToken user)
    {
        try
        {
            await delay(TimeSpan.FromMinutes(deadline.Minutes), stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (stop.IsCancellationRequested || user.IsCancellationRequested)
        {
            return;
        }

        switch (deadline.Reach())
        {
            case PlanReach.HeldDue:
                // --fail-fast (or an error) is stopping the jobs: the limit is now due, and Release applies it before any baseline starts.
                output(deadline.HeldLine);
                return;
            case PlanReach.None:
                return; // the plan ended first
        }

        output(deadline.ReachedLine);
        try
        {
            await plan.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // the plan ended meanwhile
        }
    }

    /// <summary>
    /// The base commit of every plan job with baseline: true (<see cref="ResolveBaseAsync"/>, once per distinct baseline_ref), before anything is
    /// built or sent: by job name, or the plan refused (Rejected 2, "job 'unit': ...") at the first job whose base cannot be resolved.
    /// </summary>
    private async Task<(Dictionary<string, BaseCommit> Bases, PlanRunResult? Refused)> ResolvePlanBasesAsync(IReadOnlyList<PlanJobSpec> ordered, CancellationToken ct)
    {
        var bases = new Dictionary<string, BaseCommit>(StringComparer.Ordinal);
        var byRef = new Dictionary<string, BaseCommit>(StringComparer.Ordinal);
        foreach (var spec in ordered.Where(j => j.Request.Baseline))
        {
            // A baseline_ref from lanq.yml (a file of the repository, not the developer's own command line) must name a branch or tag: a commit
            // id or HEAD~n could pick any old commit and send its tree to a worker. --baseline-ref on the command line takes any commit. The
            // exact full ref the check found is what is resolved, so the check and the resolution see the same object.
            var request = spec.Request;
            string? fullRef = null;
            if (request.BaselineRef is { } named)
            {
                NamedRef checkedRef;
                try
                {
                    checkedRef = await payloads.BranchOrTagAsync(request.RepoDir, named, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    // A git call that failed to run or timed out (GitTimeoutException): refused as a --baseline base that cannot be read is.
                    return (bases, new PlanRunResult(nameof(JobStatus.Rejected), 2, SafeLine($"job '{spec.Name}': --baseline: could not read the repository: {ex.Message}"), [])
                    {
                        Decided = spec.Name,
                        RejectKind = Jobs.RejectKind.Arguments,
                    });
                }

                if (checkedRef.FullRef is null)
                {
                    return (bases, new PlanRunResult(nameof(JobStatus.Rejected), 2, $"{spec.Where ?? $"job '{spec.Name}'"}: {checkedRef.Error ?? BaseRef.PlanRefMessage}", [])
                    {
                        Decided = spec.Name,
                        RejectKind = Jobs.RejectKind.PlanFile,
                    });
                }

                fullRef = checkedRef.FullRef;
                request = request with { BaselineRef = fullRef };
            }

            var key = fullRef ?? "";
            if (!byRef.TryGetValue(key, out var b))
            {
                var (resolved, refused) = await ResolveBaseAsync("plan", request, ct).ConfigureAwait(false);
                if (refused is not null)
                {
                    return (bases, new PlanRunResult(refused.Status.ToString(), refused.CliExitCode, $"job '{spec.Name}': {refused.Message}", [])
                    {
                        Decided = spec.Name,
                        RejectKind = refused.RejectKind,
                    });
                }

                // Shown as written in lanq.yml (main), though resolved as its full ref (refs/heads/main).
                b = fullRef is null || spec.Request.BaselineRef is not { } written
                    ? resolved!
                    : resolved! with { Ref = written, Label = resolved.Label.Replace(fullRef, written, StringComparison.Ordinal) };
                byRef[key] = b;
            }

            bases[spec.Name] = b;
        }

        return (bases, null);
    }

    /// <summary>
    /// baseline: true in a plan: after every job has ended, the failed jobs that asked for it (a single job that ended Failed, or each Failed
    /// shard of a sharded job; never a job Skipped after them) are rerun at their base all at once. Each result goes on its job (or shard); the
    /// plan's status, exit code and message stay as they are. Nothing reruns after Ctrl+C: each such job's baseline is inconclusive, "cancelled
    /// (Ctrl+C) before it started".
    /// </summary>
    private async Task<PlanRunResult> WithPlanBaselinesAsync(
        IReadOnlyList<PlanJobSpec> ordered, PlanRunResult result, Dictionary<string, BaseCommit> bases, string planRun, JobPayload snapshot, CancellationToken ct,
        PlanDeadline? deadline = null)
    {
        var targets = new List<(string Name, int? Shard, BaselineTarget Target)>();
        foreach (var j in result.Jobs)
        {
            if (!bases.TryGetValue(j.Name, out var b) || ordered.FirstOrDefault(s => s.Name == j.Name) is not { } spec)
            {
                continue;
            }

            var req = spec.Request with { PlanJob = spec.Name, PlanRun = planRun };
            if (j.Single is { Status: JobStatus.Failed } one)
            {
                targets.Add((j.Name, null, new BaselineTarget(req, b, one.JobId, one.Machine)));
            }

            foreach (var s in j.Sharded?.Shards.Where(x => x.Status == nameof(JobStatus.Failed) && x.JobId is not null) ?? [])
            {
                targets.Add((j.Name, s.Index, new BaselineTarget(ShardRequest(req, new ShardInfo(s.Index, j.Sharded!.Count)), b, s.JobId!, s.Machine)));
            }
        }

        if (targets.Count == 0)
        {
            return result;
        }

        if (deadline is not null)
        {
            deadline.Running = targets.Count;
        }

        var baselines = await RunBaselinesAsync([.. targets.Select(t => t.Target)], snapshot, ct).ConfigureAwait(false);
        var stopped = deadline is { Expired: true } && baselines.Any(b => b.Message.Contains("plan_timeout", StringComparison.Ordinal));
        var found = targets.Zip(baselines).ToDictionary(x => (x.First.Name, x.First.Shard), x => x.Second);
        return result with
        {
            Jobs = [.. result.Jobs.Select(j =>
            {
                var single = found.GetValueOrDefault((j.Name, (int?)null));
                var sharded = j.Sharded is { } r
                    ? r with { Shards = [.. r.Shards.Select(s => found.TryGetValue((j.Name, s.Index), out var sb) ? s with { Baseline = sb } : s)] }
                    : null;
                return j with { Baseline = single ?? j.Baseline, Sharded = sharded };
            })],
            PlanTimedOut = result.PlanTimedOut || stopped,
        };
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
            ? new PlanJobOutcome(j.Name, r.Status.ToString(), r.CliExitCode, r.Message, j.After) { Retryable = r.Retryable, Excluded = r.Excluded, FileOrder = j.FileOrder }
            : SkippedJob(j, $"not started: job '{first}' can never run as configured, so the plan did not start") with { FileOrder = j.FileOrder })];
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

    /// <param name="ct">Ctrl+C.</param>
    /// <param name="planToken">The plan's token: Ctrl+C, or <paramref name="deadline"/> reached (it is set expired first).</param>
    private async Task<PlanRunResult> DispatchPlan(
        IReadOnlyList<PlanJobSpec> ordered, bool failFast, PreparedPayload prepared, string planRun, CancellationToken ct, CancellationToken planToken, PlanDeadline? deadline,
        bool closeAtEnd = true)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(planToken);
        var progress = new PlanProgress();
        var sinceStart = Stopwatch.StartNew();
        var outcomes = new Dictionary<string, PlanJobOutcome>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        var running = new Dictionary<Task<PlanJobOutcome>, string>();
        string? failedFast = null;
        Exception? loopError = null;
        Task? gate = null;

        // Completes when the plan is stopped (Ctrl+C, --fail-fast, --plan-timeout), so the loop never waits past it, even with nothing running.
        var stopped = Task.Delay(Timeout.Infinite, stop.Token);

        try
        {
            while (true)
            {
                if (deadline is not null)
                {
                    deadline.Running = running.Count;
                }

                foreach (var task in running.Keys.Where(t => t.IsCompleted).ToList())
                {
                    running.Remove(task);
                    var o = await task.ConfigureAwait(false);
                    outcomes[o.Name] = o;
                    // The plan's limit may have come first; then it, not --fail-fast, stops the other jobs.
                    if (failFast && failedFast is null && o.Status != nameof(JobStatus.Completed) && !stop.IsCancellationRequested && (deadline is null || deadline.Hold()))
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
                    // Every job has ended (or was skipped). Without baselines to follow the plan is over: a limit reached from now on changes
                    // nothing. One reached just before stopped nothing either (see PlanTimedOutResult).
                    if (closeAtEnd)
                    {
                        deadline?.Close();
                    }

                    break;
                }

                var waits = new List<Task>(running.Keys) { stopped };
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
            deadline?.Hold();
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

        // --plan-timeout came first (Ctrl+C after it does not change that): the jobs it stopped end TimedOut, never Cancelled.
        var timedOut = deadline is { Expired: true } && failedFast is null && loopError is null;
        if (timedOut)
        {
            foreach (var (name, o) in outcomes.ToList())
            {
                outcomes[name] = PlanTimedOutJob(o, deadline!);
            }
        }

        var userCancelled = ct.IsCancellationRequested && !timedOut;
        List<PlanJobOutcome> jobs = [.. ordered.Select(j => (outcomes.TryGetValue(j.Name, out var o)
            ? o
            : SkippedJob(j, failedFast is not null ? $"not started: --fail-fast after {failedFast}"
                : loopError is not null ? SafeLine($"not started: unexpected error: {loopError.GetType().Name}: {loopError.Message}")
                : timedOut ? PlanDeadline.NotStartedMessage
                : userCancelled ? "not started: cancelled (Ctrl+C)"
                : "not started")) with { FileOrder = j.FileOrder })];
        var result = AggregatePlan(jobs, userCancelled && jobs.Any(j => j.Status is Cancelled or PlanJobOutcome.Skipped));
        if (loopError is not null && result.Status != nameof(JobStatus.Failed))
        {
            return result with { Status = nameof(JobStatus.InfraError), ExitCode = 70, InfraKind = Jobs.InfraKind.Unexpected, Message = SafeLine($"{result.Message}; unexpected error: {loopError.GetType().Name}: {loopError.Message}") };
        }

        return timedOut ? PlanTimedOutResult(result, deadline!) : result;
    }

    /// <summary>
    /// The plan's status once --plan-timeout was reached: TimedOut (124) unless a Failed or Rejected job decides it, also when the limit came
    /// between after: waves or while every job was waiting (nothing running). The job that decides is the first TimedOut one in lanq.yml, if any.
    /// </summary>
    /// <remarks>
    /// When the limit stopped nothing (every job had already ended, the last one only cleaning up), the result is returned unchanged and not
    /// flagged: a plan never ends Completed with planTimedOut. Otherwise it is flagged <see cref="PlanRunResult.PlanTimedOut"/>.
    /// </remarks>
    internal static PlanRunResult PlanTimedOutResult(PlanRunResult result, PlanDeadline deadline)
    {
        if (!result.Jobs.Any(j => j.Status is nameof(JobStatus.TimedOut) or PlanJobOutcome.Skipped or Cancelled))
        {
            return result;
        }

        if (result.Status is nameof(JobStatus.Failed) or nameof(JobStatus.Rejected))
        {
            return result with { PlanTimedOut = true };
        }

        var jobs = result.Jobs;
        var done = jobs.Count(j => j.Status == nameof(JobStatus.Completed));
        var first = jobs.Where(j => j.Status == nameof(JobStatus.TimedOut)).OrderBy(j => j.FileOrder).FirstOrDefault();
        return new PlanRunResult(
            nameof(JobStatus.TimedOut),
            ProcessSshClient.TimedOutExitCode,
            string.Create(CultureInfo.InvariantCulture, $"{done} of {jobs.Count} job(s) completed; plan_timeout {deadline.Minutes} min reached")
                + (first is null ? "" : $"; {first.Name}: {first.Status}: {first.Message}"),
            jobs)
        {
            Decided = first?.Name,
            PlanTimedOut = true,
        };
    }

    /// <summary>
    /// One job's outcome after --plan-timeout stopped the plan: Cancelled becomes TimedOut (124), "cancelled and cleaned up" when it held a slot,
    /// "while waiting for a slot" when it never did; a sharded job's cancelled shards the same, and its status is aggregated again. Others stay.
    /// </summary>
    internal static PlanJobOutcome PlanTimedOutJob(PlanJobOutcome o, PlanDeadline deadline)
    {
        if (o.Sharded is { } r)
        {
            if (!r.Shards.Any(s => s.Status == Cancelled))
            {
                return o;
            }

            var shards = r.Shards.Select(s => s.Status != Cancelled ? s : s with
            {
                Status = nameof(JobStatus.TimedOut),
                ExitCode = ProcessSshClient.TimedOutExitCode,
                Message = s.Machine is null ? PlanDeadline.WaitingMessage : deadline.RunningMessage,
            }).ToList();
            var again = Aggregate(r.Count, shards, cancelled: false) with { Timing = r.Timing };
            return o with { Status = again.Status, ExitCode = again.ExitCode, Message = again.Message, Sharded = again, RejectKind = again.RejectKind, InfraKind = again.InfraKind };
        }

        return o.Status != Cancelled
            ? o
            : o with
            {
                Status = nameof(JobStatus.TimedOut),
                ExitCode = ProcessSshClient.TimedOutExitCode,
                Message = o.Machine is null ? PlanDeadline.WaitingMessage : deadline.RunningMessage,
            };
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
    /// 130. Otherwise the job with the most telling status decides, the first in lanq.yml among equals (<see cref="PlanJobOutcome.FileOrder"/>,
    /// never the launch order, which follows run history): Failed (its own exit code, as for shards)
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

        var worst = jobs.Select((j, i) => (j, i)).Where(x => x.j.Status != nameof(JobStatus.Completed)).OrderBy(x => PlanRank(x.j.Status)).ThenBy(x => x.j.FileOrder).ThenBy(x => x.i).Select(x => x.j).FirstOrDefault();
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
