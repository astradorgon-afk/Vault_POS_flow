using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Reports;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Reports;

/// <summary>
/// The transfer and distribution reports of ROADMAP §Phase 15. Scope is the risk
/// here: a transfer is as much the receiving store's business as the sending
/// one's, and filtering on the source alone would hide every incoming shipment
/// from the people waiting for it.
/// </summary>
public sealed class TransferReportRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId warehouse;
    private LocationId storeOne;
    private LocationId storeTwo;
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
        Location main = Location.Create(
            organization, "WH", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila").Value;
        Location one = Location.Create(organization, "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        Location two = Location.Create(organization, "S2", "Store Two", LocationKind.Store, "Asia/Manila").Value;
        this.context.Locations.AddRange(main, one, two);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        this.context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        this.context.UnitsOfMeasure.Add(piece);
        await this.context.SaveChangesAsync();

        this.warehouse = main.Id;
        this.storeOne = one.Id;
        this.storeTwo = two.Id;

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
    public async Task ATransferIsVisibleToBothEnds()
    {
        await TransferAsync(this.warehouse, this.storeOne, requested: 10m);

        // The sending warehouse sees it.
        (await TransfersAsync(locations: [this.warehouse])).Rows.Should().ContainSingle();

        // And so does the receiving store, which is the half a source-only filter
        // would have hidden from the people waiting for the stock.
        (await TransfersAsync(locations: [this.storeOne])).Rows.Should().ContainSingle();

        // A store at neither end sees nothing.
        (await TransfersAsync(locations: [this.storeTwo])).Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TheThreeQuantitiesAreThreeColumns()
    {
        await TransferAsync(
            this.warehouse, this.storeOne, requested: 10m, picked: 8m, received: 6m, damaged: 1m);

        TransferReportRow row = (await TransfersAsync()).Rows.Single();

        // Asked for, actually sent, actually counted. Collapsing them is how a
        // shipment two cartons short comes to look complete.
        row.RequestedQuantity.Should().Be(10m);
        row.DispatchedQuantity.Should().Be(8m);
        row.ReceivedQuantity.Should().Be(6m);
        row.DamagedQuantity.Should().Be(1m);
        row.LineCount.Should().Be(1);
    }

    [Fact]
    public async Task StockStillInTransitCountsItsDays_AndStockReceivedDoesNot()
    {
        await TransferAsync(
            this.warehouse, this.storeOne, requested: 5m, picked: 5m, dispatchedAt: Now.AddDays(-14));
        await TransferAsync(
            this.warehouse, this.storeTwo, requested: 5m, picked: 5m, received: 5m,
            dispatchedAt: Now.AddDays(-20));

        TransferReport report = await TransfersAsync();

        // Longest in flight first: what is stuck is what somebody opens this to
        // find, and a transfer already received is not stuck at all.
        report.Rows[0].DestinationCode.Should().Be("S1");
        report.Rows[0].DaysInFlight.Should().Be(14);
        report.Rows[1].DaysInFlight.Should().BeNull("it arrived; there is nothing in flight to count");
    }

    [Fact]
    public async Task OpenOnly_LeavesOutWhatHasArrived()
    {
        await TransferAsync(this.warehouse, this.storeOne, requested: 5m, picked: 5m);
        await TransferAsync(this.warehouse, this.storeTwo, requested: 5m, picked: 5m, received: 5m);

        (await TransfersAsync(openOnly: true)).Rows.Should().ContainSingle()
            .Which.DestinationCode.Should().Be("S1");
    }

    [Fact]
    public async Task Distribution_CountsWhatLeft_AndShowsWhatHasNotArrived()
    {
        await TransferAsync(this.warehouse, this.storeOne, requested: 10m, picked: 10m, received: 7m, damaged: 1m);
        await TransferAsync(this.warehouse, this.storeOne, requested: 4m, picked: 4m);
        await TransferAsync(this.warehouse, this.storeTwo, requested: 2m, picked: 2m, received: 2m);

        List<DistributionLaneRow> lanes = [.. await DistributionAsync()];

        lanes.Should().HaveCount(2);

        DistributionLaneRow busiest = lanes[0];
        busiest.DestinationCode.Should().Be("S1");
        busiest.TransferCount.Should().Be(2);
        busiest.DispatchedQuantity.Should().Be(14m);
        busiest.ReceivedQuantity.Should().Be(7m);
        busiest.DamagedQuantity.Should().Be(1m);

        // Ten sent, seven counted, one damaged, four still out there.
        busiest.ShortfallQuantity.Should().Be(6m);
    }

    [Fact]
    public async Task Distribution_IgnoresATransferThatNeverLeft()
    {
        await TransferAsync(this.warehouse, this.storeOne, requested: 10m);

        // Counting it would make a lane look busy on stock that is still sitting
        // in the warehouse.
        (await DistributionAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Distribution_CountsAgainstWhenTheStockLeft_NotWhenItWasAskedFor()
    {
        await TransferAsync(
            this.warehouse, this.storeOne, requested: 5m, picked: 5m,
            createdAt: Now.AddDays(-40), dispatchedAt: Now.AddDays(-2));

        // Raised last month, sent this week. The question a distribution report
        // answers is what the warehouse sent, not what it was asked for.
        (await DistributionAsync(from: Now.AddDays(-7))).Should().ContainSingle();
        (await DistributionAsync(from: Now.AddDays(-1))).Should().BeEmpty();
    }

    private Task<TransferReport> TransfersAsync(
        IReadOnlyCollection<LocationId>? locations = null,
        bool openOnly = false,
        int limit = 100)
        => new TransferReportRepository(this.context).GetTransfersAsync(
            Now.AddDays(-90), Now, locations ?? [], openOnly, limit, CancellationToken.None);

    private Task<IReadOnlyList<DistributionLaneRow>> DistributionAsync(DateTimeOffset? from = null)
        => new TransferReportRepository(this.context).GetDistributionAsync(
            from ?? Now.AddDays(-90), Now, [], CancellationToken.None);

    /// <summary>
    /// Drives a transfer through as much of its real workflow as the test needs.
    /// </summary>
    /// <remarks>
    /// Through the aggregate's own methods rather than by setting columns: the
    /// report reads what the workflow wrote, and a fixture that wrote those
    /// columns directly could pass against a lifecycle the domain would refuse.
    /// </remarks>
    private async Task TransferAsync(
        LocationId source,
        LocationId destination,
        decimal requested,
        decimal? picked = null,
        decimal? received = null,
        decimal damaged = 0m,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? dispatchedAt = null)
    {
        UserId actor = UserId.New();
        DateTimeOffset created = createdAt ?? Now.AddDays(-30);

        Transfer transfer = Transfer.Create(
            source, destination, [new TransferLineSpec(this.product, requested)], actor, created).Value;

        if (picked is { } pickedQuantity)
        {
            transfer.Submit(actor, created.AddMinutes(1)).IsSuccess.Should().BeTrue();
            transfer.Review(actor, created.AddMinutes(2), null).IsSuccess.Should().BeTrue();
            transfer.Approve(actor, created.AddMinutes(3)).IsSuccess.Should().BeTrue();

            transfer.Pick(
                actor,
                created.AddMinutes(4),
                [new TransferPickAllocationSpec(1, null, pickedQuantity, 10m)]).IsSuccess.Should().BeTrue();

            transfer.Ready(created.AddMinutes(5)).IsSuccess.Should().BeTrue();

            this.nextNumber++;
            DateTimeOffset dispatched = dispatchedAt ?? created.AddMinutes(6);

            transfer.Dispatch(
                dispatched,
                DocumentNumber.FromTrustedSource(
                    FormattableString.Invariant($"TRF-2026-{this.nextNumber:D6}")),
                TransferShipmentId.New(),
                FormattableString.Invariant($"SHP-2026-{this.nextNumber:D6}"),
                actor).IsSuccess.Should().BeTrue();

            if (received is { } receivedQuantity)
            {
                transfer.Receive(
                    actor,
                    dispatched.AddDays(1),
                    TransferReceiptId.New(),
                    FormattableString.Invariant($"TRC-2026-{this.nextNumber:D6}"),
                    [new TransferReceiveAllocationSpec(1, null, receivedQuantity, damaged)])
                    .IsSuccess.Should().BeTrue();
            }
        }

        this.context.Transfers.Add(transfer);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }
}
