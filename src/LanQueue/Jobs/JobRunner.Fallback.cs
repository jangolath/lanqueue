using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Plan;
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
    private readonly Action discardTypeahead;

    /// <summary>
    /// The reader starts only at the first prompt (review A-L13): a run that never asks never reads the developer's typeahead. Before every
    /// prompt, what was typed and not yet read is discarded at the terminal itself (<paramref name="discardTypeahead"/>, final review L3), and
    /// lines the reader already took are dropped from the queue, so typeahead is never taken as the answer, however slow the reader thread is.
    /// </summary>
    /// <param name="discardTypeahead">Discards the terminal's pending input; null uses <see cref="ConsoleInput.Discard"/>.</param>
    public TerminalPrompt(TextReader input, TextWriter error, TimeSpan timeout, Action? discardTypeahead = null)
    {
        this.input = input;
        this.error = error;
        this.timeout = timeout;
        this.discardTypeahead = discardTypeahead ?? ConsoleInput.Discard;
    }

    /// <summary>How long a prompt waits for y or n.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly BlockingCollection<string?> _lines = [];
    private int _started;

    /// <summary>Starts the one reader thread (once).</summary>
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
        // At the first prompt nothing has read the terminal yet, so this drops all typeahead before the reader starts (deterministic, no
        // settle time). At later prompts it drops what the running reader has not taken yet; what it took is drained below.
        discardTypeahead();
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

    // One runner is one lanq run (a single job, a shard set or a plan). A yes at the ask prompt covers the remaining jobs of the run (the prompt
    // says so), but each job (a single job, a shard set, a plan job, a baseline rerun) still falls back only at its own NoCapacity point, after
    // its own wait budgets (review A-M1): the fallback is a candidate only for a unit that reached that point. A prompt that got no answer is
    // not repeated in the run (M7): later jobs then act as off.
    private volatile bool _fallbackConsented;
    private volatile bool _notAsking;
    private int _fallbackWarned;
    private readonly SemaphoreSlim _askGate = new(1, 1);

    /// <summary>The policy for this job (0.13.0 B3): off without a fallback entry, for a lanq.yml fallback: off job or with --on; else --fallback, else policy.fallback.</summary>
    internal FallbackMode FallbackModeOf(JobRequest req) =>
        config.Fallback is null || req.FallbackOff || req.On is not null ? FallbackMode.Off : req.Fallback ?? config.Policy.Fallback;

    /// <summary>
    /// The policy as it can really act (review A-M2): ask that cannot prompt here (no terminal, --json, CI, an agent) or that got no answer
    /// earlier in this run, and on or ask that the fallback refuses (lanq elevated, work_root inside the repository), act exactly as off: no
    /// 120 s floor, and a job no pool machine can ever take ends at once from the config.
    /// </summary>
    internal FallbackMode EffectiveFallbackModeOf(JobRequest req)
    {
        var mode = FallbackModeOf(req);
        if (mode == FallbackMode.Off)
        {
            return mode;
        }

        if (mode == FallbackMode.Ask && !_fallbackConsented && (Prompt is null || _notAsking))
        {
            return FallbackMode.Off;
        }

        return FallbackRefusal(config.Fallback!, req) is null ? mode : FallbackMode.Off;
    }

    /// <summary>
    /// The wait budgets a job waits under: its own, but with a fallback that can really act and no wait flag given, at least 120 s for a slot
    /// (M1); and while a Wake-on-LAN this run sent before falling back is pending (<paramref name="wakePending"/>, <see cref="WakesBeforeFallback"/>),
    /// at least <see cref="FallbackWakeWait"/> for an eligible machine.
    /// </summary>
    internal WaitBudgets BudgetsOf(JobRequest req, bool wakePending = false)
    {
        var budgets = req.EffectiveBudgets;
        if (EffectiveFallbackModeOf(req) != FallbackMode.Off && !req.WaitGiven && budgets.Slot < FallbackSlotFloor)
        {
            budgets = budgets with { Slot = FallbackSlotFloor };
        }

        return wakePending && WakesBeforeFallback(req) && budgets.Eligible < FallbackWakeWait ? budgets with { Eligible = FallbackWakeWait } : budgets;
    }

    /// <summary>The Wake-on-LAN packets this run sent (or tried to) and what came of each, for its output, JSON wakes and next steps.</summary>
    public IReadOnlyList<WakeAttempt> WakeAttempts => probe.Waker?.Attempts ?? [];

    /// <summary>How long a run that may fall back waits for a pool machine it woke before it uses this computer (0.13.0, wake before fallback).</summary>
    internal static readonly TimeSpan FallbackWakeWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Wake before fallback (0.13.0, owner decision; replaces final review A-L6): a job that would fall back to this computer first wakes a
    /// sleeping pool machine that could take it and waits up to <see cref="FallbackWakeWait"/> for it (never past that machine's
    /// wake.wait_seconds). Only when the fallback could really take the job (<see cref="FallbackCouldTake"/>: on, or ask that can prompt),
    /// without --no-wake, and when the caller left the eligible budget to lanq (no --wait, no --wait-eligible: <see cref="JobRequest.EligibleWaitGiven"/>);
    /// a given eligible budget decides as for any run (above 0 it may wake, 0 never does). Off: unchanged.
    /// </summary>
    internal bool WakesBeforeFallback(JobRequest req) => !req.NoWake && !req.EligibleWaitGiven && FallbackCouldTake(req);

    /// <summary>Whether a job may wake a sleeper at all: an eligible budget above 0 to wait in, or wake before fallback; never with --no-wake.</summary>
    internal bool MayWake(JobRequest req) => !req.NoWake && (req.EffectiveBudgets.Eligible > TimeSpan.Zero || WakesBeforeFallback(req));

    /// <summary>True while a packet this run sent to one of the job's candidates may still bring it up, within <see cref="FallbackWakeWait"/> of the send.</summary>
    private bool WakePendingFor(JobRequest req) =>
        probe.Waker is { } waker && Candidates(req).Any(m => waker.IsPending(m.Name, FallbackWakeWait));

    /// <summary>
    /// The packets this run sent to the job's candidates and what came of them, for the NoCapacity message and the fallback line: "" when none.
    /// "Wake-on-LAN: build-02 woken, no ssh answer within 120 s."
    /// </summary>
    internal string WakeNote(JobRequest req)
    {
        if (probe.Waker is not { } waker)
        {
            return "";
        }

        var names = Candidates(req).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var parts = waker.Attempts.Where(a => names.Contains(a.Machine)).Select(WakeText).ToList();
        return parts.Count == 0 ? "" : " Wake-on-LAN: " + string.Join("; ", parts) + ".";
    }

    /// <summary>"; Wake-on-LAN: build-02 woken, no ssh answer within 120 s": the wake part of why the job falls back (the ask question, the fallback line); "" when none.</summary>
    internal string WakeWhy(JobRequest req) => WakeNote(req) is { Length: > 0 } note ? ";" + note.TrimEnd('.') : "";

    /// <summary>"build-02 woken, no ssh answer within 120 s": one wake attempt for people (also the JSON wakes message).</summary>
    internal static string WakeText(WakeAttempt a) => a.Outcome switch
    {
        WakeOutcome.Awake => $"{a.Machine} woken and answering ssh",
        WakeOutcome.NoAnswer => string.Create(CultureInfo.InvariantCulture, $"{a.Machine} woken, no ssh answer within {a.WaitSeconds} s"),
        WakeOutcome.SendFailed => $"{a.Machine}: the packet could not be sent ({SafeLine(a.Error ?? "error")})",
        _ => $"{a.Machine} woken, no ssh answer yet",
    };

    /// <summary>
    /// True when the fallback could ever take the job as far as the config says (its capabilities, a declared size:, the job's --env), with the
    /// policy able to act (<see cref="EffectiveFallbackModeOf"/>): then the job is not ended before any probe even when no pool machine could
    /// ever take it (<see cref="NeverByConfig"/>).
    /// </summary>
    private bool FallbackCouldTake(JobRequest req) =>
        EffectiveFallbackModeOf(req) != FallbackMode.Off && config.Fallback is { } fb && FallbackFits(fb, req);

    /// <summary>The config-only checks of the fallback for a job: capabilities, a declared size:, the job's --env.</summary>
    private static bool FallbackFits(Machine fb, JobRequest req) =>
        Eligibility.Missing(fb, req.Needs).Count == 0 && Eligibility.DeclaredSizeMismatch(fb, req.Size) is null
        && EnvExclusion(fb, req.Env, req.Env.Count > 0 && !JobPolicy.ValidateForWindows(req.Argv).Ok) is null;

    /// <summary>
    /// One stderr line per run, at the job's start, when the developer asked for the fallback but it cannot act (review A-L7, A-M2, A-L8):
    /// --fallback on|ask with no usable fallback entry in machines.yml, or on/ask while the fallback refuses (lanq elevated, its work_root inside
    /// the repository).
    /// </summary>
    private void WarnFallbackOnce(JobRequest req)
    {
        string? line = null;
        if (config.Fallback is null && req.Fallback is FallbackMode.On or FallbackMode.Ask && !req.FallbackOff && req.On is null)
        {
            line = $"[lanq] --fallback {(req.Fallback == FallbackMode.On ? "on" : "ask")}: machines.yml has no usable fallback entry (local: true, fallback: true, this computer's os); the job uses the pool only";
        }
        else if (config.Fallback is { } fb && FallbackModeOf(req) is var mode && mode != FallbackMode.Off && FallbackRefusal(fb, req) is { } refused)
        {
            line = $"[lanq] fallback: {(mode == FallbackMode.Ask ? "ask" : "on")}, but {refused.Why}; it will not be used";
        }

        if (line is not null && Interlocked.Exchange(ref _fallbackWarned, 1) == 0)
        {
            Say(req)(line);
        }
    }

    /// <summary>The hint a NoCapacity decided before any probe carries (<see cref="Precheck"/>): the same as the gate's off answer.</summary>
    private FallbackVerdict PrecheckFallbackHint(JobRequest req)
    {
        if (req.FallbackOff || req.On is not null)
        {
            return FallbackVerdict.None;
        }

        if (config.Fallback is not { } fb)
        {
            return FallbackVerdict.None with { Hint = FallbackHint.SetUp };
        }

        if (!FallbackFits(fb, req))
        {
            return FallbackVerdict.None;
        }

        // Never suggest --fallback on when the fallback would only refuse (review A-M2): say what to change instead, and with the policy on or
        // ask name the fallback's own reason next to the pool's machines.
        var mode = FallbackModeOf(req);
        if (FallbackRefusal(fb, req) is { } refused)
        {
            return mode == FallbackMode.Off
                ? new FallbackVerdict(false, null, $" This computer ({fb.Name}) could run it, but {refused.Why}.", HintOf(refused.Reason))
                : new FallbackVerdict(false, RefusedRow(fb, refused), "", HintOf(refused.Reason));
        }

        if (mode == FallbackMode.Ask)
        {
            // Here ask acts as off (EffectiveFallbackModeOf): no terminal to ask at, or no answer earlier in this run.
            return new FallbackVerdict(
                false, null, _notAsking ? " fallback: ask got no answer earlier in this run, so it acted as off." : $" fallback: ask needs a terminal ({NoAskReason ?? "no terminal"}), so it acted as off.", FallbackHint.TurnOn);
        }

        return new FallbackVerdict(false, null, $" This computer ({fb.Name}) could run it: lanq run --fallback on ...", FallbackHint.TurnOn);
    }

    private static FallbackHint HintOf(Exclusion refused) => refused == Exclusion.FallbackWorkRoot ? FallbackHint.WorkRoot : FallbackHint.Elevated;

    /// <summary>The fallback judged as a candidate after the pool, only for a unit that reached its own NoCapacity point and fell back (A-M1), or null.</summary>
    private async Task<Assessment?> EngagedFallbackAsync(JobRequest req, JobNeeds needs, bool unitFellBack, CancellationToken ct) =>
        unitFellBack && FallbackModeOf(req) != FallbackMode.Off && config.Fallback is { } fb
            ? await AssessFallbackAsync(fb, req, needs, ct).ConfigureAwait(false)
            : null;

    /// <summary>The fallback judged for the job like a worker (probe, capabilities, size, --mem with its floor, Windows, --env, its own slots).</summary>
    private async Task<Assessment> AssessFallbackAsync(Machine fb, JobRequest req, JobNeeds needs, CancellationToken ct)
    {
        if (FallbackRefusal(fb, req) is { } refused)
        {
            return RefusedRow(fb, refused);
        }

        var health = CountStuckStale(await ProbeSharedAsync(fb, ct).ConfigureAwait(false));
        var own = needs with { EnvRuledOut = EnvRuledOut([fb], req.Env, needs.WindowsUnsafe) };
        return Scheduler.Assess([(fb, health)], own)[0];
    }

    private static Assessment RefusedRow(Machine fb, (Exclusion Reason, string Why) refused) =>
        new(fb, Unprobed(fb), refused.Reason, refused.Why, true, 0, 0);

    /// <summary>
    /// Why the fallback may never run this job here: lanq runs elevated or as root (the job would inherit those rights;
    /// <see cref="Exclusion.FallbackRefused"/>), or the fallback's work_root is inside the repository or holds it (cleanup deletes under
    /// work_root/jobs; <see cref="Exclusion.FallbackWorkRoot"/>). The repository is its top folder, not the current one (review A-M3), and
    /// links on either path are followed. Null when neither.
    /// </summary>
    private (Exclusion Reason, string Why)? FallbackRefusal(Machine fb, JobRequest req) =>
        _refusals.GetOrAdd(req.RepoDir, (_, args) => args.Self.Refusal(args.fb, args.req), (Self: this, fb, req));

    // FallbackRefusal is asked at every look (the wait budgets depend on it): judged once per repository folder in a run.
    private readonly ConcurrentDictionary<string, (Exclusion Reason, string Why)?> _refusals = new(StringComparer.Ordinal);

    private (Exclusion Reason, string Why)? Refusal(Machine fb, JobRequest req)
    {
        if (Elevated())
        {
            return (Exclusion.FallbackRefused, "lanq runs elevated (administrator) or as root; the fallback never hands those rights to a job");
        }

        return WorkRootOverlaps(fb.WorkRoot, PlanFileLoader.RepositoryTop(req.RepoDir) ?? req.RepoDir)
            ? (Exclusion.FallbackWorkRoot, "its work_root is inside the repository being run (or holds it); give the fallback a work_root of its own")
            : null;
    }

    /// <summary>
    /// True when <paramref name="workRoot"/> is <paramref name="repo"/>, inside it, or holds it, after following links on both paths; on Windows
    /// also through subst drives, 8.3 short names and volume mount points (final review L5): each path is compared by every name Windows knows
    /// it by (<see cref="WindowsPathNames"/>), so any match refuses. Linux bind mounts are not followed (documented).
    /// </summary>
    internal static bool WorkRootOverlaps(string workRoot, string repo)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = PathNames(workRoot);
        var tops = PathNames(repo);
        return roots.Any(root => tops.Any(top => root.StartsWith(top, comparison) || top.StartsWith(root, comparison)));
    }

    /// <summary>The path with its links followed, plus (Windows) every other name of it; each ends in a separator.</summary>
    private static List<string> PathNames(string path)
    {
        static string Folder(string p) => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar;
        var resolved = Resolved(Path.GetFullPath(path));
        return [Folder(resolved), .. WindowsPathNames.Of(resolved).Select(Folder)];
    }

    /// <summary>The full path with every existing link on it (a symlink or junction on any folder) followed; a part that does not exist yet is kept as written.</summary>
    internal static string Resolved(string full)
    {
        try
        {
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                var info = new DirectoryInfo(current);
                if (!info.Exists)
                {
                    return Path.Combine([current, .. parts[(i + 1)..]]);
                }

                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.GetFullPath(target.FullName);
                }
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    /// <summary>
    /// The moment a job (or the waiting shards of a run) would end NoCapacity (0.13.0 B3): after its wait budgets, or at once when every pool
    /// machine is ruled out for good. Off: no fallback, and the result carries the hint for the next steps. On: the fallback is judged like a
    /// worker; eligible, it is engaged for this unit (the caller looks again with it as the last candidate). Ask: the same, after a yes at the
    /// terminal; that yes covers the remaining jobs of the run, each again only at its own NoCapacity point (review A-M1).
    /// </summary>
    /// <param name="why">Why the pool could not take the job, for the prompt and the label ("no pool slot", "no pool machine can take it").</param>
    /// <param name="unitFellBack">True when this unit fell back already: the fallback was a candidate in this very look and could not take it either.</param>
    private async Task<FallbackVerdict> FallbackGateAsync(JobRequest req, JobNeeds needs, string why, bool unitFellBack, Action<string> say, CancellationToken ct)
    {
        if (config.Fallback is not { } fb)
        {
            return PrecheckFallbackHint(req);
        }

        if (unitFellBack)
        {
            return FallbackVerdict.None;
        }

        var mode = FallbackModeOf(req);
        if (mode == FallbackMode.Off)
        {
            return PrecheckFallbackHint(req);
        }

        if (FallbackRefusal(fb, req) is { } refused)
        {
            return new FallbackVerdict(false, RefusedRow(fb, refused), "", HintOf(refused.Reason));
        }

        if (mode == FallbackMode.Ask && !_fallbackConsented && Prompt is null)
        {
            // ask acts as off here (review A-M2): the fallback is not even probed.
            say($"[lanq] fallback: ask needs a terminal ({NoAskReason ?? "no terminal"}); behaving as off");
            return new FallbackVerdict(false, null, $" fallback: ask needs a terminal ({NoAskReason ?? "no terminal"}), so it acted as off.", FallbackHint.TurnOn);
        }

        if (mode == FallbackMode.Ask && !_fallbackConsented && _notAsking)
        {
            return new FallbackVerdict(false, null, " fallback: ask got no answer earlier in this run, so it acted as off.", FallbackHint.TurnOn);
        }

        var judged = await AssessFallbackAsync(fb, req, needs, ct).ConfigureAwait(false);
        if (!judged.Eligible)
        {
            return new FallbackVerdict(false, judged, "", null);
        }

        if (mode == FallbackMode.Ask && !await AskAsync(req, fb, why, say, ct).ConfigureAwait(false))
        {
            return new FallbackVerdict(false, null, " fallback: not run on this computer (no yes at the prompt).", FallbackHint.TurnOn);
        }

        return new FallbackVerdict(true, null, "", null);
    }

    /// <summary>The ask prompt. A yes covers the remaining jobs of this run (the question says so), each only at its own NoCapacity point.</summary>
    private async Task<bool> AskAsync(JobRequest req, Machine fb, string why, Action<string> say, CancellationToken ct)
    {
        await _askGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fallbackConsented)
            {
                return true; // a yes earlier in this run (or a sibling's, while this one waited) covers this job, which reached its own NoCapacity point
            }

            if (_notAsking || Prompt is null)
            {
                return false;
            }

            // Never the command's arguments, env values or paths: only the job's name and the pool's reason.
            var job = req.Label ?? req.PlanJob ?? (req.Shard is { } s ? "shard " + s : "the job");
            var answer = await Prompt.AskAsync(SafeLine(AskQuestion(job, why, fb.Name)), ct).ConfigureAwait(false);
            if (answer is null)
            {
                _notAsking = true;
                say("[lanq] ask: no answer; not asking again in this run");
                return false;
            }

            _fallbackConsented = answer.Value;
            return answer.Value;
        }
        finally
        {
            _askGate.Release();
        }
    }

    /// <summary>The ask question: why the pool could not take the job, and that a yes covers the remaining jobs of the run, each only after its own wait.</summary>
    internal static string AskQuestion(string job, string why, string fallback) =>
        $"[lanq] {job}: {why}. Run it on this computer ({fallback})? A yes also covers the remaining jobs in this run, each only after its own wait runs out. [y/N]";

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
