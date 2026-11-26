using Sample.Core;
using Xunit;

namespace Sample.UnitTests;

public sealed class InventoryReceiveTests
{
    [Fact]
    public void ReceiveAddsStock()
    {
        var inv = new Inventory();
        inv.Receive("A", 3);
        inv.Receive("A", 2);
        Assert.Equal(5, inv.OnHand("A"));
    }

    [Fact]
    public void UnknownSkuIsEmpty() => Assert.Equal(0, new Inventory().Available("missing"));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void ReceiveRefusesNonPositive(int quantity) => Assert.Throws<ArgumentOutOfRangeException>(() => new Inventory().Receive("A", quantity));
}

public sealed class InventoryReservationTests
{
    private static Inventory Stocked()
    {
        var inv = new Inventory();
        inv.Receive("A", 10);
        return inv;
    }

    [Fact]
    public void ReserveLowersAvailable()
    {
        var inv = Stocked();
        Assert.True(inv.TryReserve("A", 4));
        Assert.Equal((10, 4, 6), (inv.OnHand("A"), inv.Reserved("A"), inv.Available("A")));
    }

    [Fact]
    public void CannotReserveMoreThanAvailable()
    {
        var inv = Stocked();
        Assert.True(inv.TryReserve("A", 8));
        Assert.False(inv.TryReserve("A", 3));
    }

    [Fact]
    public void ReleaseGivesBack()
    {
        var inv = Stocked();
        inv.TryReserve("A", 5);
        inv.Release("A", 5);
        Assert.Equal(10, inv.Available("A"));
    }

    [Fact]
    public void ReleaseNeverGoesNegative()
    {
        var inv = Stocked();
        inv.Release("A", 5);
        Assert.Equal(0, inv.Reserved("A"));
    }

    [Fact]
    public void CommitShipsReservedStock()
    {
        var inv = Stocked();
        inv.TryReserve("A", 4);
        inv.Commit("A", 4);
        Assert.Equal((6, 0), (inv.OnHand("A"), inv.Reserved("A")));
    }

    [Fact]
    public void CommitWithoutReservationFails() => Assert.Throws<InvalidOperationException>(() => Stocked().Commit("A", 1));
}
