using System.Diagnostics;
using System.Globalization;
using LanQueue.Config;
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
public sealed record JobRequest(
    IReadOnlyList<string> Needs, IReadOnlyList<string> Argv, string Directory, string RepoDir, TimeSpan Wait, TimeSpan? Timeout = null, double? MinMemGb = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(4);
}

public sealed record JobResult(string JobId, JobStatus Status, int ExitCode, string? Machine, string Message)
{
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
    TimeProvider? clock = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

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
            // Only the bundle is built up front; an archive is built lazily, the first time an archive machine actually gets the job.
            var kinds = config.Machines.Any(m => m.Transfer == TransferMode.Git) ? PayloadKinds.Bundle : PayloadKinds.None;
            payload = await payloads.BuildAsync(jobId, req.RepoDir, scratchDir, kinds, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new JobResult(jobId, JobStatus.InfraError, 70, null, "Could not snapshot the repository: " + ex.Message);
        }

        var archive = new LazyArchive(payloads, jobId, req.RepoDir, scratchDir, payload.Sha, output);
        try
        {
            var findings = SecretScanner.ScanText(payload.OverlayDiff, "uncommitted changes")
                .Concat(SecretScanner.ScanFileNames(payload.ChangedFiles))
                .ToList();
            if (findings.Count > 0)
            {
                return Rejected(jobId, findings);
            }

            return await Dispatch(jobId, req, payload, archive, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(payload.BundlePath);
            TryDelete(payload.ArchivePath);
            await archive.DeleteAsync().ConfigureAwait(false);
        }
    }

    internal const string WindowsUnsafeHold = "arguments not safe for Windows cmd shims";

    /// <summary>lanq run --mem: a machine must report at least that much available memory; one that cannot report it is not trusted.</summary>
    private static MachineHealth WithMemRequirement(MachineHealth h, double? needGb) =>
        needGb is not { } need || !h.Reachable || h.Hold is not null || h.MemFreeGb >= need
            ? h
            : h with
            {
                Hold = h.MemFreeGb is { } free
                    ? string.Create(CultureInfo.InvariantCulture, $"{free:0.0} GB free memory, --mem needs {need:0.0} GB")
                    : "free memory unknown (--mem needs the machine to report it)",
            };

    private static bool HasCapabilities(Machine m, IReadOnlyList<string> needs) =>
        needs.All(n => m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase));

    private async Task<JobResult> Dispatch(string jobId, JobRequest req, JobPayload payload, LazyArchive archive, CancellationToken ct)
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
                .Select(h => windowsUnsafe && h.First.Os == MachineOs.Windows ? (h.First, h.Second with { Hold = WindowsUnsafeHold }) : h)
                .Select(h => (h.First, WithMemRequirement(h.Item2, req.MinMemGb)))
                .ToList();

            var ranked = Scheduler.Rank(health, req.Needs).ToList();
            foreach (var machine in ranked)
            {
                var windowEnds = health.First(h => ReferenceEquals(h.Item1, machine)).Item2.WindowEndsUtc;
                var result = await TryOnMachine(jobId, req, payload, archive, machine, windowEnds, gitCanEverTakeIt, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
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

    /// <summary>Returns null when the machine had no free slot (caller tries the next one).</summary>
    private async Task<JobResult?> TryOnMachine(
        string jobId, JobRequest req, JobPayload payload, LazyArchive archive, Machine machine, DateTimeOffset? windowEnds, bool gitCanEverTakeIt, CancellationToken ct)
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

        var acquire = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.AcquireSlot(machine, jobId)), null, TimeSpan.FromSeconds(30), ct)
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
                result = await Transfer(jobId, req, payload, archivePath, machine, windowEnds, OnLine, ct).ConfigureAwait(false);
            }
            finally
            {
                var cleanup = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Cleanup(machine, jobId, slot)), null, TimeSpan.FromSeconds(60), CancellationToken.None)
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
        string jobId, JobRequest req, JobPayload payload, string? archivePath, Machine machine, DateTimeOffset? windowEnds, Action<string> onLine, CancellationToken ct)
    {
        string executeCommand;
        try
        {
            executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, machine.Name, ex.Message);
        }

        var step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.EnsureIncoming(machine)), onLine, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var local = machine.Transfer == TransferMode.Archive ? archivePath : payload.BundlePath;
        if (step.Ok && local is null)
        {
            return new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, $"No {machine.Transfer.ToString().ToLowerInvariant()} payload was built for this machine.");
        }

        if (step.Ok)
        {
            step = await ssh.UploadAsync(machine, local!, RemoteScripts.Incoming(machine, jobId), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        }

        if (step.Ok)
        {
            step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Prepare(machine, jobId)), onLine, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
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
        if (byWindow)
        {
            timeout = windowLimit > TimeSpan.FromSeconds(1) ? windowLimit.Value : TimeSpan.FromSeconds(1);
            onLine($"[lanq] {machine.Name}: allowed hours end soon; the job will be stopped after {ProcessSshClient.Describe(timeout)} (window end + {grace.TotalMinutes:0} min grace)");
        }

        var run = await ssh.RunAsync(
            machine,
            executeCommand,
            onLine,
            timeout,
            ct).ConfigureAwait(false);
        if (run.TimedOut && byWindow)
        {
            return new JobResult(jobId, JobStatus.WindowClosed, ProcessSshClient.TimedOutExitCode, machine.Name,
                $"stopped after {ProcessSshClient.Describe(timeout)}: {machine.Name}'s allowed hours ended (plus {grace.TotalMinutes:0} min grace)");
        }

        if (run.TimedOut)
        {
            return new JobResult(jobId, JobStatus.TimedOut, ProcessSshClient.TimedOutExitCode, machine.Name, "timed out after " + ProcessSshClient.Describe(timeout));
        }

        if (run.IsSshFailure)
        {
            return new JobResult(jobId, JobStatus.InfraError, run.ExitCode, machine.Name, "SSH connection failed while running the job.");
        }

        return new JobResult(
            jobId,
            run.Ok ? JobStatus.Completed : JobStatus.Failed,
            run.ExitCode,
            machine.Name,
            $"exit {run.ExitCode.ToString(CultureInfo.InvariantCulture)}");
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
            || (c >= 0x202a && c <= 0x202e) || (c >= 0x2066 && c <= 0x2069); // bidi embeddings, overrides and isolates
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

    private static JobResult Rejected(string jobId, IEnumerable<SecretFinding> findings) =>
        new(jobId, JobStatus.Rejected, 2, null,
            "Refused: possible secrets in payload (" + string.Join(", ", findings.Select(f => $"{f.Rule} in {f.Where}")) + "). Nothing was sent.");

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
