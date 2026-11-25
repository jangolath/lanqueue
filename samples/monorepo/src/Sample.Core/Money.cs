using System.Globalization;

namespace Sample.Core;

/// <summary>An amount in whole cents of one currency.</summary>
public readonly record struct Money(long Cents, string Currency)
{
    public static Money Of(decimal amount, string currency) => new((long)Math.Round(amount * 100m, MidpointRounding.ToEven), currency);

    public Money Add(Money other) => other.Currency == Currency
        ? this with { Cents = checked(Cents + other.Cents) }
        : throw new InvalidOperationException($"Cannot add {other.Currency} to {Currency}.");

    public Money Times(int quantity) => this with { Cents = checked(Cents * quantity) };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Cents / 100m:0.00} {Currency}");
}
