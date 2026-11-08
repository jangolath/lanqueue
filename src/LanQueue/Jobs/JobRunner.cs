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
}

public sealed record JobRequest(IReadOnlyList<string> Needs, IReadOnlyList<string> Argv, string Directory, string RepoDir, TimeSpan Wait);

public sealed record JobResult(string JobId, JobStatus Status, int ExitCode, string? Machine, string Message)
{
    /// <summary>Process exit code for the CLI: the remote code on completion/failure, 75 for no capacity, 2 for rejected, 70 for infra.</summary>
    public int CliExitCode => Status switch
    {
        JobStatus.Completed or JobStatus.Failed => ExitCode,
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
    Action<string> output)
{
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

        JobPayload payload;
        try
        {
            payload = await payloads.BuildAsync(jobId, req.RepoDir, scratchDir, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new JobResult(jobId, JobStatus.InfraError, 70, null, "Could not snapshot the repository: " + ex.Message);
        }

        try
        {
            var findings = SecretScanner.ScanText(payload.OverlayDiff, "uncommitted changes")
                .Concat(SecretScanner.ScanFileNames(payload.ChangedFiles))
                .ToList();
            if (findings.Count > 0)
            {
                return Rejected(jobId, findings);
            }

            return await Dispatch(jobId, req, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(payload.BundlePath);
        }
    }

    private async Task<JobResult> Dispatch(string jobId, JobRequest req, JobPayload payload, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var health = new List<(Machine, MachineHealth)>();
            foreach (var m in config.Machines)
            {
                health.Add((m, await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false)));
            }

            foreach (var machine in Scheduler.Rank(health, req.Needs))
            {
                var result = await TryOnMachine(jobId, req, payload, machine, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }

            if (deadline.Elapsed >= req.Wait)
            {
                var held = string.Join("; ", health.Select(h => $"{h.Item1.Name}: {h.Item2.Hold ?? h.Item2.Error ?? "no match / full"}"));
                return new JobResult(jobId, JobStatus.NoCapacity, 75, null, "No eligible machine. " + held);
            }

            output("[lanq] no eligible machine; waiting 15s");
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Returns null when the machine had no free slot (caller tries the next one).</summary>
    private async Task<JobResult?> TryOnMachine(string jobId, JobRequest req, JobPayload payload, Machine machine, CancellationToken ct)
    {
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
        await using var logFile = new StreamWriter(logPath, append: false);
        void OnLine(string line)
        {
            logFile.WriteLine(line);
            output(line);
        }

        JobResult result;
        try
        {
            result = await Transfer(jobId, req, payload, machine, OnLine, ct).ConfigureAwait(false);
        }
        finally
        {
            await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Cleanup(machine, jobId, slot)), null, TimeSpan.FromSeconds(60), CancellationToken.None)
                .ConfigureAwait(false);
        }

        log.Append(new JobRecord(jobId, machine.Name, command, started, Math.Round(clock.Elapsed.TotalSeconds, 2), result.ExitCode, result.Status.ToString()));
        return result;
    }

    private async Task<JobResult> Transfer(string jobId, JobRequest req, JobPayload payload, Machine machine, Action<string> onLine, CancellationToken ct)
    {
        var step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.EnsureIncoming(machine)), onLine, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (step.Ok)
        {
            step = await ssh.UploadAsync(machine, payload.BundlePath, RemoteScripts.Incoming(machine, jobId), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        }

        if (step.Ok)
        {
            step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Prepare(machine, jobId)), onLine, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }

        if (!step.Ok)
        {
            return new JobResult(jobId, JobStatus.InfraError, step.ExitCode, machine.Name, "Could not prepare the job on the machine.");
        }

        var run = await ssh.RunAsync(
            machine,
            RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv)),
            onLine,
            TimeSpan.FromHours(4),
            ct).ConfigureAwait(false);
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

    private static string NewId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    private static void TryDelete(string path)
    {
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
