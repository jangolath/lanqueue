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

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task VersionPrintsLanqAndTheEmbeddedMakereadyVersionOnStdout(string arg)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunAsync([arg], o, e, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("", e.ToString());
        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^lanq \d+\.\d+\.\d+", lines[0]);
        Assert.Equal(
            $"lanq-makeready {Cli.MakereadyVersion} (embedded: lanq-makeready.ps1 {Cli.MakereadyScript(Config.MachineOs.Windows).Length} bytes, "
            + $"lanq-makeready.sh {Cli.MakereadyScript(Config.MachineOs.Linux).Length} bytes)",
            lines[1]);
    }

    [Fact]
    public void VersionTakesNoOptions() =>
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["--version", "--json"]));

    [Fact]
    public async Task AnUnknownCommandPrintsUsageOnStderrAndExits2()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.RunAsync(["frobnicate"], o, e, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Equal("", o.ToString());
        Assert.Contains("lanq health", e.ToString(), StringComparison.Ordinal);

        o = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["--help"], o, new StringWriter(), CancellationToken.None));
        Assert.Contains("lanq health", o.ToString(), StringComparison.Ordinal); // asked-for help stays on stdout
    }

    [Theory]
    [InlineData("health")]
    [InlineData("stats")]
    [InlineData("unlock", "build-01")]
    [InlineData("doctor")]
    [InlineData("makeready", "build-01")]
    public async Task OnlyRunTakesACommandAfterDoubleDash(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse([.. args, "--", "rm", "-rf", "/"]));
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync([.. args, "--", "x"], new StringWriter(), e, CancellationToken.None));
        Assert.Contains("--", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserInputInArgumentErrorsIsSanitised()
    {
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--bogus\u001b]0;pwned\u0007\u001b[2J"], new StringWriter(), e, CancellationToken.None));
        Assert.DoesNotContain('\u001b', e.ToString());
        Assert.DoesNotContain('\u0007', e.ToString());
    }

    [Fact]
    public void UserInputInConfigErrorsIsSanitised()
    {
        var ex = Assert.Throws<LanQueue.Config.ConfigException>(() => LanQueue.Config.ConfigLoader.Parse(T.Yaml.Replace("name: build-01", "name: \"bad\\e[2J\\u202Ename\"", StringComparison.Ordinal)));
        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.DoesNotContain('‮', ex.Message);
        Assert.Contains("bad?[2J?name", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void MemOptionIsParsedInGigabytes()
    {
        Assert.Null(ParsedArgs.Parse(["run", "--", "dotnet", "test"]).MemGb);
        Assert.Equal(2.5, ParsedArgs.Parse(["run", "--mem", "2.5", "--", "dotnet", "test"]).MemGb);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("100000")]
    public async Task BadMemExitsWith2(string value)
    {
        var err = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(["run", "--mem", value, "--", "dotnet", "test"], new StringWriter(), err, CancellationToken.None));
        Assert.Contains("--mem", err.ToString(), StringComparison.Ordinal);
    }
}
