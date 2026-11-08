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
}
