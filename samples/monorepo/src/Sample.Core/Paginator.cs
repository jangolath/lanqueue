namespace Sample.Core;

/// <summary>One page of a list, with its 1-based number and the page count.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Number, int PageCount, int TotalItems);

public static class Paginator
{
    /// <summary>Page <paramref name="number"/> (clamped to the existing pages) of <paramref name="size"/> items.</summary>
    public static Page<T> Get<T>(IReadOnlyList<T> all, int number, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var count = Math.Max(1, (all.Count + size - 1) / size);
        var n = Math.Clamp(number, 1, count);
        return new Page<T>([.. all.Skip((n - 1) * size).Take(size)], n, count, all.Count);
    }
}
