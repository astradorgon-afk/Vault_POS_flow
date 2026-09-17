using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The same ledger, against the device's encrypted store. Not a second
/// implementation: <see cref="InventoryLedger"/> is the type the server runs,
/// pointed at <see cref="PosDeviceDbContext"/> through
/// <see cref="ILedgerStore"/> (ADR-0008). A second ledger would drift, and the
/// drift would be invisible until inventory disagreed.
/// </summary>
public sealed class DeviceLedgerTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;
    private static readonly LocationId Store = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly LocationId Customer = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    [Fact]
    public async Task AReceiptPostsBothLegs_AndTheBucketHoldsTheStock()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        Result<PostedMovementGroup> posted = await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None);

        posted.IsSuccess.Should().BeTrue(Because(posted));
        await host.SaveAsync();

        posted.Value.LegCount.Should().Be(2);

        (await host.QuantityAsync(Store, InventoryState.Available)).Should().Be(100m);
        (await host.QuantityAsync(Supplier, InventoryState.External))
            .Should().Be(-100m, "the counterparty leg is what keeps the ledger closed");
    }

    [Fact]
    public async Task AnOfflineSaleDrawsTheStockDown()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        (await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        Result<PostedMovementGroup> sale = await host.Ledger.PostAsync(Sale(12m, 45m), CancellationToken.None);

        sale.IsSuccess.Should().BeTrue(Because(sale));
        await host.SaveAsync();

        (await host.QuantityAsync(Store, InventoryState.Available)).Should().Be(88m);
        (await host.QuantityAsync(Customer, InventoryState.External)).Should().Be(12m);
    }

    [Fact]
    public async Task TheLedgerRefusesToSellStockTheDeviceDoesNotHave()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        Result<PostedMovementGroup> sale = await host.Ledger.PostAsync(Sale(3m, 45m), CancellationToken.None);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be(
            "inventory.insufficient_stock",
            "an outage does not relax the negative-stock policy");
    }

    [Fact]
    public async Task ARepeatedEventReplaysRatherThanPostingTwice()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        MovementGroupSpec receipt = Receipt(100m, 45m);

        (await host.Ledger.PostAsync(receipt, CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        Result<PostedMovementGroup> replayed = await host.Ledger.PostAsync(receipt, CancellationToken.None);

        replayed.IsSuccess.Should().BeTrue(Because(replayed));
        replayed.Value.WasDuplicate.Should().BeTrue("the event identifier is what makes a retry safe");

        (await host.QuantityAsync(Store, InventoryState.Available))
            .Should().Be(100m, "not 200: the second post replayed the first outcome");
    }

    [Fact]
    public async Task WritingAQuantityDirectlyIsRefusedByTheDatabase()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        (await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        // The one rule, tested where it would actually be broken: raw SQL on a
        // keyed connection, with no movements behind it.
        Func<Task> setStock = () => host.ExecuteRawAsync(
            "UPDATE local_inventory_balance SET quantity = '999.000' WHERE location_id = $location",
            StoredId(Store));

        // Outside a device context the guard function does not exist, so the
        // statement cannot even be prepared. Either way the write is refused —
        // that is what failing closed means.
        (await setStock.Should().ThrowAsync<SqliteException>())
            .Which.Message.Should().ContainAny("only change through the inventory ledger", "vf_ledger_writer");

        (await host.QuantityAsync(Store, InventoryState.Available)).Should().Be(100m);
    }

    [Fact]
    public async Task DeletingABalanceIsRefused()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        (await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        Func<Task> discard = () => host.ExecuteRawAsync(
            "DELETE FROM local_inventory_balance WHERE location_id = $location", StoredId(Store));

        (await discard.Should().ThrowAsync<SqliteException>())
            .WithMessage("*not deletable*");
    }

    [Fact]
    public async Task RewritingHistoryIsRefused()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        (await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        Func<Task> edit = () => host.ExecuteRawAsync(
            "UPDATE local_inventory_movement SET quantity_delta = '5.000' WHERE location_id = $location",
            StoredId(Store));

        (await edit.Should().ThrowAsync<SqliteException>()).WithMessage("*append-only*");

        Func<Task> erase = () => host.ExecuteRawAsync(
            "DELETE FROM local_inventory_movement WHERE location_id = $location", StoredId(Store));

        (await erase.Should().ThrowAsync<SqliteException>()).WithMessage("*append-only*");
    }

    [Fact]
    public async Task TheLedgerIsClosed_EveryGroupSummingToZero()
    {
        await using LedgerHost host = await LedgerHost.StartAsync();

        (await host.Ledger.PostAsync(Receipt(100m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();
        (await host.Ledger.PostAsync(Sale(12m, 45m), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await host.SaveAsync();

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        List<decimal> perGroup = await context.InventoryMovements
            .AsNoTracking()
            .GroupBy(m => m.MovementGroupId)
            .Select(g => g.Sum(m => m.QuantityDelta))
            .ToListAsync(CancellationToken.None);

        perGroup.Should().HaveCount(2).And.OnlyContain(sum => sum == 0m);
    }

    /// <summary>A location identifier as the device file stores it: upper-case text.</summary>
    private static string StoredId(LocationId id) => id.Value.ToString().ToUpperInvariant();

    private static string Because(Result<PostedMovementGroup> result)
        => result.IsFailure ? result.Error.ToString() : string.Empty;

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
            Leg(Store, LocationKind.Store, InventoryState.Available, +quantity, unitCost),
        ],
        NewActor(),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private static MovementGroupSpec Sale(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.PosSale,
        ReferenceDocumentType.Sale,
        Guid.CreateVersion7(),
        "SAL-2026-D03-000001",
        [
            Leg(Store, LocationKind.Store, InventoryState.Available, -quantity, unitCost),
            Leg(Customer, LocationKind.External, InventoryState.External, +quantity, unitCost),
        ],
        NewActor(),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    /// <summary>A device store with the server's ledger pointed at it.</summary>
    private sealed class LedgerHost : IAsyncDisposable
    {
        public TemporaryDeviceDatabase Database { get; private set; } = null!;

        public PosDeviceDbContext Context { get; private set; } = null!;

        public InventoryLedger Ledger { get; private set; } = null!;

        public static async Task<LedgerHost> StartAsync()
        {
            LedgerHost host = new() { Database = await TemporaryDeviceDatabase.CreateAsync() };
            host.Context = await host.Database.OpenContextAsync();
            host.Ledger = new InventoryLedger(
                host.Context, host.Database.Clock, new DeviceLedgerPolicyProvider(host.Context));

            return host;
        }

        public Task<int> SaveAsync() => Context.SaveChangesAsync(CancellationToken.None);

        public Task<decimal> QuantityAsync(LocationId location, InventoryState state)
            => Ledger.GetQuantityAsync(location, Coke, BatchId.Empty, state, CancellationToken.None);

        /// <summary>Runs raw SQL on a keyed connection, as tooling would.</summary>
        /// <remarks>
        /// The statements are test constants written above; the only value that
        /// varies is parameterized. Attacking the ledger from raw SQL is the
        /// point of these tests, so the analyzer's concern is the subject rather
        /// than a defect.
        /// </remarks>
        public async Task ExecuteRawAsync(string sql, string locationId)
        {
            await using SqliteConnection connection = new(Database.EncryptedConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
            command.CommandText = sql;
#pragma warning restore CA2100
            command.Parameters.AddWithValue("$location", locationId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
