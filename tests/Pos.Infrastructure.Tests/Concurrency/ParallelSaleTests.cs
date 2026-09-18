using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Tests.Concurrency;

/// <summary>
/// Several tills selling the last of a product at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// This is the oversell case, and the one a shop notices: two cashiers scan the
/// same item off the same shelf within the same second, each reads a stock
/// figure that says there is enough, and both sales complete. The check that
/// says "there is enough" happens before the write, so on its own it proves
/// nothing — the guarantee has to come from the version token on the balance
/// projection, which makes the second writer re-read and decide again.
/// </para>
/// <para>
/// A store that runs AllowOfflineWithReview accepts a draw its shelf cannot
/// cover, by design: the goods went out at the till and refusing the record does
/// not put them back. That is a different policy and a different test; here the
/// policy prohibits negative stock, which is what a connected till runs under.
/// </para>
/// </remarks>
public sealed class ParallelSaleTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Store = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly LocationId Customer = LocationId.New();
    private static readonly ProductId Biscuits = ProductId.New();
    private static readonly UserId Cashier = UserId.New();

    private readonly ConcurrentDatabase database = new();

    /// <inheritdoc />
    public Task InitializeAsync() => this.database.CreateSchemaAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await this.database.DisposeAsync();

    [Fact]
    public async Task FourTillsSellingTheLastTen_SellTenAndRefuseTheRest()
    {
        const int tillCount = 4;
        const decimal perSale = 4m;
        const decimal onHand = 10m;

        await PostAsync(Receipt(onHand));

        // Every till starts from the same barrier, so all four read a projection
        // that says ten are available before any of them writes.
        using Barrier startGate = new(tillCount);

        Result<PostedMovementGroup>[] sales = await Task.WhenAll(
            Enumerable.Range(0, tillCount)
                .Select(_ => Task.Run(() => SellAsync(startGate, perSale)))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(60));

        // Ten on the shelf, four a sale: two tills get their stock and the other
        // two are refused. Not "most of the time" — the refusal is a decision
        // taken against the committed projection, not against what was read.
        sales.Count(s => s.IsSuccess).Should().Be(
            2,
            because: "only two sales of four fit in ten: "
                + string.Join(" | ", sales.SelectMany(s => s.Errors).Select(e => e.Code)));

        sales.Where(s => s.IsFailure)
            .Should().OnlyContain(s => s.Errors.Any(e => e.Code == "inventory.insufficient_stock"));

        await using ConcurrentDatabase.Session verify = await this.database.OpenAsync();

        InventoryBalance shelf = await verify.Context.InventoryBalances
            .SingleAsync(b => b.LocationId == Store && b.State == InventoryState.Available);

        shelf.Quantity.Should().Be(onHand - (2 * perSale));
        shelf.Quantity.Should().BeGreaterThanOrEqualTo(0m, because: "a prohibiting policy never lets a shelf go negative");

        // Two sales, two legs each, plus the receipt's two: the refused sales
        // left nothing behind.
        (await verify.Context.InventoryMovements.CountAsync()).Should().Be(6);
        (await verify.Context.InventoryMovements.SumAsync(m => m.QuantityDelta)).Should().Be(0m);

        // And what the customer side received is exactly what left the shelf.
        InventoryBalance sold = await verify.Context.InventoryBalances
            .SingleAsync(b => b.LocationId == Customer);

        sold.Quantity.Should().Be(2 * perSale);
    }

    [Fact]
    public async Task TillsSellingWithinStock_AllSucceed_AndTheShelfConverges()
    {
        const int tillCount = 6;
        const decimal perSale = 3m;
        const decimal onHand = 40m;

        await PostAsync(Receipt(onHand));

        using Barrier startGate = new(tillCount);

        Result<PostedMovementGroup>[] sales = await Task.WhenAll(
            Enumerable.Range(0, tillCount)
                .Select(_ => Task.Run(() => SellAsync(startGate, perSale)))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(60));

        // Contention is not refusal: when the stock is there, every till gets
        // its sale and the ledger's retry loop is what makes that true.
        sales.Should().OnlyContain(
            s => s.IsSuccess,
            because: string.Join("; ", sales.SelectMany(s => s.Errors).Select(e => e.Code)));

        await using ConcurrentDatabase.Session verify = await this.database.OpenAsync();

        InventoryBalance shelf = await verify.Context.InventoryBalances
            .SingleAsync(b => b.LocationId == Store && b.State == InventoryState.Available);

        shelf.Quantity.Should().Be(onHand - (tillCount * perSale));

        // Every apply bumped the version: CreateEmpty starts at 1, the receipt
        // is the second, and each sale one more. No write was silently lost.
        shelf.Version.Should().Be(tillCount + 2);

        (await verify.Context.InventoryMovements.Select(m => m.EventId).Distinct().CountAsync())
            .Should().Be(tillCount + 1);
    }

    /// <summary>One till: its own connection, its own ledger, one sale.</summary>
    /// <param name="startGate">Released once every till has opened.</param>
    /// <param name="quantity">The quantity sold.</param>
    /// <returns>What the ledger decided.</returns>
    private async Task<Result<PostedMovementGroup>> SellAsync(Barrier startGate, decimal quantity)
    {
        await using ConcurrentDatabase.Session session = await this.database.OpenAsync();
        InventoryLedger ledger = new(session.Context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        startGate.SignalAndWait();

        return await ledger.PostAsync(Sale(quantity), CancellationToken.None);
    }

    /// <summary>Posts one group and insists it succeeded.</summary>
    /// <param name="spec">The group.</param>
    /// <returns>A task that completes when the group is committed.</returns>
    private async Task PostAsync(MovementGroupSpec spec)
    {
        await using ConcurrentDatabase.Session session = await this.database.OpenAsync();
        InventoryLedger ledger = new(session.Context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        Result<PostedMovementGroup> posted = await ledger.PostAsync(spec, CancellationToken.None);
        posted.IsSuccess.Should().BeTrue(because: string.Join("; ", posted.Errors.Select(e => e.Code)));
    }

    private static MovementGroupSpec Receipt(decimal quantity) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-000001",
        [
            new MovementLegSpec(
                Biscuits, null, Supplier, LocationKind.External, InventoryState.External, -quantity, 10m, false),
            new MovementLegSpec(
                Biscuits, null, Store, LocationKind.Store, InventoryState.Available, quantity, 10m, false),
        ],
        new LedgerActor(Cashier, Cashier, null, CorrelationId.New()),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    // The legs a completed sale posts: off the store's Available bucket and on to
    // the external customer location, which is what CompleteSaleCommandHandler
    // builds for every sale item.
    private static MovementGroupSpec Sale(decimal quantity) => new(
        EventId.New(),
        InventoryMovementType.PosSale,
        ReferenceDocumentType.Sale,
        Guid.CreateVersion7(),
        "SAL-2026-000001",
        [
            new MovementLegSpec(
                Biscuits, null, Store, LocationKind.Store, InventoryState.Available, -quantity, 10m, false),
            new MovementLegSpec(
                Biscuits, null, Customer, LocationKind.External, InventoryState.External, quantity, 10m, false),
        ],
        new LedgerActor(Cashier, Cashier, null, CorrelationId.New()),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
