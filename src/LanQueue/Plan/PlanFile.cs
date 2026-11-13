using LanQueue.Output;

namespace LanQueue.Plan;

/// <summary>
/// A problem with a plan file (lanq run --plan: Rejected, exit 2). The message starts with the file's name, names the job and line where it
/// can ("lanq.yml: job 'integration' (line 14): ..."), and is sanitised like worker output because it may quote what the file says.
/// </summary>
public sealed class PlanFileException(string message) : Exception(Jobs.JobRunner.SafeLine(message));

/// <summary>
/// One job of a plan file, expanded to the exact lanq run command line it stands for: every rule, range and message is the CLI's because
/// <see cref="Args"/> is <see cref="ParsedArgs.Parse"/> of <see cref="RunArgs"/>.
/// </summary>
/// <param name="Name">The job's name as written in the file (unique ignoring case).</param>
/// <param name="Line">The line of the job's name in the file (1-based).</param>
/// <param name="Index">The job's place in the file (0-based): ties in launch order go to the earlier job.</param>
/// <param name="After">The jobs this one starts after (as written in the file; each one is a job of the file, never itself, no cycles).</param>
/// <param name="RunArgs">The lanq run arguments without the program name: "run", the options from the job and defaults:, "--", the command.</param>
/// <param name="Args">The parsed <see cref="RunArgs"/>: needs, dir, mem, size, on, timeout, shards, fetch and the command (Argv).</param>
public sealed record PlanJob(string Name, int Line, int Index, IReadOnlyList<string> After, IReadOnlyList<string> RunArgs, ParsedArgs Args)
{
    /// <summary>"lanq run ..." as one line for bash and PowerShell, or null when an argument cannot be written safely for both (see <see cref="CommandLine.Render"/>).</summary>
    public string? RunCommand => CommandLine.Render(["lanq", .. RunArgs]);
}

/// <summary>A loaded, checked plan file (lanq.yml): its jobs in file order and the warnings a dry run shows (stderr on a real run).</summary>
/// <param name="Path">The file as it was named (the full path when read by <see cref="PlanFileLoader.Load"/>).</param>
/// <param name="Version">The file's version: (always 1 for now).</param>
public sealed record PlanFile(string Path, int Version, IReadOnlyList<PlanJob> Jobs, IReadOnlyList<string> Warnings)
{
    /// <summary>plan_timeout: M (0.10.0): the whole plan's limit in minutes (--plan-timeout on the command line replaces it); null without it.</summary>
    public int? PlanTimeoutMinutes { get; init; }

    /// <summary>The job of this name (ignoring case, as names are unique that way), or null.</summary>
    public PlanJob? Job(string name) => Jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
}
