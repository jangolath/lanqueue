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
    public void WithoutAStreamCallbackTheStartOfTheOutputIsKept()
    {
        var c = new OutputCollector(keepTailOnly: false);
        for (var i = 0; i < 2_000; i++)
        {
            c.Add($"line {i:D6} " + new string('x', 100));
        }

        Assert.Contains("line 000000", c.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverlongLineIsTruncatedWithAMarker()
    {
        var lines = new List<string>();
        await LineReader.PumpAsync(new StringReader(new string('y', 1_000_000) + (char)10 + "next"), lines.Add);
        Assert.Equal(2, lines.Count);
        Assert.Equal(OutputCollector.MaxLineChars + LineReader.Truncated.Length, lines[0].Length);
        Assert.EndsWith("[lanq: line truncated]", lines[0], StringComparison.Ordinal);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public async Task TruncationNeverSplitsASurrogatePair()
    {
        var lines = new List<string>();
        var emoji = char.ConvertFromUtf32(0x1F600);
        await LineReader.PumpAsync(new StringReader(new string('a', OutputCollector.MaxLineChars - 1) + emoji + "tail"), lines.Add);
        var kept = lines[0][..^LineReader.Truncated.Length];
        Assert.False(char.IsHighSurrogate(kept[^1]));
        Assert.Equal(OutputCollector.MaxLineChars - 1, kept.Length);
    }

    [Fact]
    public async Task CrLfLineEndingsAreStripped()
    {
        var lines = new List<string>();
        await LineReader.PumpAsync(new StringReader("a" + (char)13 + (char)10 + "b"), lines.Add);
        Assert.Equal(["a", "b"], lines);
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

    private static (string Exe, List<string> Args) Shell(string powershell, string sh) => OperatingSystem.IsWindows()
        ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", powershell])
        : ("sh", ["-c", sh]);

    [Fact]
    public async Task NoLineIsDeliveredAfterATimedOutCommandReturns()
    {
        var (exe, args) = Shell("while ($true) { [Console]::Out.WriteLine('x' * 200) }", "while :; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done");
        var returned = 0;
        var late = 0;
        var r = await ProcessSshClient.Execute(exe, args, _ =>
        {
            if (Volatile.Read(ref returned) == 1)
            {
                Interlocked.Increment(ref late);
            }
        }, TimeSpan.FromSeconds(2), CancellationToken.None);
        Volatile.Write(ref returned, 1);
        await Task.Delay(500);

        Assert.True(r.TimedOut);
        Assert.Equal(124, r.ExitCode);
        Assert.Equal(0, Volatile.Read(ref late));
    }

    [Fact]
    public async Task ANewlineFreeFloodIsReadInBoundedPieces()
    {
        // 50 MB of NUL bytes and no newline at all.
        var (exe, args) = Shell(
            "$s = [string]::new([char]0, 1048576); for ($i = 0; $i -lt 50; $i++) { [Console]::Out.Write($s) }",
            "dd if=/dev/zero bs=1048576 count=50 2>/dev/null");
        var longest = 0;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var r = await ProcessSshClient.Execute(exe, args, l => longest = Math.Max(longest, l.Length), TimeSpan.FromMinutes(2), CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(0, r.ExitCode);
        Assert.True(longest <= OutputCollector.MaxLineChars + 100, $"a line of {longest} chars was delivered");
        Assert.True(r.Output.Length <= OutputCollector.TailChars + 200, $"kept {r.Output.Length} chars");
        Assert.True(allocated < 64L * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB reading a 50 MB stream");
    }

    [Fact]
    public async Task WithoutAStreamCallbackOnlyABoundedHeadIsKept()
    {
        var (exe, args) = Shell(
            "'SLOT=1'; $s = 'junk ' * 100; for ($i = 0; $i -lt 20000; $i++) { [Console]::Out.WriteLine($s) }",
            "echo SLOT=1; dd if=/dev/zero bs=1048576 count=5 2>/dev/null");
        var r = await ProcessSshClient.Execute(exe, args, null, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("SLOT=1", r.Output, StringComparison.Ordinal);
        Assert.True(r.Output.Length <= OutputCollector.HeadChars + 200, $"kept {r.Output.Length} chars");
        Assert.Equal("1", LanQueue.Jobs.JobRunner.ParseSlot(r.Output));
    }

    [Fact]
    public void ProbeKeysStillParseFromTheHeadOfOversizedOutput()
    {
        var c = new OutputCollector(keepTailOnly: false);
        foreach (var l in new[] { "load=0.25", "disk_free_gb=50", "slots_used=0", "tool=ok" })
        {
            c.Add(l);
        }

        for (var i = 0; i < 100_000; i++)
        {
            c.Add("junk " + new string('j', 100));
        }

        var text = c.ToString();
        Assert.True(text.Length <= OutputCollector.HeadChars + 200, $"kept {text.Length} chars");
        var values = LanQueue.Health.HealthProbe.Parse(text);
        Assert.Equal("0.25", values["load"]);
        Assert.Equal("ok", values["tool"]);
    }
}
