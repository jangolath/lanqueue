using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Policy;
using LanQueue.Scheduling;
using LanQueue.Ssh;

namespace LanQueue.Jobs;

/// <summary>0.13.0 B3: asks the person at the terminal whether a job may run on this computer. Null: no answer in time.</summary>
public interface IFallbackPrompt
{
    Task<bool?> AskAsync(string question, CancellationToken ct);
}

/// <summary>
/// 0.13.0 B3 (review M7): the terminal prompt of policy.fallback ask. One reader thread feeds a queue for the whole run, so a prompt that timed
/// out never leaves a read behind that would swallow the answer to the next one; lines typed before a prompt is shown are discarded; the
/// timeout ends only the wait (= no answer), never the reader.
/// </summary>
public sealed class TerminalPrompt : IFallbackPrompt
{
    private readonly TextReader input;
    private readonly TextWriter error;
    private readonly TimeSpan timeout;

    /// <summary>Starts the reader at once, so that lines typed before the first prompt are read, and discarded, too.</summary>
    public TerminalPrompt(TextReader input, TextWriter error, TimeSpan timeout)
    {
        this.input = input;
        this.error = error;
        this.timeout = timeout;
        StartReader();
    }

    /// <summary>How long a prompt waits for y or n.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly BlockingCollection<string?> _lines = [];
    private int _started;

    private void StartReader()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var reader = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = input.ReadLine()) is not null)
                {
                    _lines.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the terminal went away: nothing more to read
            }

            _lines.Add(null); // end of input: every later prompt gets "no"
        })
        {
            IsBackground = true,
            Name = "lanq fallback prompt",
        };
        reader.Start();
    }

    public async Task<bool?> AskAsync(string question, CancellationToken ct)
    {
        StartReader();
        while (_lines.TryTake(out var early))
        {
            if (early is null)
            {
                _lines.Add(null);
                return false;
            }
        }

        await error.WriteLineAsync(question).ConfigureAwait(false);
        await error.FlushAsync(ct).ConfigureAwait(false);
        var answer = await Task.Run(() => _lines.TryTake(out var line, (int)timeout.TotalMilliseconds, ct) ? line : "\0timeout", CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return answer switch
        {
            "\0timeout" => null,
            null => false,
            _ => answer.Trim().ToLowerInvariant() is "y" or "yes",
        };
    }
}

/// <summary>0.13.0 B3: when policy.fallback ask may prompt at all.</summary>
public static class FallbackAsk
{
    /// <summary>Environment variables that mark a coding agent or other unattended caller: ask never prompts under one.</summary>
    internal static readonly string[] AgentMarkers = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CODEX_*", "CURSOR_AGENT", "GEMINI_CLI", "AIDER_*"];

    /// <summary>
    /// Why ask cannot prompt here, or null when it may: stdin and stderr must both be terminals, --json off, CI unset, TERM not dumb,
    /// LANQ_NONINTERACTIVE unset and no known agent marker set (a stricter rule than makeready --add's).
    /// </summary>
    public static string? WhyNot(bool json, bool stdinRedirected, bool stderrRedirected, IDictionary env)
    {
        string? Get(string name) => env[name] as string;
        if (json)
        {
            return "--json";
        }

        if (stdinRedirected || stderrRedirected)
        {
            return "no terminal";
        }

        if (!string.IsNullOrEmpty(Get("CI")))
        {
            return "CI is set";
        }

        if (string.Equals(Get("TERM"), "dumb", StringComparison.Ordinal))
        {
            return "TERM=dumb";
        }

        if (!string.IsNullOrEmpty(Get("LANQ_NONINTERACTIVE")))
        {
            return "LANQ_NONINTERACTIVE is set";
        }

        foreach (DictionaryEntry e in env)
        {
            if (e.Key is string name && !string.IsNullOrEmpty(e.Value as string)
                && AgentMarkers.Any(m => m.EndsWith('*') ? name.StartsWith(m[..^1], StringComparison.Ordinal) : name == m))
            {
                return "a coding agent runs lanq";
            }
        }

        return null;
    }
}

/// <summary>What the fallback gate decided for a job that would otherwise end NoCapacity (0.13.0 B3).</summary>
/// <param name="Go">True: the fallback is engaged; the job looks again with this computer as its last candidate.</param>
/// <param name="Row">The fallback's own exclusion for the NoCapacity result (it is named next to the pool's machines), or null.</param>
/// <param name="Note">Appended to the NoCapacity message ("" for none).</param>
/// <param name="Hint">The NoCapacity next-step hint (<see cref="JobResult.FallbackHint"/>).</param>
internal sealed record FallbackVerdict(bool Go, Assessment? Row, string Note, FallbackHint? Hint)
{
    public static readonly FallbackVerdict None = new(false, null, "", null);
}

public sealed partial class JobRunner
{
    /// <summary>With policy fallback on or ask and no wait flag given, a job waits at least this long for a pool slot before it falls back (M1).</summary>
    internal static readonly TimeSpan FallbackSlotFloor = TimeSpan.FromSeconds(120);

    /// <summary>The prompt of policy.fallback ask; null when lanq may not prompt here (<see cref="NoAskReason"/> says why), so ask acts as off.</summary>
    public IFallbackPrompt? Prompt { get; init; }

    /// <summary>Why ask cannot prompt (<see cref="FallbackAsk.WhyNot"/>), for the line that says ask acts as off; null when it can.</summary>
    public string? NoAskReason { get; init; }

    /// <summary>Whether lanq runs elevated or as root (the fallback then refuses); tests replace it.</summary>
    public Func<bool> Elevated { get; init; } = LocalProcessClient.IsElevated;

    // One runner is one lanq run (a single job, a shard set or a plan): once one unit of it fell back, its other units may use the fallback's
    // slot like any slot (M2), while the pool's machines stay ahead of it. A prompt that got no answer is not repeated in the run (M7).
    private volatile bool _fallbackEngaged;
    private volatile bool _notAsking;
    private readonly SemaphoreSlim _askGate = new(1, 1);

    /// <summary>The policy for this job (0.13.0 B3): off without a fallback entry, for a lanq.yml fallback: off job or with --on; else --fallback, else policy.fallback.</summary>
    internal FallbackMode FallbackModeOf(JobRequest req) =>
        config.Fallback is null || req.FallbackOff || req.On is not null ? FallbackMode.Off : req.Fallback ?? config.Policy.Fallback;

    /// <summary>The wait budgets a job waits under: its own, but with a fallback in play and no wait flag given, at least 120 s for a slot (M1).</summary>
    internal WaitBudgets BudgetsOf(JobRequest req)
    {
        var budgets = req.EffectiveBudgets;
        return FallbackModeOf(req) != FallbackMode.Off && !req.WaitGiven && budgets.Slot < FallbackSlotFloor ? budgets with { Slot = FallbackSlotFloor } : budgets;
    }

    /// <summary>
    /// True when the fallback could ever take the job as far as the config says (its capabilities, a declared size:, the job's --env), with the
    /// policy not off: then the job is not ended before any probe even when no pool machine could ever take it (<see cref="NeverByConfig"/>).
    /// </summary>
    private bool FallbackCouldTake(JobRequest req) =>
        FallbackModeOf(req) != FallbackMode.Off && config.Fallback is { } fb && FallbackFits(fb, req);

    /// <summary>The config-only checks of the fallback for a job: capabilities, a declared size:, the job's --env.</summary>
    private static bool FallbackFits(Machine fb, JobRequest req) =>
        Eligibility.Missing(fb, req.Needs).Count == 0 && Eligibility.DeclaredSizeMismatch(fb, req.Size) is null
        && EnvExclusion(fb, req.Env, req.Env.Count > 0 && !JobPolicy.ValidateForWindows(req.Argv).Ok) is null;

    /// <summary>The hint a NoCapacity decided before any probe carries (<see cref="Precheck"/>): the same as the gate's off answer.</summary>
    private FallbackVerdict PrecheckFallbackHint(JobRequest req) =>
        req.FallbackOff || req.On is not null ? FallbackVerdict.None
        : config.Fallback is not { } fb ? FallbackVerdict.None with { Hint = FallbackHint.SetUp }
        : FallbackFits(fb, req) ? new FallbackVerdict(false, null, $" This computer ({fb.Name}) could run it: lanq run --fallback on ...", FallbackHint.TurnOn)
        : FallbackVerdict.None;

    /// <summary>The fallback judged as a candidate after the pool (only once this run engaged it), or null.</summary>
    private async Task<Assessment?> EngagedFallbackAsync(JobRequest req, JobNeeds needs, CancellationToken ct) =>
        _fallbackEngaged && FallbackModeOf(req) != FallbackMode.Off && config.Fallback is { } fb
            ? await AssessFallbackAsync(fb, req, needs, ct).ConfigureAwait(false)
            : null;

    /// <summary>The fallback judged for the job like a worker (probe, capabilities, size, --mem with its floor, Windows, --env, its own slots).</summary>
    private async Task<Assessment> AssessFallbackAsync(Machine fb, JobRequest req, JobNeeds needs, CancellationToken ct)
    {
        if (FallbackRefusal(fb, req) is { } refused)
        {
            return new Assessment(fb, Unprobed(fb), Exclusion.FallbackRefused, refused, true, 0, 0);
        }

        var health = CountStuckStale(await ProbeSharedAsync(fb, ct).ConfigureAwait(false));
        var own = needs with { EnvRuledOut = EnvRuledOut([fb], req.Env, needs.WindowsUnsafe) };
        return Scheduler.Assess([(fb, health)], own)[0];
    }

    /// <summary>
    /// Why the fallback may never run this job here: lanq runs elevated or as root (the job would inherit those rights), or the fallback's
    /// work_root is inside the repository or holds it (cleanup deletes under work_root/jobs). Null when neither.
    /// </summary>
    private string? FallbackRefusal(Machine fb, JobRequest req)
    {
        if (Elevated())
        {
            return "lanq runs elevated (administrator) or as root; the fallback never hands those rights to a job";
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var root = Full(fb.WorkRoot);
        var repo = Full(req.RepoDir);
        return root.StartsWith(repo, comparison) || repo.StartsWith(root, comparison)
            ? "its work_root is inside the repository being run (or holds it); give the fallback a work_root of its own"
            : null;
    }

    /// <summary>
    /// The moment a job (or the waiting shards of a run) would end NoCapacity (0.13.0 B3): after its wait budgets, or at once when every pool
    /// machine is ruled out for good. Off: no fallback, and the result carries the hint for the next steps. On: the fallback is judged like a
    /// worker; eligible, it is engaged (the caller looks again with it as the last candidate). Ask: the same, after a yes at the terminal.
    /// </summary>
    /// <param name="why">Why the pool could not take the job, for the prompt ("no pool slot", "no pool machine can take it").</param>
    private async Task<FallbackVerdict> FallbackGateAsync(JobRequest req, JobNeeds needs, string why, Action<string> say, CancellationToken ct)
    {
        if (config.Fallback is not { } fb)
        {
            return PrecheckFallbackHint(req);
        }

        var mode = FallbackModeOf(req);
        if (_fallbackEngaged || mode == FallbackMode.Off)
        {
            // Engaged already: the fallback was a candidate in this very look, and it could not take the job either.
            return _fallbackEngaged ? FallbackVerdict.None : PrecheckFallbackHint(req);
        }

        var judged = await AssessFallbackAsync(fb, req, needs, ct).ConfigureAwait(false);
        if (!judged.Eligible)
        {
            return new FallbackVerdict(false, judged, "", null);
        }

        if (mode == FallbackMode.Ask && !await AskAsync(req, fb, why, say, ct).ConfigureAwait(false))
        {
            return new FallbackVerdict(false, null, Prompt is null ? $" fallback: ask needs a terminal ({NoAskReason ?? "no terminal"}), so it acted as off." : " fallback: not run on this computer (no yes at the prompt).", FallbackHint.TurnOn);
        }

        _fallbackEngaged = true;
        return new FallbackVerdict(true, null, "", null);
    }

    private async Task<bool> AskAsync(JobRequest req, Machine fb, string why, Action<string> say, CancellationToken ct)
    {
        if (Prompt is null)
        {
            say($"[lanq] fallback: ask needs a terminal ({NoAskReason ?? "no terminal"}); behaving as off");
            return false;
        }

        await _askGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fallbackEngaged)
            {
                return true; // a sibling's prompt got a yes while this one waited: the run uses the fallback already
            }

            if (_notAsking)
            {
                return false;
            }

            // Never the command's arguments, env values or paths: only the job's name and the pool's reason.
            var job = req.Label ?? req.PlanJob ?? (req.Shard is { } s ? "shard " + s : "the job");
            var answer = await Prompt.AskAsync(SafeLine($"[lanq] no pool slot for {job} ({why}); run it on this computer ({fb.Name})? [y/N]"), ct).ConfigureAwait(false);
            if (answer is null)
            {
                _notAsking = true;
                say("[lanq] ask: no answer; not asking again in this run");
                return false;
            }

            return answer.Value;
        }
        finally
        {
            _askGate.Release();
        }
    }

    /// <summary>A NoCapacity result with the fallback's verdict: its row next to the pool's machines, its note, and its hint.</summary>
    private static JobResult WithFallback(JobResult none, FallbackVerdict v)
    {
        if (v.Row is null && v.Note.Length == 0 && v.Hint is null)
        {
            return none;
        }

        var excluded = none.Excluded ?? [];
        if (v.Row is { } row)
        {
            excluded = [.. excluded, new ExcludedMachine(row.Machine.Name, row.Code ?? Eligibility.Code(Exclusion.Busy)!, "this computer (fallback): " + WhyNot(row), row.Permanent)];
        }

        var rowNote = v.Row is { } r ? $" This computer ({r.Machine.Name}, fallback): {WhyNot(r)}." : "";
        return none with
        {
            Message = none.Message + rowNote + v.Note,
            Excluded = excluded,
            Retryable = none.Retryable == true || v.Row is { Permanent: false },
            FallbackHint = v.Hint,
        };
    }

    /// <summary>"[lanq] Completed (2 of 7 jobs on this computer, fallback)": the status line's note when some jobs ran on the fallback, else "".</summary>
    internal static string FallbackNote(int onFallback, int jobs) => onFallback == 0
        ? ""
        : string.Create(CultureInfo.InvariantCulture, $" ({onFallback} of {jobs} job{(jobs == 1 ? "" : "s")} on this computer, fallback)");
}
