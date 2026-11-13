using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Scheduling;
using LanQueue.Ssh;

namespace LanQueue;

/// <summary>lanq run --dry-run: what lanq run would do now, by the same checks, snapshot scan, probe and choice (see <see cref="JobRunner.PlanAsync"/>).</summary>
public static partial class Cli
{
    /// <summary>The first line of every dry run's text.</summary>
    internal const string DryRunHeader = "lanq run --dry-run: nothing was sent, no slot claimed, nothing woken";

    /// <summary>
    /// lanq run --dry-run. The plan goes to stdout (with --json to stderr, and stdout gets one <see cref="PlanJson"/>); the status line
    /// "[lanq] WouldRun: ..." (or Rejected, NoCapacity, InfraError, Cancelled) is the last line on stderr. Exit 0 would start now, 75 no
    /// machine could take it now, 2 refused, 70 the snapshot failed (or an unexpected error), 130 Ctrl+C. Every run option is accepted; --fetch
    /// and --log-tail are checked and shown, but nothing is fetched and logTail is empty.
    /// </summary>
    /// <param name="clock">The clock for allowed hours and the wake rules (tests); null is the system clock.</param>
    internal static async Task<int> DryRun(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, JobLog log, string home, string repoDir,
        TextWriter stdout, TextWriter stderr, CancellationToken ct, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;

        // No waker: this probe can never send a Wake-on-LAN packet. No BaseStore file: bases.json is never read or written. No output: the
        // plan prints nothing while it works.
        var probe = new HealthProbe(ssh, time);
        var runner = new JobRunner(config, ssh, payloads, probe, log, Path.Combine(home, "tmp"), _ => { }, clock: time);
        var req = RequestOf(p, repoDir, null);

        JobPlan plan;
        try
        {
            plan = await runner.PlanAsync(req, p.Shards, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            plan = new JobPlan(JobRunner.Cancelled, 130, "cancelled (Ctrl+C); nothing was sent");
        }
        catch (Exception ex)
        {
            plan = new JobPlan(nameof(JobStatus.InfraError), 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}")) { InfraKind = InfraKind.Unexpected };
        }

        await (p.Json ? stderr : stdout).WriteAsync(PlanText(p, plan)).ConfigureAwait(false);
        var next = NextSteps.ForPlan(p, plan);
        await NextSteps.WriteAsync(stderr, next, "[lanq] ").ConfigureAwait(false);
        await stderr.WriteLineAsync($"[lanq] {plan.Status}: {JobRunner.SafeLine(plan.Message)}").ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(PlanJsonOf(plan, p.LogTail) with { NextSteps = next, Env = EnvNames(p.Env) }, LanqJson.Default.PlanJson)).ConfigureAwait(false);
        }

        return plan.ExitCode;
    }

    /// <summary>The plan for people. A run refused or ended before any machine was looked at gets only the header (the status line says why).</summary>
    internal static string PlanText(ParsedArgs p, JobPlan plan)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');
        Line(DryRunHeader);
        if (plan.Assessed.Count == 0)
        {
            return sb.ToString();
        }

        var scan = plan.UncommittedFiles is { } files
            ? string.Create(CultureInfo.InvariantCulture, $"command line and {files} uncommitted file(s) clean")
            : "command line clean; no snapshot taken, lanq run would end before it";
        Line($"Command:  {JobRunner.SafeLine(string.Join(' ', p.Argv))}   (allowed by \"{JobRunner.SafeLine(plan.AllowedBy ?? "?")}\"; dir {JobRunner.SafeLine(p.Directory)}; secret scan: {scan})");
        Line("Needs:    " + (p.Needs.Count > 0 ? JobRunner.SafeLine(string.Join(", ", p.Needs)) : "(nothing)")
            + (p.MemGb is { } mem ? string.Create(CultureInfo.InvariantCulture, $"   --mem {mem}") : "")
            + (p.Size is { } size ? "   " + size.Text : "")
            + (p.On is { } on ? "   --on " + JobRunner.SafeLine(string.Join(',', on)) + " (only these machines)" : ""));
        if (OptionsText(p) is { } options)
        {
            Line("Options:  " + options);
        }

        if (plan.Ranked.Count > 0)
        {
            var best = plan.Ranked[0];
            var rest = plan.Ranked.Skip(1).Select(a => JobRunner.SafeLine(a.Machine.Name)).ToList();
            Line(string.Create(CultureInfo.InvariantCulture, $"Would run on: {JobRunner.SafeLine(best.Machine.Name)} (load {best.Health.LoadRatio:0.00}, slots free {best.FreeSlots}/{best.Machine.MaxJobs})")
                + (rest.Count > 0 ? $", then {string.Join(", ", rest)} if that slot is taken" : ""));
        }
        else
        {
            Line("Would run on: no machine can take it now");
        }

        if (plan.ShardCountAuto is true && plan.AutoWhy is { } why)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"--shards auto would pick {plan.ShardCount} ({JobRunner.SafeLine(why)})"));
        }

        if (plan.Timing is { } timing)
        {
            Line("Timing:   " + JobRunner.SafeLine(timing.Why));
        }

        if (plan.Placement is { } placement)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"Placement now: {(placement.Count > 0 ? JobRunner.SafeLine(JobRunner.PlacementText(placement)) : "none")}; {plan.Waiting} waiting"));
        }

        var excluded = plan.Assessed.Where(a => !a.Eligible).ToList();
        if (excluded.Count == 0)
        {
            Line("Excluded: none");
        }
        else
        {
            Line("Excluded:");
            var nameWidth = excluded.Max(a => JobRunner.SafeLine(a.Machine.Name).Length) + 2;
            var whyWidth = Math.Max(53, excluded.Max(a => JobRunner.SafeLine(JobRunner.WhyNot(a)).Length) + 2);
            foreach (var a in excluded)
            {
                var byConfig = a.Reason is Exclusion.MissingCapability or Exclusion.WindowsUnsafeArgs
                    || (a.Reason == Exclusion.WrongSize && MachineSize.Declared(a.Machine) is not null);
                var when = !a.Permanent ? "can change" : byConfig ? "never (config)" : "never (machine)";
                Line($"  {JobRunner.SafeLine(a.Machine.Name).PadRight(nameWidth)}{JobRunner.SafeLine(JobRunner.WhyNot(a)).PadRight(whyWidth)}{when}");
            }
        }

        if (plan.WouldWake is { } sleeper)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"Would wake: {JobRunner.SafeLine(sleeper)} (one Wake-on-LAN packet, then lanq run waits for it within --wait {p.WaitSeconds})"));
        }

        Line(plan.Never
            ? "No machine can ever take this job as configured: lanq run would end with exit 75 at once (waiting will not help)."
            : p.WaitSeconds == 0
                ? "With --wait 0, lanq run would end with exit 75 at once if nothing were free."
                : string.Create(CultureInfo.InvariantCulture, $"With --wait {p.WaitSeconds}, lanq run would wait up to {p.WaitSeconds} s if nothing were free, then end with exit 75."));
        return sb.ToString();
    }

    /// <summary>--timeout (when not the default), --fetch, --env and --log-tail as lanq run would use them; null when none was given.</summary>
    private static string? OptionsText(ParsedArgs p)
    {
        var parts = new List<string>();
        if (p.TimeoutMinutes != ParsedArgs.DefaultTimeoutMinutes)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"--timeout {p.TimeoutMinutes} min"));
        }

        if (p.Fetch.Count > 0)
        {
            var bytes = p.FetchMaxBytes ?? FetchLimits.DefaultMaxTotalBytes;
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"--fetch {JobRunner.SafeLine(string.Join(", ", p.Fetch))} into {JobRunner.SafeLine(p.FetchDir ?? ".lanq/artifacts")}/<job id>/ (at most {p.FetchMaxFiles ?? FetchLimits.DefaultMaxFiles} files, {JobRunner.Size(bytes)}; a dry run fetches nothing)"));
        }

        if (p.Env.Count > 0)
        {
            // The values as typed: the run line holds them anyway, and this text is only printed once the secret scan passed.
            parts.Add("--env " + JobRunner.SafeLine(string.Join(' ', p.Env.Select(e => e.Name + "=" + e.Value))));
        }

        if (p.LogTail is { } tail)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"--log-tail {tail} (empty: a dry run starts no job)"));
        }

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    /// <summary>The <see cref="PlanJson"/> of a plan, sanitised; logTail is empty with --log-tail (a dry run starts no job) and absent without it.</summary>
    internal static PlanJson PlanJsonOf(JobPlan plan, int? logTail) => new(plan.Status, plan.ExitCode)
    {
        Message = JobRunner.SafeLine(plan.Message),
        Machine = plan.Machine is { } m ? JobRunner.SafeLine(m) : null,
        Ranked = [.. plan.Ranked.Select(a => JobRunner.SafeLine(a.Machine.Name))],
        ShardCount = plan.ShardCount,
        ShardCountAuto = plan.ShardCountAuto,
        ShardCountWhy = plan.ShardCountAuto is true && plan.AutoWhy is { } why ? JobRunner.SafeLine(ShardTiming.ShardCountWhy(plan.Timing, why)) : null,
        ShardTiming = TimingJson(plan.Timing),
        Placement = plan.Placement?.Select(x => new PlacementJson(JobRunner.SafeLine(x.Machine.Name), x.Shards)).ToList(),
        Machines = [.. plan.Assessed.Select(a => new PlanMachineJson(
            JobRunner.SafeLine(a.Machine.Name), a.Eligible, a.Code, a.Why is { } why ? JobRunner.SafeLine(why) : null, a.Permanent, a.FreeSlots, a.JobSlots))],
        WouldWake = plan.WouldWake is { } w ? JobRunner.SafeLine(w) : null,
        Retryable = plan.Retryable,
        LogTail = logTail is null ? null : [],
    };

    /// <summary>The one --dry-run --json object when no plan exists (bad arguments or config).</summary>
    private static Task WritePlanJsonError(string status, int exitCode, string message, TextWriter stdout, bool logTail, IReadOnlyList<NextStepJson> steps) =>
        stdout.WriteLineAsync(JsonSerializer.Serialize(PlanJsonOf(new JobPlan(status, exitCode, message), logTail ? 1 : null) with { NextSteps = steps }, LanqJson.Default.PlanJson));
}
