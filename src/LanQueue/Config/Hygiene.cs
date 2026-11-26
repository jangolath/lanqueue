namespace LanQueue.Config;

/// <summary>
/// machines.yml hygiene: (0.13.0 D1): named environment profiles for a worker's jobs, expanded into job variables. They apply to the job
/// process only, like env:; an explicit env: name wins over a profile's, and a job's own --env wins too (without an env conflict, review L7).
/// lanq never runs "dotnet build-server shutdown": it would stop every build server of the account, a sibling slot's included.
/// </summary>
public static class Hygiene
{
    /// <summary>MSBuild node reuse off (each build's nodes end with it), no telemetry, no logo.</summary>
    public const string Dotnet = "dotnet";

    /// <summary><see cref="Dotnet"/> plus UseSharedCompilation=false: the compiler runs in the build process, so no compiler server stays resident.</summary>
    public const string DotnetLean = "dotnet-lean";

    /// <summary>The profile words machines.yml accepts.</summary>
    public static readonly string[] Words = [Dotnet, DotnetLean];

    private static readonly (string Name, string Value)[] DotnetVariables =
        [("MSBUILDDISABLENODEREUSE", "1"), ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1")];

    /// <summary>
    /// The variables the machine's hygiene: adds to its jobs, in a fixed order, leaving out every name its own env: sets (names compared ignoring
    /// case). Empty without hygiene:.
    /// </summary>
    public static List<(string Name, string Value)> EnvFor(Machine m)
    {
        var vars = new List<(string Name, string Value)>();
        if (m.Hygiene is not { Count: > 0 } words)
        {
            return vars;
        }

        vars.AddRange(DotnetVariables);
        if (words.Contains(DotnetLean, StringComparer.Ordinal))
        {
            vars.Add(("UseSharedCompilation", "false"));
        }

        return [.. vars.Where(v => !m.Env.Keys.Contains(v.Name, StringComparer.OrdinalIgnoreCase))];
    }
}
