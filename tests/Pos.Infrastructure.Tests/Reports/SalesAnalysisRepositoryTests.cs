using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Reports;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The sales analysis of ROADMAP §Phase 15. What matters here is the arithmetic:
/// a margin measured on the wrong base flatters every product by the tax rate,
/// and a total that disagrees with the rows under it is the one defect a reader
/// cannot work around.
/// </summary>
public sealed class SalesAnalysisRepositoryTests : IAsyncLifetime
{
    private const decimal VatRate = 0.12m;
    private static readonly DateOnly Day = new(2026, 9, 18);
    private static readonly DateTimeOffset At = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private LocationId other;
    private CategoryId category;
    private UnitOfMeasureId unit;
    private ProductId biscuits;
    private ProductId soap;
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
        Location main = Location.Create(organization, "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        Location second = Location.Create(organization, "S2", "Store Two", LocationKind.Store, "Asia/Manila").Value;
        this.context.Locations.AddRange(main, second);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        this.context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        this.context.UnitsOfMeasure.Add(piece);
        await this.context.SaveChangesAsync();

        this.store = main.Id;
        this.other = second.Id;
        this.category = grocery.Id;
        this.unit = piece.Id;

        Product one = Product.Create("SKU-1", "Biscuits", this.category, this.unit, UserId.New()).Value;
        Product two = Product.Create("SKU-2", "Soap", this.category, this.unit, UserId.New()).Value;
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
    public async Task Revenue_IsNetOfVat_AndMarginIsMeasuredOnIt()
    {
        // One unit at 112.00 tax-inclusive, costing 50.00. The VAT-exclusive
        // revenue is 100.00, so the margin is 50% — not the 55.36% that measuring
        // on the tax-inclusive price would report.
        await SellAsync(this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m);

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Product)).Rows.Single();

        row.GrossRevenue.Should().Be(112m);
        row.Revenue.Should().Be(100m, "margin measured on tax the business hands on is not margin");
        row.Cost.Should().Be(50m);
        row.GrossProfit.Should().Be(50m);
        row.MarginPercent.Should().Be(50m);
    }

    [Fact]
    public async Task AVatExemptLine_EarnsItsWholePrice()
    {
        await SellAsync(
            this.store, this.biscuits, "Biscuits", quantity: 2m, unitPrice: 50m, unitCost: 20m, vatExempt: true);

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Product)).Rows.Single();

        // Nothing is handed on, so the revenue is the price. The same column reads
        // correctly for both tax classes, which is why there is only one.
        row.Revenue.Should().Be(100m);
        row.Cost.Should().Be(40m);
        row.MarginPercent.Should().Be(60m);
    }

    [Fact]
    public async Task ADiscountReducesRevenueAndMargin_AndIsReportedSeparately()
    {
        await SellAsync(
            this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m, discount: 22.40m);

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Product)).Rows.Single();

        row.GrossRevenue.Should().Be(112m, "what the customer was quoted before the discount came off");
        row.Discount.Should().Be(22.40m);
        row.Revenue.Should().Be(80m, "89.60 charged, less the VAT on it");
        row.GrossProfit.Should().Be(30m);
    }

    [Fact]
    public async Task SomethingGivenAway_ReportsNoMarginRatherThanThrowing()
    {
        // A free line alongside a paid one: the shop cannot ring up a sale that
        // settles for nothing, but it can and does give one item away inside a
        // sale that pays. That line earns nothing and still costs what it cost.
        await SellTwoLinesAsync(
            paid: (this.biscuits, "Biscuits", 112m, 50m),
            free: (this.soap, "Soap", 20m));

        SalesAnalysisRow given = (await AnalyseAsync(SalesAnalysisGrouping.Product))
            .Rows.Single(r => r.GroupName == "Soap");

        // A report that divides by the nothing it earned is one nobody can open
        // that week.
        given.Revenue.Should().Be(0m);
        given.MarginPercent.Should().Be(0m);
        given.GrossProfit.Should().Be(-20m, "the cost is still real");
    }

    [Fact]
    public async Task ReturnedUnitsAreReported_ButNotNettedOutOfThePeriodThatSoldThem()
    {
        Sale sale = await SellAsync(
            this.store, this.biscuits, "Biscuits", quantity: 5m, unitPrice: 112m, unitCost: 50m);

        // Set through the tracker rather than the aggregate: accumulating a return
        // is internal to the domain, and a return posted properly here would drag
        // in the whole returns flow to prove an arithmetic point about reporting.
        SaleItem line = await this.context.SaleItems.AsTracking().SingleAsync(i => i.SaleId == sale.Id);
        this.context.Entry(line).Property(i => i.ReturnedQuantity).CurrentValue = 2m;
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Product)).Rows.Single();

        row.QuantitySold.Should().Be(5m);
        row.QuantityReturned.Should().Be(2m);

        // The period sold five. Netting the return out would rewrite a report
        // somebody already printed, and would put a line's revenue and its
        // reversal in different months.
        row.Revenue.Should().Be(500m);
        row.Cost.Should().Be(250m);
    }

    [Fact]
    public async Task TheTotalsAgreeWithTheRowsUnderThem()
    {
        await SellAsync(this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m);
        await SellAsync(this.store, this.soap, "Soap", quantity: 3m, unitPrice: 56m, unitCost: 20m);

        SalesAnalysisReport report = await AnalyseAsync(SalesAnalysisGrouping.Product);

        report.Rows.Should().HaveCount(2);
        report.Totals.Revenue.Should().Be(report.Rows.Sum(r => r.Revenue));
        report.Totals.Cost.Should().Be(report.Rows.Sum(r => r.Cost));
        report.Totals.QuantitySold.Should().Be(4m);

        // Largest revenue first: 150 of soap ahead of 100 of biscuits.
        report.Rows.Select(r => r.GroupName).Should().Equal(["Soap", "Biscuits"]);
    }

    [Fact]
    public async Task TheTotalsCoverEverything_EvenWhenTheRowsAreTruncated()
    {
        await SellAsync(this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m);
        await SellAsync(this.store, this.soap, "Soap", quantity: 3m, unitPrice: 56m, unitCost: 20m);

        SalesAnalysisReport report = await AnalyseAsync(SalesAnalysisGrouping.Product, limit: 1);

        report.Rows.Should().ContainSingle();
        report.Truncated.Should().BeTrue("a report silently missing its tail is one somebody will act on");
        report.Totals.Revenue.Should().Be(250m, "the total is the period, not the page");
    }

    [Fact]
    public async Task ByCategory_RollsEveryProductUnderIt()
    {
        await SellAsync(this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m);
        await SellAsync(this.store, this.soap, "Soap", quantity: 3m, unitPrice: 56m, unitCost: 20m);

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Category)).Rows.Single();

        row.GroupCode.Should().Be("GROC");
        row.GroupName.Should().Be("Grocery");
        row.Revenue.Should().Be(250m);
    }

    [Fact]
    public async Task ByLocation_SeparatesTheStores_AndAScopeOfOneExcludesTheOther()
    {
        await SellAsync(this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m);
        await SellAsync(this.other, this.biscuits, "Biscuits", quantity: 2m, unitPrice: 112m, unitCost: 50m);

        SalesAnalysisReport both = await AnalyseAsync(SalesAnalysisGrouping.Location);
        both.Rows.Select(r => r.GroupCode).Should().BeEquivalentTo(new[] { "S1", "S2" });
        both.Totals.Revenue.Should().Be(300m);

        SalesAnalysisReport onlyMine = await AnalyseAsync(
            SalesAnalysisGrouping.Location, locations: [this.store]);
        onlyMine.Rows.Should().ContainSingle().Which.GroupCode.Should().Be("S1");
        onlyMine.Totals.Revenue.Should().Be(100m);
    }

    [Fact]
    public async Task OutsideThePeriod_IsNotCounted()
    {
        await SellAsync(
            this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m,
            businessDate: Day.AddDays(-1));

        SalesAnalysisReport report = await AnalyseAsync(SalesAnalysisGrouping.Product);

        report.Rows.Should().BeEmpty();
        report.Totals.Revenue.Should().Be(0m);
        report.Totals.MarginPercent.Should().Be(0m);
    }

    [Fact]
    public async Task TheProductNameIsTheOneOnTheReceipt()
    {
        await SellAsync(this.store, this.biscuits, "Digestives, 200g", quantity: 1m, unitPrice: 112m, unitCost: 50m);

        SalesAnalysisRow row = (await AnalyseAsync(SalesAnalysisGrouping.Product)).Rows.Single();

        // Frozen at the till. A product renamed since is reported under what the
        // receipt says, because that is what the person holding it will ask about.
        row.GroupName.Should().Be("Digestives, 200g");
    }

    [Fact]
    public async Task PaymentsAreBrokenDownByMethod()
    {
        await SellAsync(
            this.store, this.biscuits, "Biscuits", quantity: 1m, unitPrice: 112m, unitCost: 50m,
            payments: [
                new PaymentSpec(PaymentMethod.Cash, 62m, 100m, null),
                new PaymentSpec(PaymentMethod.Card, 50m, null, "tkn-1"),
            ]);

        IReadOnlyList<SalesPaymentMethodRow> rows = await new SalesAnalysisRepository(this.context)
            .GetPaymentMethodBreakdownAsync(Day, Day, [this.store], CancellationToken.None);

        rows.Select(r => r.Method).Should().Equal(new[] { "Cash", "Card" });
        rows[0].Amount.Should().Be(62m);
        rows[0].Change.Should().Be(38m);
        rows[1].Amount.Should().Be(50m);
        rows[1].Change.Should().Be(0m);
    }

    private async Task SellTwoLinesAsync(
        (ProductId Id, string Name, decimal UnitPrice, decimal UnitCost) paid,
        (ProductId Id, string Name, decimal UnitCost) free)
    {
        this.nextNumber++;

        Sale sale = Sale.Create(
            DocumentNumber.FromTrustedSource(
                FormattableString.Invariant($"SAL-2026-{this.nextNumber:D6}")),
            EventId.New(),
            this.store,
            CashierShiftId.New(),
            DeviceId.New(),
            customerId: null,
            Day,
            At,
            UserId.New(),
            [Item(paid.Id, paid.Name, 1m, paid.UnitPrice, paid.UnitCost), Item(free.Id, free.Name, 1m, 0m, free.UnitCost)],
            [new PaymentSpec(PaymentMethod.Cash, paid.UnitPrice, paid.UnitPrice, null)]).Value;

        this.context.Sales.Add(sale);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private ItemSpec Item(
        ProductId productId,
        string productName,
        decimal quantity,
        decimal unitPrice,
        decimal unitCost,
        decimal discount = 0m,
        bool vatExempt = false)
        => new(
            productId,
            productName,
            Barcode: null,
            quantity,
            this.unit,
            unitPrice,
            ProductPriceId.New(),
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            discount,
            discount > 0m ? UserId.New() : null,
            vatExempt ? null : VatRate,
            vatExempt,
            IsZeroRated: false,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            unitCost,
            TracksBatches: false);

    private Task<SalesAnalysisReport> AnalyseAsync(
        SalesAnalysisGrouping groupBy,
        int limit = 100,
        IReadOnlyCollection<LocationId>? locations = null)
        => new SalesAnalysisRepository(this.context).GetSalesAnalysisAsync(
            new SalesAnalysisQuery(Day, Day, groupBy, locations ?? [], limit),
            CancellationToken.None);

    private async Task<Sale> SellAsync(
        LocationId locationId,
        ProductId productId,
        string productName,
        decimal quantity,
        decimal unitPrice,
        decimal unitCost,
        decimal discount = 0m,
        bool vatExempt = false,
        DateOnly? businessDate = null,
        IReadOnlyList<PaymentSpec>? payments = null)
    {
        decimal net = (unitPrice * quantity) - discount;

        ItemSpec item = Item(productId, productName, quantity, unitPrice, unitCost, discount, vatExempt);

        this.nextNumber++;

        Sale sale = Sale.Create(
            DocumentNumber.FromTrustedSource(
                FormattableString.Invariant($"SAL-2026-{this.nextNumber:D6}")),
            EventId.New(),
            locationId,
            CashierShiftId.New(),
            DeviceId.New(),
            customerId: null,
            businessDate ?? Day,
            At,
            UserId.New(),
            [item],
            payments ?? [new PaymentSpec(PaymentMethod.Cash, net, net, null)]).Value;

        this.context.Sales.Add(sale);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();

        return sale;
    }
}
