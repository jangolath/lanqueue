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

public sealed class PolicyConfig
{
    public double LoadThreshold { get; set; } = 0.80;
    public double MinFreeDiskGb { get; set; } = 5;
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
    public string? AllowedHours { get; set; }
    public TransferMode Transfer { get; set; } = TransferMode.Git;
}

public sealed class FleetConfig
{
    public PolicyConfig Policy { get; set; } = new();
    public List<Machine> Machines { get; set; } = [];
}
