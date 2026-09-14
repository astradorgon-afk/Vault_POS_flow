using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>First-expired-first-out batch allocation for sale lines (POS.md §2.3).</summary>
public sealed class FefoBatchesTests
{
    private static readonly ProductId Product = ProductId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly BatchId Early = BatchId.New();
    private static readonly BatchId Late = BatchId.New();
    private static readonly BatchId Open = BatchId.New();

    private static readonly IReadOnlyList<AllocatableBatch> Mixed = new List<AllocatableBatch>
    {
        new(Late, "LOT-LATE", Quantity: 5m, UnitCost: 60m, ExpiresOn: new DateOnly(2026, 3, 1)),
        new(Early, "LOT-EARLY", Quantity: 3m, UnitCost: 50m, ExpiresOn: new DateOnly(2026, 1, 1)),
        new(Open, "LOT-OPEN", Quantity: 10m, UnitCost: 40m, ExpiresOn: null),
    };

    [Fact]
    public void Allocate_ConsumesEarliestExpiryFirst()
    {
        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested: 6m, Mixed);

        result.IsSuccess.Should().BeTrue();
        IReadOnlyList<AllocatedSlice> slices = result.Value;
        slices.Should().HaveCount(2);
        slices[0].BatchId.Should().Be(Early);
        slices[0].Quantity.Should().Be(3m);
        slices[0].UnitCost.Should().Be(50m);
        slices[1].BatchId.Should().Be(Late);
        slices[1].Quantity.Should().Be(3m);
        slices[1].UnitCost.Should().Be(60m);
    }

    [Fact]
    public void Allocate_MovesToExpirylessBatchOnlyAfterDatedOnes()
    {
        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested: 9m, Mixed);

        result.IsSuccess.Should().BeTrue();
        IReadOnlyList<AllocatedSlice> slices = result.Value;
        slices.Should().HaveCount(3);
        slices[0].BatchId.Should().Be(Early);
        slices[1].BatchId.Should().Be(Late);
        slices[2].BatchId.Should().Be(Open);
        slices[2].Quantity.Should().Be(1m);
    }

    [Fact]
    public void Allocate_SkipsBatchesWithNoQuantity()
    {
        List<AllocatableBatch> batches =
        [
            new(Open, "LOT-OPEN", Quantity: 0m, UnitCost: 40m, ExpiresOn: null),
            new(Early, "LOT-EARLY", Quantity: 2m, UnitCost: 50m, ExpiresOn: new DateOnly(2026, 1, 1)),
        ];

        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested: 2m, batches);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.BatchId.Should().Be(Early);
    }

    [Fact]
    public void Allocate_NonBatchProduct_ReturnsSingleEmptyBatchSlice()
    {
        List<AllocatableBatch> batches =
        [
            new(BatchId: null, BatchCode: null, Quantity: 10m, UnitCost: 45m, ExpiresOn: null),
        ];

        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested: 4m, batches);

        result.IsSuccess.Should().BeTrue();
        IReadOnlyList<AllocatedSlice> slices = result.Value;
        slices.Should().ContainSingle();
        slices[0].BatchId.Should().BeNull();
        slices[0].Quantity.Should().Be(4m);
        slices[0].UnitCost.Should().Be(45m);
    }

    [Fact]
    public void Allocate_WhenStockIsInsufficient_ReportsAvailableAndRequested()
    {
        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested: 20m, Mixed);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("inventory.insufficient_stock");
        result.Error.Type.Should().Be(ErrorType.Conflict);
        result.Error.Metadata!["available"].Should().Be(18m);
        result.Error.Metadata["requested"].Should().Be(20m);
        result.Error.Metadata["productId"].Should().Be(Product.Value);
        result.Error.Metadata["locationId"].Should().Be(Store.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Allocate_NonPositiveRequest_IsRejected(decimal requested)
    {
        Result<IReadOnlyList<AllocatedSlice>> result = FefoBatches.Allocate(Product, Store, requested, Mixed);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.item.quantity_invalid");
    }

    [Fact]
    public void Allocate_NullBatches_IsRejected()
    {
        Action allocate = () => FefoBatches.Allocate(Product, Store, 1m, null!);

        allocate.Should().Throw<ArgumentNullException>();
    }
}