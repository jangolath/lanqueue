namespace Sample.Core;

/// <summary>A line of an order: a product code, a unit price and a quantity.</summary>
public sealed record OrderLine(string Sku, Money UnitPrice, int Quantity);

/// <summary>Totals an order: lines, a percentage discount on the subtotal, then tax.</summary>
public static class PriceCalculator
{
    public static Money Subtotal(IReadOnlyList<OrderLine> lines, string currency)
    {
        var total = new Money(0, currency);
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                throw new ArgumentException($"Quantity of {line.Sku} must be positive.", nameof(lines));
            }

            total = total.Add(line.UnitPrice.Times(line.Quantity));
        }

        return total;
    }

    /// <summary>The subtotal less <paramref name="discountPercent"/>, plus <paramref name="taxPercent"/> of the discounted amount.</summary>
    public static Money Total(IReadOnlyList<OrderLine> lines, string currency, decimal discountPercent, decimal taxPercent)
    {
        if (discountPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(discountPercent));
        }

        var sub = Subtotal(lines, currency).Cents;
        var discounted = sub - (long)Math.Round(sub * discountPercent / 100m, MidpointRounding.ToEven);
        var tax = (long)Math.Round(discounted * taxPercent / 100m, MidpointRounding.ToEven);
        return new Money(discounted + tax, currency);
    }
}
