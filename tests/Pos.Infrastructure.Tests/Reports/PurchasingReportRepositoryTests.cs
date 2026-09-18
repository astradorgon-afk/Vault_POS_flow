using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Purchasing;
using Pos.Domain.Reports;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The purchasing reports of ROADMAP §Phase 15. The scorecard is the risk: a
/// rate without its denominator turns one late delivery into a verdict, and
/// counting an unpromised delivery as punctual rewards a supplier for refusing to
/// commit to a date.
/// </summary>
public sealed class PurchasingReportRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId warehouse;
    private LocationId store;
    private SupplierId alpha;
    private SupplierId beta;
    private ProductId product;
    private UnitOfMeasureId unit;
    private int nextNumber;

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
        Location main = Location.Create(
            organization, "WH", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila").Value;
        Location one = Location.Create(organization, "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        this.context.Locations.AddRange(main, one);

        Supplier a = Supplier.Create("SUP-A", "Alpha Trading", null, 30, 7).Value;
        Supplier b = Supplier.Create("SUP-B", "Beta Supply", null, 30, 7).Value;
        this.context.Suppliers.AddRange(a, b);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        this.context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        this.context.UnitsOfMeasure.Add(piece);
        await this.context.SaveChangesAsync();

        this.warehouse = main.Id;
        this.store = one.Id;
        this.alpha = a.Id;
        this.beta = b.Id;
        this.unit = piece.Id;

        Product biscuits = Product.Create("SKU-1", "Biscuits", grocery.Id, piece.Id, UserId.New()).Value;
        this.context.Products.Add(biscuits);
        await this.context.SaveChangesAsync();

        this.product = biscuits.Id;
        this.context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task APurchaseOrder_ReportsWhatWasOrderedAndWhatArrived()
    {
        await OrderAsync(this.alpha, ordered: 100m, received: 90m, damaged: 5m);

        PurchaseOrderReportRow row = (await PurchasesAsync()).Rows.Single();

        row.SupplierName.Should().Be("Alpha Trading");
        row.DestinationCode.Should().Be("WH");
        row.OrderedQuantity.Should().Be(100m);
        row.ReceivedQuantity.Should().Be(90m);
        row.AcceptedQuantity.Should().Be(85m, "five of the ninety arrived damaged");
        row.LineCount.Should().Be(1);
        row.GrandTotal.Should().BePositive();
    }

    [Fact]
    public async Task AnOrderThatHasNotArrived_ShowsNoReceiptDate()
    {
        await OrderAsync(this.alpha, ordered: 50m);

        PurchaseOrderReportRow row = (await PurchasesAsync()).Rows.Single();

        row.FirstReceivedAtUtc.Should().BeNull();
        row.ReceivedQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task Punctuality_IsScoredOnlyWhereThereWasSomethingToScore()
    {
        // Promised and late.
        await OrderAsync(this.alpha, ordered: 10m, received: 10m, expectedAt: Now.AddDays(-10), receivedAt: Now.AddDays(-6));

        // Promised and early.
        await OrderAsync(this.alpha, ordered: 10m, received: 10m, expectedAt: Now.AddDays(-10), receivedAt: Now.AddDays(-12));

        // Never promised a date, and arrived. Counting this as on time would
        // reward a supplier for refusing to commit to one.
        await OrderAsync(this.alpha, ordered: 10m, received: 10m, receivedAt: Now.AddDays(-3));

        // Promised and still not here.
        await OrderAsync(this.alpha, ordered: 10m, expectedAt: Now.AddDays(-2));

        SupplierPerformanceRow row = (await PerformanceAsync()).Single(r => r.SupplierCode == "SUP-A");

        row.OrdersPlaced.Should().Be(4);
        row.OrdersReceived.Should().Be(3);

        // Only the two that were both promised and received can be scored.
        row.OrdersScoredForTime.Should().Be(2);
        row.OnTimeRate.Should().Be(0.5m);
        row.AverageDaysLate.Should().Be(1m, "four days late and two days early average to one");
    }

    [Fact]
    public async Task ASupplierNothingCanBeScoredAgainst_ReportsNoRateRatherThanZero()
    {
        await OrderAsync(this.beta, ordered: 10m);

        SupplierPerformanceRow row = (await PerformanceAsync()).Single(r => r.SupplierCode == "SUP-B");

        // Nothing arrived and nothing was promised. A zero here would read as a
        // supplier who is never on time, which is a different accusation.
        row.OnTimeRate.Should().BeNull();
        row.AverageDaysLate.Should().BeNull();
        row.OrdersScoredForTime.Should().Be(0);
    }

    [Fact]
    public async Task EveryRateComesWithTheCountItWasComputedFrom()
    {
        await OrderAsync(this.alpha, ordered: 10m, received: 10m, expectedAt: Now.AddDays(-5), receivedAt: Now.AddDays(-1));

        SupplierPerformanceRow row = (await PerformanceAsync()).Single(r => r.SupplierCode == "SUP-A");

        // One delivery, late. The rate says 0%; the count says it is one delivery,
        // which is the difference between a verdict and an anecdote.
        row.OnTimeRate.Should().Be(0m);
        row.OrdersScoredForTime.Should().Be(1);
    }

    [Fact]
    public async Task FillRateAndQualityAreCounted()
    {
        await OrderAsync(this.alpha, ordered: 100m, received: 80m, damaged: 10m, documentsMissing: true);

        SupplierPerformanceRow row = (await PerformanceAsync()).Single(r => r.SupplierCode == "SUP-A");

        row.OrderedQuantity.Should().Be(100m);
        row.AcceptedQuantity.Should().Be(70m);
        row.FillRate.Should().Be(0.7m);
        row.RejectedQuantity.Should().Be(10m);
        row.ReceiptsWithMissingDocuments.Should().Be(1);
    }

    [Fact]
    public async Task AScopeOfOneLocation_ExcludesAnotherStoresOrders()
    {
        await OrderAsync(this.alpha, ordered: 10m);
        await OrderAsync(this.beta, ordered: 10m, destination: this.store);

        (await PurchasesAsync()).Rows.Should().HaveCount(2);
        (await PurchasesAsync(locations: [this.store])).Rows.Should().ContainSingle()
            .Which.SupplierName.Should().Be("Beta Supply");
    }

    private Task<PurchaseOrderReport> PurchasesAsync(
        IReadOnlyCollection<LocationId>? locations = null,
        int limit = 100)
        => new PurchasingReportRepository(this.context).GetPurchaseOrdersAsync(
            Now.AddDays(-90), Now, locations ?? [], null, limit, CancellationToken.None);

    private Task<IReadOnlyList<SupplierPerformanceRow>> PerformanceAsync()
        => new PurchasingReportRepository(this.context).GetSupplierPerformanceAsync(
            Now.AddDays(-90), Now, [], CancellationToken.None);

    /// <summary>
    /// Raises a purchase order and, when something arrived, books a goods receipt
    /// against it through the real receiving planner.
    /// </summary>
    private async Task OrderAsync(
        SupplierId supplierId,
        decimal ordered,
        decimal? received = null,
        decimal damaged = 0m,
        bool documentsMissing = false,
        DateTimeOffset? expectedAt = null,
        DateTimeOffset? receivedAt = null,
        LocationId? destination = null)
    {
        LocationId to = destination ?? this.warehouse;

        PurchaseOrder order = PurchaseOrder.Create(
            supplierId,
            to,
            [new PurchaseOrderLineSpec(this.product, this.unit, ordered, 12m)],
            UserId.New(),
            Now.AddDays(-30),
            "PHP",
            expectedAt).Value;

        this.context.PurchaseOrders.Add(order);
        await this.context.SaveChangesAsync();

        if (received is { } arrived)
        {
            PurchaseOrderLine line = order.Lines[0];

            GoodsReceipt receipt = GoodsReceipt.Create(
                order.Id,
                supplierId,
                to,
                [new GoodsReceiptLineSpec(line.Id, arrived, damaged, 0m, 0m, 12m)],
                new Dictionary<PurchaseOrderLineId, PurchaseOrderLineReceivingInfo>
                {
                    [line.Id] = new(line.LineNo, this.product, ordered, 12m),
                },
                new Dictionary<PurchaseOrderLineId, decimal> { [line.Id] = 0m },
                new Dictionary<ProductId, ProductReceivingTrackingInfo>
                {
                    [this.product] = new(TracksBatches: false, TracksExpiry: false),
                },
                documentsMissing,
                DateOnly.FromDateTime((receivedAt ?? Now.AddDays(-5)).UtcDateTime),
                UserId.New(),
                receivedAt ?? Now.AddDays(-5)).Value;

            // Numbered as the receiving flow numbers it. Without one every
            // receipt carries the empty string and the second collides on the
            // unique index, which is the database keeping its own promise.
            this.nextNumber++;
            receipt.AssignNumber(DocumentNumber.FromTrustedSource(
                FormattableString.Invariant($"GRN-2026-{this.nextNumber:D6}"))).IsSuccess.Should().BeTrue();

            this.context.GoodsReceipts.Add(receipt);
            await this.context.SaveChangesAsync();
        }

        this.context.ChangeTracker.Clear();
    }
}
