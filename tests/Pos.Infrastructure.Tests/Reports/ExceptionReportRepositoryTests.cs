using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Reports;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The inventory exception reports of ROADMAP §Phase 15. What counts as a loss is
/// the whole question: a transfer and a count correction both reduce a bucket,
/// and only one of them is stock the business no longer has.
/// </summary>
public sealed class ExceptionReportRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private LocationId external;
    private ProductId product;
    private IReadOnlyList<ProductId> countable = [];

    public async Task InitializeAsync()
    {
        this.connection = new SqliteConnection("Data Source=:memory:");
        await this.connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(this.connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        this.context = new PosDbContext(options);
        await this.context.Database.EnsureCreatedAsync();

        OrganizationId organization = OrganizationId.New();
        Location main = Location.Create(organization, "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        Location customer = Location.CreateSystemExternal(organization, "EXT-CUSTOMER", "Customers").Value;
        this.context.Locations.AddRange(main, customer);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        this.context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        this.context.UnitsOfMeasure.Add(piece);
        await this.context.SaveChangesAsync();

        this.store = main.Id;
        this.external = customer.Id;

        Product biscuits = Product.Create("SKU-1", "Biscuits", grocery.Id, piece.Id, UserId.New()).Value;
        Product soap = Product.Create("SKU-2", "Soap", grocery.Id, piece.Id, UserId.New()).Value;
        Product rice = Product.Create("SKU-3", "Rice", grocery.Id, piece.Id, UserId.New()).Value;
        this.context.Products.AddRange(biscuits, soap, rice);
        await this.context.SaveChangesAsync();

        this.product = biscuits.Id;

        // A count line is keyed by product, not by position, so a sheet with
        // three lines needs three products.
        this.countable = [biscuits.Id, soap.Id, rice.Id];
        this.context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task Adjustments_AreGroupedByWhy()
    {
        await WriteOffAsync(InventoryMovementType.Damage, AdjustmentReasonCode.Damaged, 3m);
        await WriteOffAsync(InventoryMovementType.Damage, AdjustmentReasonCode.Damaged, 2m);
        await WriteOffAsync(InventoryMovementType.Theft, AdjustmentReasonCode.Theft, 10m);

        List<AdjustmentSummaryRow> rows = [.. await AdjustmentsAsync()];

        rows.Should().HaveCount(2);
        rows[0].MovementType.Should().Be(InventoryMovementType.Theft);
        rows[0].QuantityOut.Should().Be(10m);
        rows[0].Entries.Should().Be(1);

        rows[1].MovementType.Should().Be(InventoryMovementType.Damage);
        rows[1].QuantityOut.Should().Be(5m);
        rows[1].Entries.Should().Be(2);
        rows[1].LocationCode.Should().Be("S1");
    }

    [Fact]
    public async Task Adjustments_IgnoreASale()
    {
        await SaleAsync(7m);

        // A sale reduces a bucket and is not an adjustment, however much the sign
        // of the leg looks like one.
        (await AdjustmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Shrinkage_ValuesWhatLeftAtWhatItWasCarriedAt()
    {
        await WriteOffAsync(InventoryMovementType.Spoilage, AdjustmentReasonCode.Spoilage, 4m, unitCost: 25m);

        ShrinkageReport report = await ShrinkageAsync();

        report.Rows.Should().ContainSingle();
        report.TotalQuantity.Should().Be(4m);
        report.TotalValue.Should().Be(100m);
        report.Rows[0].ReasonCode.Should().Be(AdjustmentReasonCode.Spoilage);
    }

    [Fact]
    public async Task Shrinkage_LeavesOutACountCorrection()
    {
        await WriteOffAsync(InventoryMovementType.Theft, AdjustmentReasonCode.Theft, 5m, unitCost: 10m);
        await WriteOffAsync(
            InventoryMovementType.CountAdjustmentDecrease, AdjustmentReasonCode.CountCorrection, 100m, unitCost: 10m);

        // The count says the books were wrong, not that goods left the building.
        // Folding it in would let a business shrink its shrinkage by counting more
        // often.
        ShrinkageReport report = await ShrinkageAsync();

        report.Rows.Should().ContainSingle().Which.MovementType.Should().Be(InventoryMovementType.Theft);
        report.TotalValue.Should().Be(50m);

        // It is still an adjustment, and the adjustment report shows it.
        (await AdjustmentsAsync()).Should().Contain(
            r => r.MovementType == InventoryMovementType.CountAdjustmentDecrease);
    }

    [Fact]
    public async Task StockPutBackIsReportedAsComingIn_AndIsNeverALoss()
    {
        await WriteOffAsync(InventoryMovementType.Damage, AdjustmentReasonCode.Damaged, 5m, unitCost: 10m);
        await PutBackAsync(2m);

        // The adjustment report keeps the two directions apart, because an
        // adjustment that adds stock back is still an adjustment somebody made.
        AdjustmentSummaryRow back = (await AdjustmentsAsync())
            .Single(r => r.MovementType == InventoryMovementType.ApprovedStockAdjustment);

        back.QuantityIn.Should().Be(2m);
        back.QuantityOut.Should().Be(0m);

        // Shrinkage counts only what left. Stock arriving is not a loss, and
        // letting a positive leg through would report the same goods missing and
        // then found as two separate losses.
        ShrinkageReport report = await ShrinkageAsync();

        report.TotalQuantity.Should().Be(5m);
        report.TotalValue.Should().Be(50m);
    }

    [Fact]
    public async Task CountVariance_TellsUncountedApartFromCountedAndRight()
    {
        await CountAsync(
            [(10m, 8m), (5m, 5m), (3m, null)]);

        // Only the line that was counted and wrong.
        CountVarianceReport wrong = await VariancesAsync();
        wrong.Rows.Should().ContainSingle();
        wrong.Rows[0].Variance.Should().Be(-2m);
        wrong.Rows[0].VarianceValue.Should().Be(-20m);

        // And with the uncounted asked for, the line nobody looked at as well —
        // which is not a variance of zero.
        CountVarianceReport withGaps = await VariancesAsync(includeUncounted: true);
        withGaps.Rows.Should().HaveCount(2);

        CountVarianceRow missed = withGaps.Rows.Single(r => r.PhysicalQuantity is null);
        missed.Variance.Should().BeNull();
        missed.VarianceValue.Should().BeNull();
        missed.SystemQuantity.Should().Be(3m);
    }

    [Fact]
    public async Task CountVariance_SortsByHowWrongItWas()
    {
        await CountAsync([(10m, 8m), (100m, 60m)]);

        CountVarianceReport report = await VariancesAsync();

        report.Rows.Select(r => r.Variance).Should().Equal(new decimal?[] { -40m, -2m });
    }

    private Task<IReadOnlyList<AdjustmentSummaryRow>> AdjustmentsAsync()
        => new ExceptionReportRepository(this.context).GetAdjustmentsAsync(
            Now.AddDays(-90), Now, [], CancellationToken.None);

    private Task<ShrinkageReport> ShrinkageAsync()
        => new ExceptionReportRepository(this.context).GetShrinkageAsync(
            Now.AddDays(-90), Now, [], CancellationToken.None);

    private Task<CountVarianceReport> VariancesAsync(bool includeUncounted = false, int limit = 100)
        => new ExceptionReportRepository(this.context).GetCountVariancesAsync(
            Now.AddDays(-90), Now, [], includeUncounted, limit, CancellationToken.None);

    private Task WriteOffAsync(
        InventoryMovementType type,
        AdjustmentReasonCode reason,
        decimal quantity,
        decimal unitCost = 10m)
        => PostAsync(
            type,
            reason,
            [
                new MovementLegSpec(
                    this.product, null, this.store, LocationKind.Store, InventoryState.Available,
                    -quantity, unitCost, ProductTracksBatches: false),
                new MovementLegSpec(
                    this.product, null, this.external, LocationKind.External, InventoryState.External,
                    quantity, unitCost, ProductTracksBatches: false),
            ]);

    /// <summary>An approved adjustment that puts stock back on the shelf.</summary>
    private Task PutBackAsync(decimal quantity, decimal unitCost = 10m)
        => PostAsync(
            InventoryMovementType.ApprovedStockAdjustment,
            AdjustmentReasonCode.CountCorrection,
            [
                new MovementLegSpec(
                    this.product, null, this.store, LocationKind.Store, InventoryState.Available,
                    quantity, unitCost, ProductTracksBatches: false),
                new MovementLegSpec(
                    this.product, null, this.external, LocationKind.External, InventoryState.External,
                    -quantity, unitCost, ProductTracksBatches: false),
            ]);

    private Task SaleAsync(decimal quantity)
        => PostAsync(
            InventoryMovementType.PosSale,
            null,
            [
                new MovementLegSpec(
                    this.product, null, this.store, LocationKind.Store, InventoryState.Available,
                    -quantity, 10m, ProductTracksBatches: false),
                new MovementLegSpec(
                    this.product, null, this.external, LocationKind.External, InventoryState.External,
                    quantity, 10m, ProductTracksBatches: false),
            ]);

    private async Task PostAsync(
        InventoryMovementType type,
        AdjustmentReasonCode? reason,
        IReadOnlyList<MovementLegSpec> legs)
    {
        // Each movement type accepts only its own kind of document, which is the
        // ledger refusing a write-off that cites a sale.
        (ReferenceDocumentType document, string number) = type switch
        {
            InventoryMovementType.PosSale => (ReferenceDocumentType.Sale, "SAL-2026-000001"),
            InventoryMovementType.CountAdjustmentIncrease or InventoryMovementType.CountAdjustmentDecrease
                => (ReferenceDocumentType.InventoryCount, "CNT-2026-000001"),
            _ => (ReferenceDocumentType.StockAdjustment, "ADJ-2026-000001"),
        };

        MovementGroupSpec spec = new(
            EventId.New(),
            type,
            document,
            Guid.CreateVersion7(),
            number,
            legs,
            new LedgerActor(UserId.New(), reason is null ? null : UserId.New(), null, CorrelationId.New()),
            Now.AddDays(-3),
            DateOnly.FromDateTime(Now.AddDays(-3).UtcDateTime),
            reason,
            reason is null ? null : "Recorded in a stock check.");

        InventoryMovementGroup group = InventoryMovementGroup.Create(spec, Now.AddDays(-3)).Value;

        this.context.InventoryMovements.AddRange(group.Movements);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task CountAsync(IReadOnlyList<(decimal System, decimal? Physical)> lines)
    {
        InventoryCount count = InventoryCount.Open(
            DocumentNumber.FromTrustedSource("CNT-2026-000001"),
            this.store,
            InventoryCountKind.FullPhysical,
            hasCategories: false,
            hasProducts: false,
            [
                .. lines.Select((l, i) => new InventoryCountSheetItem(this.countable[i], null, l.System, 10m)),
            ],
            null,
            UserId.New(),
            Now.AddDays(-2)).Value;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Physical is { } physical)
            {
                count.RecordCount(
                    this.countable[i],
                    null,
                    tracksBatches: false,
                    physical,
                    lines[i].System,
                    10m,
                    UserId.New(),
                    Now.AddDays(-1)).IsSuccess.Should().BeTrue();
            }
        }

        this.context.InventoryCounts.Add(count);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }
}
