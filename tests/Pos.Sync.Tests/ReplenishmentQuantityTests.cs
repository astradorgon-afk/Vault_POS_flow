using FluentAssertions;
using Pos.SharedUI;

namespace Pos.Sync.Tests;

/// <summary>
/// The suggested restock quantity is shared by the online restock page, the
/// offline app, and the inventory page's quick "Request stock" button, so the
/// three never disagree about how much to ask for.
/// </summary>
public class ReplenishmentQuantityTests
{
    [Fact]
    public void ReachesTheTargetAndSubtractsWhatIsAlreadyOnTheWay()
    {
        ReplenishmentQuantity.Suggest(available: 2m, inTransit: 3m, targetStock: 20m, reorderPoint: 5m)
            .Should().Be(15m);
    }

    [Fact]
    public void FallsBackToTheReorderPointWhenThereIsNoTarget()
    {
        ReplenishmentQuantity.Suggest(available: 2m, inTransit: 0m, targetStock: null, reorderPoint: 12m)
            .Should().Be(10m);
    }

    [Fact]
    public void FallsBackToASingleUnitWhenNothingIsConfigured()
    {
        ReplenishmentQuantity.Suggest(available: 0m, inTransit: 0m, targetStock: null, reorderPoint: null)
            .Should().Be(1m);
    }

    [Fact]
    public void AZeroTargetIsTreatedAsUnset()
    {
        ReplenishmentQuantity.Suggest(available: 0m, inTransit: 0m, targetStock: 0m, reorderPoint: 4m)
            .Should().Be(4m);
    }

    [Fact]
    public void AZeroReorderPointIsTreatedAsUnset()
    {
        ReplenishmentQuantity.Suggest(available: 0m, inTransit: 0m, targetStock: 0m, reorderPoint: 0m)
            .Should().Be(1m);
    }

    [Fact]
    public void AnItemAlreadyAboveTargetNeverAsksForANegativeOrZeroQuantity()
    {
        ReplenishmentQuantity.Suggest(available: 50m, inTransit: 0m, targetStock: 20m, reorderPoint: 5m)
            .Should().Be(ReplenishmentQuantity.Minimum);
    }

    [Fact]
    public void AnItemWithEverythingInTransitAndNothingOnHandStillRaisesARequest()
    {
        // The whole order is on the way, so the suggested quantity is the
        // smallest one worth recording rather than a negative number.
        ReplenishmentQuantity.Suggest(available: 0m, inTransit: 20m, targetStock: 20m, reorderPoint: 5m)
            .Should().Be(ReplenishmentQuantity.Minimum);
    }
}
