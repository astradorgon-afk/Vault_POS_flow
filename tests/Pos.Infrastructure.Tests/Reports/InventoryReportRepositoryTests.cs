using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Reports;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The inventory reports of ROADMAP §Phase 15. The roll-up across state buckets
/// is the whole risk here: a shelf count that includes stock nobody may sell, or
/// a unit cost averaged from averages, is a number somebody orders against.
/// </summary>
public sealed class InventoryReportRepositoryTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private LocationId other;
    private LocationId external;
    private ProductId biscuits;
    private ProductId soap;

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
        Location second = Location.Create(organization, "S2", "Store Two", LocationKind.Store, "Asia/Manila").Value;
        Location customer = Location.CreateSystemExternal(organization, "EXT-CUSTOMER", "Customers").Value;
        this.context.Locations.AddRange(main, second, customer);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        this.context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        this.context.UnitsOfMeasure.Add(piece);
        await this.context.SaveChangesAsync();

        this.store = main.Id;
        this.other = second.Id;
        this.external = customer.Id;

        Product one = Product.Create("SKU-1", "Biscuits", grocery.Id, piece.Id, UserId.New()).Value;
        Product two = Product.Create("SKU-2", "Soap", grocery.Id, piece.Id, UserId.New()).Value;
        this.context.Products.AddRange(one, two);
        await this.context.SaveChangesAsync();

        this.biscuits = one.Id;
        this.soap = two.Id;
        this.context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task OnHand_SeparatesWhatMayBeSoldFromWhatIsMerelyThere()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.store, this.biscuits, InventoryState.Quarantine, 4m, 50m);
        await BucketAsync(this.store, this.biscuits, InventoryState.Damaged, 1m, 50m);
        await BucketAsync(this.store, this.biscuits, InventoryState.InTransit, 6m, 50m);

        InventoryOnHandRow row = (await OnHandAsync()).Single();

        row.Available.Should().Be(10m, "only available stock may be sold, and a till refuses on this number");
        row.OnHand.Should().Be(15m, "quarantined and damaged stock is standing there whether it sells or not");
        row.InFlight.Should().Be(6m, "dispatched and not yet received is ours but is not at the location");

        // Ordered by the state itself, so the breakdown reads the same way on every
        // row whatever order the buckets happen to have been written in.
        row.ByState.Select(b => b.State).Should().Equal(new[]
        {
            InventoryState.Available, InventoryState.InTransit, InventoryState.Quarantine, InventoryState.Damaged,
        });
    }

    [Fact]
    public async Task OnHand_OmitsStatesHoldingNothing()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.store, this.biscuits, InventoryState.Damaged, 0m, 0m);

        InventoryOnHandRow row = (await OnHandAsync()).Single();

        row.ByState.Should().ContainSingle().Which.State.Should().Be(InventoryState.Available);
    }

    [Fact]
    public async Task OnHand_LeavesOutABucketThatHoldsNothingAtAll_UnlessAsked()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.store, this.soap, InventoryState.Available, 0m, 0m);

        // A catalogue of ten thousand products at forty stores is four hundred
        // thousand rows of zero, and the question this answers is what is there.
        (await OnHandAsync()).Should().ContainSingle().Which.Sku.Should().Be("SKU-1");

        (await OnHandAsync(includeEmpty: true)).Should().HaveCount(2);
    }

    [Fact]
    public async Task OnHand_NeverCountsACounterparty()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.external, this.biscuits, InventoryState.External, -900m, 50m);

        // The external bucket is the other leg of every sale ever rung up.
        // Counting it would report the whole history of the shop as stock.
        List<InventoryOnHandRow> rows = [.. await OnHandAsync()];

        rows.Should().ContainSingle();
        rows[0].LocationCode.Should().Be("S1");
    }

    [Fact]
    public async Task Valuation_DerivesUnitCostFromTheTotals_NotFromAnAverageOfAverages()
    {
        // One unit at 100 and a thousand at 10. The true average is 10.09; an
        // average of the two bucket averages would say 55, and somebody would
        // reorder against it.
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 1m, 100m);
        await BucketAsync(this.store, this.biscuits, InventoryState.Quarantine, 1000m, 10m);

        InventoryValuationReport report = await ValuationAsync();
        InventoryValuationRow row = report.Rows.Single();

        row.Quantity.Should().Be(1001m);
        row.TotalValue.Should().Be(10100m);
        row.AverageUnitCost.Should().Be(10.0899m);
    }

    [Fact]
    public async Task Valuation_WithNoUnitsLeft_StatesNoUnitCost()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 0m, 0m, totalValue: 0.0003m);

        InventoryValuationRow row = (await ValuationAsync()).Rows.Single();

        // A residue left behind by rounding is something to investigate, not a
        // unit cost of infinity.
        row.Quantity.Should().Be(0m);
        row.AverageUnitCost.Should().Be(0m);
        row.TotalValue.Should().Be(0.0003m);
    }

    [Fact]
    public async Task Valuation_TotalsEverythingThatMatched_NotJustThePage()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.store, this.soap, InventoryState.Available, 10m, 5m);

        InventoryValuationReport report = await ValuationAsync(limit: 1);

        report.Rows.Should().ContainSingle();
        report.Truncated.Should().BeTrue();

        // A total that only covers the rows that fitted is one somebody will put
        // in a set of accounts.
        report.TotalValue.Should().Be(550m);
        report.Quantity.Should().Be(20m);
    }

    [Fact]
    public async Task AScopeOfOneStore_ExcludesTheOther()
    {
        await BucketAsync(this.store, this.biscuits, InventoryState.Available, 10m, 50m);
        await BucketAsync(this.other, this.biscuits, InventoryState.Available, 7m, 50m);

        (await OnHandAsync()).Should().HaveCount(2);

        List<InventoryOnHandRow> mine = [.. await OnHandAsync(locations: [this.store])];
        mine.Should().ContainSingle();
        mine[0].Available.Should().Be(10m);
    }

    [Fact]
    public async Task Movements_ComeBackInTheOrderTheLedgerWroteThem()
    {
        DateTimeOffset now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

        // An offline sale that happened first and was recorded last. Ordering by
        // when it happened would put it before the receipt it drew against, and
        // the running total would never tie back to the balance.
        await MovementAsync(this.biscuits, occurred: now.AddDays(-2), recorded: now.AddMinutes(2), delta: -3m);
        await MovementAsync(this.biscuits, occurred: now, recorded: now.AddMinutes(1), delta: -20m);

        InventoryMovementReport report = await MovementsAsync(now.AddDays(-7), now.AddDays(1));

        // Two sales, two legs each — and only the store's leg is listed. The
        // counterparty leg is bookkeeping, not stock moving at a place somebody
        // works, and listing it would double the length of every history.
        report.Rows.Select(r => r.QuantityDelta).Should().Equal(new[] { -20m, -3m });
        report.Truncated.Should().BeFalse();
        report.Rows[0].Sku.Should().Be("SKU-1");
        report.Rows.Should().OnlyContain(r => r.LocationCode == "S1");
    }

    [Fact]
    public async Task Movements_SayWhenThereAreMoreBehindThePage()
    {
        DateTimeOffset now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

        await MovementAsync(this.biscuits, now, now, -1m);
        await MovementAsync(this.biscuits, now, now.AddMinutes(1), -2m);

        InventoryMovementReport report = await MovementsAsync(now.AddDays(-1), now.AddDays(1), limit: 1);

        report.Rows.Should().ContainSingle();
        report.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Movements_OutsideTheWindow_AreNotReturned()
    {
        DateTimeOffset now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

        await MovementAsync(this.biscuits, now.AddDays(-30), now.AddDays(-30), -5m);

        InventoryMovementReport report = await MovementsAsync(now.AddDays(-1), now.AddDays(1));

        report.Rows.Should().BeEmpty();
    }

    private Task<IReadOnlyList<InventoryOnHandRow>> OnHandAsync(
        bool includeEmpty = false,
        int limit = 100,
        IReadOnlyCollection<LocationId>? locations = null)
        => new InventoryReportRepository(this.context).GetOnHandAsync(
            new InventoryReportQuery(locations ?? [], null, includeEmpty, limit), CancellationToken.None);

    private Task<InventoryValuationReport> ValuationAsync(int limit = 100)
        => new InventoryReportRepository(this.context).GetValuationAsync(
            new InventoryReportQuery([], null, IncludeEmpty: false, limit), CancellationToken.None);

    private Task<InventoryMovementReport> MovementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100)
        => new InventoryReportRepository(this.context).GetMovementsAsync(
            from, to, new InventoryReportQuery([], null, IncludeEmpty: false, limit), CancellationToken.None);

    /// <summary>
    /// Puts a quantity in a bucket without posting a ledger movement.
    /// </summary>
    /// <remarks>
    /// The quantity only changes by applying a leg, which is right — but posting
    /// real legs to prove an arithmetic point about a roll-up would drag the whole
    /// ledger into a reporting test and pin nothing extra.
    /// </remarks>
    private async Task BucketAsync(
        LocationId locationId,
        ProductId productId,
        InventoryState state,
        decimal quantity,
        decimal unitCost,
        decimal? totalValue = null)
    {
        InventoryBalance balance = InventoryBalance.CreateEmpty(locationId, productId, BatchId.Empty, state);
        this.context.InventoryBalances.Add(balance);

        this.context.Entry(balance).Property(b => b.Quantity).CurrentValue = quantity;
        this.context.Entry(balance).Property(b => b.AverageUnitCost).CurrentValue = unitCost;
        this.context.Entry(balance).Property(b => b.TotalValue).CurrentValue = totalValue ?? quantity * unitCost;

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task MovementAsync(
        ProductId productId,
        DateTimeOffset occurred,
        DateTimeOffset recorded,
        decimal delta)
    {
        // A real balanced group: stock leaving the shelf has to arrive somewhere,
        // and the counterparty leg is what keeps the ledger balanced. Constructing
        // legs any other way is not possible, which is the point of the design.
        MovementGroupSpec spec = new(
            EventId.New(),
            InventoryMovementType.PosSale,
            ReferenceDocumentType.Sale,
            Guid.CreateVersion7(),
            "SAL-2026-000001",
            [
                new MovementLegSpec(
                    productId, null, this.store, LocationKind.Store,
                    InventoryState.Available, delta, 50m, ProductTracksBatches: false),
                new MovementLegSpec(
                    productId, null, this.external, LocationKind.External,
                    InventoryState.External, -delta, 50m, ProductTracksBatches: false),
            ],
            new LedgerActor(UserId.New(), null, null, CorrelationId.New()),
            occurred,
            DateOnly.FromDateTime(occurred.UtcDateTime));

        InventoryMovementGroup group = InventoryMovementGroup.Create(spec, recorded).Value;

        this.context.InventoryMovements.AddRange(group.Movements);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }
}
