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

/// <summary>Converts the browser-measured row centres into an order destination.</summary>
public static class OrderDrag
{
    public static int TargetPlace(IReadOnlyList<double> midpoints, double pointerY)
    {
        if (midpoints.Count == 0) return -1;

        var best = 0;
        var bestDistance = Math.Abs(midpoints[0] - pointerY);
        for (var index = 1; index < midpoints.Count; index++)
        {
            var distance = Math.Abs(midpoints[index] - pointerY);
            if (distance >= bestDistance) continue;
            best = index;
            bestDistance = distance;
        }

        return best;
    }
}
