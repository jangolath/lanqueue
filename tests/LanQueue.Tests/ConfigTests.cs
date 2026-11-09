using LanQueue.Config;
using Xunit;

namespace LanQueue.Tests;

public class ConfigTests
{
    [Fact]
    public void ExampleConfigParses()
    {
        var c = T.Config();
        Assert.Equal(2, c.Machines.Count);
        Assert.Equal(MachineOs.Windows, c.Machines[1].Os);
        Assert.Equal(2, c.Machines[0].MaxJobs);
    }

    [Fact]
    public void RepoExampleFileParses()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "machines.example.yml")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        Assert.NotEmpty(ConfigLoader.Load(Path.Combine(dir, "machines.example.yml")).Machines);
    }

    [Fact]
    public void PasswordFieldIsRefusedAsUnknownKey()
    {
        var yaml = T.Yaml.Replace("os: linux", "os: linux\n    password: hunter2", StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
    }

    [Theory]
    [InlineData("ssh: user@build-01.lan", "ssh: -oProxyCommand=evil")]
    [InlineData("work_root: /srv/lanq", "work_root: /srv/../etc")]
    [InlineData("work_root: /srv/lanq", "work_root: relative/path")]
    [InlineData("max_jobs: 2", "max_jobs: 0")]
    [InlineData("allowed_hours: \"22:00-07:00\"", "allowed_hours: \"nonsense\"")]
    [InlineData("ssh: user@build-01.lan", "ssh: \"user@build-01.lan\\n\"")] // "$" would match before a trailing newline; "\z" does not
    [InlineData("name: build-01", "name: \"build-01\\n\"")]
    [InlineData("work_root: /srv/lanq", "work_root: \"/srv/lanq\\n\"")]
    [InlineData("work_root: D:/lanq", "work_root: \"D:/lanq\\n\"")]
    public void UnsafeValuesAreRejected(string from, string to) =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace(from, to, StringComparison.Ordinal)));

    [Fact]
    public void EmptyAllowlistIsRejected() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            T.Yaml.Replace("[\"dotnet build\", \"dotnet test\", \"npm test\"]", "[]", StringComparison.Ordinal)));

    [Theory]
    [InlineData("work_root: D:/lanq", "work_root: /srv/lanq")]
    [InlineData("work_root: D:/lanq", "work_root: lanq")]
    [InlineData("work_root: /srv/lanq", "work_root: D:/lanq")]
    public void WorkRootMustMatchTheMachineOs(string good, string bad) =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace(good, bad, StringComparison.Ordinal)));

    [Fact]
    public void NegativeMinFreeDiskIsRefused() =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8\n  min_free_disk_gb: -1", StringComparison.Ordinal)));

    [Fact]
    public void MinFreeDiskIsReadFromConfig() =>
        Assert.Equal(12, ConfigLoader.Parse(T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8\n  min_free_disk_gb: 12", StringComparison.Ordinal)).Policy.MinFreeDiskGb);

    [Fact]
    public void KeyEnvIsOptional() => Assert.Null(T.Windows().KeyEnv);

    [Theory]
    [InlineData("C:/keys/id_ed25519")]
    [InlineData("~/.ssh/id")]
    [InlineData("")]
    [InlineData("has space")]
    public void KeyEnvMustBeAnEnvironmentVariableNameNotAPath(string value)
    {
        var yaml = T.Yaml.Replace("key_env: LANQ_KEY_BUILD01", $"key_env: \"{value}\"", StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
    }

    [Fact]
    public void TransferDefaultsToGitAndAcceptsArchive()
    {
        Assert.All(T.Config().Machines, m => Assert.Equal(TransferMode.Git, m.Transfer));
        var yaml = T.Yaml.Replace("os: linux", "os: linux\n    transfer: archive", StringComparison.Ordinal);
        Assert.Equal(TransferMode.Archive, ConfigLoader.Parse(yaml).Machines[0].Transfer);
        Assert.Equal(TransferMode.Git, ConfigLoader.Parse(yaml.Replace("transfer: archive", "transfer: git", StringComparison.Ordinal)).Machines[0].Transfer);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("rsync")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("[git]")]
    public void UnknownTransferIsRefused(string value)
    {
        var yaml = T.Yaml.Replace("os: linux", "os: linux\n    transfer: " + value, StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml));
    }

    [Fact]
    public void TransferWordsAreCaseInsensitiveAndTheErrorIsFriendly()
    {
        var yaml = T.Yaml.Replace("os: linux", "os: linux\n    transfer: Archive", StringComparison.Ordinal);
        Assert.Equal(TransferMode.Archive, ConfigLoader.Parse(yaml).Machines[0].Transfer);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(yaml.Replace("Archive", "1", StringComparison.Ordinal)));
        Assert.Contains("'git' or 'archive'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleShowsBothTransferModes()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "machines.example.yml")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var c = ConfigLoader.Load(Path.Combine(dir!, "machines.example.yml"));
        Assert.Contains(c.Machines, m => m.Transfer == TransferMode.Git);
        Assert.Contains(c.Machines, m => m.Transfer == TransferMode.Archive);
    }

    [Theory]
    [InlineData("22:00-22:00")]
    [InlineData("07:30 - 07:30")]
    public void AllowedHoursWithTheSameStartAndEndIsRejected(string window)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(T.Yaml.Replace("22:00-07:00", window, StringComparison.Ordinal)));
        Assert.Contains("start and end", ex.Message, StringComparison.Ordinal);
    }

    private const char NL = (char)10;

    private static string WithPolicy(string line) => T.Yaml.Replace("load_threshold: 0.8", "load_threshold: 0.8" + NL + "  " + line, StringComparison.Ordinal);

    private static string WithLinuxMachine(string line) => T.Yaml.Replace("max_jobs: 2", "max_jobs: 2" + NL + "    " + line, StringComparison.Ordinal);

    [Fact]
    public void MemoryPolicyDefaultsAndOverrides()
    {
        Assert.Equal(1.0, T.Config().Policy.MinFreeMemGb);
        Assert.Equal(20, T.Config().Policy.MaxMemPressure);
        Assert.Null(T.Linux().MinFreeMemGb);
        Assert.Equal(2.5, ConfigLoader.Parse(WithPolicy("min_free_mem_gb: 2.5")).Policy.MinFreeMemGb);
        Assert.Equal(40, ConfigLoader.Parse(WithPolicy("max_mem_pressure: 40")).Policy.MaxMemPressure);
        Assert.Equal(0.5, ConfigLoader.Parse(WithLinuxMachine("min_free_mem_gb: 0.5")).Machines[0].MinFreeMemGb);
    }

    [Theory]
    [InlineData("min_free_mem_gb: -1", "min_free_mem_gb")]
    [InlineData("min_free_mem_gb: .nan", "min_free_mem_gb")]
    [InlineData("max_mem_pressure: -5", "max_mem_pressure")]
    [InlineData("max_mem_pressure: 101", "max_mem_pressure")]
    public void BadMemoryPolicyIsRefused(string line, string named)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithPolicy(line)));
        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeMachineMinFreeMemIsRefused()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithLinuxMachine("min_free_mem_gb: -0.5")));
        Assert.Contains("min_free_mem_gb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleShowsAFamilyPcUsingEveryWorkerSetting()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "machines.example.yml")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var path = Path.Combine(dir!, "machines.example.yml");
        var family = Assert.Single(ConfigLoader.Load(path).Machines, m => m.Name == "family-pc");
        Assert.True(family.AllowedHours!.Count > 1);
        Assert.Equal(JobPriority.Low, family.Priority);
        Assert.NotNull(family.MinFreeMemGb);
        Assert.Contains("NODE_OPTIONS", family.Env.Keys);
        Assert.NotEmpty(family.PathPrepend);
        Assert.Contains("PAUSE", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
