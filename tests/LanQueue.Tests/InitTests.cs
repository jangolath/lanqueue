using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq init: a starter machines.yml that loads as written, never written over an existing file.</summary>
public sealed class InitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-init-" + Guid.NewGuid().ToString("N"));

    public InitTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int Code, string Out, string Err)> Init(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Init(ParsedArgs.Parse(["init", .. args]), _dir, _ => null, windows: false, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task WritesMachinesYmlInTheCurrentFolderAndPrintsTheNextSteps()
    {
        var (code, o, _) = await Init();
        Assert.Equal(0, code);
        var path = Path.Combine(_dir, "machines.yml");
        Assert.True(File.Exists(path));
        Assert.Contains(path, o, StringComparison.Ordinal);
        Assert.Contains("docs/WORKERS.md", o, StringComparison.Ordinal);
        Assert.Contains("lanq makeready --ssh user@host --os windows|linux", o, StringComparison.Ordinal);
        Assert.Contains("lanq doctor --remote", o, StringComparison.Ordinal);
        // lanq never reads a config from the current folder by itself, so the steps say how to point at this one.
        Assert.Contains("--config " + path, o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGeneratedFileLoadsAsWritten()
    {
        Assert.Equal(0, (await Init()).Code);
        var config = ConfigLoader.Load(Path.Combine(_dir, "machines.yml"));
        var machine = Assert.Single(config.Machines);
        Assert.Equal(1, machine.MaxJobs);
        Assert.True(config.Policy.MinFreeMemGb >= 1);
        Assert.NotEmpty(config.Policy.AllowedCommands);
    }

    /// <summary>The commented-out examples are valid too: uncommented, they load, with allowed_hours, wake and a key_env.</summary>
    [Fact]
    public void TheCommentedExamplesLoadWhenUncommented()
    {
        var lines = Cli.StarterConfig.Split('\n').Select(l => Regex.Replace(l, @"^  # (?=-|  )", "  ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        var config = ConfigLoader.Parse(string.Join('\n', lines));
        Assert.Equal(3, config.Machines.Count);
        Assert.Contains(config.Machines, m => m.Os == MachineOs.Windows && m.KeyEnv is not null);
        Assert.Contains(config.Machines, m => m.Os == MachineOs.Linux && m.AllowedHours is { Count: > 0 } && m.Wake is not null);
    }

    [Fact]
    public void TheStarterUsesOnlyExampleAddressesAndLfLineEndings()
    {
        Assert.DoesNotContain("\r", Cli.StarterConfig, StringComparison.Ordinal);
        foreach (Match m in Regex.Matches(Cli.StarterConfig, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            Assert.Matches(@"^(10\.20\.30\.|192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)", m.Value);
        }
    }

    [Fact]
    public async Task RefusesToOverwriteAnExistingFile()
    {
        var path = Path.Combine(_dir, "machines.yml");
        await File.WriteAllTextAsync(path, "mine");
        var (code, o, e) = await Init();
        Assert.Equal(2, code);
        Assert.Equal("mine", await File.ReadAllTextAsync(path));
        Assert.Contains("already exists", e, StringComparison.Ordinal);
        Assert.Empty(o);
    }

    [Fact]
    public async Task RefusesAnExistingFolderWithAClearMessage()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "machines.yml"));
        var (code, o, e) = await Init("--json");
        Assert.Equal(2, code);
        Assert.Contains("is a folder", e, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>A link whose target does not exist is not "free": writing would create a file wherever the link points.</summary>
    [Fact]
    public async Task RefusesADanglingLink()
    {
        var path = Path.Combine(_dir, "machines.yml");
        var nowhere = Path.Combine(_dir, "missing", "target.yml");
        try
        {
            File.CreateSymbolicLink(path, nowhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege: a junction to a missing folder is a dangling link too.
            FetchReceiverTests.MakeDirLink(path, nowhere);
        }

        var (code, _, e) = await Init();
        Assert.Equal(2, code);
        Assert.Contains("is a link", e, StringComparison.Ordinal);
        Assert.False(File.Exists(nowhere));
        Assert.False(Directory.Exists(Path.GetDirectoryName(nowhere)));
    }

    [Fact]
    public async Task AFileThatAppearsBeforeTheWriteIsNotOverwritten()
    {
        var path = Path.Combine(_dir, "machines.yml");
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Init(ParsedArgs.Parse(["init", "--json"]), _dir, _ => null, windows: false, o, e, beforeWrite: () => File.WriteAllText(path, "theirs"));
        Assert.Equal(2, code);
        Assert.Equal("theirs", await File.ReadAllTextAsync(path));
        Assert.Contains("already exists", e.ToString(), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void TheStarterRecommendsOnlyDocumentedMemorySettings() =>
        Assert.DoesNotContain("NODECOUNT", Cli.StarterConfig, StringComparison.Ordinal); // MSBuild has no documented node-count variable

    [Fact]
    public async Task ConfigPathIsUsedAndItsFolderCreated()
    {
        var path = Path.Combine(_dir, "sub", "fleet.yml");
        var (code, _, _) = await Init("--config", path);
        Assert.Equal(0, code);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("\r", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonPrintsOneObjectWithStatusAndPath()
    {
        var (code, o, e) = await Init("--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(o);
        Assert.Equal("Created", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(Path.Combine(_dir, "machines.yml"), doc.RootElement.GetProperty("path").GetString());
        Assert.Contains("lanq doctor --remote", e, StringComparison.Ordinal); // the next steps still go to people, on stderr

        (code, o, _) = await Init("--json");
        Assert.Equal(2, code);
        using var again = JsonDocument.Parse(o);
        Assert.Equal("Rejected", again.RootElement.GetProperty("status").GetString());
        Assert.Equal(Path.Combine(_dir, "machines.yml"), again.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task JsonPrintsOneObjectForAnArgumentError()
    {
        var o = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["init", "--json", "--bogus"], o, new StringWriter(), CancellationToken.None));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void InitTakesNoOtherOptions() =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["init", "--force"]));
}
