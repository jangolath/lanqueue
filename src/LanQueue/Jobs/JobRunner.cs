using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Payload;
using LanQueue.Policy;
using LanQueue.Scheduling;
using LanQueue.Ssh;

namespace LanQueue.Jobs;

public enum JobStatus
{
    Completed,
    Failed,
    Rejected,
    NoCapacity,
    InfraError,
    TimedOut,

    /// <summary>Stopped because the machine's allowed hours ended (plus policy.window_grace_minutes); exit 124 like TimedOut.</summary>
    WindowClosed,
}

/// <param name="Timeout">How long the job itself may run; null means <see cref="JobRequest.DefaultTimeout"/>.</param>
/// <param name="MinMemGb">lanq run --mem: only machines that report at least this much available memory (GB).</param>
/// <param name="Fetch">lanq run --fetch: files to bring back from the job's working folder after the job, or null.</param>
public sealed record JobRequest(
    IReadOnlyList<string> Needs, IReadOnlyList<string> Argv, string Directory, string RepoDir, TimeSpan Wait, TimeSpan? Timeout = null, double? MinMemGb = null,
    FetchRequest? Fetch = null)
{
    /// <summary>lanq run --no-wake: never wake a machine for this job.</summary>
    public bool NoWake { get; init; }

    /// <summary>lanq run --shards: this job is shard <see cref="ShardInfo.Index"/> of <see cref="ShardInfo.Count"/> (LANQ_SHARD, LANQ_SHARDS), or null.</summary>
    public ShardInfo? Shard { get; init; }

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(4);
}

/// <param name="Patterns">Validated --fetch patterns (see <see cref="FetchPatterns"/>).</param>
/// <param name="OutputRoot">The local folder; files land in OutputRoot/jobid/relpath.</param>
/// <param name="Anchor">The folder trusted as given (repository top or --fetch-dir); null means <paramref name="OutputRoot"/>.</param>
public sealed record FetchRequest(IReadOnlyList<string> Patterns, string OutputRoot, FetchLimits Limits, string? Anchor = null);

public sealed record JobResult(string JobId, JobStatus Status, int ExitCode, string? Machine, string Message)
{
    /// <summary>What --fetch brought back; null when no fetch was asked for or the job never ran. Never changes <see cref="CliExitCode"/>.</summary>
    public FetchReport? Fetch { get; init; }

    /// <summary>
    /// True when the job ended InfraError because the machine stopped answering ssh (exit 255) while the job was uploaded, prepared or run:
    /// the only failure lanq run --shard-retries re-runs. Never set for a job's own failure.
    /// </summary>
    public bool ConnectionLost { get; init; }

    /// <summary>
    /// True when the connection dropped while the job itself was running, so the job may still be running on the worker until its cleanup
    /// stops it. With <see cref="ConnectionLost"/>, a re-run is safe on that machine only when <see cref="CleanedUp"/>.
    /// </summary>
    public bool MayStillRun { get; init; }

    /// <summary>True when the cleanup after the job finished on the worker (its process stopped, folder and slot removed).</summary>
    public bool CleanedUp { get; init; }

    /// <summary>Process exit code for the CLI: the remote code on completion/failure, 75 for no capacity, 2 for rejected, 124 for timed out or window closed, 70 for infra.</summary>
    public int CliExitCode => Status switch
    {
        JobStatus.Completed or JobStatus.Failed => ExitCode,
        JobStatus.TimedOut or JobStatus.WindowClosed => ProcessSshClient.TimedOutExitCode,
        JobStatus.NoCapacity => 75,
        JobStatus.Rejected => 2,
        _ => 70,
    };
}

public sealed partial class JobRunner(
    FleetConfig config,
    ISshClient ssh,
    IPayloadBuilder payloads,
    HealthProbe probe,
    JobLog log,
    string scratchDir,
    Action<string> output,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? clock = null,
    BaseStore? bases = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Last commit each git worker has fetched, per repository: the base for incremental bundles (in memory unless given a file).</summary>
    private readonly BaseStore bases = bases ?? new BaseStore(null);

    public async Task<JobResult> RunAsync(JobRequest req, CancellationToken ct)
    {
        var jobId = NewId();
        if (Precheck(jobId, req) is { } refused)
        {
            return refused;
        }

        var (prepared, failed) = await PrepareAsync(jobId, req, output, ct).ConfigureAwait(false);
        if (prepared is null)
        {
            return failed!;
        }

        await using (prepared.ConfigureAwait(false))
        {
            return await Dispatch(jobId, req, prepared, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The checks on the command line alone (policy, secrets in it, Windows-safe arguments), before any network or git work; null when it may run.</summary>
    private JobResult? Precheck(string jobId, JobRequest req)
    {
        var policy = JobPolicy.Validate(config.Policy, req.Argv, req.Directory);
        if (!policy.Ok)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, null, policy.Reason!);
        }

        var commandFindings = SecretScanner.ScanText(string.Join(' ', req.Argv), "command line");
        if (commandFindings.Count > 0)
        {
            return Rejected(jobId, commandFindings);
        }

        // Arguments that cmd.exe shims would re-parse can still run on a Linux worker; refuse only when no non-Windows machine has
        // the needed capabilities, before any network or git work.
        var windows = JobPolicy.ValidateForWindows(req.Argv);
        return !windows.Ok && !config.Machines.Any(m => m.Os != MachineOs.Windows && HasCapabilities(m, req.Needs))
            ? new JobResult(jobId, JobStatus.Rejected, 2, null, windows.Reason!)
            : null;
    }

    /// <summary>
    /// Snapshots the repository once (under <paramref name="payloadId"/>) and scans it for secrets. Every job given the result shares its
    /// snapshot, its bundles and its archive, each built at most once, on first use; disposing it deletes them.
    /// </summary>
    private async Task<(PreparedPayload? Prepared, JobResult? Failed)> PrepareAsync(string payloadId, JobRequest req, Action<string> emit, CancellationToken ct)
    {
        JobPayload payload;
        try
        {
            // Only the snapshot is built up front; a bundle or archive is built lazily, when a machine of that kind actually gets the job.
            var kinds = config.Machines.Any(m => m.Transfer == TransferMode.Git) ? PayloadKinds.Bundle : PayloadKinds.None;
            payload = await payloads.BuildAsync(payloadId, req.RepoDir, scratchDir, kinds, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return (null, new JobResult(payloadId, JobStatus.InfraError, 70, null, "Could not snapshot the repository: " + ex.Message));
        }

        var builds = new SemaphoreSlim(1, 1);
        var prepared = new PreparedPayload(
            payloadId,
            payload,
            new LazyArchive(payloads, payloadId, req.RepoDir, scratchDir, payload.Sha, emit, builds),
            new LazyBundles(payloads, payloadId, req.RepoDir, scratchDir, payload.Sha, builds),
            builds);
        var findings = SecretScanner.ScanText(payload.OverlayDiff, "uncommitted changes")
            .Concat(SecretScanner.ScanFileNames(payload.ChangedFiles))
            .Concat(SecretScanner.ScanCommittedFileNames(payload.CommittedFiles ?? []))
            .Concat((payload.CommittedConfigs ?? new Dictionary<string, string?>()).SelectMany(c => SecretScanner.ScanCommittedConfig(c.Key, c.Value)))
            .ToList();
        if (findings.Count > 0)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            return (null, Rejected(payloadId, findings));
        }

        return (prepared, null);
    }

    /// <summary>A snapshot shared by the jobs of one lanq run (one job, or every shard): deleting it removes the snapshot files and every bundle and archive built.</summary>
    private sealed class PreparedPayload(string id, JobPayload payload, LazyArchive archive, LazyBundles bundles, SemaphoreSlim builds) : IAsyncDisposable
    {
        /// <summary>The id the payload was built under; the bundle carries the snapshot as refs/lanq/&lt;Id&gt;.</summary>
        public string Id => id;

        public JobPayload Payload => payload;

        public LazyArchive Archive => archive;

        public LazyBundles Bundles => bundles;

        public async ValueTask DisposeAsync()
        {
            TryDelete(payload.ArchivePath);
            await bundles.DeleteAsync().ConfigureAwait(false);
            await archive.DeleteAsync().ConfigureAwait(false);
            builds.Dispose();
        }
    }

    internal const string WindowsUnsafeHold = Scheduler.WindowsUnsafeHold;

    /// <summary>The worker enforces the job limit itself (timeout on Linux, a deadline wait on Windows) this long after lanq would stop it.</summary>
    internal static readonly TimeSpan WorkerDeadlineMargin = TimeSpan.FromMinutes(1);

    /// <summary>How long the --fetch step may take at most (it may get less near the end of a machine's allowed hours).</summary>
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The --fetch step may run at most this long past a machine's allowed hours plus grace (where a window-closed job stopped).</summary>
    internal static readonly TimeSpan FetchWindowMargin = TimeSpan.FromMinutes(2);

    /// <summary>How long stopping a timed-out or window-closed job before its fetch may take.</summary>
    internal static readonly TimeSpan StopBeforeFetchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>With less time than this left for the fetch (by the window), it is not started.</summary>
    internal static readonly TimeSpan MinFetchTime = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Splits the wrapper's exit marker (<see cref="RemoteScripts.ExitMarker"/>) off the end of an output line: the text before it (job output
    /// that had no final newline) and the job's exit code, or the line unchanged and null when it does not end with this job's marker.
    /// </summary>
    internal static (string Before, int? Code) ReadExitMarker(string line, string jobId)
    {
        var marker = RemoteScripts.ExitMarker(jobId);
        var at = line.TrimEnd('\r').LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return (line, null);
        }

        // -?\d{1,10}: a Windows crash code (an NTSTATUS such as 0xC0000005) is negative as an int.
        var digits = line.TrimEnd('\r')[(at + marker.Length)..];
        var unsigned = digits.StartsWith('-') ? digits[1..] : digits;
        return unsigned.Length is > 0 and <= 10 && unsigned.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? (line[..at], code)
            : (line, null);
    }

    private static bool HasCapabilities(Machine m, IReadOnlyList<string> needs) =>
        needs.All(n => m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase));

    /// <summary>Why a machine did not take the job, for the NoCapacity message: its hold or ssh error, a missing capability, or no free slot.</summary>
    internal static string WhyNot(Assessment a) => a.Why ?? "no free slot when the job tried to start (busy, or ssh failed)";

    private async Task<JobResult> Dispatch(string jobId, JobRequest req, PreparedPayload prepared, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var archive = prepared.Archive;
        var needs = new JobNeeds(req.Needs, req.MinMemGb, !JobPolicy.ValidateForWindows(req.Argv).Ok);
        var gitCanEverTakeIt = GitCanEverTakeIt(req);
        while (true)
        {
            var (probes, assessed) = await ProbeForJobAsync(needs, ct).ConfigureAwait(false);
            var ranked = Scheduler.Rank(assessed);
            foreach (var a in ranked)
            {
                var result = await TryOnMachine(jobId, req, prepared, a.Machine, a.Health.WindowEndsUtc, gitCanEverTakeIt, output, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }

            // Wake-on-LAN last resort: only when no awake machine can take the job now, only with a real --wait to wait in (a pending wake is
            // always inside that wait, so --wait 0 never blocks on one), and only one machine, the best-ranked sleeper.
            if (ranked.Count == 0 && req.Wait > TimeSpan.Zero && !req.NoWake && probe.Waker is { AnyPending: false })
            {
                await WakeBestSleeperAsync(needs, probes, assessed, archive, ct).ConfigureAwait(false);
            }

            if (deadline.Elapsed >= req.Wait)
            {
                var held = string.Join("; ", assessed.Select(a => $"{a.Machine.Name}: {WhyNot(a)}"));
                var archiveNote = archive.Failure is null ? "" : " Archive build failed: " + archive.Failure;
                return new JobResult(jobId, JobStatus.NoCapacity, 75, null, "No eligible machine. " + held + archiveNote);
            }

            output("[lanq] no eligible machine; waiting 15s");
            await delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Static property of the config: can any git-transfer machine ever take this job (capabilities only; load, hours and slots are temporary)?</summary>
    private bool GitCanEverTakeIt(JobRequest req) =>
        config.Machines.Any(m => m.Transfer == TransferMode.Git && HasCapabilities(m, req.Needs));

    /// <summary>
    /// Probes every machine at once (WhenAll keeps the config order whatever order the answers arrive in), then judges each one for this job
    /// (<see cref="Scheduler.Assess"/>): the raw probe results, and the assessments in config order.
    /// </summary>
    private async Task<(MachineHealth[] Probes, List<Assessment> Assessed)> ProbeForJobAsync(JobNeeds needs, CancellationToken ct)
    {
        var probes = await Task.WhenAll(config.Machines.Select(m => probe.ProbeAsync(m, config.Policy, ct))).ConfigureAwait(false);
        return (probes, Scheduler.Assess(config.Machines.Zip(probes), needs));
    }

    /// <summary>
    /// Whether a machine that looks asleep is worth waking for a job, by everything knowable without a probe: it has a wake: setting, the
    /// capabilities, is not Windows for arguments unsafe there, and is not an archive machine when the archive could not be built. A sleeper's
    /// memory and load are unknown, so they are not judged. <paramref name="raw"/> is the probe result as returned (no job holds).
    /// </summary>
    internal static bool CouldWakeFor(Machine m, MachineHealth raw, JobNeeds needs, bool archiveFailed) =>
        m.Wake is not null && raw.LooksAsleep && Eligibility.Missing(m, needs.Capabilities).Count == 0
        && !(needs.WindowsUnsafe && m.Os == MachineOs.Windows) && !(m.Transfer == TransferMode.Archive && archiveFailed);

    /// <summary>
    /// Wakes at most one machine: the first by name among those <see cref="CouldWakeFor"/> allows that are inside their hours. The decision uses
    /// the raw probe results; a machine that was sent a packet (or was refused one with a reason) gets the new result in <paramref name="probes"/>
    /// and is judged again in <paramref name="assessed"/>.
    /// </summary>
    private async Task WakeBestSleeperAsync(JobNeeds needs, MachineHealth[] probes, List<Assessment> assessed, LazyArchive archive, CancellationToken ct)
    {
        foreach (var i in Enumerable.Range(0, config.Machines.Count).OrderBy(i => config.Machines[i].Name, StringComparer.Ordinal))
        {
            var m = config.Machines[i];
            if (!CouldWakeFor(m, probes[i], needs, archive.Failure is not null))
            {
                continue;
            }

            var after = await probe.WakeAsync(m, config.Policy, probes[i], wait: false, ct).ConfigureAwait(false);
            if (ReferenceEquals(after, probes[i]))
            {
                continue; // not allowed now (outside its hours, already woken)
            }

            probes[i] = after;
            assessed[i] = Scheduler.Assess([(m, after)], needs)[0];
            if (probe.Waker!.AnyPending)
            {
                output($"[lanq] no awake machine can take the job; sent a Wake-on-LAN packet to {m.Name} (waiting up to {m.Wake!.WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s, within --wait)");
                return;
            }
        }
    }

    /// <summary>Returns null when the machine had no free slot (caller tries the next one).</summary>
    /// <param name="emit">Where this job's lines go (lanq's own and the job's output, already sanitised).</param>
    /// <param name="onSlot">Called once the job holds a slot on the machine (the sharded run counts --wait only while no shard holds one).</param>
    /// <param name="onRunning">Called just before the job's command starts on the machine (after upload and prepare).</param>
    private async Task<JobResult?> TryOnMachine(
        string jobId, JobRequest req, PreparedPayload prepared, Machine machine, DateTimeOffset? windowEnds, bool gitCanEverTakeIt, Action<string> emit, CancellationToken ct,
        Action? onSlot = null, Action? onRunning = null)
    {
        var (payload, archive) = (prepared.Payload, prepared.Archive);
        string? archivePath = null;
        if (machine.Transfer == TransferMode.Archive)
        {
            try
            {
                archivePath = await archive.GetAsync(ct).ConfigureAwait(false);
                archive.Failure = null; // a retried build that worked leaves no stale failure note
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                var message = "Could not build the archive: " + ex.Message;
                // If any git machine has the capabilities this job needs, it can take the job now or later (it may only be busy, full
                // or outside its hours): skip this archive machine and keep waiting. Only when no git machine could EVER take it
                // does the archive failure end the job (70) instead of waiting for a machine that cannot help.
                if (!gitCanEverTakeIt)
                {
                    return new JobResult(jobId, JobStatus.InfraError, 70, null, message);
                }

                if (archive.MarkReported())
                {
                    emit($"[lanq] {machine.Name}: {message}");
                }

                archive.Failure = message;
                return null;
            }
        }

        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var command = string.Join(' ', req.Argv);

        var acquire = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.AcquireSlot(machine, jobId, req.Timeout ?? JobRequest.DefaultTimeout)), null, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        var slot = ParseSlot(acquire.Output);
        if (!acquire.Ok || slot is null)
        {
            return null; // unreachable or full: try the next machine
        }

        onSlot?.Invoke();
        emit($"[lanq] job {jobId} -> {machine.Name} (slot {slot})");
        var logPath = log.LogPathFor(jobId);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var logFile = new StreamWriter(logPath, append: false);
        var logGate = new object();
        var logOpen = true;
        void OnLine(string line)
        {
            line = SafeLine(line);
            lock (logGate)
            {
                if (!logOpen)
                {
                    return; // a line that arrives after the job ended is dropped, never written to a closed log
                }

                logFile.WriteLine(line);
                emit(line);
            }
        }

        var cleanedUp = false;
        try
        {
            JobResult result;
            try
            {
                result = await Transfer(jobId, req, prepared, archivePath, machine, windowEnds, OnLine, ct, onRunning).ConfigureAwait(false);
            }
            finally
            {
                // Never throw from here (that would leak the slot): an unusable repository id only drops the cache steps.
                var repoId = payload.RepoId is { } id && IsRepoId(id) ? id : null;
                SshResult cleanup;
                using (await CacheGate(machine, CancellationToken.None).ConfigureAwait(false))
                {
                    cleanup = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Cleanup(machine, jobId, slot, repoId)), null, TimeSpan.FromSeconds(60), CancellationToken.None)
                        .ConfigureAwait(false);
                }

                cleanedUp = cleanup.Ok;
                if (!cleanup.Ok)
                {
                    emit($"[lanq] warning: cleanup on {machine.Name} did not finish; check {machine.WorkRoot}/jobs/{jobId}");
                }
            }

            log.Append(new JobRecord(jobId, machine.Name, command, started, Math.Round(clock.Elapsed.TotalSeconds, 2), result.ExitCode, result.Status.ToString()));
            return result with { CleanedUp = cleanedUp };
        }
        finally
        {
            lock (logGate)
            {
                logOpen = false;
            }

            await logFile.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<JobResult> Transfer(
        string jobId, JobRequest req, PreparedPayload prepared, string? archivePath, Machine machine, DateTimeOffset? windowEnds, Action<string> onLine, CancellationToken ct,
        Action? onRunning = null)
    {
        string executeCommand;
        try
        {
            // Built once up front only to refuse a command that cannot run on this machine before anything is uploaded.
            executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, req.Timeout ?? JobRequest.DefaultTimeout, req.Shard));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, machine.Name, ex.Message);
        }

        // One upload-and-prepare at a time per machine within this lanq: shards landing on one worker would otherwise race on its repository
        // cache (refs/lanq/base). A later shard there then gets a small incremental bundle on the base the first one recorded.
        SshResult step;
        using (await CacheGate(machine, ct).ConfigureAwait(false))
        {
            var (prepareStep, failed) = await Deliver(jobId, prepared, archivePath, machine, onLine, ct).ConfigureAwait(false);
            if (failed is not null)
            {
                return failed;
            }

            step = prepareStep!;
        }

        if (!step.Ok)
        {
            return new JobResult(jobId, JobStatus.InfraError, step.ExitCode, machine.Name,
                "Could not prepare the job on the machine." + (step.TimedOut ? " (timed out)" : ""))
            {
                ConnectionLost = step.IsSshFailure && !step.TimedOut,
            };
        }

        // A machine with allowed hours stops the job when its window ends plus the grace period, if that comes before --timeout. The
        // limit is taken now, after upload and prepare, so time spent getting here counts against the window.
        var timeout = req.Timeout ?? JobRequest.DefaultTimeout;
        var grace = TimeSpan.FromMinutes(config.Policy.WindowGraceMinutes);
        var windowLimit = windowEnds + grace - clock.GetUtcNow();
        var byWindow = windowLimit < timeout;
        if (windowLimit is { } left && (left <= TimeSpan.Zero || left < TimeSpan.FromMinutes(config.Policy.MinWindowMinutes)))
        {
            // Upload and prepare used up the window: hold like a closing window (cleanup runs in the caller), never a job stopped after a second.
            return new JobResult(jobId, JobStatus.NoCapacity, 75, machine.Name, string.Create(
                CultureInfo.InvariantCulture,
                $"{machine.Name}: window closing ({Math.Max(0, Math.Ceiling(left.TotalMinutes)):0} min left of its allowed hours plus grace after upload and prepare); the job was not started"));
        }

        if (byWindow)
        {
            timeout = windowLimit!.Value;
            onLine($"[lanq] {machine.Name}: allowed hours end soon; the job will be stopped after {ProcessSshClient.Describe(timeout)} (window end + {grace.TotalMinutes:0} min grace)");
        }

        // The worker enforces the same limit itself, a margin later, so a job outlives a vanished lanq by at most that margin.
        executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, timeout + WorkerDeadlineMargin, req.Shard));
        // The wrapper's exit marker counts only as the FINAL output line (or glued to the end of it): a line carrying one is held back until
        // the next line arrives, which shows it was not final, and is then passed on unchanged as ordinary output. The final one is taken off.
        var heldGate = new object();
        string? held = null;
        void OnJobLine(string line)
        {
            lock (heldGate)
            {
                if (held is not null)
                {
                    onLine(held);
                }

                var marked = ReadExitMarker(line, jobId).Code is not null;
                held = marked ? line : null;
                if (!marked)
                {
                    onLine(line);
                }
            }
        }

        onRunning?.Invoke();
        var run = await ssh.RunAsync(
            machine,
            executeCommand,
            OnJobLine,
            timeout,
            ct).ConfigureAwait(false);

        int? markedExit = null;
        if (held is { } last)
        {
            var (before, code) = ReadExitMarker(last, jobId);
            markedExit = code;
            if (before.Length > 0)
            {
                onLine(before);
            }
        }

        // ssh exits 255 both when the connection fails and when the job itself exited 255. The final marker is used for that one question
        // only: 255 is the job's own exit when the marker says 255, and a lost connection otherwise. Any other ssh exit code stands as it is.
        var sshFailed = run.IsSshFailure && markedExit != 255;
        var jobExit = run.ExitCode;
        JobResult result;
        if (run.TimedOut && byWindow)
        {
            result = new JobResult(jobId, JobStatus.WindowClosed, ProcessSshClient.TimedOutExitCode, machine.Name,
                $"stopped after {ProcessSshClient.Describe(timeout)}: {machine.Name}'s allowed hours ended (plus {grace.TotalMinutes:0} min grace)");
        }
        else if (run.TimedOut)
        {
            result = new JobResult(jobId, JobStatus.TimedOut, ProcessSshClient.TimedOutExitCode, machine.Name, "timed out after " + ProcessSshClient.Describe(timeout));
        }
        else if (sshFailed)
        {
            result = new JobResult(jobId, JobStatus.InfraError, run.ExitCode, machine.Name, "SSH connection failed while running the job.") { ConnectionLost = true, MayStillRun = true };
        }
        else
        {
            result = new JobResult(
                jobId,
                jobExit == 0 ? JobStatus.Completed : JobStatus.Failed,
                jobExit,
                machine.Name,
                $"exit {jobExit.ToString(CultureInfo.InvariantCulture)}");
        }

        // A job lanq stopped (timeout or window) may still be running on the worker, which enforces the limit only a margin later: stop it
        // first, so the fetch reads files that are no longer being written. Cleanup would kill it anyway, but only after the fetch.
        // A job whose ssh connection dropped may also still be running there: it is stopped the same way, on a fresh connection (the caller's
        // cleanup would kill it on one anyway). When the machine does not answer that either, nothing is fetched and the report says why.
        var dropped = sshFailed && !run.TimedOut;
        if (req.Fetch is not null && (dropped || result.Status is JobStatus.TimedOut or JobStatus.WindowClosed))
        {
            onLine($"[lanq] {machine.Name}: stopping the job before fetching its files");
            var stop = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.StopJob(machine, jobId)), null, StopBeforeFetchTimeout, ct).ConfigureAwait(false);
            if (dropped && stop.IsSshFailure)
            {
                var lost = new FetchReport(Path.GetFullPath(Path.Combine(req.Fetch.OutputRoot, jobId)), [], [], "not fetched: the SSH connection failed during the job, and the machine did not answer afterwards");
                Summarise(lost, result, onLine);
                return result with { Fetch = lost };
            }
        }

        // The job ran (completed, failed, or was stopped): bring back what it produced, before the caller's cleanup deletes the job folder.
        return req.Fetch is { } fetch
            ? result with { Fetch = await FetchFiles(jobId, req.Directory, fetch, machine, windowEnds, grace, result, onLine, ct).ConfigureAwait(false) }
            : result;
    }

    /// <summary>
    /// Uploads the payload to the machine and prepares the job's working folder there: the step's result, or a finished job result when
    /// nothing could be sent (a bundle that could not be built).
    /// </summary>
    private async Task<(SshResult? Step, JobResult? Failed)> Deliver(
        string jobId, PreparedPayload prepared, string? archivePath, Machine machine, Action<string> onLine, CancellationToken ct)
    {
        var (payload, bundles) = (prepared.Payload, prepared.Bundles);
        var git = machine.Transfer == TransferMode.Git;
        var local = archivePath;
        string? incrementalBase = null;
        if (git && payload.RepoId is not null)
        {
            try
            {
                var known = await BaseStoreCall(() => bases.Get(machine.Name, payload.RepoId), "read", machine, onLine, ct).ConfigureAwait(false);
                var bundle = await bundles.GetAsync(known, ct).ConfigureAwait(false);
                (local, incrementalBase) = (bundle.Path, bundle.Base);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message));
            }
        }

        var step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.EnsureIncoming(machine)), onLine, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (step.Ok && local is null)
        {
            return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, $"No {machine.Transfer.ToString().ToLowerInvariant()} payload was built for this machine."));
        }

        if (step.Ok)
        {
            step = await UploadAndPrepare(jobId, prepared, machine, local!, onLine, ct).ConfigureAwait(false);
        }

        // An incremental bundle the worker cannot use (its cache lost the base) is replaced by a full bundle, once.
        if (!step.Ok && incrementalBase is not null && step.Output.Contains(RemoteScripts.BundleNotUsable, StringComparison.Ordinal))
        {
            onLine($"[lanq] {machine.Name}: the worker cache does not have the incremental base; sending a full bundle");
            try
            {
                local = (await bundles.GetAsync(null, ct).ConfigureAwait(false)).Path;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message));
            }

            step = await UploadAndPrepare(jobId, prepared, machine, local, onLine, ct).ConfigureAwait(false);
        }

        if (step.Ok && git)
        {
            // The worker has fetched the snapshot, so HEAD (its parent, or the snapshot itself) is in its cache: the next bundle can stop there.
            await BaseStoreCall(() => { bases.Set(machine.Name, payload.RepoId!, payload.Head ?? payload.Sha); return null; }, "record", machine, onLine, ct).ConfigureAwait(false);
        }

        return (step, null);
    }

    /// <summary>
    /// lanq run --fetch. Runs the fetch script with its own timeout (<see cref="FetchTimeout"/>, cut to the machine's window end + grace +
    /// <see cref="FetchWindowMargin"/>) and writes the files through a <see cref="FetchReceiver"/>. Every problem ends up in the report;
    /// nothing here changes the job's own result or exit code.
    /// </summary>
    private async Task<FetchReport> FetchFiles(
        string jobId, string directory, FetchRequest fetch, Machine machine, DateTimeOffset? windowEnds, TimeSpan grace, JobResult job, Action<string> onLine, CancellationToken ct)
    {
        var dir = Path.Combine(fetch.OutputRoot, jobId);
        var timeout = FetchTimeout;
        if (windowEnds is { } end)
        {
            var left = end + grace + FetchWindowMargin - clock.GetUtcNow();
            if (left < MinFetchTime)
            {
                var late = new FetchReport(Path.GetFullPath(dir), [], [], $"not fetched: {machine.Name}'s allowed hours (plus grace) have ended");
                Summarise(late, job, onLine);
                return late;
            }

            timeout = left < timeout ? left : timeout;
        }

        FetchReport report;
        using (var receiver = new FetchReceiver(dir, fetch.Patterns, fetch.Limits, ignoreCase: machine.Os == MachineOs.Windows, anchor: fetch.Anchor ?? fetch.OutputRoot))
        {
            var script = FetchScripts.Fetch(machine, jobId, directory, fetch.Patterns, fetch.Limits);
            // Only stdout carries frames; stderr (a shell or PowerShell warning) is kept apart, bounded and sanitised, for the summary.
            var r = machine.Os == MachineOs.Windows
                ? await ssh.RunSplitAsync(machine, FetchScripts.WindowsStdinCommand, FetchScripts.WindowsStdin(script), receiver.OnLine, receiver.OnStderr, timeout, ct).ConfigureAwait(false)
                : await ssh.RunSplitAsync(machine, RemoteScripts.Wrap(machine, script), null, receiver.OnLine, receiver.OnStderr, timeout, ct).ConfigureAwait(false);
            var problem = r.TimedOut ? "fetch stopped after " + ProcessSshClient.Describe(timeout) + " (incomplete)"
                : r.IsSshFailure && !receiver.Done ? "the SSH connection failed during the fetch (incomplete)"
                : null;
            report = receiver.Finish(problem);
        }

        Summarise(report, job, onLine);
        return report;
    }

    private static void Summarise(FetchReport report, JobResult job, Action<string> onLine)
    {
        const int Shown = 20;
        var after = job.Status switch
        {
            JobStatus.Completed => "completed",
            JobStatus.Failed => $"failed (exit {job.ExitCode.ToString(CultureInfo.InvariantCulture)})",
            JobStatus.TimedOut => "timed out",
            JobStatus.InfraError => "lost its ssh connection",
            _ => "was stopped at the end of its machine's allowed hours",
        };
        onLine($"[lanq] fetch (the job {after}): {report.Fetched.Count.ToString(CultureInfo.InvariantCulture)} file(s), {Size(report.Bytes)} -> {report.Directory}");
        foreach (var f in report.Fetched.Take(Shown))
        {
            onLine($"[lanq]   fetched {f.Path} ({Size(f.Bytes)})");
        }

        if (report.Fetched.Count > Shown)
        {
            onLine($"[lanq]   ... and {(report.Fetched.Count - Shown).ToString(CultureInfo.InvariantCulture)} more fetched");
        }

        foreach (var s in report.Skipped.Take(Shown))
        {
            onLine($"[lanq]   skipped {s.Path}: {s.Reason}");
        }

        var more = report.Skipped.Count - Math.Min(Shown, report.Skipped.Count) + report.MoreSkipped;
        if (more > 0)
        {
            onLine($"[lanq]   ... and {more.ToString(CultureInfo.InvariantCulture)} more skipped");
        }

        if (report.Error is not null)
        {
            onLine("[lanq] fetch problem: " + report.Error);
        }

        foreach (var e in report.Stderr)
        {
            onLine("[lanq] fetch stderr: " + e);
        }
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };

    /// <summary>
    /// bases.json can be briefly locked by a parallel lanq (a Windows sharing violation) or unwritable. A failing read or write is retried
    /// twice, then reported in one line and ignored: losing a base only means the next job sends a full bundle.
    /// </summary>
    private async Task<string?> BaseStoreCall(Func<string?> call, string verb, Machine machine, Action<string> onLine, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    onLine($"[lanq] warning: could not {verb} the incremental-bundle base for {machine.Name} ({ex.Message}); "
                        + (verb == "read" ? "sending a full bundle" : "the next job there sends a full bundle"));
                    return null;
                }

                await delay(TimeSpan.FromMilliseconds(100 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<SshResult> UploadAndPrepare(string jobId, PreparedPayload prepared, Machine machine, string local, Action<string> onLine, CancellationToken ct)
    {
        var step = await ssh.UploadAsync(machine, local, RemoteScripts.Incoming(machine, jobId), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        // The bundle carries the snapshot as refs/lanq/<payload id> (shared by every shard of one run); the job's own ref is refs/lanq/<job id>.
        return step.Ok
            ? await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Prepare(machine, jobId, prepared.Payload.RepoId, prepared.Id)), onLine, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false)
            : step;
    }

    /// <summary>Per machine, one repository-cache step at a time within this lanq (upload and prepare, and cleanup); dispose to release.</summary>
    private async Task<IDisposable> CacheGate(Machine machine, CancellationToken ct)
    {
        var gate = _cacheGates.GetOrAdd(machine.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Release(gate);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _cacheGates = new(StringComparer.Ordinal);

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Remote output is untrusted: C0 controls (except tab), DEL, C1 controls, bidi controls and zero-width characters become '?', so a
    /// worker cannot move the cursor, retitle the window, send other terminal escape sequences or disguise text in the console or job log.
    /// </summary>
    internal static string SafeLine(string line)
    {
        static bool Unsafe(char c) =>
            (c < 0x20 && c != (char)0x09) || (c >= 0x7f && c <= 0x9f) // C0 (except tab), DEL, C1
            || (c >= 0x200b && c <= 0x200f) || c == 0xfeff // zero-width characters and marks
            || (c >= 0x202a && c <= 0x202e) || (c >= 0x2066 && c <= 0x2069) // bidi embeddings, overrides and isolates
            || c == 0x061c // Arabic letter mark (a bidi mark)
            || c == 0x2028 || c == 0x2029 // line and paragraph separators (a line break to some terminals and editors)
            || (c >= 0x2060 && c <= 0x2064); // word joiner and invisible operators
        if (!line.Any(Unsafe))
        {
            return line;
        }

        return string.Create(line.Length, line, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                span[i] = Unsafe(src[i]) ? '?' : src[i];
            }
        });
    }

    internal static string? ParseSlot(string output)
    {
        var line = output.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith("SLOT=", StringComparison.Ordinal));
        var value = line?["SLOT=".Length..];
        return value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool IsRepoId(string id)
    {
        try
        {
            return RemoteScripts.SafeRepoId(id) == id;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static JobResult Rejected(string jobId, IReadOnlyList<SecretFinding> findings)
    {
        const int Shown = 10;
        var listed = string.Join(", ", findings.Take(Shown).Select(f => $"{f.Rule} in {f.Where}"))
            + (findings.Count > Shown ? $", and {findings.Count - Shown} more" : "");
        var hint = findings.Any(f => f.Rule == SecretScanner.CommittedFileRule)
            ? " Committed files named like secrets are refused because the bundle or archive would carry them; rename samples to *.example or keep them under a fixtures/ or testdata/ folder."
            : "";
        return new(jobId, JobStatus.Rejected, 2, null, "Refused: possible secrets in payload (" + listed + ")." + hint + " Nothing was sent.");
    }

    /// <summary>
    /// Builds the archive at most once per payload, on first use (several shards may ask at once), and reports what a tar.gz could not carry.
    /// Builds run one at a time with the bundle builds (<paramref name="builds"/>). A build that failed is tried once more on the next request.
    /// </summary>
    private sealed class LazyArchive(IPayloadBuilder builder, string jobId, string repoDir, string dir, string sha, Action<string> output, SemaphoreSlim builds)
    {
        private readonly Lock _gate = new();
        private Task<ArchiveBuild>? _task;
        private int _started;
        private bool _reportedFailure;
        private volatile string? _failure;

        public string? Failure { get => _failure; set => _failure = value; }

        /// <summary>True the first time only: an archive failure is reported once, whichever job hit it first.</summary>
        public bool MarkReported()
        {
            lock (_gate)
            {
                var first = !_reportedFailure;
                _reportedFailure = true;
                return first;
            }
        }

        public async Task<string> GetAsync(CancellationToken ct)
        {
            bool first;
            Task<ArchiveBuild> task;
            lock (_gate)
            {
                first = _task is null;
                if (_task is null || ((_task.IsFaulted || _task.IsCanceled) && _started < MaxBuilds))
                {
                    _started++;
                    _task = Serialised(builds, () => builder.BuildArchiveAsync(jobId, repoDir, dir, sha, ct), ct);
                }

                task = _task;
            }

            var built = await task.ConfigureAwait(false);
            if (first && (built.SkippedSymlinks > 0 || built.SkippedSubmodules > 0))
            {
                output($"[lanq] archive: skipped {built.SkippedSymlinks} symlink(s) and {built.SkippedSubmodules} submodule(s) (not sent in archive mode)");
            }

            return built.Path;
        }

        public async Task DeleteAsync()
        {
            Task<ArchiveBuild>? task;
            lock (_gate)
            {
                task = _task;
            }

            if (task is null)
            {
                return;
            }

            try
            {
                TryDelete((await task.ConfigureAwait(false)).Path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                // nothing was built
            }
        }
    }

    /// <summary>
    /// Builds each distinct bundle (full, or incremental on one base) at most once per payload, on first use (several shards may ask at once).
    /// Builds run one at a time (<paramref name="builds"/>, shared with the archive): each bundle build creates and deletes the payload's
    /// local ref refs/lanq/&lt;payload id&gt;, so two at once would race on it. A build that failed is tried once more on the next request.
    /// </summary>
    private sealed class LazyBundles(IPayloadBuilder builder, string jobId, string repoDir, string dir, string sha, SemaphoreSlim builds)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (Task<BundleBuild> Task, int Started)> _built = new(StringComparer.Ordinal);

        public Task<BundleBuild> GetAsync(string? baseSha, CancellationToken ct)
        {
            var key = baseSha ?? "";
            lock (_gate)
            {
                if (!_built.TryGetValue(key, out var entry) || ((entry.Task.IsFaulted || entry.Task.IsCanceled) && entry.Started < MaxBuilds))
                {
                    entry = (Serialised(builds, () => builder.BuildBundleAsync(jobId, repoDir, dir, sha, baseSha, ct), ct), entry.Started + 1);
                    _built[key] = entry;
                }

                return entry.Task;
            }
        }

        public async Task DeleteAsync()
        {
            List<Task<BundleBuild>> built;
            lock (_gate)
            {
                built = [.. _built.Values.Select(v => v.Task)];
            }

            foreach (var task in built)
            {
                try
                {
                    TryDelete((await task.ConfigureAwait(false)).Path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
                {
                    // nothing was built
                }
            }
        }
    }

    /// <summary>A failed bundle or archive build is tried at most this many times in all (once more after the first failure).</summary>
    private const int MaxBuilds = 2;

    /// <summary>Runs one build while holding <paramref name="gate"/>: bundle and archive builds of one payload never overlap.</summary>
    private static async Task<T> Serialised<T>(SemaphoreSlim gate, Func<Task<T>> build, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await build().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string NewId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
