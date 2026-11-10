using System.Reflection;
using LanQueue.Config;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The config is read through YamlDotNet's compile-time static context (the same code in the tool and the NativeAOT binary). These tests
/// load every key in every shape the loader accepts, so a key or shape the static path cannot handle fails here and not on a user's machine.
/// </summary>
public sealed class StaticYamlTests
{
    private const string Everything = """
        policy:
          load_threshold: 0.75
          min_free_disk_gb: 7.5
          min_free_mem_gb: 1.5
          max_mem_pressure: 30
          window_grace_minutes: 20
          min_window_minutes: 5
          keep_logs_days: 45
          allowed_commands:
            - dotnet build
            - dotnet test
          allowed_directories: [".", src]
        machines:
          - name: lin
            os: linux
            ssh: build@lin.lan
            key_env: LANQ_KEY_LIN
            work_root: /srv/lanq
            capabilities: [dotnet, node]
            max_jobs: 3
            min_free_mem_gb: 2
            env:
              NODE_OPTIONS: "--max-old-space-size=2048"
              DOTNET_GCConserveMemory: "1"
            path_prepend:
              - /home/build/.nvm/versions/node/v22/bin
            priority: normal
            allowed_hours: "22:00-07:00"
            transfer: archive
            size: Heavy
            wake:
              mac: "aa:bb:cc:dd:ee:01"
              broadcast: 10.20.30.255
              port: 7
              wait_seconds: 90
          - name: win
            os: Windows
            ssh: build@win.lan
            work_root: D:/lanq
            capabilities: []
            priority: LOW
            allowed_hours:
              - "Mon-Fri 22:00-07:00"
              - "Sat,Sun 01:00-09:00"
            transfer: Git
        """;

    [Fact]
    public void EveryKeyLoadsWithItsValue()
    {
        var c = ConfigLoader.Parse(Everything);
        Assert.Equal(0.75, c.Policy.LoadThreshold);
        Assert.Equal(7.5, c.Policy.MinFreeDiskGb);
        Assert.Equal(1.5, c.Policy.MinFreeMemGb);
        Assert.Equal(30, c.Policy.MaxMemPressure);
        Assert.Equal(20, c.Policy.WindowGraceMinutes);
        Assert.Equal(5, c.Policy.MinWindowMinutes);
        Assert.Equal(45, c.Policy.KeepLogsDays);
        Assert.Equal(["dotnet build", "dotnet test"], c.Policy.AllowedCommands);
        Assert.Equal([".", "src"], c.Policy.AllowedDirectories);

        var lin = c.Machines[0];
        Assert.Equal(("lin", MachineOs.Linux, "build@lin.lan", "LANQ_KEY_LIN", "/srv/lanq"), (lin.Name, lin.Os, lin.Ssh, lin.KeyEnv, lin.WorkRoot));
        Assert.Equal(["dotnet", "node"], lin.Capabilities);
        Assert.Equal(3, lin.MaxJobs);
        Assert.Equal(2, lin.MinFreeMemGb);
        Assert.Equal("--max-old-space-size=2048", lin.Env["NODE_OPTIONS"]);
        Assert.Equal("1", lin.Env["DOTNET_GCConserveMemory"]);
        Assert.Equal(["/home/build/.nvm/versions/node/v22/bin"], lin.PathPrepend);
        Assert.Equal(JobPriority.Normal, lin.Priority);
        Assert.Equal(["22:00-07:00"], lin.AllowedHours!);
        Assert.Equal(TransferMode.Archive, lin.Transfer);
        Assert.Equal(LanQueue.Health.SizeClass.Heavy, LanQueue.Health.MachineSize.Declared(lin));
        Assert.Null(LanQueue.Health.MachineSize.Declared(c.Machines[1]));
        Assert.Equal(("aa:bb:cc:dd:ee:01", "10.20.30.255", 7, 90), (lin.Wake!.Mac, lin.Wake.Broadcast, lin.Wake.Port, lin.Wake.WaitSeconds));

        var win = c.Machines[1];
        Assert.Equal(MachineOs.Windows, win.Os);
        Assert.Null(win.KeyEnv);
        Assert.Null(win.MinFreeMemGb);
        Assert.Empty(win.Capabilities);
        Assert.Empty(win.Env);
        Assert.Equal(1, win.MaxJobs);
        Assert.Equal(JobPriority.Low, win.Priority);
        Assert.Equal(["Mon-Fri 22:00-07:00", "Sat,Sun 01:00-09:00"], win.AllowedHours!);
        Assert.Equal(TransferMode.Git, win.Transfer);
        Assert.Null(win.Wake);
    }

    /// <summary>A new config key must be added to <see cref="Everything"/>, so this file keeps testing every key the loader accepts.</summary>
    [Fact]
    public void EveryConfigPropertyIsCoveredAbove()
    {
        foreach (var type in new[] { typeof(FleetConfig), typeof(PolicyConfig), typeof(Machine), typeof(WakeConfig) })
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var key = string.Concat(p.Name.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
                Assert.True(Everything.Contains(key + ":", StringComparison.Ordinal), $"{type.Name}.{p.Name} ({key}) is not covered by the static YAML test config");
            }
        }
    }

    /// <summary>
    /// Every class the config can hold must be registered on <see cref="YamlContext"/>: the static deserializer has no reflection fallback,
    /// so a missing [YamlSerializable] makes every config that uses the key fail to load, in the tool and in the native binary alike.
    /// </summary>
    [Fact]
    public void EveryConfigClassIsInTheStaticContext()
    {
        var registered = typeof(YamlContext).GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(YamlDotNet.Serialization.YamlSerializableAttribute))
            .Select(a => (Type)a.ConstructorArguments[0].Value!)
            .ToHashSet();
        var pending = new Stack<Type>([typeof(FleetConfig)]);
        var seen = new HashSet<Type>();
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            Assert.True(registered.Contains(type), $"{type.Name} is part of the config but has no [YamlSerializable] on YamlContext");
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var element = t.IsGenericType ? t.GetGenericArguments().Last() : t.BaseType is { IsGenericType: true } b ? b.GetGenericArguments().Last() : t;
                if (element.IsClass && element != typeof(string) && element.Namespace == typeof(FleetConfig).Namespace)
                {
                    pending.Push(element);
                }
            }
        }

        Assert.Contains(typeof(WakeConfig), seen);
    }

    [Theory]
    [InlineData("allowed_hours:", null)]
    [InlineData("allowed_hours: ~", null)]
    [InlineData("allowed_hours: null", null)]
    [InlineData("allowed_hours: \"~\"", "~")] // quoted: an entry (then refused as not a window), not YAML null
    [InlineData("allowed_hours: 22:00-07:00", "22:00-07:00")]
    [InlineData("allowed_hours: [\"22:00-07:00\"]", "22:00-07:00")]
    public void AllowedHoursAcceptsAStringAListOrNull(string line, string? first)
    {
        var yaml = Everything.Replace("allowed_hours: \"22:00-07:00\"", line, StringComparison.Ordinal);
        if (first == "~")
        {
            Assert.Contains("allowed_hours entry '~'", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
            return;
        }

        var hours = ConfigLoader.Parse(yaml).Machines[0].AllowedHours;
        Assert.Equal(first, hours?.Single());
    }

    [Theory]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: { a: b }", "Invalid config")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: [[a]]", "allowed_hours must be a string or a list")]
    [InlineData("transfer: archive", "transfer: 1", "transfer must be the word 'git' or 'archive'")]
    [InlineData("priority: normal", "priority: true", "priority must be the word 'low' or 'normal'")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: dotnet", "")]
    [InlineData("max_jobs: 3", "max_jobs: three", "")]
    [InlineData("    os: linux", "    os: linux\n    password: x", "password")]
    [InlineData("max_jobs: 3", "max_jobs: ~", "max_jobs")]
    [InlineData("max_jobs: 3", "max_jobs:", "max_jobs")]
    [InlineData("load_threshold: 0.75", "load_threshold: ~", "load_threshold")]
    [InlineData("window_grace_minutes: 20", "window_grace_minutes: null", "window_grace_minutes")]
    [InlineData("    os: linux", "    os:", "'machines.os' has no value")]
    [InlineData("    os: linux", "    os: 7", "os must be the word 'windows' or 'linux'")]
    [InlineData("    os: linux", "    os: 1", "os must be the word 'windows' or 'linux'")]
    [InlineData("    os: linux", "    os: lin", "os must be the word 'windows' or 'linux'")]
    [InlineData("transfer: archive", "transfer: ~", "'machines.transfer' has no value")]
    [InlineData("priority: normal", "priority:", "'machines.priority' has no value")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: ~", "capabilities")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: [dotnet, ~]", "capabilities")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: [dotnet, \"\"]", "capabilities")]
    [InlineData("  allowed_commands:\n    - dotnet build", "  allowed_commands:\n    - ~\n    - dotnet build", "allowed_commands")]
    [InlineData("  allowed_commands:\n    - dotnet build", "  allowed_commands:\n    - \"\"\n    - dotnet build", "allowed_commands")]
    [InlineData("  allowed_commands:\n    - dotnet build", "  allowed_commands:\n    - \"  \"\n    - dotnet build", "allowed_commands")]
    [InlineData("allowed_directories: [\".\", src]", "allowed_directories: [\".\", ~]", "allowed_directories")]
    [InlineData("allowed_directories: [\".\", src]", "allowed_directories: ~", "allowed_directories")]
    [InlineData("    env:\n      NODE_OPTIONS: \"--max-old-space-size=2048\"\n      DOTNET_GCConserveMemory: \"1\"", "    env: ~", "env")]
    [InlineData("    path_prepend:\n      - /home/build/.nvm/versions/node/v22/bin", "    path_prepend: ~", "path_prepend")]
    [InlineData("    path_prepend:\n      - /home/build/.nvm/versions/node/v22/bin", "    path_prepend: [~]", "path_prepend")]
    [InlineData("      - \"Mon-Fri 22:00-07:00\"", "      - ~", "allowed_hours")]
    [InlineData(WakeBlock, "    wake: ~", "'machines.wake' has no value")]
    [InlineData(WakeBlock, "    wake:", "'machines.wake' has no value")]
    [InlineData(WakeBlock, "    wake: null", "'machines.wake' has no value")]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: ~", "'machines.wake.mac' has no value")]
    [InlineData("      port: 7", "      port:", "'machines.wake.port' has no value")]
    [InlineData("      wait_seconds: 90", "      wait_seconds: ~", "'machines.wake.wait_seconds' has no value")]
    [InlineData("      broadcast: 10.20.30.255", "      broadcast: null", "'machines.wake.broadcast' has no value")]
    [InlineData("      port: 7", "      port: 7\n      password: x", "password")]
    [InlineData("      port: 7", "      port: 7\n      mac_address: x", "mac_address")]
    [InlineData(WakeBlock, "    wake: aa:bb:cc:dd:ee:01", "Invalid config")]
    [InlineData(WakeBlock, "    wake: [aa:bb:cc:dd:ee:01]", "Invalid config")]
    [InlineData(WakeBlock, "    wake: {}", "wake.mac is required")]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: [aa]", "Invalid config")]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: \"00:00:00:00:00:00\"", "wake.mac is required")]
    [InlineData("      broadcast: 10.20.30.255", "      broadcast: 198.51.100.255", "wake.broadcast must be 255.255.255.255 or a private")]
    [InlineData("      port: 7", "      port: 8", "wake.port must be 7 or 9")]
    [InlineData("      port: 7", "      port: seven", "Invalid config")]
    [InlineData("      wait_seconds: 90", "      wait_seconds: 601", "wake.wait_seconds must be from 1 to 600")]
    public void WrongShapesAreFriendlyConfigErrors(string from, string to, string mentions)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml); // the case really changed something
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The wake: block of the first machine in <see cref="Everything"/>.</summary>
    private const string WakeBlock = "    wake:\n      mac: \"aa:bb:cc:dd:ee:01\"\n      broadcast: 10.20.30.255\n      port: 7\n      wait_seconds: 90";

    private const string OneMachine = "machines:\n  - name: a\n    os: linux\n    ssh: u@h\n    work_root: /srv/l\n";

    [Theory]
    [InlineData("just a string\n", "Invalid config")]
    [InlineData("- a\n- b\n", "Invalid config")]
    [InlineData("machines: ~\npolicy:\n  allowed_commands: [dotnet test]\n", "'machines' has no value")]
    [InlineData("machines:\n  - ~\npolicy:\n  allowed_commands: [dotnet test]\n", "machines")]
    [InlineData("policy: ~\n" + OneMachine, "policy")]
    [InlineData("policy:\n  allowed_commands: ~\n" + OneMachine, "allowed_commands")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    env: ~\n", "env")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    env:\n      A: ~\n", "env 'A' needs a value")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    name: b\n", "Duplicate key")] // ambiguous: refused
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    env:\n      A: x\n      A: y\n", "Duplicate key")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    wake:\n      mac: aa:bb:cc:dd:ee:01\n      mac: aa:bb:cc:dd:ee:02\n", "Duplicate key")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    wake:\n      mac: aa:bb:cc:dd:ee:01\n    wake:\n      mac: aa:bb:cc:dd:ee:02\n", "Duplicate key")]
    [InlineData("policy:\n  allowed_commands: [dotnet test]\n" + OneMachine + "    wake: {mac: \"aa:bb:cc:dd:ee:01\", port: 9, port: 7}\n", "Duplicate key")]
    public void WrongDocumentShapesAreFriendlyConfigErrors(string yaml, string mentions) =>
        Assert.Contains(mentions, Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);

    /// <summary>The same shapes behind --json (health, run and wake): exit 2 and exactly one JSON object on stdout, never a crash.</summary>
    [Theory]
    [InlineData("just a string\n")]
    [InlineData("machines: ~\n")]
    [InlineData("policy: ~\n" + OneMachine)]
    [InlineData("policy:\n  allowed_commands: [x, ~]\n" + OneMachine)]
    [InlineData("policy:\n  allowed_commands: [x]\nmachines:\n  - name: a\n    os: 7\n    ssh: u@h\n    work_root: /srv/l\n")]
    [InlineData("policy:\n  allowed_commands: [x]\n" + OneMachine + "    max_jobs: ~\n")]
    [InlineData("policy:\n  allowed_commands: [x]\n" + OneMachine + "    wake: ~\n")]
    [InlineData("policy:\n  allowed_commands: [x]\n" + OneMachine + "    wake:\n      mac: aa:bb:cc:dd:ee:01\n      color: red\n")]
    public async Task BadShapesStillGiveOneJsonObject(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "lanq-shape-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);
        try
        {
            foreach (var args in new[] { new[] { "health", "--json", "--config", path }, ["run", "--json", "--config", path, "--", "dotnet", "test"], ["wake", "a", "--json", "--config", path] })
            {
                var o = new StringWriter();
                var code = await Cli.RunAsync(args, o, new StringWriter(), CancellationToken.None);
                Assert.Equal(2, code);
                var line = Assert.Single(o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Explicit standard YAML tags load as they did in 0.4 (the tag is ignored, the value read as if untagged).</summary>
    [Theory]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!str \"22:00-07:00\"")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!seq [\"22:00-07:00\"]")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: [!!str \"22:00-07:00\"]")]
    [InlineData("max_jobs: 3", "max_jobs: !!str 3")]
    [InlineData("max_jobs: 3", "max_jobs: !!int \"3\"")]
    [InlineData("max_jobs: 3", "max_jobs: !!float 3.0")]
    [InlineData("min_free_mem_gb: 2", "min_free_mem_gb: !!int 2")]
    [InlineData("min_free_mem_gb: 2", "min_free_mem_gb: !!float 2.0")]
    [InlineData("transfer: archive", "transfer: !!str archive")]
    [InlineData("    ssh: build@lin.lan", "    ssh: !!str build@lin.lan")]
    [InlineData("    env:\n", "    env: !!map\n")]
    public void StandardTagsLoadAsIn04(string from, string to)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        var lin = ConfigLoader.Parse(yaml).Machines[0];
        Assert.Equal(["22:00-07:00"], lin.AllowedHours!);
        Assert.Equal(3, lin.MaxJobs);
        Assert.Equal(2, lin.MinFreeMemGb);
        Assert.Equal(TransferMode.Archive, lin.Transfer);
        Assert.Equal("build@lin.lan", lin.Ssh);
        Assert.Equal("1", lin.Env["DOTNET_GCConserveMemory"]);
    }

    /// <summary>The wake: block goes through the same tag stripping: standard tags load as the untagged value.</summary>
    [Theory]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: !!str \"aa:bb:cc:dd:ee:01\"")]
    [InlineData("      broadcast: 10.20.30.255", "      broadcast: !!str 10.20.30.255")]
    [InlineData("      port: 7", "      port: !!int \"7\"")]
    [InlineData("      port: 7", "      port: !!float 7.0")]
    [InlineData("      wait_seconds: 90", "      wait_seconds: !!str 90")]
    [InlineData("    wake:\n", "    wake: !!map\n")]
    public void StandardTagsLoadInsideWake(string from, string to)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        var wake = ConfigLoader.Parse(yaml).Machines[0].Wake!;
        Assert.Equal(("aa:bb:cc:dd:ee:01", "10.20.30.255", 7, 90), (wake.Mac, wake.Broadcast, wake.Port, wake.WaitSeconds));
    }

    [Theory]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: !mac \"aa:bb:cc:dd:ee:01\"", "!mac")]
    [InlineData("    wake:\n", "    wake: !wol\n", "!wol")]
    [InlineData("      port: 7", "      port: !!binary Bw==", "tag:yaml.org,2002:binary")]
    public void UnknownTagsInsideWakeAreRefusedByName(string from, string to, string tag)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        Assert.Contains($"tag '{tag}' is not supported", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
    }

    /// <summary>Only mac is required: the other wake keys keep their defaults when left out.</summary>
    [Fact]
    public void WakeWithOnlyAMacUsesTheDefaults()
    {
        var wake = ConfigLoader.Parse("policy:\n  allowed_commands: [x]\n" + OneMachine + "    wake: {mac: aa-bb-cc-dd-ee-02}\n").Machines[0].Wake!;
        Assert.Equal(("aa-bb-cc-dd-ee-02", "255.255.255.255", 9, 120), (wake.Mac, wake.Broadcast, wake.Port, wake.WaitSeconds));
    }

    [Fact]
    public void ATaggedNullIsTreatedAsIn04()
    {
        // 0.4: a bare (plain) !!null means "not set"; a quoted one is refused (0.4 refused !!null '' too, as an empty entry).
        Assert.Null(ConfigLoader.Parse(Everything.Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!null", StringComparison.Ordinal)).Machines[0].AllowedHours);
        Assert.Null(ConfigLoader.Parse(Everything.Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!null ~", StringComparison.Ordinal)).Machines[0].AllowedHours);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Everything.Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!null ''", StringComparison.Ordinal)));
        Assert.Contains("YAML tag 'tag:yaml.org,2002:null'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A quoted scalar is a string, so a !!null on it would let the string "null" through as a value: refused.</summary>
    [Theory]
    [InlineData("DOTNET_GCConserveMemory: \"1\"", "DOTNET_GCConserveMemory: !!null \"null\"")]
    [InlineData("  allowed_commands:\n    - dotnet build", "  allowed_commands:\n    - !!null \"null\"\n    - dotnet build")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: [dotnet, !!null '~']")]
    public void ANullTagOnAQuotedScalarIsRefused(string from, string to)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
        Assert.Contains("YAML tag 'tag:yaml.org,2002:null' does not fit", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !custom \"22:00-07:00\"", "!custom")]
    [InlineData("max_jobs: 3", "max_jobs: !!timestamp 2001-12-14", "tag:yaml.org,2002:timestamp")]
    [InlineData("max_jobs: 3", "max_jobs: !!binary aGk=", "tag:yaml.org,2002:binary")]
    [InlineData("    env:\n", "    env: !<tag:example.com,2026:env>\n", "tag:example.com,2026:env")]
    public void UnknownTagsAreRefusedByName(string from, string to, string tag)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
        Assert.Contains($"tag '{tag}' is not supported", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// !!str keeps a value a string: "~", "null" and an empty !!str are strings, never YAML null, so allowed_hours cannot silently turn
    /// into "any time" (0.4 refused these as invalid entries).
    /// </summary>
    [Theory]
    [InlineData("allowed_hours: !!str ~", "allowed_hours entry '~'")]
    [InlineData("allowed_hours: !!str null", "allowed_hours entry 'null'")]
    [InlineData("allowed_hours: !!str", "allowed_hours entry ''")]
    [InlineData("allowed_hours: !!str ''", "allowed_hours entry ''")]
    public void AStrTaggedNullWordIsAStringNotNull(string line, string mentions)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Everything.Replace("allowed_hours: \"22:00-07:00\"", line, StringComparison.Ordinal)));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStrTaggedBoolWordIsAString()
    {
        var c = ConfigLoader.Parse(Everything
            .Replace("    key_env: LANQ_KEY_LIN", "    key_env: !!str true", StringComparison.Ordinal)
            .Replace("DOTNET_GCConserveMemory: \"1\"", "DOTNET_GCConserveMemory: !!str yes", StringComparison.Ordinal));
        Assert.Equal("true", c.Machines[0].KeyEnv);
        Assert.Equal("yes", c.Machines[0].Env["DOTNET_GCConserveMemory"]);
    }

    /// <summary>A tag that contradicts its node (or the non-specific '!') is refused with the tag and line, never silently ignored.</summary>
    [Theory]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: ! \"22:00-07:00\"", "'!'")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!map [\"22:00-07:00\"]", "'tag:yaml.org,2002:map'")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!map \"22:00-07:00\"", "'tag:yaml.org,2002:map'")]
    [InlineData("    env:\n", "    env: !!seq\n", "'tag:yaml.org,2002:seq'")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!seq \"22:00-07:00\"", "'tag:yaml.org,2002:seq'")]
    [InlineData("capabilities: [dotnet, node]", "capabilities: !!str [dotnet, node]", "'tag:yaml.org,2002:str'")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: !!null \"22:00-07:00\"", "'tag:yaml.org,2002:null'")]
    [InlineData("max_jobs: 3", "max_jobs: !!int three", "'tag:yaml.org,2002:int'")]
    [InlineData("max_jobs: 3", "max_jobs: !!int 3.5", "'tag:yaml.org,2002:int'")]
    [InlineData("min_free_mem_gb: 2", "min_free_mem_gb: !!float two", "'tag:yaml.org,2002:float'")]
    [InlineData("    ssh: build@lin.lan", "    ssh: !!bool build@lin.lan", "'tag:yaml.org,2002:bool'")]
    [InlineData("      mac: \"aa:bb:cc:dd:ee:01\"", "      mac: ! aa:bb:cc:dd:ee:01", "'!'")] // inside wake: as everywhere else
    public void TagsThatContradictTheirNodeAreRefused(string from, string to, string tag)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
        Assert.Contains("YAML tag " + tag, ex.Message, StringComparison.Ordinal);
        Assert.Contains("(line ", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    os: linux", "    os: !!int 1", "os must be the word")]
    [InlineData("max_jobs: 3", "max_jobs: !!float 2.5", "Invalid config")] // 0.4 truncated it to 2
    [InlineData("  allowed_directories:", "  allowed_commands: [rm]\n  allowed_directories:", "Duplicate key")] // used to replace the allowlist
    public void OtherShapesThat04AcceptedAreRefused(string from, string to, string mentions)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        Assert.Contains(mentions, Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
    }

    /// <summary>An anchor whose node contains its own alias would recurse for ever; it is a config error, and --json still gives one object.</summary>
    [Theory]
    [InlineData("capabilities: [dotnet, node]", "capabilities: &s [*s]")]
    [InlineData("    env:\n      NODE_OPTIONS", "    env: &e\n      SELF: *e\n      NODE_OPTIONS")]
    public async Task ASelfReferencingAnchorIsRefused(string from, string to)
    {
        var yaml = Everything.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Everything, yaml);
        Assert.Contains("refers to itself", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), "lanq-cycle-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);
        try
        {
            var o = new StringWriter();
            Assert.Equal(2, await Cli.RunAsync(["health", "--json", "--config", path], o, new StringWriter(), CancellationToken.None));
            using var doc = System.Text.Json.JsonDocument.Parse(Assert.Single(o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A YAML alias for allowed_hours is accepted and means the anchored value (pinned behaviour).</summary>
    [Fact]
    public void AllowedHoursAcceptsAnAlias()
    {
        var yaml = Everything
            .Replace("allowed_hours: \"22:00-07:00\"", "allowed_hours: &night \"22:00-07:00\"", StringComparison.Ordinal)
            .Replace("    allowed_hours:\n      - \"Mon-Fri 22:00-07:00\"\n      - \"Sat,Sun 01:00-09:00\"", "    allowed_hours: *night", StringComparison.Ordinal);
        Assert.Contains("*night", yaml, StringComparison.Ordinal);
        var c = ConfigLoader.Parse(yaml);
        Assert.Equal(["22:00-07:00"], c.Machines[0].AllowedHours!);
        Assert.Equal(["22:00-07:00"], c.Machines[1].AllowedHours!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("machines: []\npolicy: {}\n")]
    public void EmptyConfigsLoadAndThenFailValidation(string yaml) =>
        Assert.Contains("No machines", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
}
