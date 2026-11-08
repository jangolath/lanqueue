using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LanQueue.Config;

public sealed class ConfigException(string message) : Exception(message);

public static partial class ConfigLoader
{
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@-]*$", RegexOptions.None, 1000)]
    private static partial Regex SshTargetRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.None, 1000)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^/[A-Za-z0-9_./-]*$", RegexOptions.None, 1000)]
    private static partial Regex PosixWorkRootRegex();

    [GeneratedRegex(@"^[A-Za-z]:/[A-Za-z0-9_./-]*$", RegexOptions.None, 1000)]
    private static partial Regex WindowsWorkRootRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, 1000)]
    private static partial Regex EnvNameRegex();

    public static FleetConfig Parse(string yaml)
    {
        // Strict: unknown keys (for example "password") are an error, not ignored.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        FleetConfig config;
        try
        {
            config = deserializer.Deserialize<FleetConfig>(yaml) ?? new FleetConfig();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigException($"Invalid config: {ex.Message} {ex.InnerException?.Message}".Trim());
        }

        Validate(config);
        return config;
    }

    public static FleetConfig Load(string path) => Parse(File.ReadAllText(path));

    public static void Validate(FleetConfig config)
    {
        if (config.Machines.Count == 0)
        {
            throw new ConfigException("No machines configured.");
        }

        if (config.Policy.AllowedCommands.Count == 0)
        {
            throw new ConfigException("policy.allowed_commands is empty; nothing could ever run.");
        }

        if (!double.IsFinite(config.Policy.MinFreeDiskGb) || config.Policy.MinFreeDiskGb < 0)
        {
            throw new ConfigException("policy.min_free_disk_gb must be >= 0.");
        }

        if (config.Policy.LoadThreshold is <= 0 or > 1)
        {
            throw new ConfigException("policy.load_threshold must be in (0, 1].");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in config.Machines)
        {
            if (!NameRegex().IsMatch(m.Name) || !seen.Add(m.Name))
            {
                throw new ConfigException($"Machine name '{m.Name}' is invalid or duplicated.");
            }

            if (!SshTargetRegex().IsMatch(m.Ssh))
            {
                throw new ConfigException($"Machine '{m.Name}': ssh target must be user@host (no leading '-', no spaces).");
            }

            if (m.KeyEnv is not null && !EnvNameRegex().IsMatch(m.KeyEnv))
            {
                throw new ConfigException($"Machine '{m.Name}': key_env must be the NAME of an environment variable (not a path). Omit it to use ssh-agent.");
            }

            var rootOk = m.Os == MachineOs.Windows ? WindowsWorkRootRegex().IsMatch(m.WorkRoot) : PosixWorkRootRegex().IsMatch(m.WorkRoot);
            if (!rootOk || m.WorkRoot.Contains("..", StringComparison.Ordinal))
            {
                throw new ConfigException(m.Os == MachineOs.Windows
                    ? $"Machine '{m.Name}': a Windows work_root must be a drive path like D:/lanq (forward slashes, no spaces, no '..')."
                    : $"Machine '{m.Name}': a Linux work_root must be an absolute path like /srv/lanq (no spaces, no '..').");
            }

            if (!Enum.IsDefined(m.Transfer))
            {
                throw new ConfigException($"Machine '{m.Name}': transfer must be 'git' or 'archive'.");
            }

            if (m.MaxJobs < 1)
            {
                throw new ConfigException($"Machine '{m.Name}': max_jobs must be >= 1.");
            }

            if (m.AllowedHours is not null && !HoursWindow.TryParse(m.AllowedHours, out _))
            {
                throw new ConfigException($"Machine '{m.Name}': allowed_hours must look like '22:00-07:00'.");
            }
        }
    }
}
