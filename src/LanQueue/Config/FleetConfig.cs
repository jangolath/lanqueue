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
    public List<string> AllowedCommands { get; set; } = [];
    public List<string> AllowedDirectories { get; set; } = [];
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
    [YamlDotNet.Serialization.YamlConverter(typeof(StringOrListConverter))]
    public List<string>? AllowedHours { get; set; }
    public TransferMode Transfer { get; set; } = TransferMode.Git;
}

public sealed class FleetConfig
{
    public PolicyConfig Policy { get; set; } = new();
    public List<Machine> Machines { get; set; } = [];
}
