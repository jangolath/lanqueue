using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Ssh;

namespace LanQueue;

public static partial class Cli
{
    internal sealed record UnlockReport(List<(string File, string Job)> Removed, List<(string File, string Reason)> Kept);

    /// <summary>Reads the REMOVED= / KEPT= lines of <see cref="RemoteScripts.Unlock"/>; anything else is ignored.</summary>
    internal static UnlockReport ParseUnlock(string output)
    {
        var report = new UnlockReport([], []);
        foreach (var raw in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var removed = raw.StartsWith("REMOVED=", StringComparison.Ordinal);
            if (!removed && !raw.StartsWith("KEPT=", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = raw[(raw.IndexOf('=', StringComparison.Ordinal) + 1)..];
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            var (file, detail) = space < 0 ? (rest, "") : (rest[..space], rest[(space + 1)..].Trim());
            (removed ? report.Removed : report.Kept).Add((file, detail));
        }

        return report;
    }

    /// <summary>Finds a configured machine by name, or explains (exit 2) which names exist.</summary>
    internal static Machine? FindMachine(FleetConfig config, string? name, string command, TextWriter stderr)
    {
        var m = config.Machines.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        if (m is null)
        {
            stderr.WriteLine(
                (name is null ? $"lanq {command} needs a machine name." : $"No machine named '{JobRunner.SafeLine(name)}' in the config.")
                + " Configured: " + string.Join(", ", config.Machines.Select(x => x.Name)));
        }

        return m;
    }

    /// <summary>
    /// lanq unlock: removes slot files past their recorded deadline (a crashed or killed lanq left them); with --all --yes every slot file.
    /// Exit 0 done, 2 bad arguments, 70 ssh failed.
    /// </summary>
    internal static async Task<int> Unlock(ParsedArgs p, FleetConfig config, ISshClient ssh, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (FindMachine(config, p.Target, "unlock", stderr) is not { } m)
        {
            return 2;
        }

        if (p.All != p.Yes)
        {
            await stderr.WriteLineAsync(p.All
                ? $"lanq unlock --all removes every slot file on {m.Name}, including the slot of a job that is still running. Add --yes to confirm."
                : "--yes only goes with --all.").ConfigureAwait(false);
            return 2;
        }

        if (p.All)
        {
            await stderr.WriteLineAsync($"warning: removing every slot file on {m.Name}. A running job there keeps running but loses its slot, so another job may start beside it.")
                .ConfigureAwait(false);
        }

        SshResult r;
        try
        {
            r = await ssh.RunAsync(m, RemoteScripts.Wrap(m, RemoteScripts.Unlock(m, p.All)), null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 70;
        }

        if (!r.Ok)
        {
            await stderr.WriteLineAsync($"{m.Name}: unlock failed (exit {r.ExitCode}): {JobRunner.SafeLine(r.Output.Trim())}").ConfigureAwait(false);
            return 70;
        }

        var report = ParseUnlock(r.Output);
        foreach (var (file, job) in report.Removed)
        {
            await stdout.WriteLineAsync(JobRunner.SafeLine($"{m.Name}: removed {file} (job {(job.Length > 0 ? job : "unknown")})")).ConfigureAwait(false);
        }

        foreach (var (file, reason) in report.Kept)
        {
            var why = reason switch
            {
                "live" => "still within its deadline: its job may be running",
                "nodeadline" => $"no deadline, written by a lanq before 0.4; lanq unlock {m.Name} --all --yes removes it",
                _ => "it changed while being checked: a new job took it",
            };
            await stdout.WriteLineAsync(JobRunner.SafeLine($"{m.Name}: kept {file} ({why})")).ConfigureAwait(false);
        }

        if (report.Removed.Count == 0 && report.Kept.Count == 0)
        {
            await stdout.WriteLineAsync($"{m.Name}: nothing to unlock").ConfigureAwait(false);
        }

        return 0;
    }
}
