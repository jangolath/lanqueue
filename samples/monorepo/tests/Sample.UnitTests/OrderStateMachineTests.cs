using Sample.Core;
using Xunit;

namespace Sample.UnitTests;

public sealed class OrderStateMachineTests
{
    [Fact]
    public void StartsNew() => Assert.Equal(OrderState.New, new OrderStateMachine().State);

    [Fact]
    public void HappyPath()
    {
        var order = new OrderStateMachine();
        order.Move(OrderState.Paid);
        order.Move(OrderState.Shipped);
        order.Move(OrderState.Delivered);
        Assert.True(order.IsFinal);
        Assert.Equal([OrderState.New, OrderState.Paid, OrderState.Shipped, OrderState.Delivered], order.History);
    }

    [Fact]
    public void CannotShipUnpaid() => Assert.Throws<InvalidOperationException>(() => new OrderStateMachine().Move(OrderState.Shipped));

    [Fact]
    public void CancelBeforeShipping()
    {
        var order = new OrderStateMachine();
        order.Move(OrderState.Paid);
        Assert.True(order.CanMove(OrderState.Cancelled));
        order.Move(OrderState.Cancelled);
        Assert.True(order.IsFinal);
    }

    [Fact]
    public void CannotCancelAfterShipping()
    {
        var order = new OrderStateMachine();
        order.Move(OrderState.Paid);
        order.Move(OrderState.Shipped);
        Assert.False(order.CanMove(OrderState.Cancelled));
    }
}

/// <summary>Medium-length: a few seconds of simulated work, so a balanced split differs from a hash split.</summary>
public sealed class OrderBatchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProcessesABatch(int batch)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        var orders = Enumerable.Range(0, 50 * batch).Select(_ => new OrderStateMachine()).ToList();
        orders.ForEach(o => o.Move(OrderState.Paid));
        Assert.All(orders, o => Assert.Equal(OrderState.Paid, o.State));
    }
}

/// <summary>The one slow class (about 20 seconds). lanq split and warm workers make this visible.</summary>
[Trait("Category", "Slow")]
public sealed class SlowReportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BuildsAQuarterlyReport(int quarter)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var lines = Enumerable.Range(1, 3).Select(m => new OrderLine($"Q{quarter}-M{m}", new Money(1000 * quarter, "EUR"), m)).ToList();
        Assert.Equal(6000 * quarter, PriceCalculator.Subtotal(lines, "EUR").Cents);
    }
}
