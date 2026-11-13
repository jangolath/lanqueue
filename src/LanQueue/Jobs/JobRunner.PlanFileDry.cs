using System.Globalization;
using LanQueue.Health;
using LanQueue.Payload;
using LanQueue.Policy;
using LanQueue.Scheduling;

namespace LanQueue.Jobs;

/// <summary>What one job of lanq run --plan --dry-run would do (see <see cref="JobRunner.PlanFileDryAsync"/>).</summary>
/// <param name="Start">now (first wave), waits_for_plan (first wave, but the plan's own jobs hold every slot it could use) or after.</param>
/// <param name="Status">WouldRun, WouldWait, Pending or NoCapacity.</param>
public sealed record PlanJobDry(string Name, string Start, string Status, string Message)
{
    public const string WouldWait = "WouldWait";

    public const string Pending = "Pending";

    public string? Machine { get; init; }

    public int? ShardCount { get; init; }

    public bool? ShardCountAuto { get; init; }

    public IReadOnlyList<ShardPlacement>? Placement { get; init; }

    /// <summary>shards: auto: the free slots behind N, or null.</summary>
    public string? AutoWhy { get; init; }

    public ShardTimingResult? Timing { get; init; }

    /// <summary>Every candidate machine judged for the job (empty when nothing was probed).</summary>
    public IReadOnlyList<Assessment> Assessed { get; init; } = [];

    /// <summary>NoCapacity only: false when the job can never run as configured.</summary>
    public bool? Retryable { get; init; }
}

/// <summary>What lanq run --plan would do now (lanq run --plan --dry-run).</summary>
/// <param name="Status">WouldRun, or NoCapacity, Rejected, InfraError (Cancelled is the CLI's).</param>
public sealed record PlanDryResult(string Status, int ExitCode, string Message, IReadOnlyList<PlanJobDry> Jobs)
{
    /// <summary>How many uncommitted files the snapshot's secret scan covered; null when no snapshot was taken.</summary>
    public int? UncommittedFiles { get; init; }

    /// <summary>The job that decided a NoCapacity, Rejected or InfraError status, or null.</summary>
    public string? Decided { get; init; }

    public bool? Retryable { get; init; }

    public RejectKind? RejectKind { get; init; }

    public InfraKind? InfraKind { get; init; }
}

public sealed partial class JobRunner
{
    /// <summary>
    /// lanq run --plan --dry-run: what <see cref="RunPlanAsync"/> would do now, by the same code: the same checks of every job (all or nothing,
    /// and the config-only never check), the snapshot and its secret scans once (no bundle or archive), one probe of every machine any job may
    /// use, then the first wave (the jobs with no after:) in launch order on one shared free-slot table: each job is judged and dealt as lanq run
    /// would (<see cref="Scheduler.Assess"/>, Rank, Deal; shards: auto picks N from what the earlier jobs left), taking the slots it gets from
    /// the table. Jobs with after: are Pending. It claims no slot, uploads nothing, appends nothing to jobs.jsonl (it may read it for
    /// target_minutes), creates no log and wakes nothing.
    /// </summary>
    public async Task<PlanDryResult> PlanFileDryAsync(IReadOnlyList<PlanJobSpec> ordered, CancellationToken ct)
    {
        if (PrecheckPlan(ordered) is { } ended)
        {
            return ended.Jobs.Count == 0
                ? new PlanDryResult(ended.Status, ended.ExitCode, ended.Message, []) { Decided = ended.Decided, RejectKind = ended.RejectKind, InfraKind = ended.InfraKind }
                : NeverDry(ordered, ended);
        }

        // baseline: a bad baseline_ref refuses the plan here too, before the snapshot (the dry run shows each job's --baseline in its run line).
        if ((await ResolvePlanBasesAsync(ordered, ct).ConfigureAwait(false)).Refused is { } badBase)
        {
            return new PlanDryResult(badBase.Status, badBase.ExitCode, badBase.Message, []) { Decided = badBase.Decided, RejectKind = badBase.RejectKind };
        }

        var (payload, failed) = await SnapshotAndScanAsync(NewId(), ordered[0].Request, PayloadKinds.None, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return new PlanDryResult(failed!.Status.ToString(), failed.CliExitCode, failed.Message, []) { RejectKind = failed.RejectKind, InfraKind = failed.InfraKind };
        }

        TryDelete(payload.ArchivePath); // never built (PayloadKinds.None); nothing may stay behind

        // One probe of every machine some job may use, in config order.
        var union = config.Machines.Where(m => ordered.Any(j => Candidates(j.Request).Contains(m))).ToList();
        var probes = await Task.WhenAll(union.Select(m => ProbeSharedAsync(m, ct))).ConfigureAwait(false);
        var health = union.Zip(probes).ToDictionary(x => x.First.Name, x => x.Second, StringComparer.Ordinal);

        var taken = new Dictionary<string, (int Slots, double Mem)>(StringComparer.Ordinal);
        var holders = new List<string>();
        var jobs = new List<PlanJobDry>();
        foreach (var spec in ordered)
        {
            var (job, refused) = DryJob(spec, health, taken, holders);
            if (refused is not null)
            {
                return new PlanDryResult(refused.Status.ToString(), refused.CliExitCode, $"job '{spec.Name}': {refused.Message}", []) { Decided = spec.Name, RejectKind = refused.RejectKind };
            }

            jobs.Add(job!);
            if (job!.Status == JobPlan.WouldRun)
            {
                holders.Add(spec.Name);
            }
        }

        var files = payload.ChangedFiles.Count;
        if (jobs.FirstOrDefault(j => j.Retryable == false) is { } never)
        {
            return new PlanDryResult(nameof(JobStatus.NoCapacity), 75, $"job '{never.Name}': {never.Message}", jobs) { Decided = never.Name, Retryable = false, UncommittedFiles = files };
        }

        if (jobs.FirstOrDefault(j => j.Status == nameof(JobStatus.NoCapacity)) is { } none)
        {
            return new PlanDryResult(nameof(JobStatus.NoCapacity), 75, $"job '{none.Name}': {none.Message}", jobs) { Decided = none.Name, Retryable = true, UncommittedFiles = files };
        }

        var now = jobs.Where(j => j.Status == JobPlan.WouldRun).Select(j => j.Name).ToList();
        var waiting = jobs.Count(j => j.Status == PlanJobDry.WouldWait);
        var pending = jobs.Count(j => j.Status == PlanJobDry.Pending);
        var message = string.Create(CultureInfo.InvariantCulture, $"{now.Count} job(s) would start now ({string.Join(", ", now)})")
            + (waiting > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {waiting} would wait for this plan's slots") : "")
            + (pending > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {pending} start after other jobs") : "");
        return new PlanDryResult(JobPlan.WouldRun, 0, message, jobs) { UncommittedFiles = files };
    }

    /// <summary>The dry run of a plan the config-only check ended: the job(s) no machine can ever take, and the rest not looked at.</summary>
    private PlanDryResult NeverDry(IReadOnlyList<PlanJobSpec> ordered, PlanRunResult ended)
    {
        List<PlanJobDry> jobs = [.. ordered.Zip(ended.Jobs).Select(x => x.Second.Status == PlanJobOutcome.Skipped
            ? new PlanJobDry(x.First.Name, x.First.After.Count > 0 ? "after" : "now", PlanJobDry.Pending, x.Second.Message)
            : new PlanJobDry(x.First.Name, x.First.After.Count > 0 ? "after" : "now", nameof(JobStatus.NoCapacity), x.Second.Message)
            {
                Retryable = false,
                Assessed = NeverByConfig(x.First.Request) ?? [],
                ShardCount = x.First.Shards?.Count,
                ShardCountAuto = x.First.Shards is { } s ? s.Count is null : null,
            })];
        var decided = ended.Jobs.First(j => j.Name == ended.Decided);
        return new PlanDryResult(ended.Status, ended.ExitCode, $"job '{decided.Name}': {decided.Message}", jobs) { Decided = ended.Decided, Retryable = false };
    }

    /// <summary>One job of the dry run, judged on the shared probe; first-wave jobs take what they get from <paramref name="taken"/>.</summary>
    private (PlanJobDry? Job, JobResult? Refused) DryJob(
        PlanJobSpec spec, Dictionary<string, MachineHealth> health, Dictionary<string, (int Slots, double Mem)> taken, List<string> holders)
    {
        var req = spec.Request;
        var options = spec.Shards;
        var fixedShards = options?.Count is { } n ? Enumerable.Range(1, n).Select(i => new ShardInfo(i, n)).ToList() : null;
        var needs = options is null ? NeedsOf(req, !JobPolicy.ValidateForWindows(req.Argv).Ok) : ShardNeeds(req, fixedShards ?? [new ShardInfo(1, 1)]);
        var assessed = Scheduler.Assess(Candidates(req).Select(m => (m, health[m.Name])), needs);
        var never = assessed.All(a => a.Permanent);
        var start = spec.After.Count > 0 ? "after" : "now";
        var job = new PlanJobDry(spec.Name, start, PlanJobDry.Pending, "pending: starts after " + string.Join(", ", spec.After))
        {
            Assessed = assessed,
            ShardCount = options?.Count,
            ShardCountAuto = options is null ? null : options.Count is null,
        };
        if (never)
        {
            return (job with { Status = nameof(JobStatus.NoCapacity), Message = NoCapacity("dry", assessed, "").Message, Retryable = false }, null);
        }

        if (spec.After.Count > 0)
        {
            return (job, null);
        }

        List<(Assessment Machine, int Free)> free = [.. Scheduler.Rank(assessed).Select(a => (a, FreeLeft(a, taken, req.MinMemGb, req.Exclusive))).Where(f => f.Item2 > 0)];
        void Take(Assessment a)
        {
            // An exclusive job (or shard) takes every slot of its machine: no later job of the plan is placed there.
            var (slots, mem) = taken.GetValueOrDefault(a.Machine.Name);
            taken[a.Machine.Name] = (req.Exclusive ? Math.Max(slots + 1, a.Machine.MaxJobs) : slots + 1, mem + (req.MinMemGb ?? 0));
        }

        if (options is null)
        {
            if (free.Count > 0)
            {
                Take(free[0].Machine);
                return (job with { Status = JobPlan.WouldRun, Message = "would run on " + free[0].Machine.Machine.Name, Machine = free[0].Machine.Machine.Name }, null);
            }

            return (Unplaced(job, assessed, holders), null);
        }

        string? autoWhy = null;
        ShardTimingResult? timing = null;
        var shards = fixedShards;
        if (shards is null)
        {
            var total = free.Sum(f => f.Free);
            var count = Math.Clamp(total, 1, ShardInfo.MaxShards);
            autoWhy = AutoShardWhy(total, free.Select(f => (f.Machine.Machine, f.Free)));
            if (options.TargetMinutes is { } minutes)
            {
                var history = JobHistory.For(log, JobHistory.Key(req.Argv), JobHistory.Dir(req.Directory), clock.GetUtcNow());
                timing = ShardTiming.Pick(history, minutes, free, assessed, ShardInfo.MaxShards);
                count = timing.Picked;
            }

            shards = [.. Enumerable.Range(1, count).Select(i => new ShardInfo(i, count))];
            if (PrecheckShards(req, shards) is { } refusal)
            {
                return (null, refusal.Refused with { Message = refusal.Message });
            }
        }

        var dealt = Scheduler.Deal(free, shards, (_, _) => true);
        foreach (var (_, at) in dealt)
        {
            Take(at);
        }

        List<ShardPlacement> placement = [.. dealt
            .GroupBy(d => d.Machine.Machine.Name, StringComparer.Ordinal)
            .Select(g => new ShardPlacement(g.First().Machine.Machine, [.. g.Select(d => d.Item.Index).Order()]))];
        job = job with { ShardCount = shards.Count, Placement = placement, AutoWhy = autoWhy, Timing = timing };
        if (dealt.Count == 0)
        {
            return (Unplaced(job, assessed, holders), null);
        }

        var waiting = shards.Count - dealt.Count;
        return (job with
        {
            Status = JobPlan.WouldRun,
            Machine = dealt[0].Machine.Machine.Name,
            Message = string.Create(
                CultureInfo.InvariantCulture,
                $"would run {shards.Count} shard(s): {PlacementText(placement)}{(waiting > 0 ? $"; {waiting} waiting for a free slot" : "")}"),
        }, null);
    }

    /// <summary>A first-wave job that gets no slot now: it waits behind the plan's own jobs when one holds a slot, else NoCapacity (retryable).</summary>
    private static PlanJobDry Unplaced(PlanJobDry job, List<Assessment> assessed, List<string> holders) => holders.Count > 0
        ? job with
        {
            Start = "waits_for_plan",
            Status = PlanJobDry.WouldWait,
            Message = $"would wait for a slot this plan holds (its machines are taken by {string.Join(", ", holders)}, or by other work)",
        }
        : job with { Status = nameof(JobStatus.NoCapacity), Message = NoCapacity("dry", assessed, "").Message, Retryable = true };

    /// <summary>
    /// The copies of a job a machine could still take after the slots (and --mem) the plan's earlier first-wave jobs took there; an
    /// <paramref name="exclusive"/> job only where every slot is still free (then 1).
    /// </summary>
    private static int FreeLeft(Assessment a, Dictionary<string, (int Slots, double Mem)> taken, double? memGb, bool exclusive)
    {
        var (slots, mem) = taken.GetValueOrDefault(a.Machine.Name);
        var h = a.Health with { SlotsUsed = a.Health.SlotsUsed + slots, MemFreeGb = a.Health.MemFreeGb - mem };
        return Scheduler.FreeFor(a.Machine, h, memGb, 0, 0, exclusive);
    }
}
