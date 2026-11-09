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
        values.TryGetValue("tool", out var tool);
        if (tool is not ("ok" or "missing"))
        {
            return new MachineHealth(machine.Name, true, 0, 0, 0, "unreadable health output (tool check missing or invalid)", null);
        }

        if (!TryNum(values, "load", out var load) || !TryNum(values, "disk_free_gb", out var disk) || !TryNum(values, "slots_used", out var slotsRaw)
            || load < 0 || disk < 0 || slotsRaw < 0)
        {
            // A missing or garbled key must never read as "idle machine with space": hold instead.
            return new MachineHealth(machine.Name, true, 0, 0, 0, "unreadable health output (load, disk_free_gb or slots_used missing or invalid)", null);
        }

        var hold = tool == "missing"
            ? machine.Transfer == TransferMode.Archive
                ? "tar is missing or unsupported on the machine (transfer: archive needs GNU tar or bsdtar plus gzip on Linux, or a Windows built-in tar.exe that accepts --no-acls --no-xattrs --no-fflags --no-same-owner --no-same-permissions)"
                : "git is not installed on the machine (transfer: git needs git; install it or set transfer: archive)"
            : Evaluate(machine, policy, outside, load, disk);
        return new MachineHealth(machine.Name, true, load, disk, (int)slotsRaw, hold, null);
    }

    /// <summary>"Hold short" rules: outside allowed hours, or load above the threshold.</summary>
    public static string? Evaluate(Machine machine, PolicyConfig policy, bool outsideHours, double load, double diskFreeGb)
    {
        _ = machine;
        if (outsideHours)
        {
            return "outside allowed hours";
        }

        if (diskFreeGb < policy.MinFreeDiskGb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"disk free {diskFreeGb:0.#} GB below minimum {policy.MinFreeDiskGb:0.#} GB");
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

    private static bool TryNum(Dictionary<string, string> v, string key, out double value)
    {
        value = 0;
        return v.TryGetValue(key, out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static string FirstLine(string text) => JobRunner.SafeLine(
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output");
}
