using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Ledger;

/// <summary>
/// End-to-end ledger behaviour against a real relational provider: mapping,
/// posting, the balance projection, idempotency and the append-only interceptor.
/// </summary>
/// <remarks>
/// Runs on SQLite in memory so it executes anywhere, including a machine with no
/// Docker. The PostgreSQL-specific guarantees (triggers, role grants) are proved
/// separately by PostgresLedgerGuardTests.
/// </remarks>
public sealed class LedgerPostingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly LocationId Customer = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private InventoryLedger _ledger = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        _context = new PosDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _ledger = new InventoryLedger(_context, new FixedClock(Now), new StrictLedgerPolicyProvider());
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SupplierReceipt_PostsLegsAndUpdatesTheBalance()
    {
        Result<PostedMovementGroup> posted = await _ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None);

        posted.IsSuccess.Should().BeTrue(because: Because(posted));
        await _context.SaveChangesAsync(CancellationToken.None);

        posted.Value.LegCount.Should().Be(2);
        posted.Value.WasDuplicate.Should().BeFalse();

        decimal available = await _ledger.GetQuantityAsync(
            Main, Coke, BatchId.Empty, InventoryState.Available, CancellationToken.None);

        available.Should().Be(100m);

        // The counterparty leg is what keeps the ledger closed.
        decimal external = await _ledger.GetQuantityAsync(
            Supplier, Coke, BatchId.Empty, InventoryState.External, CancellationToken.None);

        external.Should().Be(-100m);
    }

    [Fact]
    public async Task WholeLedger_SumsToZero()
    {
        await PostAndSave(Receipt(100m, 45m));
        await PostAndSave(Sale(3m, 45m, Main));

        decimal total = await _context.InventoryMovements.SumAsync(m => m.QuantityDelta, CancellationToken.None);

        total.Should().Be(0m, because: "every event is double-entry, so the whole ledger is closed");
    }

    [Fact]
    public async Task SameEventPostedTwice_PostsOnce_AndReportsDuplicate()
    {
        MovementGroupSpec spec = Receipt(100m, 45m);

        Result<PostedMovementGroup> first = await _ledger.PostAsync(spec, CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        Result<PostedMovementGroup> second = await _ledger.PostAsync(spec, CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        first.Value.WasDuplicate.Should().BeFalse();
        second.Value.WasDuplicate.Should().BeTrue();
        second.Value.MovementGroupId.Should().Be(first.Value.MovementGroupId);

        // The point of idempotency: a retried upload must not double the stock.
        (await _context.InventoryMovements.CountAsync(CancellationToken.None)).Should().Be(2);

        decimal available = await _ledger.GetQuantityAsync(
            Main, Coke, BatchId.Empty, InventoryState.Available, CancellationToken.None);

        available.Should().Be(100m);
    }

    [Fact]
    public async Task SaleBeyondAvailableStock_IsRefusedUnderTheDefaultPolicy()
    {
        await PostAndSave(Receipt(3m, 45m));

        Result<PostedMovementGroup> oversell = await _ledger.PostAsync(Sale(5m, 45m, Main), CancellationToken.None);

        oversell.IsFailure.Should().BeTrue();
        oversell.Error.Code.Should().Be("inventory.insufficient_stock");
        oversell.Error.Metadata.Should().Contain(new KeyValuePair<string, object?>("available", 3m));
    }

    [Fact]
    public async Task RepeatedDrawsOnOneBucket_AreCheckedInAggregate()
    {
        // Two legs of three each look affordable on their own against a balance
        // of five; together they are not.
        await PostAndSave(Receipt(5m, 45m));

        MovementGroupSpec spec = new(
            EventId.New(),
            InventoryMovementType.PosSale,
            ReferenceDocumentType.Sale,
            Guid.CreateVersion7(),
            "SAL-2026-D01-000009",
            [
                Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, -3m, 45m),
                Leg(Customer, LocationKind.External, InventoryState.External, +3m, 45m),
                Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, -3m, 45m),
                Leg(Customer, LocationKind.External, InventoryState.External, +3m, 45m),
            ],
            NewActor(),
            Now,
            DateOnly.FromDateTime(Now.UtcDateTime));

        Result<PostedMovementGroup> result = await _ledger.PostAsync(spec, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("inventory.insufficient_stock");
    }

    [Fact]
    public async Task Transfer_MovesStockWithoutCreatingOrDestroyingAny()
    {
        await PostAndSave(Receipt(100m, 45m));

        await PostAndSave(new MovementGroupSpec(
            EventId.New(),
            InventoryMovementType.TransferDispatch,
            ReferenceDocumentType.TransferShipment,
            Guid.CreateVersion7(),
            "SHP-2026-000001",
            [
                Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, -40m, 45m),
                Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, +40m, 45m),
            ],
            NewActor(),
            Now,
            DateOnly.FromDateTime(Now.UtcDateTime)));

        (await Qty(Main, InventoryState.Available)).Should().Be(60m);
        (await Qty(Main, InventoryState.InTransit)).Should().Be(40m);
        (await Qty(Store1, InventoryState.Available)).Should().Be(0m, because: "receipt has not happened yet");

        await PostAndSave(new MovementGroupSpec(
            EventId.New(),
            InventoryMovementType.TransferReceipt,
            ReferenceDocumentType.TransferReceipt,
            Guid.CreateVersion7(),
            "TRC-2026-000001",
            [
                Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, -40m, 45m),
                Leg(Store1, LocationKind.Store, InventoryState.Available, +38m, 45m),
                Leg(Main, LocationKind.MainWarehouse, InventoryState.TransitVariance, +2m, 45m),
            ],
            NewActor(),
            Now,
            DateOnly.FromDateTime(Now.UtcDateTime)));

        (await Qty(Main, InventoryState.InTransit)).Should().Be(0m);
        (await Qty(Store1, InventoryState.Available)).Should().Be(38m);
        (await Qty(Main, InventoryState.TransitVariance)).Should().Be(2m, because: "the shortfall stays on the books");

        decimal businessTotal =
            await Qty(Main, InventoryState.Available)
            + await Qty(Main, InventoryState.TransitVariance)
            + await Qty(Store1, InventoryState.Available);

        businessTotal.Should().Be(100m, because: "a transfer moves stock, it does not change how much exists");
    }

    [Fact]
    public async Task ModifyingAPostedMovement_IsRejectedByTheInterceptor()
    {
        await PostAndSave(Receipt(10m, 45m));

        InventoryMovement movement = await _context.InventoryMovements
            .AsTracking()
            .FirstAsync(m => m.LocationId == Main, CancellationToken.None);

        // Reflection is the only way to reach the value at all, which is itself
        // the point: there is no setter for application code to call.
        typeof(InventoryMovement)
            .GetProperty(nameof(InventoryMovement.QuantityDelta))!
            .SetValue(movement, 9999m);

        _context.Entry(movement).Property(nameof(InventoryMovement.QuantityDelta)).IsModified = true;

        Func<Task> act = async () => await _context.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AppendOnlyViolationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task DeletingAPostedMovement_IsRejectedByTheInterceptor()
    {
        await PostAndSave(Receipt(10m, 45m));

        InventoryMovement movement = await _context.InventoryMovements
            .AsTracking()
            .FirstAsync(CancellationToken.None);

        _context.InventoryMovements.Remove(movement);

        Func<Task> act = async () => await _context.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AppendOnlyViolationException>();
    }

    [Fact]
    public async Task BalanceIsAlwaysDerivableFromTheLedger()
    {
        await PostAndSave(Receipt(100m, 45m));
        await PostAndSave(Sale(7m, 45m, Main));
        await PostAndSave(Sale(3m, 45m, Main));

        decimal fromLedger = await _context.InventoryMovements
            .Where(m => m.LocationId == Main && m.ProductId == Coke && m.State == InventoryState.Available)
            .SumAsync(m => m.QuantityDelta, CancellationToken.None);

        decimal fromProjection = await Qty(Main, InventoryState.Available);

        fromProjection.Should().Be(fromLedger);
        fromProjection.Should().Be(90m);
    }

    private async Task<decimal> Qty(LocationId location, InventoryState state)
        => await _ledger.GetQuantityAsync(location, Coke, BatchId.Empty, state, CancellationToken.None);

    private async Task PostAndSave(MovementGroupSpec spec)
    {
        Result<PostedMovementGroup> result = await _ledger.PostAsync(spec, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: Because(result));
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    private static string Because(Result<PostedMovementGroup> result)
        => string.Join("; ", result.Errors.Select(e => e.Code));

    private static LedgerActor NewActor() => new(Actor, Actor, null, CorrelationId.New());

    private static MovementLegSpec Leg(
        LocationId location,
        LocationKind kind,
        InventoryState state,
        decimal quantity,
        decimal unitCost)
        => new(Coke, null, location, kind, state, quantity, unitCost, false);

    private static MovementGroupSpec Receipt(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-000001",
        [
            Leg(Supplier, LocationKind.External, InventoryState.External, -quantity, unitCost),
            Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, +quantity, unitCost),
        ],
        NewActor(),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private static MovementGroupSpec Sale(decimal quantity, decimal unitCost, LocationId location) => new(
        EventId.New(),
        InventoryMovementType.PosSale,
        ReferenceDocumentType.Sale,
        Guid.CreateVersion7(),
        "SAL-2026-D01-000001",
        [
            Leg(location, location == Main ? LocationKind.MainWarehouse : LocationKind.Store,
                InventoryState.Available, -quantity, unitCost),
            Leg(Customer, LocationKind.External, InventoryState.External, +quantity, unitCost),
        ],
        NewActor(),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
