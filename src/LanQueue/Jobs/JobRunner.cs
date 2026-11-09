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

public sealed class JobRunner(
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
        if (!windows.Ok && !config.Machines.Any(m => m.Os != MachineOs.Windows && HasCapabilities(m, req.Needs)))
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, null, windows.Reason!);
        }

        JobPayload payload;
        try
        {
            // Only the snapshot is built up front; a bundle or archive is built lazily, when a machine of that kind actually gets the job.
            var kinds = config.Machines.Any(m => m.Transfer == TransferMode.Git) ? PayloadKinds.Bundle : PayloadKinds.None;
            payload = await payloads.BuildAsync(jobId, req.RepoDir, scratchDir, kinds, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new JobResult(jobId, JobStatus.InfraError, 70, null, "Could not snapshot the repository: " + ex.Message);
        }

        var archive = new LazyArchive(payloads, jobId, req.RepoDir, scratchDir, payload.Sha, output);
        var bundles = new LazyBundles(payloads, jobId, req.RepoDir, scratchDir, payload.Sha);
        try
        {
            var findings = SecretScanner.ScanText(payload.OverlayDiff, "uncommitted changes")
                .Concat(SecretScanner.ScanFileNames(payload.ChangedFiles))
                .Concat(SecretScanner.ScanCommittedFileNames(payload.CommittedFiles ?? []))
                .Concat((payload.CommittedConfigs ?? new Dictionary<string, string?>()).SelectMany(c => SecretScanner.ScanCommittedConfig(c.Key, c.Value)))
                .ToList();
            if (findings.Count > 0)
            {
                return Rejected(jobId, findings);
            }

            return await Dispatch(jobId, req, payload, archive, bundles, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(payload.ArchivePath);
            await bundles.DeleteAsync().ConfigureAwait(false);
            await archive.DeleteAsync().ConfigureAwait(false);
        }
    }

    internal const string WindowsUnsafeHold = "arguments not safe for Windows cmd shims";

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

    /// <summary>lanq run --mem: a machine must report at least that much available memory; one that cannot report it is not trusted.</summary>
    private static MachineHealth WithMemRequirement(MachineHealth h, double? needGb) =>
        needGb is not { } need || !h.Reachable || h.Hold is not null || h.MemFreeGb >= need
            ? h
            : h with
            {
                HoldKind = HoldKind.Job,
                Hold = h.MemFreeGb is { } free
                    ? string.Create(CultureInfo.InvariantCulture, $"{free:0.0} GB free memory, --mem needs {need:0.0} GB")
                    : "free memory unknown (--mem needs the machine to report it)",
            };

    private static bool HasCapabilities(Machine m, IReadOnlyList<string> needs) =>
        needs.All(n => m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase));

    private async Task<JobResult> Dispatch(string jobId, JobRequest req, JobPayload payload, LazyArchive archive, LazyBundles bundles, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var windowsUnsafe = !JobPolicy.ValidateForWindows(req.Argv).Ok;
        // Static property of the config: can any git-transfer machine ever take this job (capabilities only; load, hours and slots are temporary)?
        var gitCanEverTakeIt = config.Machines.Any(m => m.Transfer == TransferMode.Git
            && req.Needs.All(n => m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase)));
        while (true)
        {
            // Probe every machine at once; WhenAll keeps the config order whatever order the answers arrive in.
            var probes = await Task.WhenAll(config.Machines.Select(m => probe.ProbeAsync(m, config.Policy, ct))).ConfigureAwait(false);
            var health = config.Machines.Zip(probes)
                .Select(h => windowsUnsafe && h.First.Os == MachineOs.Windows ? (h.First, h.Second with { Hold = WindowsUnsafeHold, HoldKind = HoldKind.Job }) : h)
                .Select(h => (h.First, WithMemRequirement(h.Item2, req.MinMemGb)))
                .ToList();

            var ranked = Scheduler.Rank(health, req.Needs).ToList();
            foreach (var machine in ranked)
            {
                var windowEnds = health.First(h => ReferenceEquals(h.Item1, machine)).Item2.WindowEndsUtc;
                var result = await TryOnMachine(jobId, req, payload, archive, bundles, machine, windowEnds, gitCanEverTakeIt, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }

            // Wake-on-LAN last resort: only when no awake machine can take the job now, only with a real --wait to wait in (a pending wake is
            // always inside that wait, so --wait 0 never blocks on one), and only one machine, the best-ranked sleeper.
            if (ranked.Count == 0 && req.Wait > TimeSpan.Zero && !req.NoWake && probe.Waker is { AnyPending: false })
            {
                await WakeBestSleeperAsync(req, probes, health, windowsUnsafe, archive, ct).ConfigureAwait(false);
            }

            if (deadline.Elapsed >= req.Wait)
            {
                var held = string.Join("; ", health.Select(h => $"{h.Item1.Name}: {h.Item2.Hold ?? h.Item2.Error ?? "no match / full"}"));
                var archiveNote = archive.Failure is null ? "" : " Archive build failed: " + archive.Failure;
                return new JobResult(jobId, JobStatus.NoCapacity, 75, null, "No eligible machine. " + held + archiveNote);
            }

            output("[lanq] no eligible machine; waiting 15s");
            await delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wakes at most one machine: the first by name among those that look asleep, have a wake: setting, are inside their hours and could take
    /// the job by everything knowable without a probe (capabilities, Windows-safe arguments, archive build). A sleeper's memory and load are
    /// unknown, so they are not judged. The decision uses the raw probe results (not the job-adjusted holds in <paramref name="health"/>).
    /// </summary>
    private async Task WakeBestSleeperAsync(
        JobRequest req, MachineHealth[] probes, List<(Machine, MachineHealth)> health, bool windowsUnsafe, LazyArchive archive, CancellationToken ct)
    {
        foreach (var i in Enumerable.Range(0, config.Machines.Count).OrderBy(i => config.Machines[i].Name, StringComparer.Ordinal))
        {
            var m = config.Machines[i];
            if (m.Wake is null || !probes[i].LooksAsleep || !HasCapabilities(m, req.Needs)
                || (windowsUnsafe && m.Os == MachineOs.Windows) || (m.Transfer == TransferMode.Archive && archive.Failure is not null))
            {
                continue;
            }

            var after = await probe.WakeAsync(m, config.Policy, probes[i], wait: false, ct).ConfigureAwait(false);
            if (ReferenceEquals(after, probes[i]))
            {
                continue; // not allowed now (outside its hours, already woken)
            }

            health[i] = (m, after);
            if (probe.Waker!.AnyPending)
            {
                output($"[lanq] no awake machine can take the job; sent a Wake-on-LAN packet to {m.Name} (waiting up to {m.Wake.WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s, within --wait)");
                return;
            }
        }
    }

    /// <summary>Returns null when the machine had no free slot (caller tries the next one).</summary>
    private async Task<JobResult?> TryOnMachine(
        string jobId, JobRequest req, JobPayload payload, LazyArchive archive, LazyBundles bundles, Machine machine, DateTimeOffset? windowEnds, bool gitCanEverTakeIt, CancellationToken ct)
    {
        string? archivePath = null;
        if (machine.Transfer == TransferMode.Archive)
        {
            try
            {
                archivePath = await archive.GetAsync(ct).ConfigureAwait(false);
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

                if (!archive.ReportedFailure)
                {
                    archive.ReportedFailure = true;
                    output($"[lanq] {machine.Name}: {message}");
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

        output($"[lanq] job {jobId} -> {machine.Name} (slot {slot})");
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
                output(line);
            }
        }

        try
        {
            JobResult result;
            try
            {
                result = await Transfer(jobId, req, payload, archivePath, bundles, machine, windowEnds, OnLine, ct).ConfigureAwait(false);
            }
            finally
            {
                // Never throw from here (that would leak the slot): an unusable repository id only drops the cache steps.
                var repoId = payload.RepoId is { } id && IsRepoId(id) ? id : null;
                var cleanup = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Cleanup(machine, jobId, slot, repoId)), null, TimeSpan.FromSeconds(60), CancellationToken.None)
                    .ConfigureAwait(false);
                if (!cleanup.Ok)
                {
                    output($"[lanq] warning: cleanup on {machine.Name} did not finish; check {machine.WorkRoot}/jobs/{jobId}");
                }
            }

            log.Append(new JobRecord(jobId, machine.Name, command, started, Math.Round(clock.Elapsed.TotalSeconds, 2), result.ExitCode, result.Status.ToString()));
            return result;
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
        string jobId, JobRequest req, JobPayload payload, string? archivePath, LazyBundles bundles, Machine machine, DateTimeOffset? windowEnds, Action<string> onLine, CancellationToken ct)
    {
        string executeCommand;
        try
        {
            // Built once up front only to refuse a command that cannot run on this machine before anything is uploaded.
            executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, req.Timeout ?? JobRequest.DefaultTimeout));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, machine.Name, ex.Message);
        }

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
                return new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message);
            }
        }

        var step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.EnsureIncoming(machine)), onLine, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (step.Ok && local is null)
        {
            return new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, $"No {machine.Transfer.ToString().ToLowerInvariant()} payload was built for this machine.");
        }

        if (step.Ok)
        {
            step = await UploadAndPrepare(jobId, payload, machine, local!, onLine, ct).ConfigureAwait(false);
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
                return new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message);
            }

            step = await UploadAndPrepare(jobId, payload, machine, local, onLine, ct).ConfigureAwait(false);
        }

        if (step.Ok && git)
        {
            // The worker has fetched the snapshot, so HEAD (its parent, or the snapshot itself) is in its cache: the next bundle can stop there.
            await BaseStoreCall(() => { bases.Set(machine.Name, payload.RepoId!, payload.Head ?? payload.Sha); return null; }, "record", machine, onLine, ct).ConfigureAwait(false);
        }

        if (!step.Ok)
        {
            return new JobResult(jobId, JobStatus.InfraError, step.ExitCode, machine.Name,
                "Could not prepare the job on the machine." + (step.TimedOut ? " (timed out)" : ""));
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
        executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, timeout + WorkerDeadlineMargin));
        var run = await ssh.RunAsync(
            machine,
            executeCommand,
            onLine,
            timeout,
            ct).ConfigureAwait(false);
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
        else if (run.IsSshFailure)
        {
            result = new JobResult(jobId, JobStatus.InfraError, run.ExitCode, machine.Name, "SSH connection failed while running the job.");
        }
        else
        {
            result = new JobResult(
                jobId,
                run.Ok ? JobStatus.Completed : JobStatus.Failed,
                run.ExitCode,
                machine.Name,
                $"exit {run.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        // A job lanq stopped (timeout or window) may still be running on the worker, which enforces the limit only a margin later: stop it
        // first, so the fetch reads files that are no longer being written. Cleanup would kill it anyway, but only after the fetch.
        // A job whose ssh connection dropped may also still be running there: it is stopped the same way, on a fresh connection (the caller's
        // cleanup would kill it on one anyway). When the machine does not answer that either, nothing is fetched and the report says why.
        var dropped = run.IsSshFailure && !run.TimedOut;
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

    private async Task<SshResult> UploadAndPrepare(string jobId, JobPayload payload, Machine machine, string local, Action<string> onLine, CancellationToken ct)
    {
        var step = await ssh.UploadAsync(machine, local, RemoteScripts.Incoming(machine, jobId), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        return step.Ok
            ? await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Prepare(machine, jobId, payload.RepoId)), onLine, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false)
            : step;
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

    /// <summary>Builds the archive at most once per job, on first use, and reports what a tar.gz could not carry.</summary>
    private sealed class LazyArchive(IPayloadBuilder builder, string jobId, string repoDir, string dir, string sha, Action<string> output)
    {
        private Task<ArchiveBuild>? _task;

        public bool ReportedFailure { get; set; }

        public string? Failure { get; set; }

        public async Task<string> GetAsync(CancellationToken ct)
        {
            var first = _task is null;
            _task ??= builder.BuildArchiveAsync(jobId, repoDir, dir, sha, ct);
            var built = await _task.ConfigureAwait(false);
            if (first && (built.SkippedSymlinks > 0 || built.SkippedSubmodules > 0))
            {
                output($"[lanq] archive: skipped {built.SkippedSymlinks} symlink(s) and {built.SkippedSubmodules} submodule(s) (not sent in archive mode)");
            }

            return built.Path;
        }

        public async Task DeleteAsync()
        {
            if (_task is null)
            {
                return;
            }

            try
            {
                TryDelete((await _task.ConfigureAwait(false)).Path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
            {
                // nothing was built
            }
        }
    }

    /// <summary>Builds each distinct bundle (full, or incremental on one base) at most once per job, on first use.</summary>
    private sealed class LazyBundles(IPayloadBuilder builder, string jobId, string repoDir, string dir, string sha)
    {
        private readonly Dictionary<string, Task<BundleBuild>> _built = new(StringComparer.Ordinal);

        public Task<BundleBuild> GetAsync(string? baseSha, CancellationToken ct)
        {
            var key = baseSha ?? "";
            if (!_built.TryGetValue(key, out var task))
            {
                task = builder.BuildBundleAsync(jobId, repoDir, dir, sha, baseSha, ct);
                _built[key] = task;
            }

            return task;
        }

        public async Task DeleteAsync()
        {
            foreach (var task in _built.Values)
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
