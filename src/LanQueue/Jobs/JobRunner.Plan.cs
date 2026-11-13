using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Payload;
using LanQueue.Policy;
using LanQueue.Scheduling;

namespace LanQueue.Jobs;

public sealed partial class JobRunner
{
    /// <summary>
    /// lanq run --dry-run: what <see cref="RunAsync"/> (or <see cref="RunShardsAsync"/> with <paramref name="shards"/>) would do now, by the
    /// same code: the command-line checks (<see cref="Precheck"/>, <see cref="PrecheckShards"/>), the snapshot and its secret scans
    /// (<see cref="SnapshotAndScanAsync"/>, with no bundle or archive), one probe of every machine (<see cref="ProbeForJobAsync"/>) and the
    /// same choice (<see cref="Scheduler.Assess"/>, Rank, AutoCount, Deal), plus whether a sleeper would be woken (<see cref="CouldWakeFor"/> and
    /// <see cref="WakeService.Evaluate"/>, never a send). It claims no slot, uploads nothing, appends nothing to jobs.jsonl, creates no log and
    /// never reads or writes bases.json. The snapshot does write its git objects to the repository's object store, as a real run does.
    /// It cannot foresee an archive that fails to build or a slot taken between the probe and the start; with --shards auto it plans from
    /// the one probe that picked N (a real run probes again before placing).
    /// </summary>
    public async Task<JobPlan> PlanAsync(JobRequest req, ShardOptions? shards, CancellationToken ct) =>
        shards is null ? await PlanOneAsync(NewId(), req, ct).ConfigureAwait(false) : await PlanShardsAsync(NewId(), req, shards, ct).ConfigureAwait(false);

    private async Task<JobPlan> PlanOneAsync(string id, JobRequest req, CancellationToken ct)
    {
        if (Precheck(id, req) is { } refused)
        {
            return refused.Status == JobStatus.NoCapacity
                ? Ended(refused, NeverByConfig(req)) with { AllowedBy = JobPolicy.AllowedBy(config.Policy, req.Argv) }
                : Ended(refused, null);
        }

        var (payload, failed) = await SnapshotAndScanAsync(id, req, PayloadKinds.None, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return Ended(failed!, null);
        }

        TryDelete(payload.ArchivePath); // never built (PayloadKinds.None); nothing may stay behind
        var needs = NeedsOf(req, !JobPolicy.ValidateForWindows(req.Argv).Ok);
        var candidates = Candidates(req);
        var (probes, assessed) = await ProbeForJobAsync(candidates, needs, ct).ConfigureAwait(false);
        var ranked = Scheduler.Rank(assessed);
        var plan = new JobPlan(JobPlan.WouldRun, 0, ranked.Count > 0 ? "would run on " + ranked[0].Machine.Name : "")
        {
            Assessed = assessed,
            Ranked = ranked,
            Machine = ranked.Count > 0 ? ranked[0].Machine.Name : null,
            AllowedBy = JobPolicy.AllowedBy(config.Policy, req.Argv),
            UncommittedFiles = payload.ChangedFiles.Count,
        };
        return ranked.Count > 0 ? plan : NoneNow(id, req, needs, candidates, probes, plan);
    }

    private async Task<JobPlan> PlanShardsAsync(string id, JobRequest template, ShardOptions options, CancellationToken ct)
    {
        var auto = options.Count is null;
        var first = template with { Argv = new ShardInfo(1, 1).Substitute(template.Argv) };
        if (Precheck("shard", first) is { } firstCheck)
        {
            return firstCheck.Status == JobStatus.NoCapacity
                ? Ended(firstCheck, NeverByConfig(first)) with
                {
                    AllowedBy = JobPolicy.AllowedBy(config.Policy, first.Argv),
                    ShardCount = options.Count ?? 1,
                    ShardCountAuto = auto,
                    Placement = [],
                    Waiting = options.Count ?? 1,
                }
                : Ended(firstCheck with { Message = "shard 1: " + firstCheck.Message }, null) with { ShardCount = options.Count, ShardCountAuto = auto };
        }

        // --shards auto picks N from a probe before the snapshot, as lanq run does; the plan then places the shards from that same probe.
        MachineHealth[]? probes = null;
        string? autoWhy = null;
        ShardTimingResult? timing = null;
        int count;
        if (options.Count is { } n)
        {
            count = n;
        }
        else
        {
            (probes, var autoAssessed) = await ProbeForJobAsync(Candidates(template), ShardNeeds(template, [new ShardInfo(1, 1)]), ct).ConfigureAwait(false);
            (count, autoWhy, timing) = PickShardCount(template, autoAssessed, options.TargetMinutes);
        }

        var shards = Enumerable.Range(1, count).Select(i => new ShardInfo(i, count)).ToList();
        if (PrecheckShards(template, shards) is { } refusal)
        {
            return Ended(refusal.Refused with { Message = refusal.Message }, null) with { ShardCount = count, ShardCountAuto = auto, AutoWhy = autoWhy, Timing = timing };
        }

        var (payload, failed) = await SnapshotAndScanAsync(id, template, PayloadKinds.None, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return Ended(failed!, null) with { ShardCount = count, ShardCountAuto = auto, AutoWhy = autoWhy, Timing = timing };
        }

        TryDelete(payload.ArchivePath);
        var needs = ShardNeeds(template, shards);
        var candidates = Candidates(template);
        List<Assessment> assessed;
        if (probes is null)
        {
            (probes, assessed) = await ProbeForJobAsync(candidates, needs, ct).ConfigureAwait(false);
        }
        else
        {
            assessed = Scheduler.Assess(candidates.Zip(probes), needs);
        }

        // A fresh run has no shard of its own anywhere yet: the free slots are FreeFor with none placed, dealt round-robin as DispatchShards does.
        var dealt = Scheduler.Deal(Placeable(assessed, template, _ => 0, _ => 0), shards, (_, _) => true);
        List<ShardPlacement> placement = [.. dealt
            .GroupBy(d => d.Machine.Machine.Name, StringComparer.Ordinal)
            .Select(g => new ShardPlacement(g.First().Machine.Machine, [.. g.Select(d => d.Item.Index).Order()]))];
        var waiting = count - dealt.Count;
        var plan = new JobPlan(JobPlan.WouldRun, 0, string.Create(
            CultureInfo.InvariantCulture,
            $"would run {count} shard(s): {PlacementText(placement)}{(waiting > 0 ? $"; {waiting} waiting for a free slot" : "")}"))
        {
            Assessed = assessed,
            Ranked = Scheduler.Rank(assessed),
            Machine = dealt.Count > 0 ? dealt[0].Machine.Machine.Name : null,
            AllowedBy = JobPolicy.AllowedBy(config.Policy, first.Argv),
            UncommittedFiles = payload.ChangedFiles.Count,
            ShardCount = count,
            ShardCountAuto = auto,
            AutoWhy = autoWhy,
            Timing = timing,
            Placement = placement,
            Waiting = waiting,
        };
        return dealt.Count > 0 ? plan : NoneNow("shard", template, needs, candidates, probes, plan);
    }

    /// <summary>"build-01 shards 1,3; micro-02 shard 2".</summary>
    internal static string PlacementText(IEnumerable<ShardPlacement> placement) =>
        string.Join("; ", placement.Select(p => $"{p.Machine.Name} shard{(p.Shards.Count == 1 ? "" : "s")} {string.Join(',', p.Shards.Select(i => i.ToString(CultureInfo.InvariantCulture)))}"));

    /// <summary>
    /// No machine can take the job now: the NoCapacity lanq run would end with (its message, retryable), and the sleeper it would wake first,
    /// by the rules of <see cref="Dispatch"/>: only with --wait above 0, without --no-wake, and when not every machine is ruled out for good.
    /// </summary>
    private JobPlan NoneNow(string id, JobRequest req, JobNeeds needs, IReadOnlyList<Machine> candidates, MachineHealth[] probes, JobPlan plan)
    {
        var none = NoCapacity(id, [.. plan.Assessed], "");
        var wake = !plan.Never && req.Wait > TimeSpan.Zero && !req.NoWake ? WouldWake(candidates, needs, probes) : null;
        return plan with { Status = nameof(JobStatus.NoCapacity), ExitCode = none.CliExitCode, Message = none.Message, Machine = null, Retryable = none.Retryable, WouldWake = wake };
    }

    /// <summary>
    /// The machine <see cref="WakeBestSleeperAsync"/> would send a packet to: the first by name that <see cref="CouldWakeFor"/> allows and that
    /// <see cref="WakeService.Evaluate"/> says to wake. Asks the rules of a fresh WakeService whose sender refuses to send, so nothing is claimed or sent.
    /// </summary>
    /// <param name="candidates">The job's candidates (<see cref="Candidates"/>); <paramref name="probes"/> is index-aligned with it.</param>
    private string? WouldWake(IReadOnlyList<Machine> candidates, JobNeeds needs, MachineHealth[] probes)
    {
        var rules = new WakeService(new RefusingMagicPacketSender(), clock);
        var now = clock.GetLocalNow().DateTime;
        return WakeOrder(candidates)
            .Where(i => CouldWakeFor(candidates[i], probes[i], needs, archiveFailed: false)
                && rules.Evaluate(candidates[i], probes[i], now, config.Policy).Decision == WakeDecision.Wake)
            .Select(i => candidates[i].Name)
            .FirstOrDefault();
    }

    /// <summary>A plan for a run that would end before starting: refused (2), no machine at all (75), or the snapshot failed (70).</summary>
    private static JobPlan Ended(JobResult r, List<Assessment>? assessed) => new(r.Status.ToString(), r.CliExitCode, r.Message)
    {
        Assessed = assessed ?? [],
        Retryable = r.Status == JobStatus.NoCapacity ? r.Retryable ?? true : null,
        RejectKind = r.RejectKind,
        InfraKind = r.InfraKind,
    };
}
