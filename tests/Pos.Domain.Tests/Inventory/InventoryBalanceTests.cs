using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Domain.Tests.Inventory;

/// <summary>
/// The balance projection. It must agree with the ledger at all times, and it
/// must be impossible to set a quantity directly.
/// </summary>
public sealed class InventoryBalanceTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Store = LocationId.New();
    private static readonly LocationId External = LocationId.New();
    private static readonly ProductId Product = ProductId.New();

    [Fact]
    public void InventoryBalance_ExposesNoWayToSetAQuantity()
    {
        // The prime directive, asserted at the type level: there is no public
        // setter, and no public method that takes a target quantity.
        System.Reflection.PropertyInfo quantity =
            typeof(InventoryBalance).GetProperty(nameof(InventoryBalance.Quantity))!;

        quantity.SetMethod.Should().Match<System.Reflection.MethodInfo?>(m => m == null || !m.IsPublic);

        typeof(InventoryBalance)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name is "Apply" or "WouldGoNegative" || m.Name.StartsWith("Set", StringComparison.Ordinal))
            .Should().OnlyContain(m => m.Name != "SetQuantity");
    }

    [Fact]
    public void ApplyingMovements_ReproducesTheLedgerSum()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);

        decimal[] deltas = [100m, -2m, -1m, 50m, -7m];

        foreach (decimal delta in deltas)
        {
            balance.Apply(Movement(InventoryState.Available, delta, unitCost: 10m));
        }

        balance.Quantity.Should().Be(deltas.Sum());
    }

    [Fact]
    public void WeightedAverageCost_IsRecalculatedOnEachIncrease()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);

        balance.Apply(Movement(InventoryState.Available, 100m, unitCost: 10m));
        balance.AverageUnitCost.Should().Be(10m);

        balance.Apply(Movement(InventoryState.Available, 100m, unitCost: 20m));
        balance.AverageUnitCost.Should().Be(15m);
        balance.TotalValue.Should().Be(3000m);
    }

    [Fact]
    public void Decreases_ConsumeAtTheCurrentAverage_LeavingItUnchanged()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);

        balance.Apply(Movement(InventoryState.Available, 100m, unitCost: 10m));
        balance.Apply(Movement(InventoryState.Available, 100m, unitCost: 20m));
        balance.Apply(Movement(InventoryState.Available, -50m, unitCost: 15m));

        balance.AverageUnitCost.Should().Be(15m);
        balance.Quantity.Should().Be(150m);
        balance.TotalValue.Should().Be(2250m);
    }

    [Fact]
    public void EmptyingABucket_ClearsItsValue()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);

        balance.Apply(Movement(InventoryState.Available, 10m, unitCost: 7m));
        balance.Apply(Movement(InventoryState.Available, -10m, unitCost: 7m));

        balance.Quantity.Should().Be(0m);
        balance.AverageUnitCost.Should().Be(0m);
        balance.TotalValue.Should().Be(0m);
    }

    [Fact]
    public void ApplyingAMovementForAnotherBucket_Throws()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);

        Action act = () => balance.Apply(Movement(InventoryState.Quarantine, 5m, unitCost: 1m));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not belong to this inventory bucket*");
    }

    [Fact]
    public void WouldGoNegative_DetectsOverdraw()
    {
        InventoryBalance balance = NewBalance(InventoryState.Available);
        balance.Apply(Movement(InventoryState.Available, 3m, unitCost: 1m));

        balance.WouldGoNegative(-3m).Should().BeFalse();
        balance.WouldGoNegative(-4m).Should().BeTrue();
    }

    private static InventoryBalance NewBalance(InventoryState state)
        => InventoryBalance.CreateEmpty(Store, Product, BatchId.Empty, state);

    private static InventoryMovement Movement(InventoryState state, decimal delta, decimal unitCost)
    {
        // Built through the group factory so the test exercises the only path
        // that can produce a movement at all.
        Result<InventoryMovementGroup> group = InventoryMovementGroup.Create(
            new MovementGroupSpec(
                EventId.New(),
                delta > 0m ? InventoryMovementType.SupplierReceipt : InventoryMovementType.PosSale,
                delta > 0m ? ReferenceDocumentType.GoodsReceipt : ReferenceDocumentType.Sale,
                Guid.CreateVersion7(),
                delta > 0m ? "GRN-2026-000001" : "SAL-2026-D01-000001",
                [
                    new MovementLegSpec(
                        Product, null, Store, LocationKind.Store, state, delta, unitCost, false),
                    new MovementLegSpec(
                        Product, null, External, LocationKind.External, InventoryState.External, -delta, unitCost, false),
                ],
                new LedgerActor(UserId.New(), UserId.New(), null, CorrelationId.New()),
                RecordedAt,
                new DateOnly(2026, 3, 4)),
            RecordedAt);

        // Availability rules are the ledger service's job; here we only need the leg.
        return group.IsSuccess
            ? group.Value.Movements.Single(m => m.LocationId == Store)
            : throw new InvalidOperationException(
                "Test setup produced an invalid movement: " + string.Join("; ", group.Errors.Select(e => e.Code)));
    }
}
