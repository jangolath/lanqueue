using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LanQueue.Config;

public sealed class ConfigException(string message) : Exception(message);

/// <summary>Accepts only the exact words 'git' and 'archive' (any case); a number, bool or anything else is a friendly error.</summary>
internal sealed class TransferModeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TransferMode);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        if (string.Equals(scalar.Value, "git", StringComparison.OrdinalIgnoreCase))
        {
            return TransferMode.Git;
        }

        return string.Equals(scalar.Value, "archive", StringComparison.OrdinalIgnoreCase)
            ? TransferMode.Archive
            : throw new YamlException(scalar.Start, scalar.End, $"transfer must be the word 'git' or 'archive' (got '{scalar.Value}').");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException();
}

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
            .WithTypeConverter(new TransferModeConverter())
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

        if (!double.IsFinite(config.Policy.MinFreeMemGb) || config.Policy.MinFreeMemGb < 0)
        {
            throw new ConfigException("policy.min_free_mem_gb must be >= 0.");
        }

        if (!double.IsFinite(config.Policy.MaxMemPressure) || config.Policy.MaxMemPressure is < 0 or > 100)
        {
            throw new ConfigException("policy.max_mem_pressure must be from 0 to 100 (percent of time stalled on memory).");
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

            if (m.MinFreeMemGb is { } minMem && (!double.IsFinite(minMem) || minMem < 0))
            {
                throw new ConfigException($"Machine '{m.Name}': min_free_mem_gb must be >= 0.");
            }

            if (m.MaxJobs < 1)
            {
                throw new ConfigException($"Machine '{m.Name}': max_jobs must be >= 1.");
            }

            if (m.AllowedHours is not null)
            {
                if (!HoursWindow.TryParse(m.AllowedHours, out var hours))
                {
                    throw new ConfigException($"Machine '{m.Name}': allowed_hours must look like '22:00-07:00'.");
                }

                if (hours.IsSingleInstant)
                {
                    throw new ConfigException(
                        $"Machine '{m.Name}': allowed_hours '{m.AllowedHours}' has the same start and end, so it would match a single minute. "
                        + "Omit allowed_hours to allow all day, or give a real window like '22:00-07:00'.");
                }
            }
        }
    }
}
