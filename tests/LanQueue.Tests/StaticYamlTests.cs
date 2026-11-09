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
              MSBUILDNODECOUNT: "1"
            path_prepend:
              - /home/build/.nvm/versions/node/v22/bin
            priority: normal
            allowed_hours: "22:00-07:00"
            transfer: archive
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
        Assert.Equal(["dotnet build", "dotnet test"], c.Policy.AllowedCommands);
        Assert.Equal([".", "src"], c.Policy.AllowedDirectories);

        var lin = c.Machines[0];
        Assert.Equal(("lin", MachineOs.Linux, "build@lin.lan", "LANQ_KEY_LIN", "/srv/lanq"), (lin.Name, lin.Os, lin.Ssh, lin.KeyEnv, lin.WorkRoot));
        Assert.Equal(["dotnet", "node"], lin.Capabilities);
        Assert.Equal(3, lin.MaxJobs);
        Assert.Equal(2, lin.MinFreeMemGb);
        Assert.Equal("--max-old-space-size=2048", lin.Env["NODE_OPTIONS"]);
        Assert.Equal("1", lin.Env["MSBUILDNODECOUNT"]);
        Assert.Equal(["/home/build/.nvm/versions/node/v22/bin"], lin.PathPrepend);
        Assert.Equal(JobPriority.Normal, lin.Priority);
        Assert.Equal(["22:00-07:00"], lin.AllowedHours!);
        Assert.Equal(TransferMode.Archive, lin.Transfer);

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
    }

    /// <summary>A new config key must be added to <see cref="Everything"/>, so this file keeps testing every key the loader accepts.</summary>
    [Fact]
    public void EveryConfigPropertyIsCoveredAbove()
    {
        foreach (var type in new[] { typeof(FleetConfig), typeof(PolicyConfig), typeof(Machine) })
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var key = string.Concat(p.Name.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
                Assert.True(Everything.Contains(key + ":", StringComparison.Ordinal), $"{type.Name}.{p.Name} ({key}) is not covered by the static YAML test config");
            }
        }
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
    public void WrongShapesAreFriendlyConfigErrors(string from, string to, string mentions)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(Everything.Replace(from, to, StringComparison.Ordinal)));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("machines: []\npolicy: {}\n")]
    public void EmptyConfigsLoadAndThenFailValidation(string yaml) =>
        Assert.Contains("No machines", Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml)).Message, StringComparison.Ordinal);
}
