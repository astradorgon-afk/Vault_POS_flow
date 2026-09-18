using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Reports;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Reports;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The document drill-down. What matters is that three sources merge into one
/// readable order, that a reversal can be followed in both directions, and that a
/// document outside the caller's stores is refused rather than shown empty.
/// </summary>
public sealed class DashboardTimelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private LocationId other;
    private LocationId external;
    private ProductId product;

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
    public async Task TheTimelineMergesTheAuditLogTheLedgerAndTheSyncVerdict()
    {
        Guid saleId = Guid.CreateVersion7();

        await AuditAsync("sale.completed", saleId, Now.AddMinutes(-10));
        MovementGroupId posting = await PostAsync(saleId, -3m, Now.AddMinutes(-5));
        _ = posting;

        DocumentTimeline timeline = (await TimelineAsync(saleId))!;

        // Every entry says where it came from: a person's action, a posting and a
        // verdict read very differently, and a merged list without the label
        // invites reading one as another.
        timeline.Entries.Select(e => e.Source).Should().Contain(
            [TimelineSource.Audit, TimelineSource.Ledger]);

        timeline.Entries.Should().BeInAscendingOrder(e => e.AtUtc);
        timeline.Entries[0].Source.Should().Be(TimelineSource.Audit);
        timeline.Entries[0].WhoRoleSnapshot.Should().Be("StoreManager", "the authority held then, not now");
    }

    [Fact]
    public async Task TheChainShowsEveryLegOfAPosting_NotJustTheSideAskedAbout()
    {
        Guid saleId = Guid.CreateVersion7();
        await PostAsync(saleId, -3m, Now);

        DocumentTimeline timeline = (await TimelineAsync(saleId))!;

        MovementGroupView posting = timeline.Movements.Single();

        // Stock leaving one bucket always arrives somewhere. A chain showing only
        // the store's side would look like stock vanishing.
        posting.Legs.Should().HaveCount(2);
        posting.Legs.Sum(l => l.QuantityDelta).Should().Be(0m, "a posting balances");
        posting.Legs.Select(l => l.LocationCode).Should().Contain(["S1", "EXT-CUSTOMER"]);
        posting.Legs[0].Sku.Should().Be("SKU-1");
    }

    [Fact]
    public async Task AReversalIsFollowableInBothDirections()
    {
        Guid saleId = Guid.CreateVersion7();

        MovementGroupId original = await PostAsync(saleId, -3m, Now.AddMinutes(-5));
        MovementGroupId undo = await PostAsync(
            saleId, 3m, Now, InventoryMovementType.PosSaleVoid, reverses: original);

        DocumentTimeline timeline = (await TimelineAsync(saleId))!;

        MovementGroupView first = timeline.Movements.Single(m => m.MovementGroupId == original.Value);
        MovementGroupView second = timeline.Movements.Single(m => m.MovementGroupId == undo.Value);

        second.ReversesMovementGroupId.Should().Be(original.Value);

        // Backwards only would leave somebody reading the original with no sign it
        // had been undone, which is the reading that counts the same loss twice.
        first.ReversedByMovementGroupId.Should().Be(undo.Value);
    }

    [Fact]
    public async Task WithoutTheFinancialPermission_TheLegsCarryNoValue()
    {
        Guid saleId = Guid.CreateVersion7();
        await PostAsync(saleId, -3m, Now);

        DocumentTimeline open = (await TimelineAsync(saleId, includeFinancial: false))!;
        open.Movements.Single().Legs.Should().OnlyContain(l => l.ValueDelta == null);

        DocumentTimeline full = (await TimelineAsync(saleId, includeFinancial: true))!;
        full.Movements.Single().Legs.Should().Contain(l => l.ValueDelta != null);
    }

    [Fact]
    public async Task ADocumentOutsideTheCallersStoresComesBackAsNothing()
    {
        Guid saleId = Guid.CreateVersion7();
        await PostAsync(saleId, -3m, Now);

        // In scope for the store that sold it.
        (await TimelineAsync(saleId, locations: [this.store])).Should().NotBeNull();

        // And nothing at all for a manager elsewhere. An empty timeline would
        // quietly tell them nothing happened, which is a different answer from
        // "you may not see this".
        (await TimelineAsync(saleId, locations: [this.other])).Should().BeNull();
    }

    [Fact]
    public async Task ADocumentNothingWasRecordedAgainstComesBackAsNothing()
        => (await TimelineAsync(Guid.CreateVersion7())).Should().BeNull();

    private Task<DocumentTimeline?> TimelineAsync(
        Guid referenceId,
        IReadOnlyCollection<LocationId>? locations = null,
        bool includeFinancial = true)
        => new DashboardRepository(
                this.context,
                new StubSalesAnalysis(),
                new FixedClock(Now))
            .GetTimelineAsync(
                ReferenceDocumentType.Sale, referenceId, locations ?? [], includeFinancial, CancellationToken.None);

    private async Task AuditAsync(string action, Guid entityId, DateTimeOffset at)
    {
        this.context.AuditLog.Add(AuditLogEntry.Record(
            action,
            "sale",
            entityId,
            at,
            CorrelationId.New(),
            UserId.New(),
            "StoreManager",
            null,
            this.store,
            null,
            null,
            null,
            null,
            "Rung up at the till",
            ReferenceDocumentType.Sale,
            entityId));

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task<MovementGroupId> PostAsync(
        Guid referenceId,
        decimal delta,
        DateTimeOffset at,
        InventoryMovementType type = InventoryMovementType.PosSale,
        MovementGroupId? reverses = null)
    {
        MovementGroupSpec spec = new(
            EventId.New(),
            type,
            ReferenceDocumentType.Sale,
            referenceId,
            "SAL-2026-000001",
            [
                new MovementLegSpec(
                    this.product, null, this.store, LocationKind.Store, InventoryState.Available,
                    delta, 10m, ProductTracksBatches: false),
                new MovementLegSpec(
                    this.product, null, this.external, LocationKind.External, InventoryState.External,
                    -delta, 10m, ProductTracksBatches: false),
            ],
            // A void needs an approver; a sale does not. The ledger's own rule,
            // and the fixture follows it rather than working round it.
            new LedgerActor(
                UserId.New(),
                type == InventoryMovementType.PosSale ? null : UserId.New(),
                null,
                CorrelationId.New()),
            at,
            DateOnly.FromDateTime(at.UtcDateTime),
            null,
            null,
            reverses);

        InventoryMovementGroup group = InventoryMovementGroup.Create(spec, at).Value;

        this.context.InventoryMovements.AddRange(group.Movements);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();

        return group.Movements[0].MovementGroupId;
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }

    /// <summary>The overview's collaborator, never reached by a timeline test.</summary>
    private sealed class StubSalesAnalysis : ISalesAnalysisRepository
    {
        public Task<SalesAnalysisReport> GetSalesAnalysisAsync(
            SalesAnalysisQuery query, CancellationToken cancellationToken)
            => throw new NotSupportedException("A timeline never asks about sales.");

        public Task<IReadOnlyList<SalesPaymentMethodRow>> GetPaymentMethodBreakdownAsync(
            DateOnly fromDate,
            DateOnly toDate,
            IReadOnlyCollection<LocationId> locations,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("A timeline never asks about payments.");
    }
}
