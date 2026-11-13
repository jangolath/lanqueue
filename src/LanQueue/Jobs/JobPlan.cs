using LanQueue.Config;
using LanQueue.Scheduling;

namespace LanQueue.Jobs;

/// <summary>lanq run --shards --dry-run: the shards (1-based indexes, ascending) one machine would take at the start.</summary>
public sealed record ShardPlacement(Machine Machine, IReadOnlyList<int> Shards);

/// <summary>
/// What lanq run would do now (lanq run --dry-run, see <see cref="JobRunner.PlanAsync"/>): the same checks, snapshot scan, probe and choice
/// as a real run, without claiming a slot, sending, logging or waking anything.
/// </summary>
/// <param name="Status"><see cref="WouldRun"/>, or what lanq run would end with before starting: Rejected, NoCapacity, InfraError (Cancelled is the CLI's).</param>
/// <param name="ExitCode">0 for WouldRun, otherwise lanq run's own code for the status (2, 70, 75).</param>
/// <param name="Message">For people: where it would run, or the message lanq run would print.</param>
public sealed record JobPlan(string Status, int ExitCode, string Message)
{
    /// <summary>The status of a plan whose job (or at least one shard) would start now.</summary>
    public const string WouldRun = "WouldRun";

    /// <summary>Every machine judged for the job, in config order (empty when the run was refused before any machine was looked at).</summary>
    public IReadOnlyList<Assessment> Assessed { get; init; } = [];

    /// <summary>The eligible machines, in the order lanq run would try them.</summary>
    public IReadOnlyList<Assessment> Ranked { get; init; } = [];

    /// <summary>The machine the job (or shard 1) would go to, or null.</summary>
    public string? Machine { get; init; }

    /// <summary>The policy.allowed_commands entry that lets the command run, once the command line passed every check.</summary>
    public string? AllowedBy { get; init; }

    /// <summary>How many uncommitted files the snapshot's secret scan covered; null when no snapshot was taken.</summary>
    public int? UncommittedFiles { get; init; }

    /// <summary>--shards: N (the auto choice for --shards auto); null without --shards.</summary>
    public int? ShardCount { get; init; }

    /// <summary>--shards: true for --shards auto; null without --shards.</summary>
    public bool? ShardCountAuto { get; init; }

    /// <summary>--shards auto: the free slots behind that N ("free slots: build-01 2, micro-02 1"), or null.</summary>
    public string? AutoWhy { get; init; }

    /// <summary>--shards auto --target-minutes: how N was picked from earlier runs (<see cref="ShardTiming.Pick"/>); null otherwise.</summary>
    public ShardTimingResult? Timing { get; init; }

    /// <summary>--shards: where the shards would start now, in rank order (empty when none could); null without --shards.</summary>
    public IReadOnlyList<ShardPlacement>? Placement { get; init; }

    /// <summary>--shards: how many shards would wait for a slot at the start.</summary>
    public int Waiting { get; init; }

    /// <summary>--baseline: the base a failure would be rerun at (or that the rerun would be skipped), from <see cref="JobRunner.PreviewBaseline"/>; else null.</summary>
    public BaselineResult? Baseline { get; init; }

    /// <summary>The sleeping machine lanq run would wake (only with --wait above 0, without --no-wake, when no awake machine can take the job), or null.</summary>
    public string? WouldWake { get; init; }

    /// <summary>NoCapacity only: as <see cref="JobResult.Retryable"/>; null otherwise.</summary>
    public bool? Retryable { get; init; }

    /// <summary>Rejected only: as <see cref="JobResult.RejectKind"/>; null otherwise.</summary>
    public RejectKind? RejectKind { get; init; }

    /// <summary>InfraError only: as <see cref="JobResult.InfraKind"/>; null otherwise.</summary>
    public InfraKind? InfraKind { get; init; }

    /// <summary>True when every machine is ruled out for good (lanq run would end at once, whatever --wait).</summary>
    public bool Never => Assessed.Count > 0 && Assessed.All(a => a.Permanent);
}
