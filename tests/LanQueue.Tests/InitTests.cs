using System.Text.Json;
using System.Text.RegularExpressions;
using LanQueue.Config;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq init: a starter machines.yml that loads as written, never written over an existing file.</summary>
public sealed class InitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-init-" + Guid.NewGuid().ToString("N"));

    /// <summary>The current folder lanq init runs in (a repository, say).</summary>
    private readonly string _cwd;

    /// <summary>The per-user config file (XDG_CONFIG_HOME points into the test folder, never at the real one).</summary>
    private readonly string _userFile;

    public InitTests()
    {
        _cwd = Path.Combine(_dir, "repo");
        _userFile = Path.Combine(_dir, "config", "lanq", "machines.yml");
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string? Env(string name) => name == "XDG_CONFIG_HOME" ? Path.Combine(_dir, "config") : null;

    private Task<(int Code, string Out, string Err)> Init(params string[] args) => InitWith(Env, args);

    private async Task<(int Code, string Out, string Err)> InitWith(Func<string, string?> env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Init(ParsedArgs.Parse(["init", .. args]), _cwd, env, windows: false, o, e);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>The user folder may not exist yet; a test that puts something at the default path first creates it.</summary>
    private void UserFolder() => Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);

    [Fact]
    public async Task InitWithoutConfigWritesToTheUserFolder()
    {
        var (code, o, _) = await Init();
        Assert.Equal(0, code);
        Assert.True(File.Exists(_userFile)); // its folder created
        Assert.Contains(_userFile, o, StringComparison.Ordinal);
        Assert.Contains("docs/WORKERS.md", o, StringComparison.Ordinal);
        Assert.Contains("lanq makeready --ssh user@host --os windows|linux", o, StringComparison.Ordinal);
        Assert.Contains("lanq doctor --remote", o, StringComparison.Ordinal);
        // lookup finds it there without flags, so the steps need no --config.
        Assert.DoesNotContain("--config", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitNeverWritesIntoTheCurrentDirectory()
    {
        Assert.Equal(0, (await Init()).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cwd));
    }

    [Fact]
    public async Task InitThenLookupFindsItWithoutFlags()
    {
        Assert.Equal(0, (await Init()).Code);
        Assert.Equal(_userFile, Cli.ResolveConfig(null, Env, windows: false, File.Exists));
        Assert.Single(ConfigLoader.Load(Cli.ResolveConfig(null, Env, windows: false, File.Exists)).Machines);
    }

    /// <summary>Windows: %APPDATA%/lanq/machines.yml, the same file lookup reads.</summary>
    [Fact]
    public async Task OnWindowsInitWritesUnderAppData()
    {
        var appData = Path.Combine(_dir, "AppData", "Roaming");
        var o = new StringWriter();
        var code = await Cli.Init(ParsedArgs.Parse(["init"]), _cwd, n => n == "APPDATA" ? appData : null, windows: true, o, new StringWriter());
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(appData, "lanq", "machines.yml")), o.ToString());
    }

    /// <summary>LANQ_CONFIG names the file lookup reads, so init writes that one.</summary>
    [Fact]
    public async Task LanqConfigNamesTheFileInitWrites()
    {
        var named = Path.Combine(_dir, "team", "fleet.yml");
        var (code, _, _) = await InitWith(n => n == "LANQ_CONFIG" ? named : Env(n));
        Assert.Equal(0, code);
        Assert.True(File.Exists(named));
        Assert.False(File.Exists(_userFile));
    }

    /// <summary>The steps show the one-time setup first, then adding a ready worker with --add --name (lanq writes it; no pasting).</summary>
    [Fact]
    public async Task InitStepsShowAddAndSetup()
    {
        var (code, o, e) = await Init();
        Assert.Equal(0, code);
        var path = _userFile;
        var setup = o.IndexOf("lanq makeready --ssh user@host --os windows|linux --setup", StringComparison.Ordinal);
        var add = o.IndexOf("lanq makeready --ssh user@host --os windows|linux --add --name NAME", StringComparison.Ordinal);
        Assert.True(setup >= 0, o);
        Assert.True(add > setup, o);
        Assert.Contains($"Delete example-01 from {path}.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Paste the entries", o, StringComparison.Ordinal);
        Assert.True(o.IndexOf("lanq doctor --remote", StringComparison.Ordinal) > add);

        // The JSON next step says the same, and the starter's own comment points at --add.
        Assert.Contains("--setup", e, StringComparison.Ordinal);
        Assert.Contains("--add --name NAME", Cli.StarterConfig, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGeneratedFileLoadsAsWritten()
    {
        Assert.Equal(0, (await Init()).Code);
        var config = ConfigLoader.Load(_userFile);
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
        UserFolder();
        var path = _userFile;
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
        Directory.CreateDirectory(_userFile);
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
        UserFolder();
        var path = _userFile;
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

        try
        {
            var (code, _, e) = await Init();
            Assert.Equal(2, code);
            Assert.Contains("is a link", e, StringComparison.Ordinal);
            Assert.False(File.Exists(nowhere));
            Assert.False(Directory.Exists(Path.GetDirectoryName(nowhere)));
        }
        finally
        {
            // The link itself, not recursively: a recursive delete of a junction to a missing folder is refused for a non-admin account.
            try
            {
                Directory.Delete(path);
            }
            catch (IOException)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AFileThatAppearsBeforeTheWriteIsNotOverwritten()
    {
        var path = _userFile;
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Init(ParsedArgs.Parse(["init", "--json"]), _cwd, Env, windows: false, o, e, beforeWrite: () => File.WriteAllText(path, "theirs"));
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
        Assert.Equal(_userFile, doc.RootElement.GetProperty("path").GetString());
        Assert.Contains("lanq doctor --remote", e, StringComparison.Ordinal); // the next steps still go to people, on stderr

        (code, o, _) = await Init("--json");
        Assert.Equal(2, code);
        using var again = JsonDocument.Parse(o);
        Assert.Equal("Rejected", again.RootElement.GetProperty("status").GetString());
        Assert.Equal(_userFile, again.RootElement.GetProperty("path").GetString());
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
