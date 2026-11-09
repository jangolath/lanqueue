using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

public class ProcessSshClientTests
{
    [Fact]
    public void StreamedOutputKeepsOnlyABoundedTail()
    {
        var c = new OutputCollector(keepTailOnly: true);
        for (var i = 0; i < 20_000; i++)
        {
            c.Add($"line {i:D6} " + new string('x', 100));
        }

        var text = c.ToString();
        Assert.True(text.Length <= OutputCollector.TailChars + 200, $"kept {text.Length} chars");
        Assert.Contains("line 019999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 000000", text, StringComparison.Ordinal);
        Assert.StartsWith("[lanq] earlier output dropped", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAStreamCallbackTheWholeOutputIsKept()
    {
        var c = new OutputCollector(keepTailOnly: false);
        for (var i = 0; i < 2_000; i++)
        {
            c.Add($"line {i:D6} " + new string('x', 100));
        }

        Assert.Contains("line 000000", c.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongLineIsTruncatedWithAMarker()
    {
        var c = new OutputCollector(keepTailOnly: true);
        var shown = c.Add(new string('y', 1_000_000));
        Assert.True(shown.Length < OutputCollector.MaxLineChars + 100, $"line is {shown.Length} chars");
        Assert.EndsWith("[lanq: line truncated]", shown, StringComparison.Ordinal);
        Assert.True(c.ToString().Length < OutputCollector.MaxLineChars + 100);
    }

    [Fact]
    public async Task TimeoutIsReportedAsTimedOutNotAsAnSshFailure()
    {
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new List<string> { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
            : ("sleep", new List<string> { "30" });
        var r = await ProcessSshClient.Execute(exe, args, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(r.TimedOut);
        Assert.Equal(124, r.ExitCode);
        Assert.False(r.IsSshFailure);
        Assert.Contains("timed out after", r.Output, StringComparison.Ordinal);
    }
}
