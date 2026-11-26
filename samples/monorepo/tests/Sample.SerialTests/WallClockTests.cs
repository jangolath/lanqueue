using System.Diagnostics;
using Sample.Core;
using Xunit;

namespace Sample.SerialTests;

/// <summary>
/// Sensitive to wall-clock time, so it runs alone (the plan's serial-tail job is exclusive: no other lanq job on that machine). On a
/// loaded machine a timer that should fire after 200 ms can fire much later; the tolerance is generous but not unlimited.
/// </summary>
[Trait("Category", "Serial")]
public sealed class WallClockTests
{
    [Fact]
    public async Task ADelayIsCloseToItsLength()
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 2000);
    }

    [Fact]
    public async Task ManyRoundTripsFinishInTime()
    {
        await using var server = new EchoServer();
        server.Start();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal("PING", await EchoServer.AskAsync(server.Port, "ping"));
        }

        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 20);
    }
}
