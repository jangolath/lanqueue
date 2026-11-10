using System.Collections;

namespace LanQueue.Config;

public enum MachineOs
{
    Windows,
    Linux,
}

public enum TransferMode
{
    /// <summary>Git bundle fetched into a bare cache repo, with a per-job worktree. Recommended; needs git on the worker.</summary>
    Git,

    /// <summary>A tar.gz of the same snapshot, extracted into a fresh per-job folder. Needs only tar on the worker.</summary>
    Archive,
}

public enum JobPriority
{
    /// <summary>The default: the job runs below normal priority so the person using the machine keeps a responsive PC.</summary>
    Low,

    /// <summary>The job runs at the build user's normal priority.</summary>
    Normal,
}

public sealed class PolicyConfig
{
    public double LoadThreshold { get; set; } = 0.80;
    public double MinFreeDiskGb { get; set; } = 5;

    /// <summary>A machine with less available memory (GB) is held; a machine's own min_free_mem_gb overrides it.</summary>
    public double MinFreeMemGb { get; set; } = 1.0;

    /// <summary>Linux PSI memory "some avg10" (percent of time stalled) above this holds the machine.</summary>
    public double MaxMemPressure { get; set; } = 20;

    /// <summary>A job may run this many minutes past the end of its machine's allowed hours before it is stopped.</summary>
    public int WindowGraceMinutes { get; set; } = 15;

    /// <summary>A machine whose allowed hours end sooner than this does not start a new job ("window closing").</summary>
    public int MinWindowMinutes { get; set; } = 10;

    /// <summary>Per-job logs in the state folder's logs/ older than this many days are deleted after a run (at most once a day); 0 keeps them for ever.</summary>
    public int KeepLogsDays { get; set; } = 30;

    public List<string> AllowedCommands { get; set; } = [];
    public List<string> AllowedDirectories { get; set; } = [];
}

/// <summary>wake: (optional per machine) Wake-on-LAN for a sleeping worker. Validated by ConfigLoader.</summary>
public sealed class WakeConfig
{
    /// <summary>aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff. Required.</summary>
    public string Mac { get; set; } = "";

    /// <summary>An IPv4 literal; the default is the limited broadcast.</summary>
    public string Broadcast { get; set; } = "255.255.255.255";

    /// <summary>UDP port, 7 or 9.</summary>
    public int Port { get; set; } = 9;

    /// <summary>How long to wait for the machine to answer ssh after the packet (1 to 600).</summary>
    public int WaitSeconds { get; set; } = 120;
}

public sealed class Machine
{
    public string Name { get; set; } = "";
    public MachineOs Os { get; set; }
    public string Ssh { get; set; } = "";
    public string? KeyEnv { get; set; }
    public string WorkRoot { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
    public int MaxJobs { get; set; } = 1;

    /// <summary>Overrides policy.min_free_mem_gb for this machine.</summary>
    public double? MinFreeMemGb { get; set; }

    /// <summary>Environment variables for the job process only (never for lanq's own remote scripts).</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Absolute directories put in front of PATH for the job (for example an nvm or pyenv bin folder).</summary>
    public List<string> PathPrepend { get; set; } = [];

    /// <summary>low (default): nice/ionice on Linux, BelowNormal on Windows. normal: unchanged.</summary>
    public JobPriority Priority { get; set; } = JobPriority.Low;

    /// <summary>allowed_hours: one entry as a string (as before 0.4) or a list of entries; null = any time.</summary>
    public HoursList? AllowedHours { get; set; }
    public TransferMode Transfer { get; set; } = TransferMode.Git;

    /// <summary>Wake-on-LAN settings; null (the default) means lanq never sends a magic packet to this machine automatically.</summary>
    public WakeConfig? Wake { get; set; }
}

public sealed class FleetConfig
{
    public PolicyConfig Policy { get; set; } = new();
    public List<Machine> Machines { get; set; } = [];
}

/// <summary>
/// The allowed_hours entries. Its own type so that <see cref="StringOrListConverter"/> can claim exactly this key by type: YamlDotNet's
/// static generator (used for trimming and NativeAOT) does not carry a property's [YamlConverter] attribute into the generated code, so a
/// per-property converter would silently be skipped.
/// </summary>
public sealed class HoursList : IEnumerable<string>
{
    // Deliberately not a List<string> subclass: YamlDotNet's static generator (18.x) emits member accessors for a registered type's
    // indexer ("this[]"), which does not compile. Without an indexer it has nothing to emit, and the converter does all the reading.
    private readonly List<string> _items = [];

    public int Count => _items.Count;

    public void Add(string entry) => _items.Add(entry);

    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
