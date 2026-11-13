using System.Text.RegularExpressions;

namespace LanQueue.Output;

/// <summary>
/// The command lines lanq suggests as next steps: editing a copy of the user's own argv (<see cref="ParsedArgs.Raw"/>) and rendering an
/// argv as one line a person or agent can paste into bash or PowerShell. The argv is always the authoritative form; the line is a convenience
/// and is left out whenever any token could not be written so that both shells pass it back unchanged.
/// </summary>
internal static partial class CommandLine
{
    /// <summary>Tokens made only of these characters are written bare; they mean nothing special to bash or PowerShell (but see <see cref="Render"/>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_./:=,+@%-]+\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BareRegex();

    /// <summary>True when the token is made only of plain characters (<see cref="BareRegex"/>) and could be written without quotes.</summary>
    public static bool IsBare(string token) => BareRegex().IsMatch(token);

    /// <summary>
    /// One line for bash and PowerShell, or null when some token cannot be written safely for both: tokens of plain characters bare, others in
    /// single quotes. Null for a token holding a single quote, a double quote (Windows PowerShell 5.1 mangles it on the way to a native
    /// program), a control or invisible character, an empty token (PowerShell 5.1 drops it) or exactly <c>--%</c> (PowerShell's stop-parsing
    /// token, consumed even when quoted). A token starting with @ is quoted (bare, PowerShell would splat a variable).
    /// </summary>
    public static string? Render(IReadOnlyList<string> argv)
    {
        var parts = new List<string>(argv.Count);
        foreach (var t in argv)
        {
            if (t.Length == 0 || t == "--%" || t.Contains('\'', StringComparison.Ordinal) || t.Contains('"', StringComparison.Ordinal)
                || Jobs.JobRunner.SafeLine(t) != t || t.Any(char.IsControl))
            {
                return null;
            }

            parts.Add(BareRegex().IsMatch(t) && !t.StartsWith('@') ? t : "'" + t + "'");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// A copy of <paramref name="raw"/> (a command line without the program name) with lanq's own options edited; everything from a bare
    /// <c>--</c> on (the job's command) is never touched. <paramref name="set"/> gives options and their new value: every occurrence gets the
    /// value, or the option is added just before <c>--</c> (at the end without one). <paramref name="add"/> flags are added when missing,
    /// <paramref name="remove"/> flags are dropped.
    /// </summary>
    public static List<string> Edit(
        IReadOnlyList<string> raw, IReadOnlyList<(string Option, string Value)>? set = null, IReadOnlyList<string>? add = null, IReadOnlyList<string>? remove = null)
    {
        var split = IndexOfDashDash(raw);
        var own = split >= 0 ? raw.Take(split).ToList() : [.. raw];
        var job = split >= 0 ? raw.Skip(split).ToList() : [];
        var found = new HashSet<string>(StringComparer.Ordinal);
        var edited = new List<string>(own.Count + 4);
        for (var i = 0; i < own.Count; i++)
        {
            var t = own[i];
            if (i > 0 && TakesValue(own[0], t) && i + 1 < own.Count)
            {
                // An option and its value: the value is never read as an option (--fetch --wait is the pattern "--wait").
                var value = own[++i];
                if (set?.FirstOrDefault(s => s.Option == t) is { Option: not null } edit)
                {
                    value = edit.Value;
                    found.Add(t);
                }

                edited.AddRange([t, value]);
            }
            else if (i == 0 || remove?.Contains(t) != true)
            {
                edited.Add(t);
                found.Add(t);
            }
        }

        foreach (var (option, value) in set ?? [])
        {
            if (!found.Contains(option))
            {
                edited.AddRange([option, value]);
            }
        }

        foreach (var flag in add ?? [])
        {
            if (!found.Contains(flag))
            {
                edited.Add(flag);
            }
        }

        return [.. edited, .. job];
    }

    /// <summary>The options that take a value (as <see cref="ParsedArgs.Parse"/> reads them); lanq wake's --wait is a flag.</summary>
    private static bool TakesValue(string command, string option) => option switch
    {
        "--wait" => command != "wake",
        "--config" or "--needs" or "--dir" or "--timeout" or "--mem" or "--fetch" or "--fetch-dir" or "--fetch-max" or "--fetch-max-files"
            or "--log-tail" or "--shards" or "--shard-retries" or "--target-minutes" or "--ssh" or "--os" or "--key-env" or "--name"
            or "--on" or "--size" or "--min-size" or "--only" or "--plan-file" or "--env" or "--baseline-ref" => true,
        _ => false,
    };

    /// <summary>The index of the first bare <c>--</c> (where the job's command starts), or -1.</summary>
    public static int IndexOfDashDash(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--")
            {
                return i;
            }
        }

        return -1;
    }
}
