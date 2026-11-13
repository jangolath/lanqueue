using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Plan;
using LanQueue.Ssh;

namespace LanQueue;

/// <summary>lanq run --plan [--plan-file PATH] [--only a,b] [--fail-fast] [--dry-run]: the jobs of the repository's lanq.yml (see docs/PLANS.md).</summary>
public static partial class Cli
{
    /// <summary>The first line of a plan's dry run.</summary>
    internal const string PlanDryRunHeader = "lanq run --plan --dry-run: nothing was sent, no slot claimed, nothing woken";

    /// <summary>
    /// lanq run --plan. Reads and checks lanq.yml (<see cref="PlanFileLoader"/>), selects (--only) and orders the jobs (<see cref="PlanGraph"/>),
    /// then runs them (<see cref="JobRunner.RunPlanAsync"/>) or, with --dry-run, says what would happen (<see cref="JobRunner.PlanFileDryAsync"/>).
    /// Lines of each job stream prefixed "[name machine] " (with --json to stderr); at the end a summary table, the Next lines, the logs of the
    /// jobs that did not complete and the status line, last on stderr. With --json stdout carries exactly one <see cref="PlanFileRunJson"/>.
    /// </summary>
    /// <param name="delay">The runner's waits (tests); null is Task.Delay.</param>
    /// <param name="clock">The runner's clock (tests); null is the system clock.</param>
    internal static async Task<int> RunPlanFile(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, HealthProbe probe, JobLog log, string home, string repoDir,
        TextWriter stdout, TextWriter stderr, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        string? path = null;
        PlanFile plan;
        PlanSelection selection;
        try
        {
            path = PlanFileLoader.Locate(p.PlanFile, repoDir);
            plan = PlanFileLoader.Load(path);
        }
        catch (PlanFileException ex)
        {
            return await PlanRefused(p, ex.Message, RejectKind.PlanFile, path, stdout, stderr).ConfigureAwait(false);
        }

        try
        {
            selection = PlanGraph.Select(plan, p.Only);
        }
        catch (PlanFileException ex)
        {
            return await PlanRefused(p, ex.Message, RejectKind.Arguments, path, stdout, stderr).ConfigureAwait(false);
        }

        // --on, --size, --min-size (0.10.0): narrow every selected job, never widen one. An unknown --on name is refused before any network; a
        // job the narrowing leaves no machine ends the plan below, before the snapshot.
        IReadOnlyList<string>? on = null;
        if (p.On is not null)
        {
            var (names, unknown) = Scheduling.Pinning.Resolve(config, p.On);
            if (unknown is not null)
            {
                return await PlanRefused(p, unknown, RejectKind.Arguments, path, stdout, stderr).ConfigureAwait(false);
            }

            on = names;
        }

        var noMachine = new Dictionary<string, string>(StringComparer.Ordinal);
        if (on is not null || p.Size is not null)
        {
            var narrowed = new List<PlanJob>(selection.Jobs.Count);
            foreach (var j in selection.Jobs)
            {
                (PlanJob Job, string? NoMachine) narrowing;
                try
                {
                    narrowing = PlanNarrowing.Narrow(j, config, on, string.Join(',', p.On ?? []), p.Size);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    // The narrowed line no longer parses: refused, never a crash.
                    return await PlanRefused(p, $"job '{j.Name}': {ex.Message}", RejectKind.Arguments, path, stdout, stderr).ConfigureAwait(false);
                }

                var (job, why) = narrowing;
                narrowed.Add(job);
                if (why is not null)
                {
                    noMachine[j.Name] = why;
                }
            }

            selection = selection with { Jobs = narrowed };
        }

        // --fetch-dir, --fetch-max, --fetch-max-files (0.10.0): the developer's choice for every selected job that fetches (lanq.yml may only
        // lower the limits and never names the folder). Refused when no selected job fetches.
        if (p.FetchDir is not null || p.FetchMaxBytes is not null || p.FetchMaxFiles is not null)
        {
            if (!PlanFetch.AnyFetches(selection.Jobs))
            {
                return await PlanRefused(p, PlanFetch.NoFetchingJob, RejectKind.Arguments, path, stdout, stderr).ConfigureAwait(false);
            }

            var fetchDir = p.FetchDir is null ? null : Path.GetFullPath(p.FetchDir);
            var fetching = new List<PlanJob>(selection.Jobs.Count);
            foreach (var j in selection.Jobs)
            {
                try
                {
                    fetching.Add(PlanFetch.Apply(j, fetchDir, p.FetchMaxBytes, p.FetchMaxFiles));
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    return await PlanRefused(p, $"job '{j.Name}': {ex.Message}", RejectKind.Arguments, path, stdout, stderr).ConfigureAwait(false);
                }
            }

            selection = selection with { Jobs = fetching };
        }

        var expected = PlanGraph.ExpectedFromHistory(log, time.GetUtcNow(), ct);
        var ordered = PlanGraph.Order(selection, expected);
        var planFile = Path.GetFullPath(plan.Path);
        var specs = ordered.Select(j => new PlanJobSpec(j.Name, PlanJobRequest(j, p, repoDir, fetch: !p.DryRun) with { PlanFile = planFile }, j.Args.Shards, selection.After[j.Name])
        {
            FileOrder = j.Index,
            Where = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(plan.Path)}: job '{j.Name}' (line {j.Line})"),
        }).ToList();
        var jobArgs = ordered.ToDictionary(j => j.Name, j => j.Args, StringComparer.Ordinal);
        var narrowedOut = noMachine.Count > 0 ? NarrowedOut(specs, noMachine) : null;

        // --plan-timeout (0.10.0) replaces lanq.yml's plan_timeout:.
        var planTimeout = p.PlanTimeoutMinutes ?? plan.PlanTimeoutMinutes;
        if (p.DryRun)
        {
            if (planTimeout is { } limit && CriticalPathSeconds(ordered, selection, expected) is { } chain && chain > limit * 60)
            {
                plan = plan with
                {
                    Warnings = [.. plan.Warnings, string.Create(
                        CultureInfo.InvariantCulture,
                        $"history's expected critical path is about {Math.Ceiling(chain / 60):0} min, over plan_timeout {limit} min: the plan would likely time out")],
                };
            }

            return await PlanFileDryRun(p, config, ssh, payloads, log, home, plan, selection, ordered, specs, expected, stdout, stderr, ct, clock, narrowedOut, planTimeout)
                .ConfigureAwait(false);
        }

        var jobOutput = p.Json ? stderr : stdout;
        var gate = new Lock();
        void Out(string line)
        {
            lock (gate)
            {
                jobOutput.WriteLine(line);
            }
        }

        // lanq's own words about the plan go to stderr (the static warnings too), before any job's output.
        var label = Path.GetFileName(plan.Path);
        await stderr.WriteLineAsync(JobRunner.SafeLine($"[lanq] plan {label}: {ordered.Count} job(s); order: {string.Join(", ", ordered.Select(j => j.Name))}")).ConfigureAwait(false);
        foreach (var w in plan.Warnings)
        {
            await stderr.WriteLineAsync(JobRunner.SafeLine($"[lanq] {label}: warning: {w}")).ConfigureAwait(false);
        }

        foreach (var note in selection.Notes)
        {
            await stderr.WriteLineAsync(JobRunner.SafeLine("[lanq] " + note)).ConfigureAwait(false);
        }

        if (planTimeout is { } minutes && narrowedOut is null)
        {
            await stderr.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"[lanq] plan_timeout: {minutes} min for the whole plan ({(p.PlanTimeoutMinutes is null ? "lanq.yml" : "--plan-timeout")}), from after the snapshot")).ConfigureAwait(false);
        }

        var runner = new JobRunner(config, ssh, payloads, probe, log, Path.Combine(home, "tmp"), Out, delay, clock, new BaseStore(Path.Combine(home, "bases.json")));
        var started = System.Diagnostics.Stopwatch.StartNew();
        PlanRunResult result;
        if (narrowedOut is not null)
        {
            // Nothing was checked further, snapshotted, sent or claimed.
            result = narrowedOut;
        }
        else
        {
            try
            {
                result = await runner.RunPlanAsync(specs, p.FailFast, ct, planTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C before any job started (during the snapshot): nothing ran, nothing to clean up.
                result = new PlanRunResult(JobRunner.Cancelled, 130, "cancelled (Ctrl+C) before any job started", []);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result = new PlanRunResult(nameof(JobStatus.InfraError), 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), [])
                {
                    InfraKind = InfraKind.Unexpected,
                };
            }

            PruneLogs(log, config, stderr);
        }
        if (result.Jobs.Count > 0)
        {
            await jobOutput.WriteAsync(PlanTable(result)).ConfigureAwait(false);
        }

        // What to do next, then the logs of the jobs that did not complete (--json carries every logPath); all before the status line.
        var next = NextSteps.ForPlanFile(p, result, jobArgs);

        // results: (0.11.0): every completed job whose results show no test executed, whatever the plan's status.
        var noTests = result.Jobs.Select(j => (j.Name, Results: NoTestsRan(j.Status, PlanJobResults(j)))).Where(x => x.Results is not null).ToList();
        next = WithNoTestsStep(next, [.. noTests.Select(x => x.Name)]);
        foreach (var (name, r) in noTests)
        {
            await stderr.WriteLineAsync(NoTestsLine(name, r!)).ConfigureAwait(false);
        }

        await WriteBaselineLines(stderr, PlanBaselines(result)).ConfigureAwait(false);
        await NextSteps.WriteAsync(stderr, next, "[lanq] ").ConfigureAwait(false);
        if (!p.Json)
        {
            foreach (var line in PlanLogLines(result))
            {
                await stderr.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        await stderr.WriteLineAsync($"[lanq] {result.Status}: {JobRunner.SafeLine(result.Message)}").ConfigureAwait(false);
        if (p.Json)
        {
            var json = PlanRunJsonOf(result, plan.Path, started.Elapsed, [.. ordered.Select(j => j.Name)], plan.Warnings, specs, p.LogTail) with
            {
                NextSteps = next,
                ScanSkipped = ScanSkippedJson(runner.ScanSkipped),
                PlanTimeoutMinutes = planTimeout,
                PlanTimedOut = Flag(result.PlanTimedOut),
                ResultsTotal = ResultsTotalOf(result),
            };
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.PlanFileRunJson)).ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    /// <summary>
    /// The request one plan job makes: its lanq run line's options (<see cref="RequestOf"/>: needs, dir, mem, size, on, timeout, fetch) and the
    /// plan's own --wait and --no-wake. Files a job fetches go to .lanq/artifacts at the repository top, as for lanq run --fetch; a dry run
    /// (<paramref name="fetch"/> false) creates no folder.
    /// </summary>
    internal static JobRequest PlanJobRequest(PlanJob job, ParsedArgs p, string repoDir, bool fetch = true)
    {
        FetchRequest? request = null;
        if (fetch && job.Args.Fetch.Count > 0)
        {
            var total = job.Args.FetchMaxBytes ?? FetchLimits.DefaultMaxTotalBytes;
            var (root, anchor) = FetchRoot(job.Args.FetchDir, repoDir);
            request = new FetchRequest(job.Args.Fetch, root, new FetchLimits(job.Args.FetchMaxFiles ?? FetchLimits.DefaultMaxFiles, total, total), anchor);
        }

        return RequestOf(job.Args, repoDir, request) with { Wait = TimeSpan.FromSeconds(p.WaitSeconds), NoWake = p.NoWake };
    }

    /// <summary>
    /// The plan ended before the snapshot because --on / --size / --min-size leave some job no machine (<see cref="PlanNarrowing.Narrow"/>): each
    /// such job NoCapacity (75, not retryable, nothing excluded: no machine was looked at), the rest Skipped, and <see cref="PlanRunResult.Runnable"/>
    /// the jobs the narrowing leaves a machine, in file order (the suggested rerun's --only).
    /// </summary>
    internal static PlanRunResult NarrowedOut(IReadOnlyList<PlanJobSpec> ordered, IReadOnlyDictionary<string, string> noMachine)
    {
        var first = ordered.Where(j => noMachine.ContainsKey(j.Name)).MinBy(j => j.FileOrder)!.Name;

        // A job that starts after a narrowed-out job, directly or through others, cannot run either: --only would drop its after: edge and
        // start it without its prerequisite.
        var blocked = new HashSet<string>(noMachine.Keys, StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var j in ordered.Where(j => !blocked.Contains(j.Name) && j.After.Any(blocked.Contains)))
            {
                grew |= blocked.Add(j.Name);
            }
        }

        List<PlanJobOutcome> jobs = [.. ordered.Select(j => noMachine.TryGetValue(j.Name, out var why)
            ? new PlanJobOutcome(j.Name, nameof(JobStatus.NoCapacity), 75, why, j.After) { Retryable = false, Excluded = [], FileOrder = j.FileOrder }
            : new PlanJobOutcome(j.Name, PlanJobOutcome.Skipped, 0, $"not started: {noMachine[first]}, so the plan did not start", j.After) { FileOrder = j.FileOrder })];
        return JobRunner.AggregatePlan(jobs, cancelled: false) with
        {
            Runnable = [.. ordered.Where(j => !blocked.Contains(j.Name)).OrderBy(j => j.FileOrder).Select(j => j.Name)],
        };
    }

    /// <summary>A plan refused before any job ran (the plan file, --only, bad arguments or config): the status line, Next lines and one JSON object.</summary>
    private static async Task<int> PlanRefused(ParsedArgs p, string message, RejectKind kind, string? path, TextWriter stdout, TextWriter stderr)
    {
        message = JobRunner.SafeLine(message);
        var result = new PlanRunResult(nameof(JobStatus.Rejected), 2, message, []) { RejectKind = kind };
        var steps = p.DryRun
            ? NextSteps.ForPlanFileDry(p, result.Status, message, null, kind, null, null)
            : NextSteps.ForPlanFile(p, result, new Dictionary<string, ParsedArgs>());
        if (p.DryRun)
        {
            await (p.Json ? stderr : stdout).WriteLineAsync(PlanDryRunHeader).ConfigureAwait(false);
        }

        await NextSteps.WriteAsync(stderr, steps, "[lanq] ").ConfigureAwait(false);
        await stderr.WriteLineAsync("[lanq] Rejected: " + message).ConfigureAwait(false);
        if (p.Json)
        {
            await WritePlanFileJsonError(p.DryRun, "Rejected", 2, message, stdout, steps, path is null ? null : Path.GetFullPath(path), p.LogTail is not null).ConfigureAwait(false);
        }

        return 2;
    }

    /// <summary>The one --plan --json object when no plan result exists (bad arguments, config, plan file): <see cref="PlanFileRunJson"/> or, with --dry-run, <see cref="PlanFileDryJson"/>.</summary>
    internal static Task WritePlanFileJsonError(
        bool dryRun, string status, int exitCode, string message, TextWriter stdout, IReadOnlyList<NextStepJson> steps, string? path = null, bool logTail = false) =>
        stdout.WriteLineAsync(dryRun
            ? JsonSerializer.Serialize(
                new PlanFileDryJson(status, exitCode) { Plan = path is null ? null : JobRunner.SafeLine(path), Message = message, LogTail = logTail ? [] : null, NextSteps = steps },
                LanqJson.Default.PlanFileDryJson)
            : JsonSerializer.Serialize(
                new PlanFileRunJson(status, exitCode, path is null ? null : JobRunner.SafeLine(path), 0, message, [], []) { NextSteps = steps },
                LanqJson.Default.PlanFileRunJson));

    /// <summary>The summary table: one line per job, in launch order (a skipped job says why).</summary>
    internal static string PlanTable(PlanRunResult result)
    {
        var width = Math.Max(12, result.Jobs.Max(j => j.Name.Length) + 1);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[lanq] {"job".PadRight(width)} shards  {"machine(s)",-19} {"status",-12} {"exit",4}  {"duration",8}\n");
        foreach (var j in result.Jobs)
        {
            var machines = j.Sharded is { } s
                ? string.Join(',', s.Shards.Select(x => x.Machine).OfType<string>().Distinct(StringComparer.Ordinal))
                : j.Machine ?? "";
            var exit = j.Status == PlanJobOutcome.Skipped ? "-" : j.ExitCode.ToString(CultureInfo.InvariantCulture);
            var note = j.Status == PlanJobOutcome.Skipped ? $"  ({j.Message})" : "";
            sb.Append(JobRunner.SafeLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[lanq] {j.Name.PadRight(width)} {(j.Sharded is { } r ? r.Count.ToString(CultureInfo.InvariantCulture) : "-"),-7} {(machines.Length > 0 ? machines : "-"),-19} {j.Status,-12} {exit,4}  {j.Duration.TotalSeconds,7:0.0}s{note}")))
                .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Every baseline rerun of a plan, labelled "job unit " (a shard: "job unit shard 2/3 "), in launch order.</summary>
    internal static IEnumerable<(string Label, BaselineResult Baseline)> PlanBaselines(PlanRunResult result)
    {
        foreach (var j in result.Jobs)
        {
            if (j.Baseline is { } b)
            {
                yield return ($"job {j.Name} ", b);
            }

            foreach (var s in j.Sharded?.Shards.Where(s => s.Baseline is not null) ?? [])
            {
                yield return (string.Create(CultureInfo.InvariantCulture, $"job {j.Name} shard {s.Index}/{j.Sharded!.Count} "), s.Baseline!);
            }
        }
    }

    /// <summary>"[lanq] log: PATH (build)" for each job, and "(unit 2/3)" for each shard, that did not complete and has a log.</summary>
    internal static IEnumerable<string> PlanLogLines(PlanRunResult result)
    {
        foreach (var j in result.Jobs.Where(j => j.Status != nameof(JobStatus.Completed)))
        {
            if (j.LogPath is { } single)
            {
                yield return $"[lanq] log: {JobRunner.SafeLine(single)} ({j.Name})";
            }

            foreach (var s in j.Sharded?.Shards.Where(s => s.Status != nameof(JobStatus.Completed) && s.LogPath is not null) ?? [])
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"[lanq] log: {JobRunner.SafeLine(s.LogPath!)} ({j.Name} {s.Index}/{j.Sharded!.Count})");
            }
        }
    }

    /// <summary>The <see cref="PlanFileRunJson"/> of a plan result (sanitised).</summary>
    internal static PlanFileRunJson PlanRunJsonOf(
        PlanRunResult result, string? path, TimeSpan elapsed, IReadOnlyList<string> order, IReadOnlyList<string> warnings, IReadOnlyList<PlanJobSpec> specs, int? logTail) => new(
        result.Status,
        result.ExitCode,
        path is null ? null : JobRunner.SafeLine(path),
        Math.Round(elapsed.TotalSeconds, 1),
        JobRunner.SafeLine(result.Message),
        order,
        [.. result.Jobs.Select(j =>
        {
            var req = specs.FirstOrDefault(s => s.Name == j.Name)?.Request;
            return PlanJobJsonOf(j, req?.Fetch is not null, logTail) with { Env = req is null ? null : EnvNames(req.Env), Exclusive = Flag(req?.Exclusive == true) };
        })])
        {
            Retryable = result.Status == nameof(JobStatus.NoCapacity) ? result.Retryable ?? true : null,
            Excluded = result.Status == nameof(JobStatus.NoCapacity) ? ExcludedJsons(result.Excluded) : null,
            Warnings = [.. warnings.Select(JobRunner.SafeLine)],
        };

    /// <summary>One job of <see cref="PlanFileRunJson"/>: exitCode null only for Skipped; logTail (single job) and fetch keys as lanq run --json.</summary>
    internal static PlanFileJobJson PlanJobJsonOf(PlanJobOutcome j, bool fetch, int? logTail)
    {
        var noCapacity = j.Status == nameof(JobStatus.NoCapacity);
        var shards = j.Sharded is { } s ? ShardJson(s, TimeSpan.Zero, fetch, logTail).Shards : null;
        var single = j.Sharded is null && fetch ? WithFetch(new RunJson(j.Status, j.ExitCode, null, null, 0, ""), true, j.Single?.Fetch) : null;
        return new PlanFileJobJson(
            j.Name,
            j.Status,
            j.Status == PlanJobOutcome.Skipped ? null : j.ExitCode,
            j.After,
            j.Machine is { } m ? JobRunner.SafeLine(m) : null,
            j.JobId,
            j.StartedSeconds,
            Math.Round(j.Duration.TotalSeconds, 1),
            JobRunner.SafeLine(j.Message),
            j.LogPath is { } l ? JobRunner.SafeLine(l) : null,
            j.Sharded?.Count,
            shards)
        {
            LogTail = j.Sharded is null && logTail is { } n ? j.LogPath is null ? [] : JobLog.Tail(j.LogPath, n) : null,
            Retryable = noCapacity ? j.Retryable ?? true : null,
            Excluded = noCapacity ? ExcludedJsons(j.Excluded) : null,
            Fetched = single?.Fetched,
            FetchSkipped = single?.FetchSkipped,
            FetchSkippedMore = single?.FetchSkippedMore,
            FetchDir = single?.FetchDir,
            FetchError = single?.FetchError,
            ShardTiming = TimingJson(j.Sharded?.Timing),
            Baseline = BaselineJsonOf(j.Baseline),
            Results = ResultsJsonOf(PlanJobResults(j)),
        };
    }

    /// <summary>
    /// lanq run --plan --dry-run: the plan for people (stdout; with --json to stderr and stdout gets one <see cref="PlanFileDryJson"/>), the
    /// Next lines and the status line last on stderr. Exit 0 the plan would start, 75 a first-wave job has no machine and nothing to wait behind
    /// (or a job can never run), 2 refused, 70 the snapshot failed, 130 Ctrl+C. Nothing is sent, claimed, logged or woken.
    /// </summary>
    private static async Task<int> PlanFileDryRun(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, JobLog log, string home, PlanFile plan, PlanSelection selection,
        IReadOnlyList<PlanJob> ordered, IReadOnlyList<PlanJobSpec> specs, Func<PlanJob, double?> expected, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        TimeProvider? clock, PlanRunResult? narrowedOut = null, int? planTimeout = null)
    {
        var time = clock ?? TimeProvider.System;

        // No waker, no BaseStore file, no output: as lanq run --dry-run.
        var runner = new JobRunner(config, ssh, payloads, new HealthProbe(ssh, time), log, Path.Combine(home, "tmp"), _ => { }, clock: time);
        PlanDryResult dry;
        if (narrowedOut is not null)
        {
            // --on / --size / --min-size leave a job no machine: the plan would end before the snapshot, so nothing is looked at.
            dry = new PlanDryResult(narrowedOut.Status, narrowedOut.ExitCode, narrowedOut.Message, [.. ordered.Zip(narrowedOut.Jobs).Select(x => new PlanJobDry(
                x.First.Name,
                x.First.After.Count > 0 ? "after" : "now",
                x.Second.Status == PlanJobOutcome.Skipped ? PlanJobDry.Pending : x.Second.Status,
                x.Second.Message)
            {
                Retryable = x.Second.Status == PlanJobOutcome.Skipped ? null : false,
                ShardCount = x.First.Args.Shards?.Count,
            })])
            {
                Decided = narrowedOut.Decided,
                Retryable = false,
            };
        }
        else
        {
            try
            {
                dry = await runner.PlanFileDryAsync(specs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                dry = new PlanDryResult(JobRunner.Cancelled, 130, "cancelled (Ctrl+C); nothing was sent", []);
            }
            catch (Exception ex)
            {
                dry = new PlanDryResult(nameof(JobStatus.InfraError), 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), []) { InfraKind = InfraKind.Unexpected };
            }
        }

        var history = ordered.Any(j => expected(j) is not null);
        await (p.Json ? stderr : stdout).WriteAsync(PlanFileDryText(plan, selection, ordered, dry, expected, history, runner.ScanSkipped.Count, planTimeout)).ConfigureAwait(false);
        var decided = dry.Decided is { } d ? ordered.FirstOrDefault(j => j.Name == d)?.Args : null;
        var next = NextSteps.ForPlanFileDry(p, dry.Status, JobRunner.SafeLine(dry.Message), dry.Retryable, dry.RejectKind, dry.InfraKind, decided, narrowedOut);
        await NextSteps.WriteAsync(stderr, next, "[lanq] ").ConfigureAwait(false);
        await stderr.WriteLineAsync($"[lanq] {dry.Status}: {JobRunner.SafeLine(dry.Message)}").ConfigureAwait(false);
        if (p.Json)
        {
            var json = PlanFileDryJsonOf(plan, ordered, dry, expected, p.LogTail) with
            {
                NextSteps = next,
                ScanSkipped = ScanSkippedJson(runner.ScanSkipped),
                PlanTimeoutMinutes = planTimeout,
            };
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.PlanFileDryJson)).ConfigureAwait(false);
        }

        return dry.ExitCode;
    }

    /// <summary>The plan's dry run for people: the header, the plan and snapshot lines, then per job its lanq run line and what it would do.</summary>
    /// <param name="scanSkipped">How many files the secret scan did not read in full (scanSkipped), noted on the Snapshot line.</param>
    /// <param name="planTimeout">--plan-timeout or plan_timeout: in minutes, shown on its own line; null for none.</param>
    internal static string PlanFileDryText(
        PlanFile plan, PlanSelection selection, IReadOnlyList<PlanJob> ordered, PlanDryResult dry, Func<PlanJob, double?> expected, bool history, int scanSkipped = 0,
        int? planTimeout = null)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(JobRunner.SafeLine(text)).Append('\n');
        Line(PlanDryRunHeader);
        var label = Path.GetFileName(plan.Path);
        Line(string.Create(
            CultureInfo.InvariantCulture,
            $"Plan:     {label} (version {plan.Version}): {ordered.Count} job(s); order: {string.Join(", ", ordered.Select(j => j.Name))}{(history ? " (longest first from history)" : "")}"));
        if (planTimeout is { } limit)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"Timeout:  plan_timeout {limit} min for the whole plan, from after the snapshot (jobs and baselines)"));
        }

        foreach (var w in plan.Warnings)
        {
            Line("Warning:  " + w);
        }

        foreach (var note in selection.Notes)
        {
            Line("Note:     " + note);
        }

        if (dry.UncommittedFiles is { } files)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"Snapshot: command lines and {files} uncommitted file(s) clean{SkippedNote(scanSkipped)}"));
        }

        if (dry.Jobs.Count == 0)
        {
            return sb.ToString();
        }

        var width = Math.Max(12, ordered.Max(j => j.Name.Length) + 2);
        var indent = new string(' ', width);
        foreach (var (job, d) in ordered.Zip(dry.Jobs))
        {
            Line(job.Name.PadRight(width) + (job.RunCommand ?? string.Join(' ', ["lanq", .. job.RunArgs])));
            var what = d.Status switch
            {
                JobPlan.WouldRun when d.ShardCountAuto is true && d.AutoWhy is { } why => $"--shards auto would pick {d.ShardCount} ({why}); {d.Message}",
                _ => d.Message,
            };
            if (expected(job) is { } seconds && d.Status == JobPlan.WouldRun)
            {
                what += string.Create(CultureInfo.InvariantCulture, $"; about {Math.Max(1, Math.Round(seconds / 60)):0} min of work (from history)");
            }

            Line(indent + what);
            if (d.Timing is { } timing)
            {
                Line(indent + "timing: " + timing.Why);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// What history expects the plan's longest chain of after: jobs to take, in seconds: each job's expected time (none counts 0) plus the
    /// longest chain before it. Null when history knows none of the jobs.
    /// </summary>
    internal static double? CriticalPathSeconds(IReadOnlyList<PlanJob> ordered, PlanSelection selection, Func<PlanJob, double?> expected)
    {
        if (!ordered.Any(j => expected(j) is not null))
        {
            return null;
        }

        var byName = ordered.ToDictionary(j => j.Name, StringComparer.Ordinal);
        var memo = new Dictionary<string, double>(StringComparer.Ordinal);
        double Of(PlanJob j)
        {
            if (memo.TryGetValue(j.Name, out var known))
            {
                return known;
            }

            var before = selection.After.GetValueOrDefault(j.Name, []).Where(byName.ContainsKey).Select(n => Of(byName[n])).DefaultIfEmpty(0).Max();
            return memo[j.Name] = (expected(j) ?? 0) + before;
        }

        return ordered.Max(Of);
    }

    /// <summary>The <see cref="PlanFileDryJson"/> of a plan's dry run (sanitised); logTail is empty with --log-tail and absent without it.</summary>
    internal static PlanFileDryJson PlanFileDryJsonOf(PlanFile plan, IReadOnlyList<PlanJob> ordered, PlanDryResult dry, Func<PlanJob, double?> expected, int? logTail) => new(dry.Status, dry.ExitCode)
    {
        Plan = JobRunner.SafeLine(plan.Path),
        Message = JobRunner.SafeLine(dry.Message),
        Order = [.. ordered.Select(j => j.Name)],
        Warnings = [.. plan.Warnings.Select(JobRunner.SafeLine)],
        Retryable = dry.Retryable,
        LogTail = logTail is null ? null : [],
        Jobs = [.. ordered.Zip(dry.Jobs).Select(x => new PlanFileDryJobJson(
            x.First.Name, x.First.After, ["lanq", .. x.First.RunArgs], x.First.RunCommand, x.Second.Start, x.Second.Status)
        {
            Message = JobRunner.SafeLine(x.Second.Message),
            Machine = x.Second.Machine is { } m ? JobRunner.SafeLine(m) : null,
            ShardCount = x.Second.ShardCount,
            ShardCountAuto = x.Second.ShardCountAuto,
            Placement = x.Second.Placement?.Select(p => new PlacementJson(JobRunner.SafeLine(p.Machine.Name), p.Shards)).ToList(),
            ShardCountWhy = x.Second.ShardCountAuto is true && x.Second.AutoWhy is { } why ? JobRunner.SafeLine(Scheduling.ShardTiming.ShardCountWhy(x.Second.Timing, why)) : null,
            ShardTiming = TimingJson(x.Second.Timing),
            ExpectedMinutes = expected(x.First) is { } s ? Math.Round(s / 60, 1) : null,
            Machines = [.. x.Second.Assessed.Select(a => new PlanMachineJson(
                JobRunner.SafeLine(a.Machine.Name), a.Eligible, a.Code, a.Why is { } w ? JobRunner.SafeLine(w) : null, a.Permanent, a.FreeSlots, DryJobSlots(a, x.First.Args.Exclusive)))],
            Retryable = x.Second.Retryable,
            Env = EnvNames(x.First.Args.Env),
            Exclusive = Flag(x.First.Args.Exclusive),
        })],
    };
}
