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

/// <summary>Why a run was refused (Rejected, exit 2): what the caller can do about it differs (see Output/NextSteps.cs).</summary>
public enum RejectKind
{
    /// <summary>Not on policy.allowed_commands or allowed_directories, or a permission-skipping flag: for the developer, never worked around.</summary>
    Policy,

    /// <summary>Possible secrets in the command line or the uncommitted changes (nothing was sent).</summary>
    Secret,

    /// <summary>
    /// Arguments unsafe on Windows (cmd.exe could re-parse them) and only Windows machines have the capabilities, or a Windows worker found
    /// that the program given &amp; or | does not resolve to an .exe there (nothing was started).
    /// </summary>
    WindowsArgs,

    /// <summary>The command line itself is unusable (no command, control characters, bad lanq options, a command the machine cannot run).</summary>
    Arguments,

    /// <summary>The config could not be found or loaded.</summary>
    Config,

    /// <summary>
    /// lanq run --env: every capable machine is ruled out by the job's variables, at least one because they make its Windows command too
    /// long. The variables (lanq.yml env: for a plan job) or the machines must change.
    /// </summary>
    JobEnv,

    /// <summary>lanq run --plan: the plan file (lanq.yml) is missing or not valid.</summary>
    PlanFile,
}

/// <summary>Where an InfraError (exit 70) came from.</summary>
public enum InfraKind
{
    /// <summary>The repository could not be snapshotted (local git).</summary>
    Snapshot,

    /// <summary>A git bundle could not be built (local git).</summary>
    Bundle,

    /// <summary>The tar.gz archive could not be built (for example a path Windows cannot store) and no git machine can ever take the job.</summary>
    Archive,

    /// <summary>Upload or prepare on the worker failed (not a lost connection).</summary>
    Prepare,

    /// <summary>The worker stopped answering ssh during upload, prepare or the job (see <see cref="JobResult.MayStillRun"/>).</summary>
    Connection,

    /// <summary>Anything else (an unexpected exception).</summary>
    Unexpected,
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

    /// <summary>The command's identity recorded in jobs.jsonl (<see cref="JobHistory.Key"/>); null means the key of <see cref="Argv"/>. Shards carry their template's.</summary>
    public string? CommandKey { get; init; }

    /// <summary>The id every shard of one --shards run records (the run's payload id); null for a single run.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// lanq run --on: the only machines the job may use (names matched ignoring case; an unknown name refuses the run before any network),
    /// or null for every machine. It only narrows: every other rule (capabilities, size, --mem, holds, slots, Windows, wake) still applies.
    /// </summary>
    public IReadOnlyList<string>? On { get; init; }

    /// <summary>lanq run --size / --min-size: the machine size classes the job may run on, or null for any.</summary>
    public SizeRule? Size { get; init; }

    /// <summary>
    /// lanq run --plan: the lanq.yml job this request runs for, or null. Its lines are prefixed "[name machine] " (shards "[name i/N machine] "),
    /// the job gets LANQ_PLAN_JOB=name, and jobs.jsonl records it as planJob.
    /// </summary>
    public string? PlanJob { get; init; }

    /// <summary>lanq run --plan: the id every job of one plan run shares (jobs.jsonl planRun), or null.</summary>
    public string? PlanRun { get; init; }

    /// <summary>
    /// lanq run --plan: the wait gate every job of one plan shares (<see cref="Jobs.PlanProgress"/>): --wait then counts only while no job of the
    /// plan holds a slot, so a job waits behind its siblings even with --wait 0. Null for a plain run (--wait counts from the start).
    /// </summary>
    public PlanProgress? Progress { get; init; }

    /// <summary>lanq run --plan: called once the job holds its first slot or begins waiting for one (the next job's start waits for it).</summary>
    public Action? Placed { get; init; }

    /// <summary>
    /// lanq run --env (lanq.yml env:): the job's own variables, in the order given (parsed and deny-checked by <see cref="JobEnv.Parse"/>;
    /// policy.allowed_env and the secret scan are checked in <see cref="JobRunner"/>'s precheck; a machine whose env: sets one too is ruled
    /// out, see <see cref="JobRunner.EnvExclusion"/>). Shards and plan jobs carry their template's. jobs.jsonl and the JSON env field carry
    /// the names only.
    /// </summary>
    public IReadOnlyList<JobEnvVar> Env { get; init; } = [];

    /// <summary>
    /// lanq run --exclusive (lanq.yml exclusive: true): the job takes every slot of its machine (<see cref="RemoteScripts.AcquireAll"/>), so no
    /// other lanq job runs there while it does; it waits, like any busy machine, until every slot is free (no reservation). With --shards each
    /// shard takes a whole machine, so N is at most the machines that can take the job.
    /// </summary>
    public bool Exclusive { get; init; }

    /// <summary>
    /// lanq run --baseline / --baseline-ref (lanq.yml baseline:, baseline_ref:): when the job (a shard, a plan job) ends Failed, the same request
    /// runs again at the base commit (<see cref="BaseRef"/>) and its verdict is reported (information only: status and exit code stay the job's).
    /// </summary>
    public bool Baseline { get; init; }

    /// <summary>--baseline-ref: the branch, tag or commit to compare with (its merge-base with HEAD); null means the default branch.</summary>
    public string? BaselineRef { get; init; }

    /// <summary>A baseline rerun only: the id of the failed job it reruns (jobs.jsonl baselineOf; timing history leaves such records out).</summary>
    public string? BaselineOf { get; init; }

    /// <summary>A baseline rerun only: the base commit's short id (jobs.jsonl commit).</summary>
    public string? Commit { get; init; }

    /// <summary>Lines of this job are prefixed "[Label machine] " (a baseline rerun's "baseline 2/4"), instead of a plan job's name; null otherwise.</summary>
    public string? Label { get; init; }

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

    /// <summary>
    /// NoCapacity only: false when every machine has a reason waiting cannot cure (see <see cref="Eligibility.IsPermanent"/>), true when
    /// waiting or retrying may help (a busy, held or merely unreachable machine). Null for every other status.
    /// </summary>
    public bool? Retryable { get; init; }

    /// <summary>NoCapacity only: every machine that did not take the job and why, in config order. Null for every other status.</summary>
    public IReadOnlyList<ExcludedMachine>? Excluded { get; init; }

    /// <summary>The full path of the job's local log (see <see cref="JobLog.LogPathFor"/>), set once the job held a slot and the log was created; null otherwise.</summary>
    public string? LogPath { get; init; }

    /// <summary>Rejected only: why the run was refused. Null for every other status.</summary>
    public RejectKind? RejectKind { get; init; }

    /// <summary>InfraError only: where the error came from. Null for every other status.</summary>
    public InfraKind? InfraKind { get; init; }

    /// <summary>
    /// How long the job's own command ran: the ssh call that executes it, without the wait for a slot, upload, prepare, fetch or cleanup.
    /// Null when the command never started.
    /// </summary>
    public double? RunSeconds { get; init; }

    /// <summary>lanq run --baseline: the rerun at the base commit after this job ended Failed (never changes the status or exit code); else null.</summary>
    public BaselineResult? Baseline { get; init; }

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

/// <summary>One machine that did not take a job: its name, the reason code (missing_capability, busy, ...), the reason for people, and whether waiting can never help.</summary>
public sealed record ExcludedMachine(string Machine, string Reason, string Why, bool Permanent);

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

    private volatile string? _startedLogPath;

    /// <summary>
    /// The full path of the log of the job this runner started last (set as soon as the log file exists), or null when none was started:
    /// a single run that was cancelled or broke after its job held a slot still has a log to point at.
    /// </summary>
    public string? StartedLogPath { get => _startedLogPath; private set => _startedLogPath = value; }

    private volatile IReadOnlyList<ScanSkip> _scanSkipped = [];

    /// <summary>
    /// The snapshot's files git shows as binary that the secret scan did not read in full (over a cap; 0.9.2), for scanSkipped in the JSON;
    /// empty when there were none or no snapshot was taken. A --baseline base commit has no overlay and never adds to it.
    /// </summary>
    public IReadOnlyList<ScanSkip> ScanSkipped { get => _scanSkipped; private set => _scanSkipped = value; }

    public async Task<JobResult> RunAsync(JobRequest req, CancellationToken ct)
    {
        var jobId = NewId();
        if (Precheck(jobId, req) is { } refused)
        {
            return refused;
        }

        var (baseCommit, badBase) = await ResolveBaseAsync(jobId, req, ct).ConfigureAwait(false);
        if (badBase is not null)
        {
            return badBase;
        }

        var (prepared, failed) = await PrepareAsync(jobId, req, output, ct).ConfigureAwait(false);
        if (prepared is null)
        {
            return failed!;
        }

        await using (prepared.ConfigureAwait(false))
        {
            var result = await Dispatch(jobId, req, prepared, ct).ConfigureAwait(false);

            // --baseline: only the job's own failure is rerun at the base (never TimedOut, WindowClosed, InfraError, NoCapacity, Rejected or Cancelled).
            if (baseCommit is null || result.Status != JobStatus.Failed)
            {
                return result;
            }

            var baseline = await RunBaselinesAsync([new BaselineTarget(req, baseCommit, result.JobId, result.Machine)], prepared.Payload, ct).ConfigureAwait(false);
            return result with { Baseline = baseline[0] };
        }
    }

    /// <summary>
    /// The machines this job may use, in config order: every machine, or with --on only those named (<see cref="Pinning.Candidates"/>). Every
    /// probe, judgement and wake of the job looks at these only; probe arrays are index-aligned with this list.
    /// </summary>
    private List<Machine> Candidates(JobRequest req) => Pinning.Candidates(config, req.On);

    /// <summary>
    /// The checks on the command line alone (--on names, policy, secrets in it, --env (<see cref="PrecheckEnv"/>), Windows-safe arguments), before
    /// any network or git work; null when it may run.
    /// </summary>
    private JobResult? Precheck(string jobId, JobRequest req)
    {
        if (Pinning.Resolve(config, req.On).Error is { } unknown)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, null, unknown) { RejectKind = Jobs.RejectKind.Arguments };
        }

        var policy = JobPolicy.Validate(config.Policy, req.Argv, req.Directory);
        if (!policy.Ok)
        {
            var badArguments = req.Argv.Count == 0 || req.Argv.Any(a => a.Any(char.IsControl));
            return new JobResult(jobId, JobStatus.Rejected, 2, null, policy.Reason!) { RejectKind = badArguments ? Jobs.RejectKind.Arguments : Jobs.RejectKind.Policy };
        }

        var commandFindings = SecretScanner.ScanText(string.Join(' ', req.Argv), "command line");
        if (commandFindings.Count > 0)
        {
            return Rejected(jobId, commandFindings);
        }

        if (PrecheckEnv(jobId, req) is { } envRefused)
        {
            return envRefused;
        }

        // Arguments unsafe on Windows (" < > ^ %, or & | for a program that is not an .exe) can still run on a Linux worker; refuse only
        // when no non-Windows machine has the needed capabilities, before any network or git work.
        var windows = JobPolicy.ValidateForWindows(req.Argv);
        if (!windows.Ok && !Candidates(req).Any(m => m.Os != MachineOs.Windows && HasCapabilities(m, req.Needs)))
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, null, windows.Reason!) { RejectKind = Jobs.RejectKind.WindowsArgs };
        }

        // No machine in the config has every capability asked for: waiting, waking or probing cannot help, so end now (75, not retryable).
        return NeverByConfig(req) is { } never ? NoCapacity(jobId, never, "") : null;
    }

    /// <summary>
    /// The checks on the job's --env (<see cref="JobRequest.Env"/>), after the command line's: the hard deny table again (Policy), then
    /// policy.allowed_env (Policy), the secret scan of each NAME=VALUE (Secret; the value is never shown), a name that a candidate machine's
    /// env: in machines.yml sets too, and the Windows command-line cap with the job's variables. Those two rule out a machine, not the job
    /// (<see cref="EnvExclusion"/>), except that a job whose capable machines are all ruled out by --env, at least one by the cap, is refused
    /// here (<see cref="RejectKind.JobEnv"/>). Null when the job sets none or every check passes.
    /// </summary>
    private JobResult? PrecheckEnv(string jobId, JobRequest req)
    {
        if (req.Env.Count == 0)
        {
            return null;
        }

        JobResult Refused(string message, RejectKind kind) => new(jobId, JobStatus.Rejected, 2, null, message) { RejectKind = kind };
        foreach (var e in req.Env)
        {
            // Parse refused these already; a request built some other way gets the same answer.
            if (!JobEnv.IsValidName(e.Name))
            {
                return Refused("--env NAME=VALUE: the name must be letters, digits and _ and not start with a digit.", Jobs.RejectKind.Arguments);
            }

            if (JobEnv.IsJobDenied(e.Name))
            {
                return Refused(JobEnv.DeniedMessage(e.Name), Jobs.RejectKind.Policy);
            }

            if (!JobEnv.IsAllowed(config.Policy.AllowedEnv, e.Name))
            {
                return Refused(JobEnv.NotAllowedMessage(e.Name), Jobs.RejectKind.Policy);
            }
        }

        var findings = req.Env.SelectMany(e => SecretScanner.ScanText(e.Name + "=" + e.Value, "--env " + e.Name)).ToList();
        if (findings.Count > 0)
        {
            return Rejected(jobId, findings);
        }

        // A machine's env: clash or a Windows command too long rules out that machine only (EnvExclusion, judged again per machine when the
        // job is placed), and NeverByConfig ends the job when that leaves no capable machine. The one refusal here: every capable machine
        // not already ruled out is ruled out by --env, at least one of them because the values make its Windows command too long (the
        // values themselves must change).
        var windowsUnsafe = !JobPolicy.ValidateForWindows(req.Argv).Ok;
        var judged = Candidates(req)
            .Where(m => HasCapabilities(m, req.Needs) && !(windowsUnsafe && m.Os == MachineOs.Windows))
            .Select(m => (Machine: m, Rule: EnvExclusion(m, req.Env, windowsUnsafe)))
            .ToList();
        if (judged.Count > 0 && judged.All(j => j.Rule is not null)
            && judged.FirstOrDefault(j => j.Rule?.Reason == Exclusion.WindowsCommandTooLong).Machine is { } tooLong)
        {
            var length = string.Create(
                CultureInfo.InvariantCulture,
                $"the Windows command would be {WindowsLength(tooLong, req.Env):N0} characters (limit {RemoteScripts.MaxWindowsCommandLength:N0}) on {tooLong.Name}");
            var clashes = judged.Where(j => j.Rule?.Reason == Exclusion.EnvConflict).ToList();

            // Only the length: the message as before. Clashes too: name each machine and its variable, then the length.
            return Refused(
                clashes.Count == 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"the --env values make the Windows command {WindowsLength(tooLong, req.Env):N0} characters (limit {RemoteScripts.MaxWindowsCommandLength:N0}) on {tooLong.Name}")
                    : "--env: " + string.Join("; ", clashes.Select(j => $"{j.Machine.Name} sets {ClashName(j.Machine, req.Env)} in its env (machines.yml)")) + "; " + length,
                Jobs.RejectKind.JobEnv);
        }

        return null;
    }

    /// <summary>
    /// What the job asks of a machine (capabilities, --mem, --size / --min-size, Windows-safe arguments for <paramref name="windowsUnsafe"/>, and
    /// the candidates its --env rules out).
    /// </summary>
    private JobNeeds NeedsOf(JobRequest req, bool windowsUnsafe) => new(req.Needs, req.MinMemGb, windowsUnsafe)
    {
        Size = req.Size,
        Exclusive = req.Exclusive,
        EnvRuledOut = EnvRuledOut(Candidates(req), req.Env, windowsUnsafe),
    };

    /// <summary>The candidates the job's --env rules out (<see cref="EnvExclusion"/>), by name; empty without --env.</summary>
    private static Dictionary<string, (Exclusion Reason, string Why)> EnvRuledOut(IEnumerable<Machine> candidates, IReadOnlyList<JobEnvVar> env, bool windowsUnsafe)
    {
        var ruled = new Dictionary<string, (Exclusion Reason, string Why)>(StringComparer.Ordinal);
        foreach (var m in env.Count == 0 ? [] : candidates)
        {
            if (EnvExclusion(m, env, windowsUnsafe) is { } rule)
            {
                ruled[m.Name] = rule;
            }
        }

        return ruled;
    }

    /// <summary>
    /// Whether the job's --env rules out a machine, judged from the config alone: its env: in machines.yml sets one of the variables too
    /// (<see cref="Exclusion.EnvConflict"/>), or it is a Windows machine on which the variables make the encoded job command longer than the
    /// Windows limit (<see cref="Exclusion.WindowsCommandTooLong"/>; not judged when <paramref name="windowsUnsafe"/> rules Windows out already).
    /// Both are permanent. Null when the machine can take the job as far as --env goes.
    /// </summary>
    internal static (Exclusion Reason, string Why)? EnvExclusion(Machine m, IReadOnlyList<JobEnvVar> env, bool windowsUnsafe)
    {
        if (env.Count == 0)
        {
            return null;
        }

        if (ClashName(m, env) is { } clash)
        {
            return (Exclusion.EnvConflict, $"env {clash} is set by this machine's env in machines.yml");
        }

        if (m.Os == MachineOs.Windows && !windowsUnsafe && WindowsLength(m, env) is var size && size > RemoteScripts.MaxWindowsCommandLength)
        {
            return (Exclusion.WindowsCommandTooLong, string.Create(
                CultureInfo.InvariantCulture, $"with these --env values the Windows command is {size:N0} characters (limit {RemoteScripts.MaxWindowsCommandLength:N0})"));
        }

        return null;
    }

    /// <summary>The first of the job's variables that the machine's env: sets too (as the job spelled it), or null when none does.</summary>
    private static string? ClashName(Machine m, IReadOnlyList<JobEnvVar> env) =>
        env.FirstOrDefault(e => m.Env.Keys.Contains(e.Name, StringComparer.OrdinalIgnoreCase))?.Name;

    private static int WindowsLength(Machine m, IReadOnlyList<JobEnvVar> env) => RemoteScripts.WorstCaseWindowsCommandLength(m, env);

    /// <summary>
    /// When the config alone rules out every candidate machine (<see cref="Candidates"/>): each lacks a capability the job needs, has a
    /// declared size: outside --size / --min-size, or is ruled out by the job's --env (<see cref="EnvExclusion"/>). Each judged from the config alone (never probed), all permanently ruled out. Null when at
    /// least one candidate could take the job as far as the config says.
    /// </summary>
    private List<Assessment>? NeverByConfig(JobRequest req)
    {
        var windowsUnsafe = req.Env.Count > 0 && !JobPolicy.ValidateForWindows(req.Argv).Ok;
        var judged = new List<Assessment>();
        foreach (var m in Candidates(req))
        {
            var missing = Eligibility.Missing(m, req.Needs);
            if (missing.Count > 0)
            {
                judged.Add(new Assessment(m, Unprobed(m), Exclusion.MissingCapability, Eligibility.Lacks(m, missing), true, 0, 0));
            }
            else if (Eligibility.DeclaredSizeMismatch(m, req.Size) is { } wrongSize)
            {
                judged.Add(new Assessment(m, Unprobed(m), Exclusion.WrongSize, wrongSize, true, 0, 0));
            }
            else if (EnvExclusion(m, req.Env, windowsUnsafe) is { } env)
            {
                judged.Add(new Assessment(m, Unprobed(m), env.Reason, env.Why, true, 0, 0));
            }
            else
            {
                return null;
            }
        }

        return judged;
    }

    /// <summary>
    /// The per-shard command-line checks of lanq run --shards (<see cref="Precheck"/> on each shard's substituted arguments, in index order):
    /// the first refusal with its message ("shard i/N: ..."), or null when every shard may run. An exclusive run's N may not exceed the
    /// machines that can take the job as far as the config says (<see cref="ExclusiveMachines"/>): each shard takes a whole machine.
    /// </summary>
    private (string Message, JobResult Refused)? PrecheckShards(JobRequest template, IReadOnlyList<ShardInfo> shards)
    {
        foreach (var s in shards)
        {
            if (Precheck("shard", template with { Argv = s.Substitute(template.Argv), Shard = s }) is { } refused)
            {
                return ($"shard {s}: {refused.Message}", refused);
            }
        }

        if (template.Exclusive && ExclusiveMachines(template) is var machines && shards.Count > machines)
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"--exclusive --shards {shards.Count}: only {machines} machine{(machines == 1 ? "" : "s")} can take this job");
            return (message, new JobResult("shard", JobStatus.Rejected, 2, null, message) { RejectKind = Jobs.RejectKind.Arguments });
        }

        return null;
    }

    /// <summary>
    /// How many candidate machines (<see cref="Candidates"/>) could ever take the job as far as the config says: every capability it needs and
    /// no declared size: outside --size / --min-size, and not ruled out by its --env (as <see cref="NeverByConfig"/> judges them).
    /// </summary>
    private int ExclusiveMachines(JobRequest req) =>
        Candidates(req).Count(m => Eligibility.Missing(m, req.Needs).Count == 0 && Eligibility.DeclaredSizeMismatch(m, req.Size) is null
            && EnvExclusion(m, req.Env, req.Env.Count > 0 && !JobPolicy.ValidateForWindows(req.Argv).Ok) is null);

    /// <summary>A placeholder health for a machine judged from the config alone (never probed).</summary>
    private static MachineHealth Unprobed(Machine m) => new(m.Name, false, 0, 0, 0, null, null);

    /// <summary>
    /// Snapshots the repository once (under <paramref name="payloadId"/>) and scans it for secrets. Every job given the result shares its
    /// snapshot, its bundles and its archive, each built at most once, on first use; disposing it deletes them.
    /// </summary>
    private Task<(PreparedPayload? Prepared, JobResult? Failed)> PrepareAsync(string payloadId, JobRequest req, Action<string> emit, CancellationToken ct) =>
        PrepareAsync(payloadId, req, Candidates(req).Any(m => m.Transfer == TransferMode.Git), emit, ct);

    /// <param name="anyGit">Whether any machine that may take a job of this payload transfers by git (then the snapshot gets a repository id).</param>
    private async Task<(PreparedPayload? Prepared, JobResult? Failed)> PrepareAsync(string payloadId, JobRequest req, bool anyGit, Action<string> emit, CancellationToken ct)
    {
        // Only the snapshot is built up front; a bundle or archive is built lazily, when a machine of that kind actually gets the job.
        var kinds = anyGit ? PayloadKinds.Bundle : PayloadKinds.None;
        var (payload, failed) = await SnapshotAndScanAsync(payloadId, req, kinds, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return (null, failed);
        }

        var builds = new SemaphoreSlim(1, 1);
        return (new PreparedPayload(
            payloadId,
            payload,
            new LazyArchive(payloads, payloadId, req.RepoDir, scratchDir, payload.Sha, emit, builds),
            new LazyBundles(payloads, payloadId, req.RepoDir, scratchDir, payload.Sha, builds),
            builds), null);
    }

    /// <summary>
    /// Snapshots the repository (under <paramref name="payloadId"/>, through a throw-away index: the user's index, branches and working tree
    /// are never touched, though git writes the snapshot's objects to the repository's object store) and scans the uncommitted changes, the
    /// changed and committed file names and the package-manager configs for secrets. Builds no bundle, and an archive only when
    /// <paramref name="kinds"/> asks for one up front. The payload, or the InfraError (70) or Rejected (2) result; nothing is left behind on a refusal.
    /// </summary>
    private async Task<(JobPayload? Payload, JobResult? Failed)> SnapshotAndScanAsync(string payloadId, JobRequest req, PayloadKinds kinds, CancellationToken ct)
    {
        JobPayload payload;
        try
        {
            payload = await payloads.BuildAsync(payloadId, req.RepoDir, scratchDir, kinds, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return (null, new JobResult(payloadId, JobStatus.InfraError, 70, null, "Could not snapshot the repository: " + ex.Message) { InfraKind = Jobs.InfraKind.Snapshot });
        }

        // Files git shows as binary that were over a cap and not read in full (0.9.2): said once, kept for the JSON (scanSkipped). One
        // snapshot per lanq run (one job, every shard, a whole plan), so this is set once.
        if (payload.ScanSkipped.Count > 0)
        {
            ScanSkipped = payload.ScanSkipped;
            output(ScanSkippedLine(payload.ScanSkipped));
        }

        if (ScanPayload(payloadId, payload) is { } refused)
        {
            TryDelete(payload.ArchivePath);
            return (null, refused);
        }

        return (payload, null);
    }

    /// <summary>
    /// "[lanq] secret scan: 2 file(s) not read in full: assets/big.bin (52 MB), ..." (at most 5 named, sanitised): files git shows as binary
    /// that were over a cap of the content scan.
    /// </summary>
    internal static string ScanSkippedLine(IReadOnlyList<ScanSkip> skipped)
    {
        const int Named = 5;
        var named = string.Join(", ", skipped.Take(Named).Select(s => string.Create(CultureInfo.InvariantCulture, $"{SafeLine(s.Path)} ({s.Bytes / (1024.0 * 1024.0):0.#} MB)")));
        var more = skipped.Count > Named ? string.Create(CultureInfo.InvariantCulture, $", and {skipped.Count - Named} more") : "";
        return string.Create(CultureInfo.InvariantCulture, $"[lanq] secret scan: {skipped.Count} file(s) not read in full: {named}{more} (over the scan's caps; listed in scanSkipped)");
    }

    /// <summary>
    /// The secret scans of a payload: the uncommitted changes and their file names (none for a baseline's clean base commit), the committed file
    /// names and the package-manager configs. The Rejected (Secret) result, or null when it is clean.
    /// </summary>
    private static JobResult? ScanPayload(string payloadId, JobPayload payload)
    {
        var findings = SecretScanner.ScanText(payload.OverlayDiff, "uncommitted changes")
            .Concat(payload.BinaryFindings)
            .Concat(SecretScanner.ScanFileNames(payload.ChangedFiles))
            .Concat(SecretScanner.ScanCommittedFileNames(payload.CommittedFiles ?? []))
            .Concat((payload.CommittedConfigs ?? new Dictionary<string, string?>()).SelectMany(c => SecretScanner.ScanCommittedConfig(c.Key, c.Value)))
            .ToList();
        return findings.Count > 0 ? Rejected(payloadId, findings) : null;
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
    internal static (string Before, int? Code) ReadExitMarker(string line, string jobId, string? nonce = null)
    {
        var marker = RemoteScripts.ExitMarker(jobId, nonce);
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

    /// <summary>
    /// What the Windows wrapper said it resolved the program to when it refused to start a job given &amp; or |
    /// (<see cref="RemoteScripts.WindowsArgsMarker"/>), or null when <paramref name="line"/> is not that line for this job.
    /// </summary>
    internal static string? ReadWindowsArgsRefusal(string line, string jobId, string? nonce = null)
    {
        var marker = RemoteScripts.WindowsArgsMarker(jobId, nonce);
        var text = line.TrimEnd('\r');
        return text.StartsWith(marker, StringComparison.Ordinal) ? text[marker.Length..] : null;
    }

    /// <summary>The Rejected message for a job the Windows worker would not start because its program is not a native .exe there.</summary>
    internal static string WindowsArgsRefusal(Machine machine, string program, string resolved) =>
        $"{SafeLine(machine.Name)}: '{SafeLine(program)}' "
        + (resolved.Length == 0 ? "was not found on the machine" : $"resolves to {SafeLine(resolved)} there")
        + ", not a native .exe, so its arguments with & or | were refused and nothing was started: .cmd/.bat shims re-parse the command "
        + "line through cmd.exe, which could run another command. Call the .exe directly, put the command in a script, or fix the machine's "
        + "PATH (path_prepend:).";

    private static bool HasCapabilities(Machine m, IReadOnlyList<string> needs) =>
        needs.All(n => m.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase));

    /// <summary>Why a machine did not take the job, for the NoCapacity message: its hold or ssh error, a missing capability, or no free slot.</summary>
    internal static string WhyNot(Assessment a) => a.Why ?? "no free slot when the job tried to start (busy, or ssh failed)";

    /// <summary>The start of the NoCapacity message when no machine can ever take the job (every machine has a permanent reason).</summary>
    internal const string NeverMessage = "No eligible machine, and no machine can ever take this job as configured (waiting will not help).";

    /// <summary>
    /// The NoCapacity result after the last look at the machines: retryable unless every machine has a permanent reason (then the message
    /// starts with <see cref="NeverMessage"/>), and every machine listed (one that was eligible but whose slot was taken first counts as busy).
    /// </summary>
    private static JobResult NoCapacity(string jobId, IReadOnlyList<Assessment> assessed, string note)
    {
        var never = assessed.All(a => a.Permanent);
        var held = string.Join("; ", assessed.Select(a => $"{a.Machine.Name}: {WhyNot(a)}"));
        return new JobResult(jobId, JobStatus.NoCapacity, 75, null, (never ? NeverMessage : "No eligible machine.") + " " + held + note)
        {
            Retryable = !never,
            Excluded = Excluded(assessed),
        };
    }

    internal static List<ExcludedMachine> Excluded(IEnumerable<Assessment> assessed) =>
        [.. assessed.Select(a => new ExcludedMachine(a.Machine.Name, a.Code ?? Eligibility.Code(Exclusion.Busy)!, WhyNot(a), a.Permanent))];

    /// <param name="onSlot">Called with the machine's name once the job holds a slot there (a plan job reports where it ran when cancelled).</param>
    /// <param name="prefer">
    /// A machine tried first whenever it can take the job (a baseline rerun prefers the machine the job failed on); null for none. While it can
    /// take the job but has no free slot (busy, or its slot was taken first), no other machine is tried for up to <see cref="PreferRounds"/>
    /// looks, and never past what is left of --wait: a rerun elsewhere could pass or fail because of the machine, not the commit. lanq run's
    /// --wait defaults to 0, so without --wait the rerun does not wait for that machine at all and takes any machine that can take it now.
    /// </param>
    private async Task<JobResult> Dispatch(string jobId, JobRequest req, PreparedPayload prepared, CancellationToken ct, Action<string>? onSlot = null, string? prefer = null)
    {
        var deadline = Stopwatch.StartNew();
        var archive = prepared.Archive;
        var needs = NeedsOf(req, !JobPolicy.ValidateForWindows(req.Argv).Ok);
        var gitCanEverTakeIt = GitCanEverTakeIt(req);
        var candidates = Candidates(req);
        var say = Say(req);
        var relooked = 0L;
        var preferLooks = 0;
        while (true)
        {
            var round = RoundStart();
            var (probes, assessed) = await ProbeForJobAsync(candidates, needs, ct).ConfigureAwait(false);
            var ranked = Scheduler.Rank(assessed);
            // Never longer than what is left of --wait (with --wait 0, not at all: any machine that can take it now).
            var waitLeft = req.Wait - deadline.Elapsed;
            var holdForPreferred = prefer is not null && preferLooks < PreferRounds && waitLeft > TimeSpan.Zero
                && assessed.FirstOrDefault(a => string.Equals(a.Machine.Name, prefer, StringComparison.Ordinal)) is { } preferred
                && (preferred.Eligible || preferred.Reason == Exclusion.Busy);
            if (holdForPreferred)
            {
                ranked = [.. ranked.Where(a => string.Equals(a.Machine.Name, prefer, StringComparison.Ordinal))];
            }
            else if (prefer is not null)
            {
                ranked = [.. ranked.OrderBy(a => string.Equals(a.Machine.Name, prefer, StringComparison.Ordinal) ? 0 : 1)]; // stable: the rest keep their rank
            }

            foreach (var a in ranked)
            {
                var held = false;
                JobResult? result;
                try
                {
                    result = await TryOnMachine(
                        jobId, req, prepared, a.Machine, a.Health.WindowEndsUtc, gitCanEverTakeIt, MachineLines(req, a.Machine.Name), ct,
                        onSlot: () =>
                        {
                            held = true;
                            req.Progress?.Hold();
                            onSlot?.Invoke(a.Machine.Name);
                            req.Placed?.Invoke();
                        }).ConfigureAwait(false);
                }
                finally
                {
                    if (held)
                    {
                        req.Progress?.Release();
                    }
                }

                if (result is not null)
                {
                    return result;
                }
            }

            if (holdForPreferred)
            {
                // The preferred machine can take the job but has no free slot now: wait for it, at most PreferRounds looks and never past what
                // is left of --wait (waitLeft; with --wait 0, the default, this hold is off and another machine is taken at once).
                preferLooks++;
                say(req.Progress is { AnyHolding: true } busy
                    ? $"[lanq] waiting for a slot on {prefer}, where the job failed ({busy.Holders} hold some)"
                    : string.Create(CultureInfo.InvariantCulture, $"[lanq] waiting for a slot on {prefer}, where the job failed, before taking another machine (look {preferLooks} of {PreferRounds})"));
                await PauseAsync(req, ct, waitLeft).ConfigureAwait(false);
                continue;
            }

            // Every machine is ruled out for good (its config, its RAM, its tools): waiting or waking cannot help, so end now.
            if (assessed.All(a => a.Permanent))
            {
                return NoCapacity(jobId, assessed, archive.Failure is null ? "" : " Archive build failed: " + archive.Failure);
            }

            // Wake-on-LAN last resort: only when no awake machine can take the job now, only with a real --wait to wait in (a pending wake is
            // always inside that wait, so --wait 0 never blocks on one), and only one machine, the best-ranked sleeper.
            if (ranked.Count == 0 && req.Wait > TimeSpan.Zero && !req.NoWake && probe.Waker is { AnyPending: false })
            {
                await WakeBestSleeperAsync(candidates, needs, probes, assessed, archive, say, ct).ConfigureAwait(false);
            }

            if (WaitedOut(req, deadline, round, relooked))
            {
                return NoCapacity(jobId, assessed, archive.Failure is null ? "" : " Archive build failed: " + archive.Failure);
            }

            if (Relook(req.Progress, round, relooked) is > 0 and var release)
            {
                relooked = release;
                continue; // a sibling gave back a slot after this look began: look again now, it may be free
            }

            say(req.Progress is { AnyHolding: true } holding ? $"[lanq] no eligible machine; waiting for a slot ({holding.Holders} hold some)" : "[lanq] no eligible machine; waiting 15s");
            req.Placed?.Invoke();
            await PauseAsync(req, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How many looks a baseline rerun waits for the machine the job failed on (busy there) before it takes another machine.</summary>
    internal const int PreferRounds = 2;

    /// <summary>
    /// Whether a job still waiting for a slot has used up --wait: from its own start, or in a plan (<see cref="JobRequest.Progress"/>) only
    /// while no job of the plan holds a slot (a job waits behind its siblings even with --wait 0), and only when no slot of the plan was given
    /// back since the look at the machines began (<paramref name="round"/>, <see cref="RoundStart"/>): a job that saw the slot full just before
    /// its sibling gave it back looks again instead of ending NoCapacity next to a free slot.
    /// </summary>
    private static bool WaitedOut(JobRequest req, Stopwatch own, long round, long relooked) =>
        req.Progress is { } plan ? !plan.AnyHolding && plan.IdleFor >= req.Wait && Relook(plan, round, relooked) == 0 : own.Elapsed >= req.Wait;

    /// <summary>
    /// The plan's slot release a job must look again for (<see cref="PlanProgress.ReleaseSince"/>): one given back after this look began
    /// (<paramref name="round"/>) that the job has not already looked again for (<paramref name="relooked"/>), else 0. Once per release, so a
    /// slot that is still full after the second look does not keep the job looking.
    /// </summary>
    internal static long Relook(PlanProgress? plan, long round, long relooked) =>
        plan?.ReleaseSince(round) is > 0 and var release && release != relooked ? release : 0;

    /// <summary>
    /// When the look at the machines that is about to start began, for <see cref="PlanProgress.ReleaseSince"/>: now, less
    /// <see cref="ProbeShareWindow"/>, since the look may use another job's probe that started up to that long ago.
    /// </summary>
    internal static long RoundStart() => Stopwatch.GetTimestamp() - (long)(ProbeShareWindow.TotalSeconds * Stopwatch.Frequency);

    /// <summary>The 15 s between two looks at the machines; in a plan cut short when a job of the plan gives back a slot.</summary>
    /// <param name="atMost">A shorter pause when less than 15 s is left (the wait for a baseline's preferred machine); null for 15 s.</param>
    private async Task PauseAsync(JobRequest req, CancellationToken ct, TimeSpan? atMost = null)
    {
        var timer = delay(atMost is { } most && most < TimeSpan.FromSeconds(15) ? most : TimeSpan.FromSeconds(15), ct);
        if (req.Progress is { } plan)
        {
            await Task.WhenAny(timer, plan.Released).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (timer.IsFaulted)
            {
                await timer.ConfigureAwait(false);
            }

            return;
        }

        await timer.ConfigureAwait(false);
    }

    /// <summary>Where a job's own lanq lines go: as they are, or for a plan job prefixed "[name] ".</summary>
    private Action<string> Say(JobRequest req) => (req.Label ?? req.PlanJob) is { } name ? line => output($"[{name}] {line}") : output;

    /// <summary>Where a single job's lines on a machine go: as they are, or for a plan job prefixed "[name machine] " (a baseline rerun: its label).</summary>
    private Action<string> MachineLines(JobRequest req, string machine) => (req.Label ?? req.PlanJob) is { } name ? line => output($"[{name} {machine}] {line}") : output;

    /// <summary>
    /// Static property of the config: can any git-transfer candidate ever take this job (capabilities and a declared size: only; load, hours
    /// and slots are temporary)?
    /// </summary>
    private bool GitCanEverTakeIt(JobRequest req) =>
        Candidates(req).Any(m => m.Transfer == TransferMode.Git && HasCapabilities(m, req.Needs) && Eligibility.DeclaredSizeMismatch(m, req.Size) is null);

    /// <summary>
    /// Probes every machine of <paramref name="machines"/> (the job's <see cref="Candidates"/>) at once (WhenAll keeps their order whatever
    /// order the answers arrive in), then judges each one for this job (<see cref="Scheduler.Assess"/>): the raw probe results and the
    /// assessments, both index-aligned with <paramref name="machines"/>.
    /// </summary>
    private async Task<(MachineHealth[] Probes, List<Assessment> Assessed)> ProbeForJobAsync(IReadOnlyList<Machine> machines, JobNeeds needs, CancellationToken ct)
    {
        var probes = await Task.WhenAll(machines.Select(m => ProbeSharedAsync(m, ct))).ConfigureAwait(false);
        probes = [.. probes.Select(CountStuckStale)];
        return (probes, Scheduler.Assess(machines.Zip(probes), needs));
    }

    /// <summary>
    /// Per machine, the stale slots a claim of this run reported it could not reclaim (its <see cref="RemoteScripts.ReclaimStuckMarker"/>
    /// lines: a dead reclaimer's .reclaim lock, or file permissions). For the rest of this run that many of the machine's stale slots count
    /// as used. Only that explicit line marks a machine: a plain FULL (another claim was first, or a live slot above max_jobs) never does.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, bool>> _staleStuck =
        new(StringComparer.Ordinal);

    /// <summary>
    /// On a machine where this run found stale slots it could not reclaim, counts up to that many of <paramref name="h"/>'s stale slots as
    /// used (<see cref="MachineHealth.SlotsStuck"/>), so the job waits as for a busy machine instead of claiming there again and again.
    /// </summary>
    private MachineHealth CountStuckStale(MachineHealth h)
    {
        var stuck = h.SlotsStale > 0 && _staleStuck.TryGetValue(h.Name, out var slots) ? Math.Min(h.SlotsStale, slots.Count) : 0;
        return stuck > 0 ? h with { SlotsStuck = stuck, SlotsStale = h.SlotsStale - stuck } : h;
    }

    /// <summary>The slot names a claim's <see cref="RemoteScripts.ReclaimStuckMarker"/> lines name ("slot-2"), each well formed.</summary>
    internal static List<string> StuckSlots(string output) =>
        [.. output.Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith(RemoteScripts.ReclaimStuckMarker, StringComparison.Ordinal))
            .Select(l => l[RemoteScripts.ReclaimStuckMarker.Length..])
            .Where(n => n.Length is > 5 and <= 14 && n.StartsWith("slot-", StringComparison.Ordinal) && n[5..].All(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Records the stuck slots a claim on <paramref name="machine"/> reported; prints <see cref="StuckStaleLine"/> the first time for that machine.</summary>
    internal void NoteStuckSlots(Machine machine, string output, Action<string> emit)
    {
        var stuck = StuckSlots(output);
        if (stuck.Count == 0)
        {
            return;
        }

        // GetOrAdd with a value (not a factory, which may run on two threads at once): exactly one caller's dictionary is stored, and
        // only that caller prints the line.
        var fresh = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var slots = _staleStuck.GetOrAdd(machine.Name, fresh);
        var first = ReferenceEquals(slots, fresh);
        foreach (var s in stuck)
        {
            slots.TryAdd(s, true);
        }

        if (first)
        {
            emit(StuckStaleLine(machine.Name, stuck.Count));
        }
    }

    /// <summary>
    /// The line lanq prints once per run for a machine whose stale slots could not be reclaimed:
    /// "[lanq] build-01: 1 stale slot could not be reclaimed (a .reclaim lock or file permissions); counting it as busy for this run; lanq unlock build-01 clears it".
    /// </summary>
    internal static string StuckStaleLine(string machine, int stale) => string.Create(
        CultureInfo.InvariantCulture,
        $"[lanq] {SafeLine(machine)}: {stale} stale slot{(stale == 1 ? "" : "s")} could not be reclaimed (a .reclaim lock or file permissions); counting {(stale == 1 ? "it" : "them")} as busy for this run; lanq unlock {SafeLine(machine)} clears {(stale == 1 ? "it" : "them")}");

    /// <summary>How recent an in-flight probe of a machine must be for another job of this runner to share it (<see cref="ProbeSharedAsync"/>).</summary>
    internal static readonly TimeSpan ProbeShareWindow = TimeSpan.FromSeconds(3);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Task<MachineHealth> Probe, long Started)> _probes = new(StringComparer.Ordinal);

    /// <summary>
    /// One probe of <paramref name="m"/>, shared: a job that asks while another job of this runner (a plan's jobs run side by side) has a probe of
    /// the same machine in flight that started at most <see cref="ProbeShareWindow"/> ago gets that probe's answer instead of starting its own.
    /// A finished probe is never reused, and the slot claim stays the authority on whether a slot is free.
    /// </summary>
    private async Task<MachineHealth> ProbeSharedAsync(Machine m, CancellationToken ct)
    {
        var now = Stopwatch.GetTimestamp();
        var mine = new TaskCompletionSource<MachineHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = _probes.AddOrUpdate(
            m.Name,
            _ => (mine.Task, now),
            (_, old) => !old.Probe.IsCompleted && Stopwatch.GetElapsedTime(old.Started, now) <= ProbeShareWindow ? old : (mine.Task, now));
        if (!ReferenceEquals(entry.Probe, mine.Task))
        {
            try
            {
                return await entry.Probe.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The job that started it was cancelled, this one was not: probe again on its own.
                return await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false);
            }
        }

        try
        {
            var h = await probe.ProbeAsync(m, config.Policy, ct).ConfigureAwait(false);
            mine.TrySetResult(h);
            return h;
        }
        catch (OperationCanceledException ex)
        {
            mine.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            mine.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Whether a machine that looks asleep is worth waking for a job, by everything knowable without a probe: it has a wake: setting, the
    /// capabilities, is not Windows for arguments unsafe there, and is not an archive machine when the archive could not be built. A sleeper's
    /// memory and load are unknown, so they are not judged. <paramref name="raw"/> is the probe result as returned (no job holds).
    /// </summary>
    internal static bool CouldWakeFor(Machine m, MachineHealth raw, JobNeeds needs, bool archiveFailed) =>
        m.Wake is not null && raw.LooksAsleep && Eligibility.Missing(m, needs.Capabilities).Count == 0
        && Eligibility.DeclaredSizeMismatch(m, needs.Size) is null
        && !(needs.WindowsUnsafe && m.Os == MachineOs.Windows) && !needs.EnvRuledOut.ContainsKey(m.Name) && !(m.Transfer == TransferMode.Archive && archiveFailed);

    /// <summary>
    /// Wakes at most one machine: the first by name among those <see cref="CouldWakeFor"/> allows that are inside their hours. The decision uses
    /// the raw probe results; a machine that was sent a packet (or was refused one with a reason) gets the new result in <paramref name="probes"/>
    /// and is judged again in <paramref name="assessed"/>.
    /// </summary>
    /// <param name="machines">The job's candidates (<see cref="Candidates"/>); <paramref name="probes"/> and <paramref name="assessed"/> are index-aligned with it.</param>
    private async Task WakeBestSleeperAsync(
        IReadOnlyList<Machine> machines, JobNeeds needs, MachineHealth[] probes, List<Assessment> assessed, LazyArchive archive, Action<string> emit, CancellationToken ct)
    {
        foreach (var i in WakeOrder(machines))
        {
            var m = machines[i];
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
                emit($"[lanq] no awake machine can take the job; sent a Wake-on-LAN packet to {m.Name} (waiting up to {m.Wake!.WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s, within --wait)");
                return;
            }
        }
    }

    /// <summary>The order sleepers are considered for a wake: the indexes of <paramref name="machines"/> (the job's candidates), by name (ordinal).</summary>
    private static IEnumerable<int> WakeOrder(IReadOnlyList<Machine> machines) => Enumerable.Range(0, machines.Count).OrderBy(i => machines[i].Name, StringComparer.Ordinal);

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
                    return new JobResult(jobId, JobStatus.InfraError, 70, null, message) { InfraKind = Jobs.InfraKind.Archive };
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

        // --exclusive claims every slot of the machine, all or nothing (SLOTS=1,...,n or SLOT=FULL).
        var timeout = req.Timeout ?? JobRequest.DefaultTimeout;
        var claim = req.Exclusive ? RemoteScripts.AcquireAll(machine, jobId, timeout) : RemoteScripts.AcquireSlot(machine, jobId, timeout);
        SshResult acquire;
        try
        {
            acquire = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, claim), null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ReleaseClaimAsync(machine, jobId).ConfigureAwait(false); // Ctrl+C mid-claim: it may have created slots
            throw;
        }

        var slot = ParseSlot(acquire.Output);
        if (!acquire.Ok || slot is null)
        {
            // A claim that did not report back (timed out, lost the connection, unreadable output) may still have created slot files; a
            // clean SLOT=FULL created none. Either way: unreachable or full, try the next machine.
            if (!acquire.Ok || !IsCleanFull(acquire.Output))
            {
                await ReleaseClaimAsync(machine, jobId).ConfigureAwait(false);
            }

            NoteStuckSlots(machine, acquire.Output, emit);
            return null;
        }

        NoteStuckSlots(machine, acquire.Output, emit); // it got a slot past a stale one it could not reclaim

        onSlot?.Invoke();
        emit(slot.Contains(',', StringComparison.Ordinal)
            ? $"[lanq] job {jobId} -> {machine.Name} (slots {slot}, exclusive)"
            : $"[lanq] job {jobId} -> {machine.Name} (slot {slot})");
        var logPath = log.LogPathFor(jobId);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var logFile = new StreamWriter(logPath, append: false);
        StartedLogPath = Path.GetFullPath(logPath);
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

            // Never throws: a job whose stats line cannot be written keeps its result (the log warns instead).
            log.Append(new JobRecord(jobId, machine.Name, command, started, Math.Round(clock.Elapsed.TotalSeconds, 2), result.ExitCode, result.Status.ToString())
            {
                V = 2,
                Key = req.CommandKey ?? JobHistory.Key(req.Argv),
                Dir = JobHistory.Dir(req.Directory),
                Shard = req.Shard?.Index,
                Shards = req.Shard?.Count,
                RunId = req.RunId,
                RunSeconds = result.RunSeconds,
                PlanJob = req.PlanJob,
                PlanRun = req.PlanRun,
                Exclusive = req.Exclusive ? true : null,
                EnvNames = req.Env.Count > 0 ? [.. req.Env.Select(e => e.Name)] : null,
                BaselineOf = req.BaselineOf,
                Commit = req.BaselineOf is null ? null : req.Commit,
            });
            return result with { CleanedUp = cleanedUp, LogPath = Path.GetFullPath(logPath) };
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
            executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, req.Timeout ?? JobRequest.DefaultTimeout, req.Shard, req.PlanJob, env: req.Env));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new JobResult(jobId, JobStatus.Rejected, 2, machine.Name, ex.Message) { RejectKind = Jobs.RejectKind.Arguments };
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
                InfraKind = step.IsSshFailure && !step.TimedOut ? Jobs.InfraKind.Connection : Jobs.InfraKind.Prepare,
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
            var why = string.Create(
                CultureInfo.InvariantCulture,
                $"window closing ({Math.Max(0, Math.Ceiling(left.TotalMinutes)):0} min left of its allowed hours plus grace after upload and prepare); the job was not started");
            return new JobResult(jobId, JobStatus.NoCapacity, 75, machine.Name, $"{machine.Name}: {why}")
            {
                Retryable = true,
                Excluded = [new ExcludedMachine(machine.Name, Eligibility.Code(Exclusion.WindowClosing)!, why, false)],
            };
        }

        if (byWindow)
        {
            timeout = windowLimit!.Value;
            onLine($"[lanq] {machine.Name}: allowed hours end soon; the job will be stopped after {ProcessSshClient.Describe(timeout)} (window end + {grace.TotalMinutes:0} min grace)");
        }

        // The worker enforces the same limit itself, a margin later, so a job outlives a vanished lanq by at most that margin.
        // A fresh nonce in the wrapper's marker lines, so a job printing "LANQ_EXIT=..." or "LANQ_WINARGS=..." itself is only output.
        var nonce = RemoteScripts.NewMarkerNonce();
        executeCommand = RemoteScripts.Wrap(machine, RemoteScripts.Execute(machine, jobId, req.Directory, req.Argv, timeout + WorkerDeadlineMargin, req.Shard, req.PlanJob, nonce, req.Env));
        // The wrapper's exit marker counts only as the FINAL output line (or glued to the end of it): a line carrying one is held back until
        // the next line arrives, which shows it was not final, and is then passed on unchanged as ordinary output. The final one is taken off.
        var heldGate = new object();
        string? held = null;
        string? refusedLine = null;
        void OnJobLine(string line)
        {
            lock (heldGate)
            {
                // The Windows wrapper refused to start a job given & or | (its program is not an .exe there): that line counts only when the
                // final exit marker with 2 follows it. It is held back like the marker, and passed on as ordinary output if anything else follows.
                if (refusedLine is not null && (held is not null || ReadExitMarker(line, jobId, nonce).Code is null))
                {
                    onLine(refusedLine);
                    refusedLine = null;
                }

                if (held is null && refusedLine is null && ReadWindowsArgsRefusal(line, jobId, nonce) is not null)
                {
                    refusedLine = line;
                    return;
                }

                if (held is not null)
                {
                    onLine(held);
                }

                var marked = ReadExitMarker(line, jobId, nonce).Code is not null;
                held = marked ? line : null;
                if (!marked)
                {
                    onLine(line);
                }
            }
        }

        onRunning?.Invoke();
        var runStarted = clock.GetTimestamp();
        var run = await ssh.RunAsync(
            machine,
            executeCommand,
            OnJobLine,
            timeout,
            ct).ConfigureAwait(false);
        var runSeconds = Math.Round(clock.GetElapsedTime(runStarted).TotalSeconds, 2);

        int? markedExit = null;
        string? refusedAs = null;
        if (held is { } last)
        {
            var (before, code) = ReadExitMarker(last, jobId, nonce);
            markedExit = code;
            if (refusedLine is not null && code == 2 && before.Length == 0)
            {
                refusedAs = ReadWindowsArgsRefusal(refusedLine, jobId, nonce);
            }
            else if (refusedLine is not null)
            {
                onLine(refusedLine);
            }

            if (before.Length > 0)
            {
                onLine(before);
            }
        }
        else if (refusedLine is not null)
        {
            onLine(refusedLine);
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
        else if (refusedAs is not null && machine.Os == MachineOs.Windows)
        {
            // Nothing was started, so there is nothing to stop or fetch; the caller's cleanup still releases the slot.
            return new JobResult(jobId, JobStatus.Rejected, 2, machine.Name, WindowsArgsRefusal(machine, req.Argv[0], refusedAs))
            {
                RejectKind = Jobs.RejectKind.WindowsArgs,
                RunSeconds = runSeconds,
            };
        }
        else if (sshFailed)
        {
            result = new JobResult(jobId, JobStatus.InfraError, run.ExitCode, machine.Name, "SSH connection failed while running the job.")
            {
                ConnectionLost = true,
                MayStillRun = true,
                InfraKind = Jobs.InfraKind.Connection,
            };
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

        result = result with { RunSeconds = runSeconds };

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
        string? known = null;
        if (git && payload.RepoId is not null)
        {
            try
            {
                known = await BaseStoreCall(() => bases.Get(machine.Name, payload.RepoId), "read", machine, onLine, ct).ConfigureAwait(false);
                var bundle = await bundles.GetAsync(known, ct).ConfigureAwait(false);
                (local, incrementalBase) = (bundle.Path, bundle.Base);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message) { InfraKind = Jobs.InfraKind.Bundle });
            }
        }

        var step = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.EnsureIncoming(machine)), onLine, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (step.Ok && local is null)
        {
            return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, $"No {machine.Transfer.ToString().ToLowerInvariant()} payload was built for this machine.") { InfraKind = Jobs.InfraKind.Prepare });
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
                return (null, new JobResult(jobId, JobStatus.InfraError, 70, machine.Name, "Could not build the bundle: " + ex.Message) { InfraKind = Jobs.InfraKind.Bundle });
            }

            step = await UploadAndPrepare(jobId, prepared, machine, local, onLine, ct).ConfigureAwait(false);
        }

        // The worker has fetched the snapshot, so HEAD (its parent, or the snapshot itself) is in its cache: the next bundle can stop there.
        // Not when HEAD is an ancestor of the recorded base (a lanq run --baseline rerun of an older commit): the worker kept its newer
        // refs/lanq/base (see RemoteScripts.Prepare), and so does lanq, so the next run's bundle stays incremental on it.
        var head = payload.Head ?? payload.Sha;
        if (step.Ok && git && !(known is not null && await payloads.IsAncestorAsync(bundles.RepoDir, head, known, ct).ConfigureAwait(false)))
        {
            await BaseStoreCall(() => { bases.Set(machine.Name, payload.RepoId!, head); return null; }, "record", machine, onLine, ct).ConfigureAwait(false);
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

    /// <summary>How long the best-effort release after a claim that did not report back may take.</summary>
    internal static readonly TimeSpan ReleaseClaimTimeout = TimeSpan.FromSeconds(15);

    /// <summary>True when a claim's output says SLOT=FULL (it created no slot file) and no slot.</summary>
    internal static bool IsCleanFull(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries).Any(l => l == "SLOT=FULL")
        && !output.Split('\n', StringSplitOptions.TrimEntries).Any(l => l.StartsWith("SLOTS=", StringComparison.Ordinal) || (l.StartsWith("SLOT=", StringComparison.Ordinal) && l != "SLOT=FULL"));

    /// <summary>
    /// Best effort, never throws: removes the slot files this job's claim may have created on <paramref name="machine"/>
    /// (<see cref="RemoteScripts.ReleaseClaim"/>: only files whose first line is this job's id), with a short timeout.
    /// </summary>
    private async Task ReleaseClaimAsync(Machine machine, string jobId)
    {
        try
        {
            await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.ReleaseClaim(machine, jobId)), null, ReleaseClaimTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: the slots then go stale and are reclaimed as any crashed run's are.
        }
    }

    /// <summary>
    /// The slot a claim printed: "n" from <c>SLOT=n</c> (<see cref="RemoteScripts.AcquireSlot"/>), or "1,2,...,n" from <c>SLOTS=...</c>
    /// (<see cref="RemoteScripts.AcquireAll"/>: every slot, distinct and ascending); null for <c>SLOT=FULL</c> or anything unreadable.
    /// The result is what <see cref="RemoteScripts.Cleanup"/> takes back.
    /// </summary>
    internal static string? ParseSlot(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("SLOT=", StringComparison.Ordinal))
            {
                return PositiveInt(line["SLOT=".Length..]) is { } n ? n.ToString(CultureInfo.InvariantCulture) : null;
            }

            if (line.StartsWith("SLOTS=", StringComparison.Ordinal))
            {
                var slots = line["SLOTS=".Length..].Split(',').Select(PositiveInt).ToList();
                return slots.Count <= 1024 && slots.All(n => n is not null) && slots.Zip(slots.Skip(1)).All(p => p.First < p.Second)
                    ? string.Join(',', slots.Select(n => n!.Value.ToString(CultureInfo.InvariantCulture)))
                    : null;
            }
        }

        return null;

        static int? PositiveInt(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
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
        if (findings.Any(f => f.Where.EndsWith(BlobScan.WhereSuffix, StringComparison.Ordinal)))
        {
            hint += " A file git shows as binary is read anyway: a .gitattributes entry (-diff or binary) hides a file from diffs, not from lanq; take the secret out of the file.";
        }

        return new(jobId, JobStatus.Rejected, 2, null, "Refused: possible secrets in payload (" + listed + ")." + hint + " Nothing was sent.") { RejectKind = Jobs.RejectKind.Secret };
    }

    /// <summary>
    /// Builds the archive at most once per payload, on first use (several shards may ask at once), and reports what a tar.gz could not carry.
    /// Builds run one at a time with the bundle builds (<paramref name="builds"/>). A build that failed is tried once more on the next request,
    /// unless it timed out.
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
                if (_task is null || ((_task.IsFaulted || _task.IsCanceled) && _started < MaxBuilds && !TimedOut(_task)))
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
    /// local ref refs/lanq/&lt;payload id&gt;, so two at once would race on it. A build that failed is tried once more on the next request,
    /// unless it timed out.
    /// </summary>
    private sealed class LazyBundles(IPayloadBuilder builder, string jobId, string repoDir, string dir, string sha, SemaphoreSlim builds)
    {
        /// <summary>The repository the bundles are built from (for local ancestry checks).</summary>
        public string RepoDir => repoDir;

        private readonly Lock _gate = new();
        private readonly Dictionary<string, (Task<BundleBuild> Task, int Started)> _built = new(StringComparer.Ordinal);

        public Task<BundleBuild> GetAsync(string? baseSha, CancellationToken ct)
        {
            var key = baseSha ?? "";
            lock (_gate)
            {
                if (!_built.TryGetValue(key, out var entry) || ((entry.Task.IsFaulted || entry.Task.IsCanceled) && entry.Started < MaxBuilds && !TimedOut(entry.Task)))
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

    /// <summary>A build whose git call timed out (<see cref="GitTimeoutException"/>) is final: another try would only wait as long again.</summary>
    private static bool TimedOut(Task task) => task.Exception?.InnerExceptions.Any(e => e is GitTimeoutException) == true;

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
