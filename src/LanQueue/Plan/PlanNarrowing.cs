using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Scheduling;

namespace LanQueue.Plan;

/// <summary>
/// lanq run --plan --on / --size / --min-size (0.10.0): the developer narrows every job of the plan from the command line. Narrowing only ever
/// narrows: each job's effective --on is its own on: intersected with the command line's (the command line's alone when the job has none), and
/// its effective size rule is the intersection of the size classes both allow. A job the intersection leaves no machine is reported, never
/// widened or dropped (the plan then fails closed before the snapshot: NoCapacity 75, not retryable). Pure: nothing is probed or sent.
/// </summary>
public static class PlanNarrowing
{
    /// <summary>
    /// One job narrowed by the command line's <paramref name="on"/> (already resolved to the config's spelling by <see cref="Pinning.Resolve"/>)
    /// and <paramref name="size"/>: the job with its run line and arguments edited (the same job, unchanged, when nothing narrows it), or why the
    /// narrowing leaves it no machine. A job whose own on: names a machine the config lacks is left unchanged, so the plan's own check refuses it
    /// with the usual words.
    /// </summary>
    /// <param name="onText">The command line's --on as typed (for the message).</param>
    public static (PlanJob Job, string? NoMachine) Narrow(PlanJob job, FleetConfig config, IReadOnlyList<string>? on, string? onText, SizeRule? size)
    {
        if (on is null && size is null)
        {
            return (job, null);
        }

        var own = job.Args;
        IReadOnlyList<string>? effectiveOn = own.On;
        if (on is not null)
        {
            var (ownNames, error) = Pinning.Resolve(config, own.On);
            if (error is not null)
            {
                return (job, null);
            }

            if (ownNames is null)
            {
                effectiveOn = on;
            }
            else
            {
                var kept = ownNames.Where(n => on.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0)
                {
                    return (job, $"--on {onText} leaves job '{job.Name}' no machine (its on: [{string.Join(", ", own.On!)}])");
                }

                // Every machine of its own on: is still allowed: the job's own value, exactly as written.
                effectiveOn = kept.Count == ownNames.Count ? own.On : kept;
            }
        }

        var effectiveSize = own.Size;
        if (size is not null)
        {
            var (rule, empty) = IntersectSize(own.Size, size);
            if (empty)
            {
                return (job, $"{size.Text} leaves job '{job.Name}' no machine (its {(own.Size!.AnyOf is not null ? "size" : "min_size")}: {own.Size.Value})");
            }

            effectiveSize = rule;
        }

        if (SameNames(effectiveOn, own.On) && effectiveSize == own.Size)
        {
            return (job, null);
        }

        List<string> options = [];
        if (effectiveOn is not null)
        {
            options.AddRange(["--on", string.Join(',', effectiveOn)]);
        }

        var args = WithOptions(job.RunArgs, ["--on"], options);
        args = WithOptions(args, ["--size", "--min-size"], effectiveSize is null ? [] : [effectiveSize.Option, effectiveSize.Value]);
        return (Reparsed(job, args), null);
    }

    /// <summary>
    /// The size rule a job may run under when both <paramref name="own"/> (the job's size: or min_size:, or null) and the command line's
    /// <paramref name="cli"/> must allow the class: the job's own rule when the command line takes nothing from it, the command line's when the
    /// job had none (or it is the narrower), else --size with the classes both allow. Empty when no class is allowed by both.
    /// </summary>
    public static (SizeRule? Rule, bool Empty) IntersectSize(SizeRule? own, SizeRule cli)
    {
        if (own is null)
        {
            return (cli, false);
        }

        var both = Classes(own).Intersect(Classes(cli)).ToHashSet();
        if (both.Count == 0)
        {
            return (null, true);
        }

        return both.SetEquals(Classes(own)) ? (own, false)
            : both.SetEquals(Classes(cli)) ? (cli, false)
            : (new SizeRule(both, null), false);
    }

    /// <summary>The size classes a rule allows (never Unknown).</summary>
    private static IEnumerable<SizeClass> Classes(SizeRule rule) =>
        new[] { SizeClass.Light, SizeClass.Medium, SizeClass.Heavy }.Where(rule.Allows);

    private static bool SameNames(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>
    /// A job's run line with every <paramref name="remove"/> option (each takes a value) taken out and <paramref name="insert"/> put where the
    /// first of them was, or just before "--" when the line had none. The job's command after "--" is never touched.
    /// </summary>
    internal static List<string> WithOptions(IReadOnlyList<string> runArgs, string[] remove, IReadOnlyList<string> insert)
    {
        var split = runArgs.ToList().IndexOf("--");
        var own = split >= 0 ? runArgs.Take(split).ToList() : [.. runArgs];
        var command = split >= 0 ? runArgs.Skip(split).ToList() : [];
        var edited = new List<string>(own.Count + insert.Count);
        var at = -1;
        for (var i = 0; i < own.Count; i++)
        {
            if (i > 0 && remove.Contains(own[i], StringComparer.Ordinal) && i + 1 < own.Count)
            {
                at = at < 0 ? edited.Count : at;
                i++;
                continue;
            }

            edited.Add(own[i]);
        }

        edited.InsertRange(at < 0 ? edited.Count : at, insert);
        return [.. edited, .. command];
    }

    /// <summary>The job with <paramref name="args"/> as its run line, parsed again by the CLI's own parser so the arguments match the line exactly.</summary>
    internal static PlanJob Reparsed(PlanJob job, List<string> args) => job with { RunArgs = args, Args = ParsedArgs.Parse([.. args]) };
}
