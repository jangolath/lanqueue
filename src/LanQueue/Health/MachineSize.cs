using LanQueue.Config;

namespace LanQueue.Health;

/// <summary>A machine's size class, for <c>lanq health</c> (suited for) and for choosing where work goes.</summary>
public enum SizeClass
{
    /// <summary>The machine did not report its total memory.</summary>
    Unknown,

    /// <summary>Under 6 GB (rounded up to whole GB): lint and unit-test shards.</summary>
    Light,

    /// <summary>6 to under 16 GB (or 16 GB and more with fewer than 4 cores): test shards and builds.</summary>
    Medium,

    /// <summary>16 GB or more and at least 4 cores: full suites and large builds.</summary>
    Heavy,
}

/// <summary>
/// "Suited for", from the reported total memory, cores (logical CPUs) and capabilities, by the same rule as lanq-makeready, so one PC gets
/// the same tier from both: the visible memory is first rounded UP to whole GB (it is always a little under the installed RAM, and the
/// scripts size by installed RAM: the sh script rounds MemTotal up to whole GiB, the PowerShell one adds 0.5 GB and rounds), then under 6 GB
/// is light, 16 GB or more with 4 or more cores is heavy, anything between (or a big machine with fewer than 4 cores) is medium. A docker
/// capability adds container builds. The wording is the scripts' own.
/// </summary>
public static class MachineSize
{
    /// <summary>Rounded memory below this is light.</summary>
    public const double LightBelowGb = 6;

    /// <summary>Rounded memory from this is heavy, with <see cref="HeavyMinCores"/>.</summary>
    public const double HeavyFromGb = 16;

    public const int HeavyMinCores = 4;

    /// <summary>Visible memory as lanq-makeready counts it: rounded up to whole GB (15.8 is 16, 5.8 is 6, 5.0 stays 5).</summary>
    public static double RoundedGb(double memTotalGb) => Math.Ceiling(memTotalGb);

    public static SizeClass Classify(double? memTotalGb, int? cores) => memTotalGb is not { } gb ? SizeClass.Unknown : RoundedGb(gb) switch
    {
        < LightBelowGb => SizeClass.Light,
        >= HeavyFromGb when cores >= HeavyMinCores => SizeClass.Heavy,
        _ => SizeClass.Medium,
    };

    /// <summary>The "suited for" text, word for word as lanq-makeready prints it, plus " + container builds" with a docker capability.</summary>
    public static string SuitedFor(Machine machine, double? memTotalGb, int? cores)
    {
        var size = Classify(memTotalGb, cores);
        var text = size switch
        {
            SizeClass.Light => "light: lint and unit-test shards",
            SizeClass.Medium => "medium: test shards and builds",
            SizeClass.Heavy => "heavy: full suites and large builds",
            _ => "unknown: the machine did not report its memory",
        };
        return machine.Capabilities.Contains("docker", StringComparer.OrdinalIgnoreCase) ? text + " + container builds" : text;
    }
}
