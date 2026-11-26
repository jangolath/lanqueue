namespace Sample.Core;

public enum OrderState
{
    New,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
}

/// <summary>A tiny order life cycle: only the listed transitions are allowed.</summary>
public sealed class OrderStateMachine
{
    private static readonly Dictionary<OrderState, OrderState[]> Allowed = new()
    {
        [OrderState.New] = [OrderState.Paid, OrderState.Cancelled],
        [OrderState.Paid] = [OrderState.Shipped, OrderState.Cancelled],
        [OrderState.Shipped] = [OrderState.Delivered],
        [OrderState.Delivered] = [],
        [OrderState.Cancelled] = [],
    };

    private readonly List<OrderState> _history = [OrderState.New];

    public OrderState State => _history[^1];

    public IReadOnlyList<OrderState> History => _history;

    public bool IsFinal => Allowed[State].Length == 0;

    public bool CanMove(OrderState next) => Allowed[State].Contains(next);

    public void Move(OrderState next)
    {
        if (!CanMove(next))
        {
            throw new InvalidOperationException($"{State} -> {next} is not allowed.");
        }

        _history.Add(next);
    }
}
