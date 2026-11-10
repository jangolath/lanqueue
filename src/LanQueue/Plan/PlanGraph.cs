using LanQueue.Jobs;
using LanQueue.Scheduling;

namespace LanQueue.Plan;

/// <summary>The jobs lanq run --plan runs: the selection (all, or --only), each one's after: edges within it, and notes for dropped edges.</summary>
/// <param name="Jobs">The selected jobs, in file order.</param>
/// <param name="After">For each selected job (by name): the selected jobs it starts after. Edges to jobs not selected are dropped.</param>
/// <param name="Notes">One line per dropped edge: "--only: serial starts without waiting for integration (not selected)" (P3 prints it as "[lanq] ...").</param>
public sealed record PlanSelection(IReadOnlyList<PlanJob> Jobs, IReadOnlyDictionary<string, IReadOnlyList<string>> After, IReadOnlyList<string> Notes);

/// <summary>
/// Pure graph work on a <see cref="PlanFile"/>: --only selection, cycle detection (used by the loader) and the launch order. The order is a
/// topological order of the after: edges in which, among the jobs ready at each step, those with history come first, longest expected work
/// first (<see cref="ExpectedFromHistory(IReadOnlyList{JobRecord}, DateTimeOffset)"/>), then the jobs without history in file order. A runner
/// starting the ready job that comes first in this order therefore follows the same rule.
/// </summary>
public static class PlanGraph
{
    /// <summary>
    /// All jobs (<paramref name="only"/> null), or exactly those named (ignoring case, in file order). after: edges to a job that is not
    /// selected are dropped (the job starts without waiting for it), each with a note.
    /// </summary>
    /// <exception cref="PlanFileException">--only names a job that is not in the file (the message lists the file's jobs).</exception>
    public static PlanSelection Select(PlanFile plan, IReadOnlyList<string>? only)
    {
        var label = Path.GetFileName(plan.Path) is { Length: > 0 } name ? name : PlanFileLoader.FileName;
        HashSet<string> chosen;
        if (only is null)
        {
            chosen = new HashSet<string>(plan.Jobs.Select(j => j.Name), StringComparer.Ordinal);
        }
        else
        {
            var unknown = only.Where(o => plan.Job(o) is null).ToList();
            if (unknown.Count > 0)
            {
                throw new PlanFileException(
                    $"--only names a job that is not in {label}: {string.Join(", ", unknown)}. Jobs: {string.Join(", ", plan.Jobs.Select(j => j.Name))}.");
            }

            chosen = new HashSet<string>(only.Select(o => plan.Job(o)!.Name), StringComparer.Ordinal);
        }

        var jobs = plan.Jobs.Where(j => chosen.Contains(j.Name)).ToList();
        var after = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var job in jobs)
        {
            after[job.Name] = [.. job.After.Where(chosen.Contains)];
            notes.AddRange(job.After.Where(d => !chosen.Contains(d)).Select(d => $"--only: {job.Name} starts without waiting for {d} (not selected)"));
        }

        return new PlanSelection(jobs, after, notes);
    }

    /// <summary>A cycle in the after: edges as "a", "b", "a" (first job again at the end), or null when there is none. Unknown names are ignored.</summary>
    public static IReadOnlyList<string>? FindCycle(IReadOnlyList<(string Name, IReadOnlyList<string> After)> jobs)
    {
        var edges = jobs.ToDictionary(j => j.Name, j => j.After, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 on the stack, 2 done
        var stack = new List<string>();

        List<string>? Visit(string n)
        {
            state[n] = 1;
            stack.Add(n);
            foreach (var d in edges[n].Where(edges.ContainsKey))
            {
                if (state.GetValueOrDefault(d) == 1)
                {
                    return [.. stack.Skip(stack.IndexOf(d)), d];
                }

                if (state.GetValueOrDefault(d) == 0 && Visit(d) is { } found)
                {
                    return found;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
            return null;
        }

        foreach (var (name, _) in jobs)
        {
            if (state.GetValueOrDefault(name) == 0 && Visit(name) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }

    /// <summary>
    /// The launch order (see the class summary): every selected job once, each after the jobs it waits for. <paramref name="expectedSeconds"/>
    /// gives a job's expected work in seconds, or null without history.
    /// </summary>
    public static IReadOnlyList<PlanJob> Order(PlanSelection selection, Func<PlanJob, double?> expectedSeconds)
    {
        var expected = selection.Jobs.ToDictionary(j => j.Name, expectedSeconds, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<PlanJob>(selection.Jobs.Count);
        while (order.Count < selection.Jobs.Count)
        {
            var next = selection.Jobs
                .Where(j => !placed.Contains(j.Name) && selection.After[j.Name].All(placed.Contains))
                .OrderBy(j => expected[j.Name] is null ? 1 : 0)
                .ThenByDescending(j => expected[j.Name] ?? 0)
                .ThenBy(j => j.Index)
                .FirstOrDefault() ?? throw new InvalidOperationException("the plan's after: edges hold a cycle"); // the loader refuses cycles
            placed.Add(next.Name);
            order.Add(next);
        }

        return order;
    }

    /// <summary>
    /// Expected work per job from the job log (read only, as --target-minutes reads it): <see cref="ShardTiming.Estimate"/>'s W for the job's
    /// command and --dir (<see cref="JobHistory.Key"/>, <see cref="JobHistory.Dir"/>): the median over the newest completed runs of the
    /// summed run seconds, each shard divided by its machine's speed factor; a single job is a run of one shard. Null without such a run.
    /// </summary>
    public static Func<PlanJob, double?> ExpectedFromHistory(IReadOnlyList<JobRecord> records, DateTimeOffset now)
    {
        var speeds = ShardTiming.Speeds(records, now);
        double Speed(string machine) => speeds.TryGetValue(machine, out var s) && s.Factor is { } f ? f : 1;
        return job => ShardTiming.Estimate(JobHistory.From(records, JobHistory.Key(job.Args.Argv), JobHistory.Dir(job.Args.Directory), now, speeds), Speed)?.WorkSeconds;
    }

    /// <summary>The same from <paramref name="log"/> (jobs.jsonl, read only, never written); a log that cannot be read means no history.</summary>
    public static Func<PlanJob, double?> ExpectedFromHistory(JobLog log, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            return ExpectedFromHistory(log.Read(ct).Records, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _ => null;
        }
    }
}
