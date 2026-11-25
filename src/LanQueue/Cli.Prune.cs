using System.Globalization;
using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Ssh;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>What lanq prune found or did on one machine (0.13.0 D2).</summary>
    internal sealed record PruneReport(
        List<(string Kind, string Name, long Bytes)> Entries, List<(string Name, string Why)> Skipped, List<(string Kind, string Name)> Failed, int SlotFiles, string? Refused);

    /// <summary>Reads the lines of <see cref="RemoteScripts.Prune"/>; anything else is ignored.</summary>
    internal static PruneReport ParsePrune(string output, PruneReport? into = null)
    {
        var r = into ?? new PruneReport([], [], [], -1, null);
        foreach (var raw in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (raw.StartsWith("REFUSED ", StringComparison.Ordinal))
            {
                r = r with { Refused = raw["REFUSED ".Length..] };
            }
            else if (raw.StartsWith("SLOTFILES=", StringComparison.Ordinal) && int.TryParse(raw["SLOTFILES=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                r = r with { SlotFiles = n };
            }
            else if (f.Length == 4 && f[0] is "WOULD" or "REMOVED" && long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            {
                r.Entries.Add((f[1], f[2], bytes));
            }
            else if (f.Length == 3 && f[0] == "FAIL")
            {
                r.Failed.Add((f[1], f[2]));
            }
            else if (f.Length == 3 && f[0] == "SKIP")
            {
                r.Skipped.Add((f[1], f[2]));
            }
        }

        return r;
    }

    /// <summary>--older-than: Nh or Nd, from 1 hour to 365 days (seconds).</summary>
    internal static long ParseAge(string text)
    {
        var unit = text.Length > 1 ? text[^1] : ' ';
        return unit is 'h' or 'd' && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && (unit == 'h' ? n : n * 24) is >= 1 and <= 365 * 24 and var hours
            ? hours * 3600
            : throw new ArgumentException("--older-than must be Nh or Nd from 1 hour to 365 days, for example --older-than 2d.");
    }

    /// <summary>The default age: two days.</summary>
    internal const long DefaultPruneAgeSeconds = 2 * 24 * 3600;

    /// <summary>--caches: a repository cache untouched for at least this long (30 days), or --older-than when larger.</summary>
    internal const long CacheAgeSeconds = 30L * 24 * 3600;

    /// <summary>
    /// lanq prune MACHINE|--all [--older-than 2d] [--caches] [--yes] [--force] [--json] (0.13.0 D2): lists (without --yes) or removes the leftovers
    /// of jobs lanq no longer runs on a worker (see <see cref="RemoteScripts.Prune"/> for what may go). Steps that touch a repository cache run
    /// only on a machine with no slot file at all and only while prune holds every slot (review M10); otherwise "repos busy". A held machine
    /// (paused, outside its hours) is skipped unless --force. Exit 0 done, 1 something could not be removed, 2 bad arguments, 70 ssh failure,
    /// 75 every selected machine held.
    /// </summary>
    internal static async Task<int> Prune(ParsedArgs p, FleetConfig config, ISshClient ssh, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        List<Machine> machines;
        if (p.All == (p.Target is not null))
        {
            return await PruneError(p, stdout, stderr, "lanq prune needs one machine name or --all.").ConfigureAwait(false);
        }

        if (p.All)
        {
            machines = config.Machines;
        }
        else if (config.Fallback is { } fb && string.Equals(fb.Name, p.Target, StringComparison.Ordinal))
        {
            machines = [fb];
        }
        else if (FindMachine(config, p.Target, "prune", stderr) is { } m)
        {
            machines = [m];
        }
        else
        {
            return 2;
        }

        var age = p.OlderThanSeconds ?? DefaultPruneAgeSeconds;
        var rows = new List<PruneMachineJson>();
        var (failed, sshFailed, held) = (false, false, 0);
        foreach (var m in machines)
        {
            var (row, outcome) = await PruneOne(m, config.Policy, ssh, probe, age, p, ct).ConfigureAwait(false);
            rows.Add(row);
            failed |= outcome == "failed";
            sshFailed |= outcome == "ssh";
            held += outcome == "held" ? 1 : 0;
            foreach (var line in PruneLines(row, p.Yes))
            {
                await (p.Json ? stderr : stdout).WriteLineAsync(JobRunner.SafeLine(line)).ConfigureAwait(false);
            }
        }

        var code = sshFailed ? 70 : failed ? 1 : held == machines.Count ? 75 : 0;
        var status = code switch { 0 => p.Yes ? "Completed" : "Listed", 1 => "Incomplete", 70 => "InfraError", _ => "Held" };
        var steps = !p.Yes && rows.Any(r => r.WouldRemove is { Count: > 0 })
            ? [NextSteps.Step(NextAction.AskDeveloper, "lanq prune only listed what it would remove; removing it is the developer's call (lanq prune ... --yes)")]
            : NextSteps.Nothing("prune " + status.ToLowerInvariant());
        await stderr.WriteLineAsync($"[lanq] {status}").ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(new PruneJson(status, code, rows) { NextSteps = steps }, LanqJson.Default.PruneJson)).ConfigureAwait(false);
        }

        return code;
    }

    private static async Task<int> PruneError(ParsedArgs p, TextWriter stdout, TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync(message).ConfigureAwait(false);
        if (p.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(new PruneJson("Rejected", 2, []) { NextSteps = NextSteps.ArgumentsError(message) }, LanqJson.Default.PruneJson)).ConfigureAwait(false);
        }

        return 2;
    }

    private static async Task<(PruneMachineJson Row, string Outcome)> PruneOne(Machine m, PolicyConfig policy, ISshClient ssh, HealthProbe probe, long age, ParsedArgs p, CancellationToken ct)
    {
        PruneMachineJson Row(string? why = null) => new(m.Name) { Note = why };
        var h = await probe.ProbeOnceAsync(m, policy, ct).ConfigureAwait(false);
        if (!h.Reachable)
        {
            return (Row("unreachable: " + (h.Hold ?? "no answer")), "ssh");
        }

        if (h.HoldKind is HoldKind.Paused or HoldKind.OutsideHours && !p.Force)
        {
            return (Row($"skipped: {h.Hold} (--force prunes it anyway)"), "held");
        }

        var limit = TimeSpan.FromMinutes(10);
        async Task<SshResult> Run(string script) =>
            await ssh.RunWithInputAsync(m, RemoteScripts.FindToolchainCommand(m.Os), Encoding.UTF8.GetBytes(script), 512_000, limit, ct).ConfigureAwait(false);

        var files = await Run(RemoteScripts.Prune(m, PrunePhase.Files, age, p.Yes)).ConfigureAwait(false);
        if (files.IsSshFailure)
        {
            return (Row("unreachable during prune"), "ssh");
        }

        var report = ParsePrune(files.Output);
        if (report.Refused is { } refused)
        {
            return (Row("refused: " + refused), "failed");
        }

        // Cache steps (worktree prune, the pruned ids' refs, --caches) only with no slot file at all, and with --yes only while prune holds every
        // slot, so no job can be starting on a cache that is being changed (review M10).
        var busy = report.SlotFiles != 0;
        if (!busy && (p.Yes || p.Caches))
        {
            var cacheAge = Math.Max(CacheAgeSeconds, age);
            var pruned = report.Entries.Where(e => e.Kind == "job").Select(e => e.Name).ToList();
            if (!p.Yes)
            {
                ParsePrune((await Run(RemoteScripts.Prune(m, PrunePhase.Caches, age, false, p.Caches, cacheAge)).ConfigureAwait(false)).Output, report);
            }
            else
            {
                var pruneId = "prune-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
                var claim = await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.AcquireAll(m, pruneId, TimeSpan.FromMinutes(10))), null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (claim.Ok && JobRunner.ParseSlot(claim.Output) is not null)
                {
                    try
                    {
                        ParsePrune((await Run(RemoteScripts.Prune(m, PrunePhase.Caches, age, true, p.Caches, cacheAge, pruned)).ConfigureAwait(false)).Output, report);
                    }
                    finally
                    {
                        await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.ReleaseClaim(m, pruneId)), null, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else
                {
                    busy = true; // a job claimed a slot in the meantime (all or nothing: AcquireAll took none)
                }
            }
        }

        if (busy)
        {
            report.Skipped.Add(("repos", "busy"));
        }

        var entries = report.Entries.Select(e => new PruneEntryJson(e.Kind, e.Name, e.Bytes)).ToList();
        var row = new PruneMachineJson(m.Name)
        {
            Removed = p.Yes ? entries : null,
            WouldRemove = p.Yes ? null : entries,
            Skipped = [.. report.Skipped.Select(s => new PruneSkipJson(s.Name, s.Why))],
            Failed = report.Failed.Count == 0 ? null : [.. report.Failed.Select(f => new PruneEntryJson(f.Kind, f.Name, 0))],
        };
        return (row, report.Failed.Count > 0 ? "failed" : "ok");
    }

    /// <summary>The text lines of one machine: per kind a count and size, every failure, and what was skipped.</summary>
    internal static IEnumerable<string> PruneLines(PruneMachineJson row, bool yes)
    {
        if (row.Note is { } note)
        {
            yield return $"{row.Name}: {note}";
        }

        var entries = (yes ? row.Removed : row.WouldRemove) ?? [];
        foreach (var kind in entries.GroupBy(e => e.Kind))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{row.Name}: {(yes ? "removed" : "would remove")} {kind.Count()} {kind.Key} ({JobRunner.Size(kind.Sum(e => e.Bytes))})");
        }

        foreach (var f in row.Failed ?? [])
        {
            yield return $"{row.Name}: could not remove {f.Kind} {f.Name}";
        }

        foreach (var s in row.Skipped ?? [])
        {
            yield return $"{row.Name}: kept {s.Name} ({(s.Why == "busy" ? "slots in use: repository caches are left alone" : "a slot or a running process names it")})";
        }

        if (row.Note is null && entries.Count == 0 && row.Failed is null)
        {
            yield return $"{row.Name}: nothing to prune";
        }
    }
}
