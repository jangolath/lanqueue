using Sample.Core;
using Xunit;

namespace Sample.UnitTests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(1.005, 100)]
    [InlineData(1.015, 102)]
    [InlineData(0, 0)]
    [InlineData(19.99, 1999)]
    public void RoundsToEvenCents(decimal amount, long cents) => Assert.Equal(cents, Money.Of(amount, "EUR").Cents);

    [Fact]
    public void AddsSameCurrency() => Assert.Equal(new Money(350, "EUR"), new Money(100, "EUR").Add(new Money(250, "EUR")));

    [Fact]
    public void RefusesMixedCurrencies() => Assert.Throws<InvalidOperationException>(() => new Money(1, "EUR").Add(new Money(1, "USD")));

    [Fact]
    public void MultipliesByQuantity() => Assert.Equal(1200, new Money(400, "EUR").Times(3).Cents);

    [Fact]
    public void OverflowIsAnError() => Assert.Throws<OverflowException>(() => new Money(long.MaxValue, "EUR").Times(2));

    [Fact]
    public void FormatsInvariant() => Assert.Equal("12.50 EUR", new Money(1250, "EUR").ToString());
}

public sealed class PriceCalculatorTests
{
    private static readonly OrderLine[] Lines =
    [
        new("A-1", new Money(1000, "EUR"), 2),
        new("B-2", new Money(250, "EUR"), 4),
    ];

    [Fact]
    public void SubtotalSumsLines() => Assert.Equal(3000, PriceCalculator.Subtotal(Lines, "EUR").Cents);

    [Fact]
    public void EmptyOrderIsZero() => Assert.Equal(0, PriceCalculator.Subtotal([], "EUR").Cents);

    [Fact]
    public void DiscountThenTax() => Assert.Equal(2970, PriceCalculator.Total(Lines, "EUR", 10, 10).Cents);

    [Fact]
    public void NoDiscountNoTax() => Assert.Equal(3000, PriceCalculator.Total(Lines, "EUR", 0, 0).Cents);

    [Fact]
    public void FullDiscountIsFree() => Assert.Equal(0, PriceCalculator.Total(Lines, "EUR", 100, 20).Cents);

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void DiscountOutOfRange(decimal discount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.Total(Lines, "EUR", discount, 0));

    [Fact]
    public void ZeroQuantityRefused() =>
        Assert.Throws<ArgumentException>(() => PriceCalculator.Subtotal([new("C", new Money(1, "EUR"), 0)], "EUR"));
}
