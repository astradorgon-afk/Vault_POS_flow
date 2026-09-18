using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Quarantine;
using Pos.Domain.Reports;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The audit, unauthorized-inventory and expiry reports of ROADMAP §Phase 15.
/// The scoping decision is the interesting one: an audit entry with no location
/// is business-wide, and it must not reach a report about one shop.
/// </summary>
public sealed class AuditReportRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 18);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private LocationId other;
    private ProductId product;
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
    public async Task AScopedCaller_SeesTheirOwnStore_AndNothingBusinessWide()
    {
        await AuditAsync("sale.completed", this.store);
        await AuditAsync("sale.completed", this.other);
        await AuditAsync("role.granted", null);

        // An owner sees everything, the business-wide entry included — those are
        // the ones that matter most.
        (await ActivityAsync()).Rows.Should().HaveCount(3);

        // A store manager sees their own shop. The role grant is head-office
        // activity and would leak through a report about their store.
        AuditActivityReport mine = await ActivityAsync(locations: [this.store]);
        mine.Rows.Should().ContainSingle();
        mine.Rows[0].LocationCode.Should().Be("S1");
    }

    [Fact]
    public async Task TheActivityListCarriesNoBeforeAndAfterPayloads()
    {
        await AuditAsync("price.changed", this.store, reason: "Quarterly repricing");

        AuditActivityRow row = (await ActivityAsync()).Rows.Single();

        row.Action.Should().Be("price.changed");
        row.Reason.Should().Be("Quarterly repricing");
        row.UserRoleSnapshot.Should().Be("StoreManager", "what authority they held then, not now");

        // The payloads can carry customer details and prices, and an activity list
        // is read far more often than a single entry is examined. Whoever needs
        // them opens the entry.
        typeof(AuditActivityRow).GetProperty("PreviousValueJson").Should().BeNull();
        typeof(AuditActivityRow).GetProperty("NewValueJson").Should().BeNull();
    }

    [Fact]
    public async Task ActivityCanBeNarrowedToOneActorOrOneAction()
    {
        UserId cashier = UserId.New();
        await AuditAsync("sale.voided", this.store, userId: cashier);
        await AuditAsync("sale.voided", this.store);
        await AuditAsync("sale.completed", this.store, userId: cashier);

        (await ActivityAsync(userId: cashier)).Rows.Should().HaveCount(2);
        (await ActivityAsync(action: "sale.voided")).Rows.Should().HaveCount(2);
        (await ActivityAsync(userId: cashier, action: "sale.voided")).Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ActivitySaysWhenThereIsMoreBehindThePage()
    {
        await AuditAsync("sale.completed", this.store);
        await AuditAsync("sale.completed", this.store);

        AuditActivityReport report = await ActivityAsync(limit: 1);

        report.Rows.Should().ContainSingle();
        report.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task AnOpenIncidentAgesToNow_AndAClosedOneKeepsHowLongItTook()
    {
        await IncidentAsync(this.store, Now.AddDays(-12), lines: 2, unidentified: 1);
        await IncidentAsync(this.store, Now.AddDays(-40), resolvedAt: Now.AddDays(-37));

        List<QuarantineIncidentRow> rows = [.. await IncidentsAsync()];

        // Open first, and one number answers both "how long has this been sitting"
        // and "how long did that one take".
        rows[0].ResolvedAtUtc.Should().BeNull();
        rows[0].DaysOpen.Should().Be(12);
        rows[0].LineCount.Should().Be(2);
        rows[0].UnidentifiedLines.Should().Be(1, "unidentifiable stock is the most worth investigating");

        rows[1].ResolvedAtUtc.Should().NotBeNull();
        rows[1].DaysOpen.Should().Be(3);
    }

    [Fact]
    public async Task OpenOnly_LeavesOutWhatWasClosed()
    {
        await IncidentAsync(this.store, Now.AddDays(-5));
        await IncidentAsync(this.store, Now.AddDays(-9), resolvedAt: Now.AddDays(-8));

        (await IncidentsAsync(openOnly: true)).Should().ContainSingle();
    }

    [Fact]
    public async Task ExpiredStockIsIncludedHoweverFarBackItWent()
    {
        await ExpiringAsync("LOT-OLD", Today.AddDays(-200), 3m);
        await ExpiringAsync("LOT-SOON", Today.AddDays(10), 5m);
        await ExpiringAsync("LOT-LATER", Today.AddDays(200), 7m);

        List<ExpiringStockRow> rows = [.. await ExpiringStockAsync(withinDays: 30)];

        // A batch that expired last month is more urgent than one expiring next
        // week, not less, so no floor is applied to the window.
        rows.Select(r => r.LotNumber).Should().Equal(new[] { "LOT-OLD", "LOT-SOON" });
        rows[0].DaysToExpiry.Should().Be(-200);
        rows[1].DaysToExpiry.Should().Be(10);
    }

    [Fact]
    public async Task StockWithNothingLeftIsNotReported()
    {
        await ExpiringAsync("LOT-GONE", Today.AddDays(-5), 0m);

        // Nothing is standing there, so there is nothing to throw away.
        (await ExpiringStockAsync()).Should().BeEmpty();
    }

    private Task<AuditActivityReport> ActivityAsync(
        IReadOnlyCollection<LocationId>? locations = null,
        UserId? userId = null,
        string? action = null,
        int limit = 100)
        => new AuditReportRepository(this.context).GetActivityAsync(
            Now.AddDays(-90), Now, locations ?? [], userId, action, limit, CancellationToken.None);

    private Task<IReadOnlyList<QuarantineIncidentRow>> IncidentsAsync(bool openOnly = false)
        => new AuditReportRepository(this.context).GetQuarantineIncidentsAsync(
            Now.AddDays(-90), Now, [], openOnly, Now, CancellationToken.None);

    private Task<IReadOnlyList<ExpiringStockRow>> ExpiringStockAsync(int withinDays = 30)
        => new AuditReportRepository(this.context).GetExpiringStockAsync(
            Today, withinDays, [], 100, CancellationToken.None);

    private async Task AuditAsync(
        string action,
        LocationId? locationId,
        UserId? userId = null,
        string? reason = null)
    {
        this.context.AuditLog.Add(AuditLogEntry.Record(
            action,
            "test",
            Guid.CreateVersion7(),
            Now.AddDays(-1),
            CorrelationId.New(),
            userId ?? UserId.New(),
            "StoreManager",
            null,
            locationId,
            null,
            null,
            """{"before":"secret"}""",
            """{"after":"secret"}""",
            reason));

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task IncidentAsync(
        LocationId locationId,
        DateTimeOffset createdAt,
        DateTimeOffset? resolvedAt = null,
        int lines = 1,
        int unidentified = 0)
    {
        this.nextNumber++;

        QuarantineIncident incident = QuarantineIncident.Create(
            DocumentNumber.FromTrustedSource(
                FormattableString.Invariant($"QIN-2026-{this.nextNumber:D6}")),
            locationId,
            [
                .. Enumerable.Range(0, lines).Select(i => new QuarantineLineSpec(
                    FormattableString.Invariant($"999{this.nextNumber}{i}"), 4m, 10m, "Claimed goods")),
            ],
            UserId.New(),
            createdAt).Value;

        // Identify the lines that are meant to be recognised, through the domain's
        // own method — the report counts what is left unidentified, and a fixture
        // that set the column would not prove the count reads the right thing.
        for (int line = 1; line <= lines - unidentified; line++)
        {
            incident.Identify(line, this.product, null, registered: true, UserId.New(), createdAt, null)
                .IsSuccess.Should().BeTrue();
        }

        this.context.QuarantineIncidents.Add(incident);
        await this.context.SaveChangesAsync();

        if (resolvedAt is { } closed)
        {
            // Set through the tracker: closing an incident properly runs the whole
            // disposition workflow, which pins nothing extra about a report that
            // only reads when it opened and when it shut.
            QuarantineIncident tracked = await this.context.QuarantineIncidents
                .AsTracking().SingleAsync(i => i.Id == incident.Id);

            this.context.Entry(tracked).Property(i => i.ResolvedAtUtc).CurrentValue = closed;
            await this.context.SaveChangesAsync();
        }

        this.context.ChangeTracker.Clear();
    }

    private async Task ExpiringAsync(string lotNumber, DateOnly expiresOn, decimal quantity)
    {
        Batch batch = Batch.Create(
            this.product,
            SupplierId.New(),
            lotNumber,
            expiresOn.AddDays(-90),
            null,
            expiresOn,
            10m,
            UserId.New(),
            Now.AddDays(-90)).Value;

        this.context.Batches.Add(batch);
        await this.context.SaveChangesAsync();

        InventoryBalance balance = InventoryBalance.CreateEmpty(
            this.store, this.product, batch.Id, InventoryState.Available);

        this.context.InventoryBalances.Add(balance);
        this.context.Entry(balance).Property(b => b.Quantity).CurrentValue = quantity;
        this.context.Entry(balance).Property(b => b.AverageUnitCost).CurrentValue = 10m;
        this.context.Entry(balance).Property(b => b.TotalValue).CurrentValue = quantity * 10m;

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }
}
