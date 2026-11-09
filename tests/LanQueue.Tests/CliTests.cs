using Xunit;

namespace LanQueue.Tests;

public class CliTests
{
    [Fact]
    public void EverythingAfterDoubleDashIsTheJobCommand()
    {
        var p = ParsedArgs.Parse(["run", "--needs", "dotnet, linux", "--dir", "src", "--", "dotnet", "test", "--needs", "x"]);
        Assert.Equal("run", p.Command);
        Assert.Equal(["dotnet", "linux"], p.Needs);
        Assert.Equal("src", p.Directory);
        Assert.Equal(["dotnet", "test", "--needs", "x"], p.Argv);
    }

    [Fact]
    public void UnknownOptionIsAnError() =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--password", "x"]));

    [Fact]
    public async Task NoArgsPrintsUsage()
    {
        var o = new StringWriter();
        var code = await Cli.RunAsync([], o, new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("lanq health", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConfigFailsCleanlyWithExit2()
    {
        var err = new StringWriter();
        var code = await Cli.RunAsync(["health", "--config", "does-not-exist.yml"], new StringWriter(), err, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("not found", err.ToString(), StringComparison.Ordinal);
    }

    private static Func<string, string?> Env(params (string K, string V)[] kv) => k => kv.FirstOrDefault(p => p.K == k).V;

    [Fact]
    public void LanqHomeDefaultsToPerUserFolderNeverTheRepoOnWindows()
    {
        var home = Cli.ResolveHome(Env(("LOCALAPPDATA", "C:/Users/me/AppData/Local")), windows: true);
        Assert.Equal(Path.Combine("C:/Users/me/AppData/Local", "lanq"), home);
        Assert.DoesNotContain(".lanq", home, StringComparison.Ordinal);
    }

    [Fact]
    public void LanqHomeDefaultsToXdgStateOnLinux()
    {
        Assert.Equal(Path.Combine("/home/me/.state", "lanq"), Cli.ResolveHome(Env(("XDG_STATE_HOME", "/home/me/.state"), ("HOME", "/home/me")), windows: false));
        Assert.Equal(Path.Combine("/home/me", ".local", "state", "lanq"), Cli.ResolveHome(Env(("HOME", "/home/me")), windows: false));
    }

    [Fact]
    public void ExplicitLanqHomeWins() =>
        Assert.Equal("/custom", Cli.ResolveHome(Env(("LANQ_HOME", "/custom"), ("HOME", "/home/me")), windows: false));

    [Fact]
    public void ConfigIsNeverTakenImplicitlyFromTheCurrentDirectory()
    {
        var ex = Assert.Throws<LanQueue.Config.ConfigException>(() =>
            Cli.ResolveConfig(null, Env(("HOME", "/home/me")), windows: false, exists: p => p == "machines.local.yml" || p == "./machines.local.yml"));
        Assert.Contains("--config", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigLookupOrderIsFlagThenEnvThenUserConfigDir()
    {
        Assert.Equal("a.yml", Cli.ResolveConfig("a.yml", Env(("LANQ_CONFIG", "b.yml")), windows: false, exists: _ => true));
        Assert.Equal("b.yml", Cli.ResolveConfig(null, Env(("LANQ_CONFIG", "b.yml")), windows: false, exists: _ => true));
        var user = Path.Combine("/home/me", ".config", "lanq", "machines.yml");
        Assert.Equal(user, Cli.ResolveConfig(null, Env(("HOME", "/home/me")), windows: false, exists: p => p == user));
    }

    [Fact]
    public void MissingExplicitConfigIsAnError() =>
        Assert.Throws<LanQueue.Config.ConfigException>(() => Cli.ResolveConfig("nope.yml", Env(), windows: false, exists: _ => false));

    [Fact]
    public void TimeoutDefaultsTo240MinutesAndCanBeSet()
    {
        Assert.Equal(240, ParsedArgs.Parse(["run", "--", "dotnet", "test"]).TimeoutMinutes);
        Assert.Equal(30, ParsedArgs.Parse(["run", "--timeout", "30", "--", "dotnet", "test"]).TimeoutMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000")]
    public void OutOfRangeTimeoutIsAnError(string minutes) =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--timeout", minutes, "--", "dotnet", "test"]));

    [Fact]
    public async Task BadTimeoutExitsWith2()
    {
        var err = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--timeout", "abc", "--", "dotnet", "test"], new StringWriter(), err, CancellationToken.None));
    }

    [Fact]
    public async Task HealthProbesRunConcurrentlyAndPrintInConfigOrder()
    {
        var ssh = new GatedProbeSsh();
        var o = new StringWriter();
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        var code = await Cli.Health(config, new LanQueue.Health.HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(["build-02", "build-01"], ssh.Finished); // build-02 answered first
        var lines = o.ToString().Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("build-01", lines[0], StringComparison.Ordinal); // but output keeps config order
        Assert.StartsWith("build-02", lines[1], StringComparison.Ordinal);
    }
}
