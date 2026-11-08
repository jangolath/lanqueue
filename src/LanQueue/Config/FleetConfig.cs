namespace LanQueue.Config;

public enum MachineOs
{
    Windows,
    Linux,
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
}

public sealed class FleetConfig
{
    public PolicyConfig Policy { get; set; } = new();
    public List<Machine> Machines { get; set; } = [];
}
