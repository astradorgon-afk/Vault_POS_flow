using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Tests.Concurrency;

/// <summary>
/// What happens when two people act on the same transfer at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// A transfer passes through several pairs of hands by design — a picker readies
/// it, a dispatcher ships it, a receiver books it in — and the same shipment can
/// sit open on two screens. The aggregate's guards (a transfer that already
/// carries a number is never dispatched again; a receipt needs the Dispatched
/// state) are in-memory and see only the copy that context loaded, so two
/// contexts both pass them.
/// </para>
/// <para>
/// What actually stops the second write is the custody chain: every step appends
/// a custody event, and (transfer, sequence) is unique. Two writers that read the
/// same transfer compute the same next sequence, so the loser collides at the
/// database instead of overwriting the winner. The transfer row itself carries no
/// version token, which makes that index the whole guard — and the reason these
/// tests exist, because nothing else would notice if it were relaxed.
/// </para>
/// </remarks>
public sealed class TransferRaceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentDatabase database = new();

    private LocationId warehouse;
    private LocationId store;
    private ProductId product;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await this.database.CreateSchemaAsync();

        await using ConcurrentDatabase.Session session = await this.database.OpenAsync();

        OrganizationId organization = OrganizationId.New();
        Location main = Location.Create(
            organization, "WH", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila").Value;
        Location one = Location.Create(organization, "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        session.Context.Locations.AddRange(main, one);

        ProductCategory grocery = ProductCategory.Create("GROC", "Grocery", null, 1).Value;
        session.Context.Categories.Add(grocery);

        UnitOfMeasure piece = UnitOfMeasure.Create("PC", "Piece", 0).Value;
        session.Context.UnitsOfMeasure.Add(piece);
        await session.Context.SaveChangesAsync();

        Product biscuits = Product.Create("SKU-1", "Biscuits", grocery.Id, piece.Id, UserId.New()).Value;
        session.Context.Products.Add(biscuits);
        await session.Context.SaveChangesAsync();

        this.warehouse = main.Id;
        this.store = one.Id;
        this.product = biscuits.Id;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await this.database.DisposeAsync();

    [Fact]
    public async Task TwoDispatchersShippingTheSameTransfer_OnlyOneShipmentLands()
    {
        TransferOrderId transferId = await ReadyTransferAsync();

        Attempt[] attempts = await RaceAsync(
            (transfer, gate) =>
            {
                gate.SignalAndWait();

                return transfer.Dispatch(
                    Now.AddMinutes(6),
                    DocumentNumber.FromTrustedSource(Number(transfer, "TRF")),
                    TransferShipmentId.New(),
                    Number(transfer, "SHP"),
                    UserId.New());
            },
            transferId);

        attempts.Count(a => a.Saved).Should().Be(
            1,
            because: "the goods can only leave the shelf once, however many screens the transfer is open on: "
                + Outcomes(attempts));

        // The loser is refused by the database, not by the aggregate: both
        // dispatchers read a Ready, unnumbered transfer and both got past every
        // in-memory guard.
        attempts.Single(a => !a.Saved).Outcome.Should().StartWith("save:");

        await using ConcurrentDatabase.Session verify = await this.database.OpenAsync();

        Transfer stored = await verify.Context.Transfers
            .Include(t => t.CustodyEvents)
            .SingleAsync(t => t.Id == transferId);

        stored.Status.Should().Be(TransferStatus.Dispatched);
        stored.Number.Should().Be(attempts.Single(a => a.Saved).Number);

        // One dispatch, one custody event: the loser left nothing behind.
        stored.CustodyEvents.Count(e => e.Kind == TransferCustodyEventKind.Dispatched).Should().Be(1);
    }

    [Fact]
    public async Task TwoReceiversBookingInTheSameShipment_OnlyOneReceiptLands()
    {
        TransferOrderId transferId = await ReadyTransferAsync();

        await using (ConcurrentDatabase.Session dispatcher = await this.database.OpenAsync())
        {
            Transfer transfer = await LoadAsync(dispatcher, transferId);

            transfer.Dispatch(
                Now.AddMinutes(6),
                DocumentNumber.FromTrustedSource("TRF-2026-000001"),
                TransferShipmentId.New(),
                "SHP-2026-000001",
                UserId.New()).IsSuccess.Should().BeTrue();

            await dispatcher.Context.SaveChangesAsync();
        }

        Attempt[] attempts = await RaceAsync(
            (transfer, gate) =>
            {
                gate.SignalAndWait();

                return transfer.Receive(
                    UserId.New(),
                    Now.AddDays(1),
                    TransferReceiptId.New(),
                    Number(transfer, "TRC"),
                    [new TransferReceiveAllocationSpec(1, null, 10m, 0m)]);
            },
            transferId);

        attempts.Count(a => a.Saved).Should().Be(
            1,
            because: "stock cannot be booked into the store twice: " + Outcomes(attempts));

        await using ConcurrentDatabase.Session verify = await this.database.OpenAsync();

        Transfer stored = await verify.Context.Transfers
            .Include(t => t.CustodyEvents)
            .SingleAsync(t => t.Id == transferId);

        stored.Status.Should().Be(TransferStatus.Received);
        stored.CustodyEvents.Count(e => e.Kind == TransferCustodyEventKind.Received).Should().Be(1);
    }

    /// <summary>
    /// Runs one step of the workflow from two writers at once, each on its own
    /// connection, both released from the same barrier once both have read.
    /// </summary>
    /// <param name="step">The step, given the loaded transfer and the barrier.</param>
    /// <param name="transferId">The transfer both writers act on.</param>
    /// <returns>What each writer did.</returns>
    private async Task<Attempt[]> RaceAsync(Func<Transfer, Barrier, Result> step, TransferOrderId transferId)
    {
        using Barrier startGate = new(2);

        Task<Attempt>[] writers =
        [
            Task.Run(() => AttemptAsync(step, startGate, transferId)),
            Task.Run(() => AttemptAsync(step, startGate, transferId)),
        ];

        return await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>One writer: load the aggregate the way the repository does, take
    /// the step, save.</summary>
    /// <param name="step">The step to take.</param>
    /// <param name="startGate">Released once every writer has read.</param>
    /// <param name="transferId">The transfer to act on.</param>
    /// <returns>What the attempt did.</returns>
    private async Task<Attempt> AttemptAsync(
        Func<Transfer, Barrier, Result> step,
        Barrier startGate,
        TransferOrderId transferId)
    {
        await using ConcurrentDatabase.Session session = await this.database.OpenAsync();

        Transfer transfer = await LoadAsync(session, transferId);

        Result taken = step(transfer, startGate);

        if (taken.IsFailure)
        {
            return new Attempt(false, transfer.Number, "refused: " + string.Join(";", taken.Errors.Select(e => e.Code)));
        }

        try
        {
            await session.Context.SaveChangesAsync();
            return new Attempt(true, transfer.Number, "saved");
        }
        catch (DbUpdateException ex)
        {
            return new Attempt(false, transfer.Number, "save: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>Loads the aggregate exactly as TransferRepository does.</summary>
    /// <param name="session">The writer's session.</param>
    /// <param name="transferId">The transfer to load.</param>
    /// <returns>The tracked aggregate.</returns>
    private static Task<Transfer> LoadAsync(ConcurrentDatabase.Session session, TransferOrderId transferId)
        => session.Context.Transfers
            .AsTracking()
            .Include(t => t.Lines)
            .Include(t => t.Allocations)
            .Include(t => t.Discrepancies)
            .Include(t => t.CustodyEvents)
            .SingleAsync(t => t.Id == transferId);

    /// <summary>
    /// A document number unique to this writer, so that a number collision can
    /// never be what decides the race.
    /// </summary>
    /// <param name="transfer">The transfer, whose tracked instance differs per writer.</param>
    /// <param name="prefix">The document prefix.</param>
    /// <returns>The number.</returns>
    private static string Number(Transfer transfer, string prefix)
        => FormattableString.Invariant(
            $"{prefix}-2026-{Environment.CurrentManagedThreadId:D6}-{transfer.GetHashCode():X8}");

    private static string Outcomes(IEnumerable<Attempt> attempts)
        => string.Join(" | ", attempts.Select(a => a.Outcome));

    /// <summary>
    /// Stages a transfer through its workflow up to Ready, the state a dispatcher
    /// acts on.
    /// </summary>
    /// <returns>The transfer's id.</returns>
    private async Task<TransferOrderId> ReadyTransferAsync()
    {
        await using ConcurrentDatabase.Session session = await this.database.OpenAsync();

        UserId actor = UserId.New();

        Transfer transfer = Transfer.Create(
            this.warehouse, this.store, [new TransferLineSpec(this.product, 10m)], actor, Now).Value;

        transfer.Submit(actor, Now.AddMinutes(1)).IsSuccess.Should().BeTrue();
        transfer.Review(actor, Now.AddMinutes(2), null).IsSuccess.Should().BeTrue();
        transfer.Approve(actor, Now.AddMinutes(3)).IsSuccess.Should().BeTrue();
        transfer.Pick(
            actor,
            Now.AddMinutes(4),
            [new TransferPickAllocationSpec(1, null, 10m, 10m)]).IsSuccess.Should().BeTrue();
        transfer.Ready(Now.AddMinutes(5)).IsSuccess.Should().BeTrue();

        session.Context.Transfers.Add(transfer);
        await session.Context.SaveChangesAsync();

        return transfer.Id;
    }

    /// <param name="Saved">Whether this writer's step landed.</param>
    /// <param name="Number">The transfer number it saw or assigned.</param>
    /// <param name="Outcome">What happened, for the failure message.</param>
    private sealed record Attempt(bool Saved, string Number, string Outcome);
}
