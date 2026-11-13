using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Payload;
using LanQueue.Policy;
using LanQueue.Scheduling;

namespace LanQueue.Jobs;

/// <summary>One shard of a lanq run --shards run: <see cref="Index"/> is 1-based, <see cref="Count"/> is N.</summary>
public sealed record ShardInfo(int Index, int Count)
{
    /// <summary>The most shards one run may have (--shards 1 to 64).</summary>
    public const int MaxShards = 64;

    /// <summary>The most re-runs of one shard after a lost connection (--shard-retries 0 to 3).</summary>
    public const int MaxRetries = 3;

    /// <summary>The placeholder for the 1-based shard index; substituted in the job's arguments only, never expanded any further.</summary>
    public const string IndexToken = "{shard}";

    /// <summary>The placeholder for the number of shards.</summary>
    public const string CountToken = "{shards}";

    /// <summary>
    /// The job's arguments for this shard: every literal <see cref="CountToken"/> and <see cref="IndexToken"/> becomes the number, and nothing
    /// else changes (no other syntax is interpreted). The result is then checked like any command line, per shard.
    /// </summary>
    public IReadOnlyList<string> Substitute(IReadOnlyList<string> argv)
    {
        var index = Index.ToString(CultureInfo.InvariantCulture);
        var count = Count.ToString(CultureInfo.InvariantCulture);
        return [.. argv.Select(a => a.Replace(CountToken, count, StringComparison.Ordinal).Replace(IndexToken, index, StringComparison.Ordinal))];
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Index}/{Count}");
}

/// <param name="Count">N, or null for --shards auto (N = the free slots that can take the job right now).</param>
/// <param name="Retries">--shard-retries: re-runs of a shard whose machine stopped answering (never a job's own failure).</param>
/// <param name="FailFast">--fail-fast: the first shard that does not complete cancels the rest.</param>
/// <param name="TargetMinutes">
/// --target-minutes M (with --shards auto only): N from earlier runs of the same command, so each shard takes about M minutes
/// (<see cref="ShardTiming.Pick"/>); null keeps plain auto (N = the free slots).
/// </param>
public sealed record ShardOptions(int? Count, int Retries = 0, bool FailFast = false, int? TargetMinutes = null);

/// <summary>One shard's outcome. Status is a <see cref="JobStatus"/> name or Cancelled; JobId and Machine are null when it never ran.</summary>
public sealed record ShardResult(int Index, string Status, int ExitCode, string? Machine, string? JobId, TimeSpan Duration, int Attempts, string Message)
{
    /// <summary>What --fetch brought back for the shard's last attempt; null when no fetch was asked for or it never ran.</summary>
    public FetchReport? Fetch { get; init; }

    /// <summary>NoCapacity only: see <see cref="JobResult.Retryable"/>.</summary>
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: see <see cref="JobResult.Excluded"/>.</summary>
    public IReadOnlyList<ExcludedMachine>? Excluded { get; init; }

    /// <summary>The full path of the local log of the shard's last attempt that got a slot (see <see cref="JobResult.LogPath"/>), or null.</summary>
    public string? LogPath { get; init; }

    /// <summary>Rejected only: see <see cref="JobResult.RejectKind"/>.</summary>
    public RejectKind? RejectKind { get; init; }

    /// <summary>InfraError only: see <see cref="JobResult.InfraKind"/>.</summary>
    public InfraKind? InfraKind { get; init; }

    /// <summary>True when the shard's job may still be running on its machine (the connection dropped while it ran and its cleanup did not finish).</summary>
    public bool MayStillRun { get; init; }

    /// <summary>lanq run --baseline: the shard's rerun at the base commit after it ended Failed; else null.</summary>
    public BaselineResult? Baseline { get; init; }
}

/// <summary>A whole lanq run --shards: the overall status and exit code (see <see cref="JobRunner.Aggregate"/>) and every shard, in index order.</summary>
public sealed record ShardRunResult(string Status, int ExitCode, int Count, IReadOnlyList<ShardResult> Shards, string Message)
{
    /// <summary>NoCapacity only: whether waiting may help (from the shard that decided the status; true when it did not say). Null otherwise.</summary>
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: every machine that took none of the waiting shards and why. Null otherwise.</summary>
    public IReadOnlyList<ExcludedMachine>? Excluded { get; init; }

    /// <summary>Rejected only: why (the whole run's refusal, or the shard that decided the status). Null otherwise.</summary>
    public RejectKind? RejectKind { get; init; }

    /// <summary>InfraError only: where it came from (the snapshot, or the shard that decided the status). Null otherwise.</summary>
    public InfraKind? InfraKind { get; init; }

    /// <summary>--shards auto --target-minutes: how N was picked (<see cref="ShardTiming.Pick"/>); null otherwise.</summary>
    public ShardTimingResult? Timing { get; init; }
}

public sealed partial class JobRunner
{
    internal const string Cancelled = "Cancelled";

    /// <summary>
    /// lanq run --shards: N jobs of one command, each with its own job id, LANQ_SHARD and LANQ_SHARDS, and {shard}/{shards} substituted in its
    /// arguments. Every shard's command line passes the same policy and secret checks as a single job before anything is built or sent. The
    /// repository is snapshotted once and its bundles and archive are built once and shared. Shards go to free slots (a machine with
    /// max_jobs 4 takes up to four at once, fewer when --mem asks for more memory than it has free for four); the rest wait for a sibling to
    /// finish. --wait is how long to wait while no shard of this run holds a slot; then the rest end NoCapacity. One shard failing never
    /// stops the others unless <see cref="ShardOptions.FailFast"/>. Cancelling stops every shard; each one's cleanup still runs.
    /// </summary>
    public async Task<ShardRunResult> RunShardsAsync(JobRequest template, ShardOptions options, CancellationToken ct)
    {
        // The command line is refused before any network work when even one shard's arguments would be.
        var firstCheck = Precheck("shard", template with { Argv = new ShardInfo(1, 1).Substitute(template.Argv) });
        if (firstCheck is { Status: JobStatus.NoCapacity } never)
        {
            // No machine in the config can ever take it: every shard ends NoCapacity at once, as after a --wait 0 look, without any probe.
            var n = options.Count ?? 1;
            return Aggregate(n, [.. Enumerable.Range(1, n).Select(i => new ShardResult(i, nameof(JobStatus.NoCapacity), 75, null, null, TimeSpan.Zero, 0, never.Message)
            {
                Retryable = never.Retryable,
                Excluded = never.Excluded,
            })], false);
        }

        if (firstCheck is not null)
        {
            return Refused(options.Count ?? 1, "shard 1: " + firstCheck.Message, firstCheck);
        }

        ShardTimingResult? timing = null;
        int count;
        if (options.Count is { } given)
        {
            count = given;
        }
        else
        {
            (count, timing) = await AutoShardCountAsync(template, options.TargetMinutes, ct).ConfigureAwait(false);
        }

        if (count is < 1 or > ShardInfo.MaxShards)
        {
            return Refused(Math.Clamp(count, 1, ShardInfo.MaxShards), $"--shards must be from 1 to {ShardInfo.MaxShards}.", null);
        }

        var shards = Enumerable.Range(1, count).Select(i => new ShardInfo(i, count)).ToList();
        if (PrecheckShards(template, shards) is { } refusal)
        {
            return Refused(count, refusal.Message, refusal.Refused) with { Timing = timing };
        }

        var (baseCommit, badBase) = await ResolveBaseAsync("shard", template, ct).ConfigureAwait(false);
        if (badBase is not null)
        {
            return Refused(count, badBase.Message, badBase) with { Timing = timing };
        }

        var payloadId = NewId();
        var (prepared, failed) = await PrepareAsync(payloadId, template, output, ct).ConfigureAwait(false);
        if (prepared is null)
        {
            return new ShardRunResult(failed!.Status.ToString(), failed.CliExitCode, count, [], failed.Message)
            {
                RejectKind = failed.RejectKind,
                InfraKind = failed.InfraKind,
                Timing = timing,
            };
        }

        await using (prepared.ConfigureAwait(false))
        {
            return await ShardsOnPreparedAsync(template, shards, options, prepared, payloadId, timing, ct, baseCommit).ConfigureAwait(false);
        }
    }

    private static ShardRunResult Refused(int n, string message, JobResult? r) =>
        new(r?.Status.ToString() ?? nameof(JobStatus.Rejected), r?.CliExitCode ?? 2, n, [], message) { RejectKind = r is null ? Jobs.RejectKind.Arguments : r.RejectKind };

    /// <summary>
    /// A sharded job on a snapshot already taken and scanned (one job of lanq run --plan, see JobRunner.PlanFile.cs): picks N for --shards auto
    /// now (one probe, as lanq run --shards does), checks every shard's command line, then dispatches the shards on <paramref name="prepared"/>.
    /// The caller has already run the first shard's checks (and every shard's, for a fixed N). The shards record their own run id.
    /// </summary>
    private async Task<ShardRunResult> RunShardsPreparedAsync(JobRequest template, ShardOptions options, PreparedPayload prepared, CancellationToken ct)
    {
        ShardTimingResult? timing = null;
        int count;
        if (options.Count is { } given)
        {
            count = given;
        }
        else
        {
            (count, timing) = await AutoShardCountAsync(template, options.TargetMinutes, ct).ConfigureAwait(false);
        }

        var shards = Enumerable.Range(1, count).Select(i => new ShardInfo(i, count)).ToList();
        if (PrecheckShards(template, shards) is { } refusal)
        {
            return Refused(count, refusal.Message, refusal.Refused) with { Timing = timing };
        }

        return await ShardsOnPreparedAsync(template, shards, options, prepared, NewId(), timing, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches the checked shards on the shared payload and aggregates their results. With <paramref name="baseCommit"/> (lanq run --shards
    /// --baseline) every shard that ended Failed is then rerun at the base, with the same LANQ_SHARD/LANQ_SHARDS (a plan reruns its own after
    /// the whole plan, so it passes null).
    /// </summary>
    private async Task<ShardRunResult> ShardsOnPreparedAsync(
        JobRequest template, List<ShardInfo> shards, ShardOptions options, PreparedPayload prepared, string runId, ShardTimingResult? timing, CancellationToken ct,
        BaseCommit? baseCommit = null)
    {
        // Every shard (and every retried attempt) records the template's key and this run's id in jobs.jsonl.
        template = template with { CommandKey = JobHistory.Key(template.Argv), RunId = runId };

        // DispatchShards returns only once every shard it started has finished (and cleaned up), so the payload outlives them all.
        var results = await DispatchShards(template, shards, options, prepared, ct).ConfigureAwait(false);
        // Ctrl+C that came after every shard had already finished does not turn a real result into Cancelled.
        var result = Aggregate(shards.Count, results, ct.IsCancellationRequested && results.Any(r => r.Status == Cancelled)) with { Timing = timing };
        return baseCommit is null ? result : await WithShardBaselinesAsync(template, result, baseCommit, prepared.Payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// --baseline on a sharded run: the shards that ended Failed (their own exit, never another status), rerun at the base all at once; each
    /// result goes on its shard. The run's status, exit code and message stay as they are. Nothing reruns after Ctrl+C (each is inconclusive).
    /// </summary>
    private async Task<ShardRunResult> WithShardBaselinesAsync(JobRequest template, ShardRunResult result, BaseCommit baseCommit, JobPayload snapshot, CancellationToken ct)
    {
        var failed = result.Shards.Where(s => s.Status == nameof(JobStatus.Failed) && s.JobId is not null).ToList();
        // After Ctrl+C each failed shard still gets its baseline object: inconclusive, "cancelled (Ctrl+C) before it started" (nothing runs).
        if (failed.Count == 0)
        {
            return result;
        }

        var targets = failed.Select(s => new BaselineTarget(ShardRequest(template, new ShardInfo(s.Index, result.Count)), baseCommit, s.JobId!, s.Machine)).ToList();
        var baselines = await RunBaselinesAsync(targets, snapshot, ct).ConfigureAwait(false);
        var byIndex = failed.Zip(baselines).ToDictionary(x => x.First.Index, x => x.Second);
        return result with { Shards = [.. result.Shards.Select(s => byIndex.TryGetValue(s.Index, out var b) ? s with { Baseline = b } : s)] };
    }

    /// <summary>One shard's request of a sharded job: {shard}/{shards} substituted, LANQ_SHARD/LANQ_SHARDS, and the template's command key.</summary>
    private static JobRequest ShardRequest(JobRequest template, ShardInfo shard) =>
        template with { Argv = shard.Substitute(template.Argv), Shard = shard, CommandKey = template.CommandKey ?? JobHistory.Key(template.Argv) };

    /// <summary>
    /// --shards auto: the free slots (<see cref="Scheduler.FreeFor"/>) on every machine that can take the job right now (capabilities, allowed
    /// hours, --mem per shard, holds, and Windows-safe arguments), at least 1 and at most <see cref="ShardInfo.MaxShards"/>; with
    /// --target-minutes, N from earlier runs (<see cref="PickShardCount"/>). Prints the choice and why.
    /// </summary>
    private async Task<(int Count, ShardTimingResult? Timing)> AutoShardCountAsync(JobRequest template, int? targetMinutes, CancellationToken ct)
    {
        var (_, assessed) = await ProbeForJobAsync(Candidates(template), ShardNeeds(template, [new ShardInfo(1, 1)]), ct).ConfigureAwait(false);
        var (n, freeWhy, timing) = PickShardCount(template, assessed, targetMinutes);
        Say(template)(string.Create(CultureInfo.InvariantCulture, $"[lanq] --shards auto: {n} shard(s) ({ShardTiming.ShardCountWhy(timing, freeWhy)})"));
        return (n, timing);
    }

    /// <summary>
    /// --shards auto's N from one probe, for lanq run and its dry run alike: <see cref="Scheduler.AutoCount"/> (and why, as
    /// <see cref="AutoShardWhy"/> says it); with <paramref name="targetMinutes"/>, <see cref="ShardTiming.Pick"/> on the command's history in
    /// jobs.jsonl (read only), the free slots dealt as a fresh run deals them. Without it the job log is not read at all.
    /// </summary>
    private (int Count, string FreeWhy, ShardTimingResult? Timing) PickShardCount(JobRequest template, IReadOnlyList<Assessment> assessed, int? targetMinutes)
    {
        var (n, total, free) = Scheduler.AutoCount(assessed, template.MinMemGb, ShardInfo.MaxShards, template.Exclusive);
        var why = AutoShardWhy(total, free);
        if (targetMinutes is not { } minutes)
        {
            return (n, why, null);
        }

        var history = JobHistory.For(log, template.CommandKey ?? JobHistory.Key(template.Argv), JobHistory.Dir(template.Directory), clock.GetUtcNow());
        var timing = ShardTiming.Pick(history, minutes, Placeable(assessed, template, _ => 0, _ => 0), assessed, ShardInfo.MaxShards);
        return (timing.Picked, why, timing);
    }

    /// <summary>What a sharded job needs of a machine: its capabilities, --mem per shard, its size rule, and Windows-safe arguments for every one of <paramref name="shards"/>.</summary>
    private JobNeeds ShardNeeds(JobRequest template, IEnumerable<ShardInfo> shards) =>
        NeedsOf(template, shards.Any(s => !JobPolicy.ValidateForWindows(s.Substitute(template.Argv)).Ok));

    /// <summary>
    /// The machines shards can go to now, in rank order, each with its free slots for this job (<see cref="Scheduler.FreeFor"/>, counting
    /// this run's own shards there: <paramref name="ours"/> placed, <paramref name="oursNotRunning"/> placed but not running yet); only those above 0.
    /// An exclusive template's shards take a whole machine each, so a machine offers at most one.
    /// </summary>
    private static List<(Assessment Machine, int Free)> Placeable(IReadOnlyList<Assessment> assessed, JobRequest template, Func<string, int> ours, Func<string, int> oursNotRunning) =>
        [.. Scheduler.Rank(assessed)
            .Select(x => (x, Scheduler.FreeFor(x.Machine, x.Health, template.MinMemGb, ours(x.Machine.Name), oursNotRunning(x.Machine.Name), template.Exclusive)))
            .Where(x => x.Item2 > 0)];

    /// <summary>Why --shards auto picked its N: "free slots: build-01 2, micro-02 1" (and the cap), or that no machine can take the job right now.</summary>
    internal static string AutoShardWhy(int total, IEnumerable<(Machine Machine, int Free)> free) => total == 0
        ? "no machine can take the job right now; 1 shard waits under --wait like a single job"
        : "free slots: " + string.Join(", ", free.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Machine.Name} {f.Free}")))
            + (total > ShardInfo.MaxShards ? string.Create(CultureInfo.InvariantCulture, $"; capped at {ShardInfo.MaxShards}") : "");

    /// <summary>
    /// The overall result. All shards completed: Completed, 0. Cancelled by the caller (Ctrl+C) while shards were unfinished: Cancelled, 130.
    /// Otherwise the shard with the most telling status decides, lowest index first: Failed (the job's own exit code) before Rejected (2),
    /// TimedOut and WindowClosed (124), InfraError (70), NoCapacity (75) and Cancelled by --fail-fast (130).
    /// </summary>
    internal static ShardRunResult Aggregate(int count, IReadOnlyList<ShardResult> shards, bool cancelled)
    {
        var done = shards.Count(s => s.Status == nameof(JobStatus.Completed));
        var summary = string.Create(CultureInfo.InvariantCulture, $"{done} of {count} shard(s) completed");
        if (cancelled)
        {
            return new ShardRunResult(Cancelled, 130, count, shards, summary + "; cancelled (Ctrl+C), every started shard was cleaned up");
        }

        var worst = shards.Where(s => s.Status != nameof(JobStatus.Completed)).OrderBy(s => Rank(s.Status)).ThenBy(s => s.Index).FirstOrDefault();
        if (worst is null)
        {
            return new ShardRunResult(nameof(JobStatus.Completed), 0, count, shards, summary);
        }

        var noCapacity = worst.Status == nameof(JobStatus.NoCapacity);
        return new ShardRunResult(worst.Status, worst.ExitCode, count, shards, string.Create(CultureInfo.InvariantCulture, $"{summary}; shard {worst.Index}/{count}: {worst.Status}: {worst.Message}"))
        {
            Retryable = noCapacity ? worst.Retryable ?? true : null,
            Excluded = noCapacity ? worst.Excluded ?? [] : null,
            RejectKind = worst.RejectKind,
            InfraKind = worst.InfraKind,
        };

        static int Rank(string status) => status switch
        {
            nameof(JobStatus.Failed) => 0,
            nameof(JobStatus.Rejected) => 1,
            nameof(JobStatus.TimedOut) => 2,
            nameof(JobStatus.WindowClosed) => 3,
            nameof(JobStatus.InfraError) => 4,
            nameof(JobStatus.NoCapacity) => 5,
            _ => 6,
        };
    }

    /// <summary>
    /// The last probe judged again for a sharded run's NoCapacity result (<see cref="WhyNot(Assessment)"/> per machine), with the one reason only shards have:
    /// a machine every waiting shard must avoid because an earlier attempt of it lost the connection there and its cleanup did not finish.
    /// </summary>
    private static List<Assessment> AssessForWaiting(IEnumerable<(Machine, MachineHealth)> raw, JobNeeds needs, IReadOnlyCollection<ShardState> waiting)
    {
        HashSet<string>? avoid = null;
        foreach (var p in waiting)
        {
            avoid ??= new HashSet<string>(p.Avoid, StringComparer.Ordinal);
            avoid.IntersectWith(p.Avoid);
        }

        return Scheduler.Assess(raw, needs, avoid);
    }

    private sealed class ShardState(ShardInfo shard)
    {
        public ShardInfo Shard => shard;

        public int Attempts { get; set; }

        public ShardResult? Final { get; set; }

        /// <summary>Machines a re-run must not use: the job may still be running there (its cleanup after a lost connection did not finish).</summary>
        public HashSet<string> Avoid { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="HadSlot">The attempt held a slot on its machine at some point (null results never did).</param>
    /// <param name="JobId">The attempt's job id (its log is <see cref="JobLog.LogPathFor"/> of it once it held a slot).</param>
    private sealed record Attempt(ShardState State, Machine Machine, JobResult? Result, TimeSpan Duration, bool Cancelled, bool HadSlot, string JobId);

    /// <summary>One started attempt: its machine, whether it holds a slot yet and whether its command runs yet (set from the attempt's own thread).</summary>
    /// <param name="placed">Called once, when the attempt holds its slot or ends without one (a plan's next job waits for the first round).</param>
    private sealed class Running(string machine, Action? placed = null)
    {
        private volatile bool _holding;
        private volatile bool _started;
        private int _placed;

        public string Machine => machine;

        public bool Holding => _holding;

        public bool Started => _started;

        public void Hold()
        {
            _holding = true;
            Placed();
        }

        public void Start() => _started = true;

        public void Placed()
        {
            if (Interlocked.Exchange(ref _placed, 1) == 0)
            {
                placed?.Invoke();
            }
        }
    }

    private async Task<List<ShardResult>> DispatchShards(JobRequest template, List<ShardInfo> shards, ShardOptions options, PreparedPayload prepared, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new Lock();
        void Emit(string line)
        {
            lock (gate)
            {
                output(line);
            }
        }

        var needs = ShardNeeds(template, shards);
        var gitCanEverTakeIt = GitCanEverTakeIt(template);
        var candidates = Candidates(template);
        var plan = template.Progress;
        var name = template.PlanJob is { } n ? n + " " : "";
        var lead = template.PlanJob is { } job ? $"[{job}] " : "";

        // lanq run --plan: the next job starts once every shard of the first round holds its slot or has ended (or nothing was placed).
        var firstRound = -1;
        void FirstRoundPlaced()
        {
            if (Interlocked.Decrement(ref firstRound) == 0)
            {
                template.Placed?.Invoke();
            }
        }
        var states = shards.Select(s => new ShardState(s)).ToList();
        var pending = new List<ShardState>(states);
        var running = new Dictionary<Task<Attempt>, Running>();

        // --wait: how long no shard of this run has held a slot. Restarted only by an attempt that really got one, so attempts that keep
        // failing to claim a slot (the probe said free, the machine said full) cannot hold the run open for ever.
        var idle = Stopwatch.StartNew();
        var round = RoundStart();
        var relooked = 0L;
        var backoff = false;
        Exception? loopError = null;

        // The latest probe, raw and judged for this job, for choosing another machine for a re-run by the same rules as placement.
        MachineHealth[] lastProbes = [];
        List<Assessment> lastAssessed = [];

        Task<Attempt> Launch(ShardState st, Machine machine, DateTimeOffset? windowEnds, Running slot)
        {
            st.Attempts++;
            var req = template with { Argv = st.Shard.Substitute(template.Argv), Shard = st.Shard };
            var prefix = $"[{name}{st.Shard} {machine.Name}] ";
            var jobId = NewId();
            return Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    var r = await TryOnMachine(
                        jobId, req, prepared, machine, windowEnds, gitCanEverTakeIt, line => Emit(prefix + line), stop.Token,
                        () =>
                        {
                            plan?.Hold();
                            slot.Hold();
                        },
                        slot.Start).ConfigureAwait(false);
                    return new Attempt(st, machine, r, clock.Elapsed, false, slot.Holding, jobId);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return new Attempt(st, machine, null, clock.Elapsed, true, slot.Holding, jobId);
                }
                catch (Exception ex)
                {
                    // Anything else (an OperationCanceledException nobody asked for included) is this attempt's result: the other shards go on.
                    var failed = new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"))
                    {
                        InfraKind = InfraKind.Unexpected,
                    };
                    return new Attempt(st, machine, failed, clock.Elapsed, false, slot.Holding, jobId);
                }
                finally
                {
                    if (slot.Holding)
                    {
                        plan?.Release();
                    }

                    slot.Placed();
                }
            }, CancellationToken.None);
        }

        string ArchiveNote() => prepared.Archive.Failure is null ? "" : " Archive build failed: " + prepared.Archive.Failure;

        // Every shard still waiting ends with this NoCapacity result.
        void EndWaiting(JobResult none)
        {
            foreach (var st in pending)
            {
                st.Final = new ShardResult(st.Shard.Index, nameof(JobStatus.NoCapacity), 75, null, null, TimeSpan.Zero, st.Attempts, none.Message)
                {
                    Retryable = none.Retryable,
                    Excluded = none.Excluded,
                };
            }

            pending.Clear();
        }

        try
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                if (stop.IsCancellationRequested)
                {
                    break;
                }

                var launched = 0;
                if (pending.Count > 0 && !backoff)
                {
                    List<Assessment> assessed;
                    MachineHealth[] probes;

                    // Which of this run's shards are not running yet, counted before the probe looks: a shard that starts while the probe is out
                    // was not running when the machine answered, so its memory is not in that answer either. (If its memory did show already, it
                    // counts twice for this one round: one shard too few, placed on the next look, never one too many.)
                    var notRunning = running.Values.Where(r => !r.Started).CountBy(r => r.Machine, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal);
                    try
                    {
                        round = RoundStart();
                        (probes, assessed) = await ProbeForJobAsync(candidates, needs, stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        break;
                    }

                    // Free slots per machine (see FreeFor; a shard just launched may not hold its slot yet when the probe looks, so this run's
                    // own shards there count too). Shards are dealt round-robin over the ranked machines, so a few shards spread out and many
                    // fill every slot: a machine with max_jobs 4 takes four. A shard never goes to a machine it must avoid.
                    var free = Placeable(
                        assessed, template, m => running.Values.Count(r => r.Machine == m), m => notRunning.GetValueOrDefault(m));
                    var deal = Scheduler.Deal(free, pending, (p, m) => !p.Avoid.Contains(m.Name));
                    var first = firstRound == -1;
                    if (first)
                    {
                        firstRound = deal.Count;
                        if (deal.Count == 0)
                        {
                            template.Placed?.Invoke();
                        }
                    }

                    foreach (var (st, at) in deal)
                    {
                        pending.Remove(st);
                        var slot = new Running(at.Machine.Name, first ? FirstRoundPlaced : null);
                        running[Launch(st, at.Machine, at.Health.WindowEndsUtc, slot)] = slot;
                        launched++;
                    }

                    (lastProbes, lastAssessed) = (probes, assessed); // a wake below updates both in place
                    if (pending.Count > 0 && assessed.All(a => a.Permanent))
                    {
                        // Every machine is ruled out for good: the waiting shards end now, without waiting or waking (running ones finish).
                        EndWaiting(NoCapacity("shard", AssessForWaiting(candidates.Zip(probes), needs, pending), ArchiveNote()));
                    }
                    else if (launched == 0 && template.Wait > TimeSpan.Zero && !template.NoWake && probe.Waker is { AnyPending: false })
                    {
                        await WakeBestSleeperAsync(candidates, needs, probes, assessed, prepared.Archive, line => Emit(lead + line), stop.Token).ConfigureAwait(false);
                    }
                }

                backoff = false;
                if (running.Values.Any(r => r.Holding))
                {
                    idle.Restart();
                }
                else if (pending.Count > 0 && running.Count == 0 && WaitedOut(template, idle, round, relooked))
                {
                    EndWaiting(NoCapacity("shard", AssessForWaiting(candidates.Zip(lastProbes), needs, pending), ArchiveNote()));
                    break;
                }
                else if (pending.Count > 0 && running.Count == 0 && launched == 0 && Relook(plan, round, relooked) is > 0 and var release)
                {
                    relooked = release;
                    continue; // a sibling job gave back a slot after this look began: look again now, it may be free
                }

                // Wait for a shard to finish; with shards still waiting for a slot, look again after 15 s at the latest.
                var waits = new List<Task>(running.Keys);
                Task? timer = null;
                if (pending.Count > 0 && launched == 0)
                {
                    if (running.Count == 0)
                    {
                        Emit(lead + string.Create(CultureInfo.InvariantCulture, $"[lanq] no eligible machine for {pending.Count} shard(s); waiting 15s"));
                    }

                    timer = delay(TimeSpan.FromSeconds(15), stop.Token);
                    waits.Add(timer);
                    if (plan is not null && running.Count == 0)
                    {
                        // In a plan, a slot given back by a sibling job is worth a look at once.
                        waits.Add(plan.Released);
                    }
                }

                if (waits.Count == 0)
                {
                    continue;
                }

                try
                {
                    await Task.WhenAny(waits).ConfigureAwait(false);
                    if (timer is { IsCanceled: true } || timer is { IsFaulted: true })
                    {
                        await timer.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }

                foreach (var task in running.Keys.Where(t => t.IsCompleted).ToList())
                {
                    running.Remove(task);
                    var a = await task.ConfigureAwait(false);
                    if (a.HadSlot)
                    {
                        idle.Restart();
                    }

                    if (Settle(a, options, pending, Emit, lastAssessed, name) && options.FailFast && !stop.IsCancellationRequested)
                    {
                        Emit($"{lead}[lanq] --fail-fast: shard {a.State.Shard} {a.State.Final!.Status}; cancelling the other shards");
                        await stop.CancelAsync().ConfigureAwait(false);
                    }

                    backoff |= a.Result is null && !a.Cancelled;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Ctrl+C or --fail-fast while probing or waking: the started shards are awaited below.
        }
        catch (Exception ex)
        {
            // The dispatch itself broke (a probe, a wake): stop the shards still running and wait for them below.
            loopError = ex;
            Emit(SafeLine($"[lanq] unexpected error: {ex.GetType().Name}: {ex.Message}; stopping the other shards"));
            await stop.CancelAsync().ConfigureAwait(false);
        }

        // Cancelled (Ctrl+C, --fail-fast or an error above): wait for every started shard to finish its cleanup. Each attempt task catches
        // everything itself, so none of these awaits throws.
        foreach (var task in running.Keys)
        {
            Settle(await task.ConfigureAwait(false), options, pending, Emit, lastAssessed, name);
        }

        foreach (var st in states.Where(s => s.Final is null))
        {
            st.Final = loopError is null
                ? new ShardResult(st.Shard.Index, Cancelled, 130, null, null, TimeSpan.Zero, st.Attempts, "cancelled before it started")
                : new ShardResult(st.Shard.Index, nameof(JobStatus.InfraError), 70, null, null, TimeSpan.Zero, st.Attempts, SafeLine($"not run: unexpected error: {loopError.GetType().Name}: {loopError.Message}"))
                {
                    InfraKind = InfraKind.Unexpected,
                };
        }

        return [.. states.Select(s => s.Final!)];
    }

    /// <summary>
    /// Records an attempt. No slot after all (null): back to the front of the queue. Lost its machine (ConnectionLost) with retries left:
    /// queued again as a new job, but only where it cannot run twice. When the connection dropped while the job ran and its cleanup did not
    /// finish, the job may still be running there, so the re-run must go to another machine; if no other machine could take it, it is not
    /// re-run. Otherwise final. True when the shard ended without completing.
    /// </summary>
    /// <param name="assessed">The latest probe judged for this job: another machine qualifies for a re-run only when placement could use it
    /// (reachable, not held by hours, pause, memory, load, --mem or Windows-unsafe arguments, and with the capabilities; it may be busy now).</param>
    /// <param name="name">A plan job's name and a space ("" otherwise), for the line prefix.</param>
    private bool Settle(Attempt a, ShardOptions options, List<ShardState> pending, Action<string> emit, List<Assessment> assessed, string name = "")
    {
        var st = a.State;
        var prefix = $"[{name}{st.Shard} {a.Machine.Name}] ";
        if (a.Cancelled)
        {
            st.Final = new ShardResult(st.Shard.Index, Cancelled, 130, a.Machine.Name, null, a.Duration, st.Attempts, "cancelled; its cleanup ran")
            {
                LogPath = LogIfCreated(a),
            };
            return false;
        }

        if (a.Result is not { } r)
        {
            st.Attempts--;
            pending.Insert(0, st);
            return false;
        }

        var note = "";
        if (r.ConnectionLost && st.Attempts <= options.Retries)
        {
            var mayRunTwice = r.MayStillRun && !r.CleanedUp;
            if (!mayRunTwice)
            {
                emit(prefix + string.Create(CultureInfo.InvariantCulture, $"[lanq] {r.Message} Re-running the shard (retry {st.Attempts} of {options.Retries}; --shard-retries)."));
                pending.Insert(0, st);
                return false;
            }

            if (assessed.Any(x => x.Machine.Name != a.Machine.Name && !st.Avoid.Contains(x.Machine.Name) && x.Reason is Exclusion.None or Exclusion.Busy))
            {
                st.Avoid.Add(a.Machine.Name);
                emit(prefix + string.Create(
                    CultureInfo.InvariantCulture,
                    $"[lanq] {r.Message} The job may still be running on {a.Machine.Name} (its cleanup did not finish); re-running the shard on another machine (retry {st.Attempts} of {options.Retries}; --shard-retries)."));
                pending.Insert(0, st);
                return false;
            }

            note = $" Not re-run: the job may still be running on {a.Machine.Name} (its cleanup did not finish) and no other machine can take the shard.";
            emit(prefix + "[lanq]" + note);
        }

        var message = SafeLine(r.Message + note) + (st.Attempts > 1 ? string.Create(CultureInfo.InvariantCulture, $" (attempt {st.Attempts})") : "");
        st.Final = new ShardResult(st.Shard.Index, r.Status.ToString(), r.CliExitCode, r.Machine, r.JobId, a.Duration, st.Attempts, message)
        {
            Fetch = r.Fetch,
            Retryable = r.Retryable,
            Excluded = r.Excluded,
            LogPath = r.LogPath ?? LogIfCreated(a),
            RejectKind = r.RejectKind,
            InfraKind = r.InfraKind,
            MayStillRun = r.MayStillRun && !r.CleanedUp,
        };
        return r.Status != JobStatus.Completed;
    }

    /// <summary>The full path of an attempt's log when it got as far as creating one (cancelled, or ended by an unexpected error), else null.</summary>
    private string? LogIfCreated(Attempt a) =>
        a.HadSlot && log.LogPathFor(a.JobId) is var path && File.Exists(path) ? Path.GetFullPath(path) : null;
}
