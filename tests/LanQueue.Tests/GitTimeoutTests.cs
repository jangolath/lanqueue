using System.Diagnostics;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.1: every local git call is bounded, reads no stdin, never prompts or pages, and a timeout kills it and fails as
/// <see cref="GitTimeoutException"/> (an InvalidOperationException, so the existing InfraError / Rejected paths report it). The runner is
/// driven with a stand-in for git (a shell that sleeps, reads stdin or prints its environment), so the tests stay fast.
/// </summary>
public class GitTimeoutTests
{
    private static readonly string Dir = Path.GetTempPath();

    private static (string Exe, string[] Args) Shell(string powershell, string sh) => OperatingSystem.IsWindows()
        ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", powershell])
        : ("sh", ["-c", sh]);

    private static Task<(int Code, string Output, string Error)> Run((string Exe, string[] Args) cmd, TimeSpan timeout, CancellationToken ct = default) =>
        GitPayloadBuilder.RunAsync(cmd.Exe, Dir, null, timeout, ct, cmd.Args);

    [Fact]
    public async Task AHungGitIsKilledAtItsLimitAndFailsAsATimeout()
    {
        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GitTimeoutException>(() => Run(Shell("Start-Sleep -Seconds 60", "sleep 60"), TimeSpan.FromSeconds(1)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.IsAssignableFrom<InvalidOperationException>(ex); // the snapshot, bundle and --baseline paths catch InvalidOperationException
        Assert.Contains("did not finish within 1 s and was stopped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlCStillCancelsAndIsNotReportedAsATimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Shell("Start-Sleep -Seconds 60", "sleep 60"), TimeSpan.FromMinutes(5), cts.Token));
        Assert.IsNotType<GitTimeoutException>(ex);
    }

    /// <summary>The hang seen under PowerShell Start-Job: git inherited a stdin nobody closes. Now stdin is closed at once, so a read ends.</summary>
    [Fact]
    public async Task StdinIsClosedSoAReadNeverWaits()
    {
        var (code, output, _) = await Run(Shell("[Console]::In.ReadToEnd() | Out-Null; 'read'", "cat >/dev/null; echo read"), TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        Assert.Equal("read", output.Trim());
    }

    [Fact]
    public async Task GitCanNeitherPromptNorPage()
    {
        var (_, output, _) = await Run(
            Shell("\"$env:GIT_TERMINAL_PROMPT|$env:GCM_INTERACTIVE|$env:GIT_PAGER\"", "echo \"$GIT_TERMINAL_PROMPT|$GCM_INTERACTIVE|$GIT_PAGER\""),
            TimeSpan.FromSeconds(30));
        Assert.Equal("0|never|cat", output.Trim());
    }

    /// <summary>Both pipes are read at once: a git that fills stderr while lanq reads stdout cannot deadlock.</summary>
    [Fact]
    public async Task LargeStdoutAndStderrAreReadTogether()
    {
        var (code, output, error) = await Run(
            Shell("[Console]::Error.Write('b' * 300000); [Console]::Out.Write('a' * 300000)", "head -c 300000 /dev/zero | tr '\\0' b >&2; head -c 300000 /dev/zero | tr '\\0' a"),
            TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        Assert.Equal(300000, output.Length);
        Assert.Equal(300000, error.Length);
    }

    [Theory]
    [InlineData("rev-parse", 60)]
    [InlineData("merge-base", 60)]
    [InlineData("cat-file", 60)]
    [InlineData("add", 600)]
    [InlineData("diff", 600)]
    [InlineData("bundle", 1800)]
    public void EachGitCallHasALimitByWhatItDoes(string subcommand, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GitPayloadBuilder.TimeoutFor(subcommand));
}
