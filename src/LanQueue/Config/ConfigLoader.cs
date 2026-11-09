using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LanQueue.Config;

public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Accepts only the enum's own names as words, in any case (for transfer: 'git' or 'archive'; for priority: 'low' or 'normal'); a number,
/// bool or anything else is a friendly error instead of being read as an enum value.
/// </summary>
internal sealed class WordEnumConverter<TEnum>(string key) : IYamlTypeConverter
    where TEnum : struct, Enum
{
    public bool Accepts(Type type) => type == typeof(TEnum);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        var name = Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, scalar.Value, StringComparison.OrdinalIgnoreCase));
        return name is not null
            ? Enum.Parse<TEnum>(name)
            : throw new YamlException(
                scalar.Start,
                scalar.End,
                $"{key} must be the word {string.Join(" or ", Enum.GetNames<TEnum>().Select(n => "'" + n.ToLowerInvariant() + "'"))} (got '{scalar.Value}').");
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

    // \z, not $: "$" would also match just before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.None, 1000)]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex(@"^[A-Za-z]:[/\\]", RegexOptions.None, 1000)]
    private static partial Regex WindowsAbsolutePathRegex();

    /// <summary>Names that would let a value hijack the dynamic loader or a shell's startup; refused in env: (compared ignoring case).</summary>
    private static readonly string[] DeniedEnvNames = ["LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "BASH_ENV", "ENV", "PROMPT_COMMAND", "PSModulePath", "COMSPEC", "PATHEXT"];

    public static FleetConfig Parse(string yaml)
    {
        // Strict: unknown keys (for example "password") are an error, not ignored.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new WordEnumConverter<TransferMode>("transfer"))
            .WithTypeConverter(new WordEnumConverter<JobPriority>("priority"))
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

            ValidateJobEnvironment(m);

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

    private static void ValidateJobEnvironment(Machine m)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in m.Env)
        {
            if (!EnvNameRegex().IsMatch(name))
            {
                throw new ConfigException($"Machine '{m.Name}': env names must be letters, digits and _ and not start with a digit (got '{name.ReplaceLineEndings(" ")}').");
            }

            if (string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigException($"Machine '{m.Name}': env '{name}' is not allowed; list extra directories under path_prepend instead.");
            }

            if (name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase) || DeniedEnvNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ConfigException($"Machine '{m.Name}': env '{name}' is refused because it can hijack the program loader or a shell.");
            }

            if (!names.Add(name))
            {
                throw new ConfigException($"Machine '{m.Name}': env '{name}' is listed twice (names are compared ignoring case, as on Windows).");
            }

            // The value is never echoed: it may be something the owner would rather not see in a terminal.
            if (value is null || value.Any(char.IsControl))
            {
                throw new ConfigException($"Machine '{m.Name}': env '{name}' needs a value without control characters (no newlines, tabs or escapes); use \"\" for empty.");
            }
        }

        foreach (var dir in m.PathPrepend)
        {
            var ok = !string.IsNullOrEmpty(dir) && !dir.Any(char.IsControl) && (m.Os == MachineOs.Windows
                ? WindowsAbsolutePathRegex().IsMatch(dir) && !dir.Contains(';', StringComparison.Ordinal)
                : dir.StartsWith('/') && !dir.Contains(':', StringComparison.Ordinal));
            if (!ok)
            {
                throw new ConfigException(m.Os == MachineOs.Windows
                    ? $"Machine '{m.Name}': each path_prepend entry must be an absolute drive path such as C:/Users/me/.cargo/bin (no ';', no control characters)."
                    : $"Machine '{m.Name}': each path_prepend entry must be an absolute directory such as /home/me/.nvm/versions/node/v22/bin (no ':', no control characters).");
            }
        }
    }
}
