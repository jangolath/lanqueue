using LanQueue.Config;
using LanQueue.Health;

namespace LanQueue.Scheduling;

public static class Scheduler
{
    /// <summary>
    /// Eligible machines (all required capabilities, reachable, not on hold, a free slot), least loaded first.
    /// Ties break on fewer used slots, then name, so the order is deterministic.
    /// </summary>
    public static List<Machine> Rank(IEnumerable<(Machine Machine, MachineHealth Health)> candidates, IReadOnlyCollection<string> needs) =>
        candidates
            .Where(c => c.Health.Reachable
                && c.Health.Hold is null
                && c.Health.SlotsUsed < c.Machine.MaxJobs
                && needs.All(n => c.Machine.Capabilities.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(c => c.Health.LoadRatio)
            .ThenBy(c => c.Health.SlotsUsed)
            .ThenBy(c => c.Machine.Name, StringComparer.Ordinal)
            .Select(c => c.Machine)
            .ToList();
}
