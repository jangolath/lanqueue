using LanQueue.Config;

namespace LanQueue.Health;

/// <summary>A machine's size class, for <c>lanq health</c> (suited for) and for choosing where work goes.</summary>
public enum SizeClass
{
    /// <summary>The machine did not report its total memory.</summary>
    Unknown,

    /// <summary>Under 6 GB: lint, type-check, unit-test shards.</summary>
    Light,

    /// <summary>6 to 16 GB (or 16 GB and more with fewer than 4 cores): test shards, builds.</summary>
    Medium,

    /// <summary>16 GB or more and at least 4 cores: full test suites, large builds.</summary>
    Heavy,
}

/// <summary>
/// "Suited for", from total memory, cores and capabilities. Thresholds are nominal installed sizes; the OS and firmware keep part of the
/// RAM back (a 16 GB PC reports about 15.5 GB, a 6 GB one about 5.8 GB), so the reported total is compared with an allowance: under 5.5 GB is
/// light, from 14.5 GB with 4 or more cores is heavy, anything between (or a big machine with fewer than 4 cores) is medium. A docker
/// capability adds container builds.
/// </summary>
public static class MachineSize
{
    /// <summary>Reported total memory below this (nominal 6 GB) is light.</summary>
    public const double LightBelowGb = 5.5;

    /// <summary>Reported total memory from this (nominal 16 GB) is heavy, with <see cref="HeavyMinCores"/>.</summary>
    public const double HeavyFromGb = 14.5;

    public const int HeavyMinCores = 4;

    public static SizeClass Classify(double? memTotalGb, int? cores) => memTotalGb switch
    {
        null => SizeClass.Unknown,
        < LightBelowGb => SizeClass.Light,
        >= HeavyFromGb when cores >= HeavyMinCores => SizeClass.Heavy,
        _ => SizeClass.Medium,
    };

    /// <summary>The "suited for" text: the class name, what it suits, and "+docker: container builds" when the machine has a docker capability.</summary>
    public static string SuitedFor(Machine machine, double? memTotalGb, int? cores)
    {
        var size = Classify(memTotalGb, cores);
        var text = size switch
        {
            SizeClass.Light => "light: lint, type-check, unit-test shards",
            SizeClass.Medium => "medium: test shards, builds",
            SizeClass.Heavy => "heavy: full test suites, large builds",
            _ => "unknown: the machine did not report its memory",
        };
        return machine.Capabilities.Contains("docker", StringComparer.OrdinalIgnoreCase) ? text + "; +docker: container builds" : text;
    }
}
