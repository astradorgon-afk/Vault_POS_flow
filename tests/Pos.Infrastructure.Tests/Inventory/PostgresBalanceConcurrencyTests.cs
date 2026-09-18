using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;
using Testcontainers.PostgreSql;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// The same concurrency and rebuild guarantees as the SQLite suites, on a real
/// PostgreSQL engine where the delete guard trigger is armed and the deferred
/// balance guard validates every transaction at commit.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL is where optimistic concurrency matters most: this is the engine
/// two nodes of the sync processor would both write to. The version token is
/// enforced by EF on every provider, so what is proven here is that the ledger's
/// retry loop converges under genuine engine-level contention, including the
/// primary-key race on a bucket both writers see as missing.
/// </para>
/// <para>
/// The suite skips itself when no Docker daemon is reachable, like the ledger
/// guard suite. CI always has Docker, so the guards are always proven there.
/// </para>
/// </remarks>
[Collection("postgres")]
public sealed class PostgresBalanceConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private PostgreSqlContainer? _container;
    private PosDbContext? _context;
    private InventoryLedger? _ledger;
    private DbContextOptions<PosDbContext> _options = null!;

    private bool DockerAvailable => _container is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("vaultflow_test")
                .WithUsername("vaultflow")
                .WithPassword("vaultflow-test-only")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _container = null;
            return;
        }

        _options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        _context = new PosDbContext(_options);
        await _context.Database.MigrateAsync();

        _ledger = new InventoryLedger(_context, new FixedClock(Now), new StrictLedgerPolicyProvider());
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task ParallelReceipts_ToAnEmptyBucket_AllSucceed_AndConverge()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        const int writerCount = 4;
        const decimal perWriter = 10m;
        const decimal expected = writerCount * perWriter;

        using Barrier startGate = new(writerCount);

        Result<PostedMovementGroup>[] results = await Task.WhenAll(
            Enumerable.Range(0, writerCount)
                .Select(_ => Task.Run(() => PostInOwnContextAsync(startGate, perWriter)))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(120));

        results.Should().OnlyContain(
            r => r.IsSuccess,
            because: string.Join("; ", results.SelectMany(r => r.Errors).Select(e => e.Code)));

        await using PosDbContext verify = new(_options);

        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main))
            .Quantity.Should().Be(expected);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Supplier))
            .Quantity.Should().Be(-expected);

        (await verify.InventoryMovements.CountAsync()).Should().Be(writerCount * 2);
        (await verify.InventoryMovements.SumAsync(m => m.QuantityDelta)).Should().Be(0m);
        (await verify.InventoryMovements.Select(m => m.EventId).Distinct().CountAsync()).Should().Be(writerCount);
    }

    [SkippableFact]
    public async Task ParallelSales_AgainstOneBucket_AllSucceed_AndConverge()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        const int writerCount = 4;
        const decimal openingQuantity = 100m;
        const decimal perSale = 5m;

        await PostAndCommitAsync(Receipt(openingQuantity, 45m));

        using Barrier startGate = new(writerCount);
        Result<PostedMovementGroup>[] results = await Task.WhenAll(
            Enumerable.Range(0, writerCount)
                .Select(_ => Task.Run(() => PostInOwnContextAsync(
                    startGate,
                    Sale(perSale, 45m))))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(120));

        results.Should().OnlyContain(
            result => result.IsSuccess,
            because: string.Join("; ", results.SelectMany(result => result.Errors).Select(error => error.Code)));

        await using PosDbContext verify = new(_options);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main && b.State == InventoryState.Available))
            .Quantity.Should().Be(openingQuantity - writerCount * perSale);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Supplier && b.State == InventoryState.External))
            .Quantity.Should().Be(-openingQuantity + writerCount * perSale);
        (await verify.InventoryMovements.CountAsync()).Should().Be(2 + writerCount * 2);
    }

    [SkippableFact]
    public async Task ParallelTransferDispatches_FromOneBucket_AllSucceed_AndConverge()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        const int writerCount = 4;
        const decimal openingQuantity = 100m;
        const decimal perTransfer = 10m;

        await PostAndCommitAsync(Receipt(openingQuantity, 45m));

        using Barrier startGate = new(writerCount);
        Result<PostedMovementGroup>[] results = await Task.WhenAll(
            Enumerable.Range(0, writerCount)
                .Select(_ => Task.Run(() => PostInOwnContextAsync(
                    startGate,
                    TransferDispatch(perTransfer, 45m))))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(120));

        results.Should().OnlyContain(
            result => result.IsSuccess,
            because: string.Join("; ", results.SelectMany(result => result.Errors).Select(error => error.Code)));

        await using PosDbContext verify = new(_options);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main && b.State == InventoryState.Available))
            .Quantity.Should().Be(openingQuantity - writerCount * perTransfer);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main && b.State == InventoryState.InTransit))
            .Quantity.Should().Be(writerCount * perTransfer);
        (await verify.InventoryMovements.CountAsync()).Should().Be(2 + writerCount * 2);
    }

    [SkippableFact]
    public async Task AWriteAgainstAStaleProjection_IsRefusedByTheConcurrencyToken()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndCommitAsync(Receipt(10m, 45m));

        // Writer B holds its own read-open transaction.
        await using PosDbContext contextB = new(_options);
        InventoryLedger ledgerB = new(contextB, new FixedClock(Now), new StrictLedgerPolicyProvider());

        await using IDbContextTransaction transactionB =
            await contextB.Database.BeginTransactionAsync();

        InventoryBalance stale = await contextB.InventoryBalances.SingleAsync(b => b.LocationId == Main);
        stale.Version.Should().Be(2);

        Result<PostedMovementGroup> staged = await ledgerB.PostAsync(Receipt(7m, 45m), CancellationToken.None);
        staged.IsSuccess.Should().BeTrue();

        // Writer C commits while B is still open. PostgreSQL row locks make this
        // deterministic: B will write against the version it read, not the one
        // C committed.
        await PostAndCommitAsync(Receipt(5m, 45m));

        Func<Task> act = () => contextB.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await transactionB.RollbackAsync();

        await using PosDbContext verify = new(_options);
        (await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main)).Quantity.Should().Be(15m);
        (await verify.InventoryMovements.CountAsync()).Should().Be(4);
    }

    [SkippableFact]
    public async Task CostValueDrift_IsDetected_AndRebuilt_WithTheTriggersArmed()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndCommitAsync(Receipt(10m, 45m));

        // Cost and value are not covered by the deferred balance guard's window
        // check (only quantity is matched to movements), so a direct UPDATE is
        // the realistic way drift appears on PostgreSQL: a stray process, not a
        // bug in the application's write path.
        await _context!.Database.ExecuteSqlRawAsync(
            """
            UPDATE inventory.inventory_balance
            SET average_unit_cost = 99, total_value = 999
            WHERE location_id = @main AND state = @state
            """,
            new Npgsql.NpgsqlParameter { ParameterName = "@main", Value = Main.Value },
            new Npgsql.NpgsqlParameter { ParameterName = "@state", Value = (short)InventoryState.Available });

        var reconciler = new BalanceReconciler(_context, NullLogger<BalanceReconciler>.Instance);

        Result<ReconciliationReport> detected = await reconciler.DetectAsync(CancellationToken.None);
        detected.IsSuccess.Should().BeTrue();
        detected.Value.Discrepancies.Select(d => d.Kind)
            .Should().BeEquivalentTo([BalanceDiscrepancyKind.AverageUnitCost, BalanceDiscrepancyKind.TotalValue]);

        // Rebuild must: delete the rows (needs the delete trigger disabled,
        // which is exactly what the maintenance path does), reinsert from the
        // ledger while the deferred balance guard stays armed, and enable the
        // delete trigger again before committing.
        Result<ReconciliationReport> rebuilt = await reconciler.RebuildAsync(CancellationToken.None);
        rebuilt.IsSuccess.Should().BeTrue();
        rebuilt.Value.WasRebuilt.Should().BeTrue();
        rebuilt.Value.IsHealthy.Should().BeTrue();

        await using PosDbContext verify = new(_options);
        InventoryBalance bucket = await verify.InventoryBalances.SingleAsync(b => b.LocationId == Main);
        bucket.Quantity.Should().Be(10m);
        bucket.AverageUnitCost.Should().Be(45m);
        bucket.TotalValue.Should().Be(450m);

        // The delete guard is armed again: any later delete is refused.
        Func<Task> delete = () => verify.Database.ExecuteSqlRawAsync("DELETE FROM inventory.inventory_balance");
        await delete.Should().ThrowAsync<Exception>();

        // The ledger is untouched: a movement update is still refused.
        Func<Task> edit = () => verify.Database.ExecuteSqlRawAsync(
            "UPDATE inventory.inventory_movement SET notes = 'hacked'");
        await edit.Should().ThrowAsync<Exception>();
    }

    private async Task<Result<PostedMovementGroup>> PostInOwnContextAsync(Barrier startGate, decimal quantity)
    {
        return await PostInOwnContextAsync(startGate, Receipt(quantity, 45m));
    }

    private async Task<Result<PostedMovementGroup>> PostInOwnContextAsync(
        Barrier startGate,
        MovementGroupSpec spec)
    {
        await using PosDbContext context = new(_options);
        InventoryLedger ledger = new(context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        startGate.SignalAndWait();

        return await ledger.PostAsync(spec, CancellationToken.None);
    }

    private async Task PostAndCommitAsync(MovementGroupSpec spec)
    {
        await using PosDbContext context = new(_options);
        InventoryLedger ledger = new(context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        Result<PostedMovementGroup> result = await ledger.PostAsync(spec, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
    }

    private static MovementGroupSpec Receipt(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-PG-CONCURRENT",
        [
            new MovementLegSpec(
                Coke, null, Supplier, LocationKind.External, InventoryState.External, -quantity, unitCost, false),
            new MovementLegSpec(
                Coke, null, Main, LocationKind.MainWarehouse, InventoryState.Available, quantity, unitCost, false),
        ],
        new LedgerActor(Actor, Actor, null, CorrelationId.New()),
        Now,
            DateOnly.FromDateTime(Now.UtcDateTime));

    private static MovementGroupSpec Sale(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.PosSale,
        ReferenceDocumentType.Sale,
        Guid.CreateVersion7(),
        "SAL-2026-PG-CONCURRENT",
        [
            new MovementLegSpec(Coke, null, Main, LocationKind.MainWarehouse, InventoryState.Available, -quantity, unitCost, false),
            new MovementLegSpec(Coke, null, Supplier, LocationKind.External, InventoryState.External, quantity, unitCost, false),
        ],
        new LedgerActor(Actor, Actor, null, CorrelationId.New()),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private static MovementGroupSpec TransferDispatch(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.TransferDispatch,
        ReferenceDocumentType.TransferShipment,
        Guid.CreateVersion7(),
        "SHP-2026-PG-CONCURRENT",
        [
            new MovementLegSpec(Coke, null, Main, LocationKind.MainWarehouse, InventoryState.Available, -quantity, unitCost, false),
            new MovementLegSpec(Coke, null, Main, LocationKind.MainWarehouse, InventoryState.InTransit, quantity, unitCost, false),
        ],
        new LedgerActor(Actor, Actor, null, CorrelationId.New()),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
