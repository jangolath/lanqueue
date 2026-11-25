namespace LanQueue.Scheduling;

/// <summary>
/// What a job waiting for a machine is waiting for at one look (0.13.0 B2). <see cref="Slot"/>: some machine could take this kind of job but
/// its slots are full, or its memory is held by lanq jobs running there (memory a finishing job gives back). <see cref="Eligible"/>: nothing
/// can take it now (held, outside hours, asleep, unreachable, low memory or pressure from other work, a claim that failed over ssh).
/// </summary>
public enum WaitKind
{
    Eligible,
    Slot,
}

/// <summary>
/// lanq run --wait-eligible E --wait-slot S (0.13.0 B2), or --wait W (both W): how long a job may wait in each state. 0/0 is the default:
/// fail fast, as --wait 0 always did.
/// </summary>
public sealed record WaitBudgets(TimeSpan Eligible, TimeSpan Slot)
{
    /// <summary>The larger budget: no job waits longer than this in all.</summary>
    public TimeSpan Longest => Eligible > Slot ? Eligible : Slot;

    /// <summary>The budget of one state.</summary>
    public TimeSpan Of(WaitKind kind) => kind == WaitKind.Slot ? Slot : Eligible;
}

/// <summary>
/// The two wait clocks of one waiting job (0.13.0 B2). Both are idle clocks, as --wait always was: they count only from
/// <c>since</c> (the job's start, or the last time its run or plan held or gave back a slot), and restart whenever that moves. Each
/// interval between two looks is charged to the state seen at its start (an interval that begins at a restart goes to the state seen at its
/// end). The job ends NoCapacity when the clock of the current state has used its budget, or when both together have used the larger budget,
/// so --wait W (both budgets W) ends exactly when 0.12 did: once W of idle time has passed. Not thread-safe: one per waiting job or shard set.
/// </summary>
public sealed class WaitClock
{
    private TimeSpan _since = TimeSpan.MinValue;
    private TimeSpan _last;
    private WaitKind? _lastKind;

    /// <summary>Idle time charged to eligible waiting since the last restart.</summary>
    public TimeSpan Eligible { get; private set; }

    /// <summary>Idle time charged to slot waiting since the last restart.</summary>
    public TimeSpan Slot { get; private set; }

    /// <summary>The state seen at the latest look, or null before the first.</summary>
    public WaitKind? Current => _lastKind;

    /// <summary>
    /// A look at the machines at <paramref name="now"/> found the job waiting in state <paramref name="kind"/>; the idle clocks count from
    /// <paramref name="since"/> (both on the same monotonic scale).
    /// </summary>
    public void Look(TimeSpan now, TimeSpan since, WaitKind kind)
    {
        if (since != _since)
        {
            _since = since;
            Eligible = Slot = TimeSpan.Zero;
            _last = since;
            _lastKind = null;
        }

        var span = now - _last;
        if (span > TimeSpan.Zero)
        {
            if ((_lastKind ?? kind) == WaitKind.Slot)
            {
                Slot += span;
            }
            else
            {
                Eligible += span;
            }

            _last = now;
        }

        _lastKind = kind;
    }

    /// <summary>The budget that ran out at the latest look (named by the state then seen), or null while the job may keep waiting.</summary>
    public WaitKind? SpentOut(WaitBudgets budgets)
    {
        if (_lastKind is not { } kind)
        {
            return null;
        }

        var own = kind == WaitKind.Slot ? Slot : Eligible;
        return own >= budgets.Of(kind) || Eligible + Slot >= budgets.Longest ? kind : null;
    }
}

/// <summary>NoCapacity after waiting (0.13.0 B2): the idle time charged to each state, and the budget that ran out.</summary>
public sealed record WaitedInfo(TimeSpan Eligible, TimeSpan Slot, WaitKind Budget);
