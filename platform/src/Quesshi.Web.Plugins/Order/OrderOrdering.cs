namespace Quesshi.Web.Plugins;

/// <summary>Immutable ordering of the opaque identifiers carried by an order card.</summary>
public sealed class OrderOrdering
{
    private readonly string[] itemIds;

    private OrderOrdering(string[] itemIds) => this.itemIds = itemIds;

    public IReadOnlyList<string> ItemIds => itemIds;

    public static OrderOrdering Served(IEnumerable<string> itemIds) => new(itemIds.ToArray());

    public OrderOrdering Move(int from, int to)
    {
        if (from < 0 || from >= itemIds.Length) return this;
        to = Math.Clamp(to, 0, itemIds.Length - 1);
        if (from == to) return this;

        var next = itemIds.ToList();
        var moved = next[from];
        next.RemoveAt(from);
        next.Insert(to, moved);
        return new(next.ToArray());
    }

    public OrderOrdering MoveUp(int place) => Move(place, place - 1);
    public OrderOrdering MoveDown(int place) => Move(place, place + 1);
}
