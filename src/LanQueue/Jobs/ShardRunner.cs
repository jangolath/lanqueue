using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
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
public sealed record ShardOptions(int? Count, int Retries = 0, bool FailFast = false);

/// <summary>One shard's outcome. Status is a <see cref="JobStatus"/> name or Cancelled; JobId and Machine are null when it never ran.</summary>
public sealed record ShardResult(int Index, string Status, int ExitCode, string? Machine, string? JobId, TimeSpan Duration, int Attempts, string Message)
{
    /// <summary>What --fetch brought back for the shard's last attempt; null when no fetch was asked for or it never ran.</summary>
    public FetchReport? Fetch { get; init; }
}

/// <summary>A whole lanq run --shards: the overall status and exit code (see <see cref="JobRunner.Aggregate"/>) and every shard, in index order.</summary>
public sealed record ShardRunResult(string Status, int ExitCode, int Count, IReadOnlyList<ShardResult> Shards, string Message);

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
        if (firstCheck is not null)
        {
            return Refused(options.Count ?? 1, "shard 1: " + firstCheck.Message, firstCheck);
        }

        var count = options.Count ?? await AutoShardCountAsync(template, ct).ConfigureAwait(false);
        if (count is < 1 or > ShardInfo.MaxShards)
        {
            return Refused(Math.Clamp(count, 1, ShardInfo.MaxShards), $"--shards must be from 1 to {ShardInfo.MaxShards}.", null);
        }

        var shards = Enumerable.Range(1, count).Select(i => new ShardInfo(i, count)).ToList();
        foreach (var s in shards)
        {
            if (Precheck("shard", template with { Argv = s.Substitute(template.Argv), Shard = s }) is { } refused)
            {
                return Refused(count, $"shard {s}: {refused.Message}", refused);
            }
        }

        var payloadId = NewId();
        var (prepared, failed) = await PrepareAsync(payloadId, template, output, ct).ConfigureAwait(false);
        if (prepared is null)
        {
            return new ShardRunResult(failed!.Status.ToString(), failed.CliExitCode, count, [], failed.Message);
        }

        await using (prepared.ConfigureAwait(false))
        {
            // DispatchShards returns only once every shard it started has finished (and cleaned up), so the payload outlives them all.
            var results = await DispatchShards(template, shards, options, prepared, ct).ConfigureAwait(false);
            // Ctrl+C that came after every shard had already finished does not turn a real result into Cancelled.
            return Aggregate(count, results, ct.IsCancellationRequested && results.Any(r => r.Status == Cancelled));
        }

        static ShardRunResult Refused(int n, string message, JobResult? r) =>
            new(r?.Status.ToString() ?? nameof(JobStatus.Rejected), r?.CliExitCode ?? 2, n, [], message);
    }

    /// <summary>
    /// How many shards a machine can take now: max_jobs minus the slots in use (or this run's own shards there, if more), and with --mem no more
    /// than the shards its free memory covers: floor((free memory - --mem for each of this run's shards placed there but not running yet) /
    /// --mem). A placed shard still uploading or preparing uses no memory yet, so the probe cannot see what it will need. A machine that does
    /// not report memory is already held under --mem.
    /// </summary>
    private static int FreeFor(Machine machine, MachineHealth h, double? memGb, int ours, int oursNotRunning)
    {
        var slots = machine.MaxJobs - Math.Max(h.SlotsUsed, ours);
        return memGb is { } need && h.MemFreeGb is { } free ? Math.Min(slots, Math.Max(0, (int)Math.Floor((free - (oursNotRunning * need)) / need))) : slots;
    }

    /// <summary>
    /// --shards auto: the free slots on every machine that can take the job right now (capabilities, allowed hours, --mem per shard, holds,
    /// and Windows-safe arguments), at least 1 and at most <see cref="ShardInfo.MaxShards"/>. Prints the choice and why.
    /// </summary>
    private async Task<int> AutoShardCountAsync(JobRequest template, CancellationToken ct)
    {
        var argv = new ShardInfo(1, 1).Substitute(template.Argv);
        var (_, health) = await ProbeForJobAsync(template, !JobPolicy.ValidateForWindows(argv).Ok, ct).ConfigureAwait(false);
        var free = Scheduler.Rank(health, template.Needs)
            .Select(m => (m.Name, Free: FreeFor(m, health.First(h => ReferenceEquals(h.Item1, m)).Item2, template.MinMemGb, 0, 0)))
            .Where(f => f.Free > 0)
            .ToList();
        var total = free.Sum(f => f.Free);
        var n = Math.Clamp(total, 1, ShardInfo.MaxShards);
        var why = total == 0
            ? "no machine can take the job right now; 1 shard waits under --wait like a single job"
            : string.Join(", ", free.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Name} {f.Free}")))
                + (total > ShardInfo.MaxShards ? string.Create(CultureInfo.InvariantCulture, $"; capped at {ShardInfo.MaxShards}") : "");
        output(string.Create(CultureInfo.InvariantCulture, $"[lanq] --shards auto: {n} shard(s) ({(total == 0 ? why : "free slots: " + why)})"));
        return n;
    }

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
        return worst is null
            ? new ShardRunResult(nameof(JobStatus.Completed), 0, count, shards, summary)
            : new ShardRunResult(worst.Status, worst.ExitCode, count, shards, string.Create(CultureInfo.InvariantCulture, $"{summary}; shard {worst.Index}/{count}: {worst.Status}: {worst.Message}"));

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
    /// <see cref="WhyNot"/> for a sharded run's NoCapacity message, plus the one reason only shards have: a machine every waiting
    /// shard must avoid because an earlier attempt of it lost the connection there and its cleanup did not finish.
    /// </summary>
    private static string ShardWhyNot(Machine m, MachineHealth h, IReadOnlyList<string> needs, IReadOnlyCollection<ShardState> waiting) =>
        h.Reachable && h.Hold is null && HasCapabilities(m, needs) && waiting.Count > 0 && waiting.All(p => p.Avoid.Contains(m.Name))
            ? "avoided: an earlier attempt of the waiting shard(s) lost the connection there and may still be running"
            : WhyNot(m, h, needs);

    private sealed class ShardState(ShardInfo shard)
    {
        public ShardInfo Shard => shard;

        public int Attempts { get; set; }

        public ShardResult? Final { get; set; }

        /// <summary>Machines a re-run must not use: the job may still be running there (its cleanup after a lost connection did not finish).</summary>
        public HashSet<string> Avoid { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="HadSlot">The attempt held a slot on its machine at some point (null results never did).</param>
    private sealed record Attempt(ShardState State, Machine Machine, JobResult? Result, TimeSpan Duration, bool Cancelled, bool HadSlot);

    /// <summary>One started attempt: its machine, whether it holds a slot yet and whether its command runs yet (set from the attempt's own thread).</summary>
    private sealed class Running(string machine)
    {
        private volatile bool _holding;
        private volatile bool _started;

        public string Machine => machine;

        public bool Holding => _holding;

        public bool Started => _started;

        public void Hold() => _holding = true;

        public void Start() => _started = true;
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

        var windowsUnsafe = shards.Any(s => !JobPolicy.ValidateForWindows(s.Substitute(template.Argv)).Ok);
        var gitCanEverTakeIt = GitCanEverTakeIt(template);
        var states = shards.Select(s => new ShardState(s)).ToList();
        var pending = new List<ShardState>(states);
        var running = new Dictionary<Task<Attempt>, Running>();

        // --wait: how long no shard of this run has held a slot. Restarted only by an attempt that really got one, so attempts that keep
        // failing to claim a slot (the probe said free, the machine said full) cannot hold the run open for ever.
        var idle = Stopwatch.StartNew();
        var backoff = false;
        Exception? loopError = null;

        // The latest probe (with this job's holds), for choosing another machine for a re-run by the same rules as placement.
        List<(Machine, MachineHealth)> lastHealth = [];

        Task<Attempt> Launch(ShardState st, Machine machine, DateTimeOffset? windowEnds, Running slot)
        {
            st.Attempts++;
            var req = template with { Argv = st.Shard.Substitute(template.Argv), Shard = st.Shard };
            var prefix = $"[{st.Shard} {machine.Name}] ";
            return Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    var r = await TryOnMachine(NewId(), req, prepared, machine, windowEnds, gitCanEverTakeIt, line => Emit(prefix + line), stop.Token, slot.Hold, slot.Start).ConfigureAwait(false);
                    return new Attempt(st, machine, r, clock.Elapsed, false, slot.Holding);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return new Attempt(st, machine, null, clock.Elapsed, true, slot.Holding);
                }
                catch (Exception ex)
                {
                    // Anything else (an OperationCanceledException nobody asked for included) is this attempt's result: the other shards go on.
                    var failed = new JobResult(NewId(), JobStatus.InfraError, 70, machine.Name, SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"));
                    return new Attempt(st, machine, failed, clock.Elapsed, false, slot.Holding);
                }
            }, CancellationToken.None);
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
                    List<(Machine, MachineHealth)> health;
                    MachineHealth[] probes;
                    try
                    {
                        (probes, health) = await ProbeForJobAsync(template, windowsUnsafe, stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        break;
                    }

                    // Free slots per machine (see FreeFor; a shard just launched may not hold its slot yet when the probe looks, so this run's
                    // own shards there count too). Shards are dealt round-robin over the ranked machines, so a few shards spread out and many
                    // fill every slot: a machine with max_jobs 4 takes four. A shard never goes to a machine it must avoid.
                    var free = Scheduler.Rank(health, template.Needs)
                        .Select(m => (Machine: m, Health: health.First(x => ReferenceEquals(x.Item1, m)).Item2))
                        .Select(x => (x.Machine, x.Health, Free: FreeFor(
                            x.Machine, x.Health, template.MinMemGb, running.Values.Count(r => r.Machine == x.Machine.Name), running.Values.Count(r => r.Machine == x.Machine.Name && !r.Started))))
                        .Where(x => x.Free > 0)
                        .ToList();
                    for (var round = 0; pending.Count > 0 && free.Any(f => f.Free > round); round++)
                    {
                        foreach (var f in free.Where(f => f.Free > round))
                        {
                            var st = pending.FirstOrDefault(p => !p.Avoid.Contains(f.Machine.Name));
                            if (st is null)
                            {
                                continue;
                            }

                            pending.Remove(st);
                            var slot = new Running(f.Machine.Name);
                            running[Launch(st, f.Machine, f.Health.WindowEndsUtc, slot)] = slot;
                            launched++;
                        }
                    }

                    lastHealth = health;
                    if (launched == 0 && template.Wait > TimeSpan.Zero && !template.NoWake && probe.Waker is { AnyPending: false })
                    {
                        await WakeBestSleeperAsync(template, probes, health, windowsUnsafe, prepared.Archive, stop.Token).ConfigureAwait(false);
                    }
                }

                backoff = false;
                if (running.Values.Any(r => r.Holding))
                {
                    idle.Restart();
                }
                else if (pending.Count > 0 && running.Count == 0 && idle.Elapsed >= template.Wait)
                {
                    var archiveNote = prepared.Archive.Failure is null ? "" : " Archive build failed: " + prepared.Archive.Failure;
                    var held = string.Join("; ", lastHealth.Select(h => $"{h.Item1.Name}: {ShardWhyNot(h.Item1, h.Item2, template.Needs, pending)}"));
                    foreach (var st in pending)
                    {
                        st.Final = new ShardResult(st.Shard.Index, nameof(JobStatus.NoCapacity), 75, null, null, TimeSpan.Zero, st.Attempts, "No eligible machine. " + held + archiveNote);
                    }

                    pending.Clear();
                    break;
                }

                // Wait for a shard to finish; with shards still waiting for a slot, look again after 15 s at the latest.
                var waits = new List<Task>(running.Keys);
                Task? timer = null;
                if (pending.Count > 0 && launched == 0)
                {
                    if (running.Count == 0)
                    {
                        Emit(string.Create(CultureInfo.InvariantCulture, $"[lanq] no eligible machine for {pending.Count} shard(s); waiting 15s"));
                    }

                    timer = delay(TimeSpan.FromSeconds(15), stop.Token);
                    waits.Add(timer);
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

                    if (Settle(a, template, options, pending, Emit, lastHealth) && options.FailFast && !stop.IsCancellationRequested)
                    {
                        Emit($"[lanq] --fail-fast: shard {a.State.Shard} {a.State.Final!.Status}; cancelling the other shards");
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
            Settle(await task.ConfigureAwait(false), template, options, pending, Emit, lastHealth);
        }

        foreach (var st in states.Where(s => s.Final is null))
        {
            st.Final = loopError is null
                ? new ShardResult(st.Shard.Index, Cancelled, 130, null, null, TimeSpan.Zero, st.Attempts, "cancelled before it started")
                : new ShardResult(st.Shard.Index, nameof(JobStatus.InfraError), 70, null, null, TimeSpan.Zero, st.Attempts, SafeLine($"not run: unexpected error: {loopError.GetType().Name}: {loopError.Message}"));
        }

        return [.. states.Select(s => s.Final!)];
    }

    /// <summary>
    /// Records an attempt. No slot after all (null): back to the front of the queue. Lost its machine (ConnectionLost) with retries left:
    /// queued again as a new job, but only where it cannot run twice. When the connection dropped while the job ran and its cleanup did not
    /// finish, the job may still be running there, so the re-run must go to another machine; if no other machine could take it, it is not
    /// re-run. Otherwise final. True when the shard ended without completing.
    /// </summary>
    /// <param name="health">The latest probe with this job's holds: another machine qualifies for a re-run only when placement could use it
    /// (reachable, not held by hours, pause, memory, load, --mem or Windows-unsafe arguments, and with the capabilities).</param>
    private bool Settle(Attempt a, JobRequest template, ShardOptions options, List<ShardState> pending, Action<string> emit, List<(Machine, MachineHealth)> health)
    {
        var st = a.State;
        var prefix = $"[{st.Shard} {a.Machine.Name}] ";
        if (a.Cancelled)
        {
            st.Final = new ShardResult(st.Shard.Index, Cancelled, 130, a.Machine.Name, null, a.Duration, st.Attempts, "cancelled; its cleanup ran");
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

            if (health.Any(h => h.Item1.Name != a.Machine.Name && !st.Avoid.Contains(h.Item1.Name) && h.Item2.Reachable && h.Item2.Hold is null && HasCapabilities(h.Item1, template.Needs)))
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
        st.Final = new ShardResult(st.Shard.Index, r.Status.ToString(), r.CliExitCode, r.Machine, r.JobId, a.Duration, st.Attempts, message) { Fetch = r.Fetch };
        return r.Status != JobStatus.Completed;
    }
}
