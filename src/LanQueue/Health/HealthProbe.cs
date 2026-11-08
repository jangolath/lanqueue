using System.Globalization;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Ssh;

namespace LanQueue.Health;

/// <param name="Hold">Null when the machine may take work; otherwise why it should be skipped.</param>
public sealed record MachineHealth(
    string Name,
    bool Reachable,
    double LoadRatio,
    double DiskFreeGb,
    int SlotsUsed,
    string? Hold,
    string? Error);

public sealed class HealthProbe(ISshClient ssh, TimeProvider clock)
{
    public async Task<MachineHealth> ProbeAsync(Machine machine, PolicyConfig policy, CancellationToken ct)
    {
        var now = TimeOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var outside = !HoursWindow.IsAllowed(machine.AllowedHours, now);

        SshResult result;
        try
        {
            result = await ssh.RunAsync(machine, RemoteScripts.Wrap(machine, RemoteScripts.Probe(machine)), null, TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new MachineHealth(machine.Name, false, 0, 0, 0, "unreachable", ex.Message);
        }

        if (!result.Ok)
        {
            return new MachineHealth(machine.Name, false, 0, 0, 0, "unreachable", FirstLine(result.Output));
        }

        var values = Parse(result.Output);
        var load = Num(values, "load");
        var disk = Num(values, "disk_free_gb");
        var slots = (int)Num(values, "slots_used");
        return new MachineHealth(machine.Name, true, load, disk, slots, Evaluate(machine, policy, outside, load), null);
    }

    /// <summary>"Hold short" rules: outside allowed hours, or load above the threshold.</summary>
    public static string? Evaluate(Machine machine, PolicyConfig policy, bool outsideHours, double load)
    {
        _ = machine;
        if (outsideHours)
        {
            return "outside allowed hours";
        }

        return load > policy.LoadThreshold
            ? string.Create(CultureInfo.InvariantCulture, $"load {load:0.00} above threshold {policy.LoadThreshold:0.00}")
            : null;
    }

    public static Dictionary<string, string> Parse(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i > 0)
            {
                map[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }

        return map;
    }

    private static double Num(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";
}
