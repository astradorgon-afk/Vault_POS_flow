namespace Pos.SharedUI;

/// <summary>
/// How much stock to ask for when a product has fallen to its reorder point.
/// The goal is the target level, falling back to the reorder point and then to
/// a single unit; anything already on the way is subtracted so an item that is
/// in transit is not ordered a second time.
/// </summary>
public static class ReplenishmentQuantity
{
    /// <summary>The smallest quantity worth putting on a request.</summary>
    public const decimal Minimum = 0.001m;

    public static decimal Suggest(decimal available, decimal inTransit, decimal? targetStock, decimal? reorderPoint)
    {
        decimal stockGoal = targetStock is > 0m and { } target
            ? target
            : reorderPoint is > 0m and { } point ? point : 1m;

        return Math.Max(Minimum, stockGoal - available - inTransit);
    }
}
