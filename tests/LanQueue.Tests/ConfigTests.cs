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
}
