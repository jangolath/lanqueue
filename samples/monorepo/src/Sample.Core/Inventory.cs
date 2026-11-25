namespace Sample.Core;

/// <summary>Stock per product code, with reservations that must be committed or released.</summary>
public sealed class Inventory
{
    private readonly Dictionary<string, int> _onHand = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reserved = new(StringComparer.Ordinal);

    public void Receive(string sku, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        _onHand[sku] = OnHand(sku) + quantity;
    }

    public int OnHand(string sku) => _onHand.GetValueOrDefault(sku);

    public int Reserved(string sku) => _reserved.GetValueOrDefault(sku);

    public int Available(string sku) => OnHand(sku) - Reserved(sku);

    public bool TryReserve(string sku, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (Available(sku) < quantity)
        {
            return false;
        }

        _reserved[sku] = Reserved(sku) + quantity;
        return true;
    }

    public void Release(string sku, int quantity) => _reserved[sku] = Math.Max(0, Reserved(sku) - quantity);

    public void Commit(string sku, int quantity)
    {
        if (Reserved(sku) < quantity)
        {
            throw new InvalidOperationException($"Only {Reserved(sku)} of {sku} reserved.");
        }

        _reserved[sku] = Reserved(sku) - quantity;
        _onHand[sku] = OnHand(sku) - quantity;
    }
}
