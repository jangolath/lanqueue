using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LanQueue.Config;

/// <summary>A config problem. The message often quotes what the user wrote, so it is sanitised like worker output (no escapes, bidi, newlines).</summary>
public sealed class ConfigException(string message) : Exception(Jobs.JobRunner.SafeLine(message));

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

/// <summary>
/// A key that accepts either one string or a list of strings (allowed_hours); anything else is a friendly error. Used only where a property
/// names it with [YamlConverter]: Accepts is false so other lists (capabilities, allowed_commands) stay lists only.
/// </summary>
internal sealed class StringOrListConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var single))
        {
            // A bare "allowed_hours:" or "~" / null is YAML null: no hours set, any time (as in 0.3). A quoted "~" stays an entry.
            return single.Style == ScalarStyle.Plain && single.Value is "" or "~" or "null" or "Null" or "NULL"
                ? null
                : new List<string> { single.Value };
        }

        var start = parser.Consume<SequenceStart>();
        var list = new List<string>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            list.Add(parser.TryConsume<Scalar>(out var item)
                ? item.Value
                : throw new YamlException(start.Start, start.End, "allowed_hours must be a string or a list of strings such as \"Mon-Fri 18:00-23:00\"."));
        }

        return list;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException();
}

public static partial class ConfigLoader
{
    // Every pattern that guards user input ends in \z, never $: "$" would also match just before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@-]*\z", RegexOptions.None, 1000)]
    private static partial Regex SshTargetRegex();

    /// <summary>The same rule a machine's ssh: must follow (user@host, no leading '-', no spaces).</summary>
    internal static bool IsValidSshTarget(string target) => SshTargetRegex().IsMatch(target);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.None, 1000)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^/[A-Za-z0-9_./-]*\z", RegexOptions.None, 1000)]
    private static partial Regex PosixWorkRootRegex();

    [GeneratedRegex(@"^[A-Za-z]:/[A-Za-z0-9_./-]*\z", RegexOptions.None, 1000)]
    private static partial Regex WindowsWorkRootRegex();

    // \z, not $: "$" would also match just before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.None, 1000)]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex(@"^[A-Za-z]:[/\\]", RegexOptions.None, 1000)]
    private static partial Regex WindowsAbsolutePathRegex();

    /// <summary>
    /// Names that would let a value hijack the dynamic loader or a shell's startup or tracing; refused in env: (compared ignoring case), as
    /// is every name starting with LD_ or DYLD_. Best effort: the config's author is trusted, this only catches the obvious mistakes.
    /// </summary>
    private static readonly string[] DeniedEnvNames =
        ["BASH_ENV", "ENV", "PROMPT_COMMAND", "SHELLOPTS", "BASHOPTS", "PS4", "GLIBC_TUNABLES", "PSModulePath", "COMSPEC", "PATHEXT"];

    private static readonly string[] DeniedEnvPrefixes = ["LD_", "DYLD_"];

    public static FleetConfig Parse(string yaml)
    {
        // Strict: unknown keys (for example "password") are an error, not ignored.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new WordEnumConverter<TransferMode>("transfer"))
            .WithTypeConverter(new WordEnumConverter<JobPriority>("priority"))
            .WithTypeConverter(new StringOrListConverter())
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

        if (config.Policy.WindowGraceMinutes is < 0 or > 1440)
        {
            throw new ConfigException("policy.window_grace_minutes must be from 0 to 1440.");
        }

        if (config.Policy.MinWindowMinutes is < 0 or > 1440)
        {
            throw new ConfigException("policy.min_window_minutes must be from 0 to 1440.");
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
            ValidateWindowsCommandLength(m);
            ValidateWake(m);

            if (m.MaxJobs < 1)
            {
                throw new ConfigException($"Machine '{m.Name}': max_jobs must be >= 1.");
            }

            if (m.AllowedHours is { Count: 0 })
            {
                throw new ConfigException($"Machine '{m.Name}': allowed_hours is an empty list. Omit it to allow any time.");
            }

            foreach (var entry in m.AllowedHours ?? [])
            {
                if (!HoursWindow.TryParse(entry, out var hours))
                {
                    throw new ConfigException(
                        $"Machine '{m.Name}': allowed_hours entry '{entry.ReplaceLineEndings(" ")}' is not valid. Use '[Days ]HH:mm-HH:mm', for example "
                        + "'22:00-07:00', 'Mon-Fri 18:00-23:00' or 'Sat,Sun 08:00-22:00' (days Mon..Sun, ranges with '-', lists with ',').");
                }

                if (hours.IsSingleInstant)
                {
                    throw new ConfigException(
                        $"Machine '{m.Name}': allowed_hours '{entry}' has the same start and end, so it would match a single minute. "
                        + "Omit allowed_hours to allow all day, or give a real window like '22:00-07:00'.");
                }
            }
        }
    }

    private static void ValidateWake(Machine m)
    {
        if (m.Wake is not { } w)
        {
            return;
        }

        if (!Health.MagicPacket.TryParseMac(w.Mac, out _))
        {
            throw new ConfigException($"Machine '{m.Name}': wake.mac is required and must look like aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff (not all zeros or all ff).");
        }

        if (!Health.MagicPacket.TryParseBroadcast(w.Broadcast, out _))
        {
            throw new ConfigException($"Machine '{m.Name}': wake.broadcast must be an IPv4 address such as 10.20.30.255 (no host names, no IPv6). Omit it for 255.255.255.255.");
        }

        if (w.Port is not (7 or 9))
        {
            throw new ConfigException($"Machine '{m.Name}': wake.port must be 7 or 9.");
        }

        if (w.WaitSeconds is < 1 or > 600)
        {
            throw new ConfigException($"Machine '{m.Name}': wake.wait_seconds must be from 1 to 600.");
        }
    }

    /// <summary>
    /// A Windows worker gets each script as one encoded command line of at most 8000 characters. The machine's env: and path_prepend: go
    /// into the job script, so if they alone, with no job arguments at all, already pass the cap, every job there would fail: refuse the
    /// config now and say by how much. Only the job script gates loading; lanq doctor checks (and reports) its own script's size.
    /// </summary>
    private static void ValidateWindowsCommandLength(Machine m)
    {
        if (m.Os != MachineOs.Windows)
        {
            return;
        }

        var size = Jobs.RemoteScripts.WindowsCommand(Jobs.RemoteScripts.Execute(m, "20260101-000000-000000", ".", [], Jobs.JobRequest.DefaultTimeout)).Length;
        var limit = Jobs.RemoteScripts.MaxWindowsCommandLength;
        if (size > limit)
        {
            throw new ConfigException(
                $"Machine '{m.Name}': its env: ({m.Env.Count} entries) and path_prepend: ({m.PathPrepend.Count} entries) make the Windows job script "
                + $"{size} characters once encoded, {size - limit} over the {limit}-character Windows command-line limit before any job arguments. "
                + "Shorten or remove some env: values or path_prepend: entries.");
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

            if (DeniedEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || DeniedEnvNames.Contains(name, StringComparer.OrdinalIgnoreCase))
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
                throw new ConfigException($"Machine '{m.Name}': env '{name}' needs a value without control characters (no newlines, tabs or escapes).");
            }

            // Windows PowerShell 5.1 has no way to set an empty variable ($env:X = '' removes it) while Linux would set it empty; one rule
            // for both: an empty value is refused instead of meaning different things per OS.
            if (value.Length == 0)
            {
                throw new ConfigException($"Machine '{m.Name}': env '{name}' is empty. Empty values are not supported (Windows cannot set one); leave the variable out instead.");
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
