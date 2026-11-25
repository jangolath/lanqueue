using Xunit;

namespace Sample.HangTests;

/// <summary>
/// Opt-in (not in Sample.slnx): one test that never returns, the fixture for hang handling. lanq.extras.yml's hang job runs it with a
/// 3-minute timeout, so the job ends TimedOut. Never add this project to the solution.
/// </summary>
[Trait("Category", "Hang")]
public sealed class HangTests
{
    [Fact]
    public async Task NeverReturns()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        Assert.Fail("unreachable");
    }
}
