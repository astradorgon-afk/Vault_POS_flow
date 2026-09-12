using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// The optimistic-concurrency contract of the balance projection, exercised
/// with real concurrent writers on a real relational engine.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory SQLite used elsewhere supports one connection and cannot show a
/// lost update, so these tests run against a throwaway file with WAL mode and a
/// generous busy timeout, where several writers to the same bucket genuinely
/// interleave. Each writer gets its own connection and context, like two nodes
/// of the sync processor would.
/// </para>
/// <para>
/// The PostgreSQL-side guarantees are proven separately by
/// PostgresBalanceConcurrencyTests; here the point is the behaviour of the
/// ledger itself, which must hold on any relational provider.
/// </para>
/// </remarks>
public sealed class BalanceConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), FormattableString.Invariant($"vaultflow-concurrency-{Guid.NewGuid():N}.db"));

    // Cache=Shared is deliberately omitted: with a file-based database the
    // shared-cache mode imposes table-level locking that causes spurious
    // SQLITE_LOCKED errors across connections.  WAL mode without shared cache
    // gives the correct per-writer semantics.
    private string ConnectionString => FormattableString.Invariant($"Data Source={_databasePath}");

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // WAL spills into sidecar files; remove all three, best effort.
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = _databasePath + suffix;

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Still held open by a failed test; leave it to the OS.
                }
            }
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParallelReceiptsToTheSameBucket_AllSucceed_AndTheProjectionConverges()
    {
        const int writerCount = 6;
        const decimal perWriter = 10m;
        decimal expectedQuantity = writerCount * perWriter;

        await CreateSchemaAsync();

        // Every worker starts from the same barrier so the reads genuinely race.
        using Barrier startGate = new(writerCount);

        Result<PostedMovementGroup>[] results = await Task.WhenAll(
            Enumerable.Range(0, writerCount)
                .Select(_ => Task.Run(() => PostInOwnDatabaseAsync(startGate, perWriter)))
                .ToArray()).WaitAsync(TimeSpan.FromSeconds(60));

        results.Should().OnlyContain(
            r => r.IsSuccess,
            because: string.Join("; ", results.SelectMany(r => r.Errors).Select(e => e.Code)));

        await using SqliteConnection verify = await OpenConnectionAsync();
        await using PosDbContext context = new(OptionsFor(verify));

        // The ledger is closed: twelve legs, summing to zero, all distinct.
        (await context.InventoryMovements.CountAsync(CancellationToken.None)).Should().Be(writerCount * 2);
        (await context.InventoryMovements.SumAsync(m => m.QuantityDelta, CancellationToken.None)).Should().Be(0m);

        // The shared buckets both converge, and their versions were bumped for
        // every apply: no write was silently lost.
        InventoryBalance main = await context.InventoryBalances
            .SingleAsync(b => b.LocationId == Main, CancellationToken.None);

        InventoryBalance supplier = await context.InventoryBalances
            .SingleAsync(b => b.LocationId == Supplier, CancellationToken.None);

        main.Quantity.Should().Be(expectedQuantity);
        main.Version.Should().Be(writerCount + 1, because: "CreateEmpty starts at 1 and every apply bumps it");

        supplier.Quantity.Should().Be(-expectedQuantity);
        supplier.Version.Should().Be(writerCount + 1);

        // One event id per writer, one group id per writer: idempotency keys
        // were not reused and no posting split into two groups.
        (await context.InventoryMovements.Select(m => m.EventId).Distinct().CountAsync(CancellationToken.None))
            .Should().Be(writerCount);
        (await context.InventoryMovements.Select(m => m.MovementGroupId).Distinct().CountAsync(CancellationToken.None))
            .Should().Be(writerCount);
    }

    [Fact]
    public async Task AWriteAgainstAStaleProjection_IsRefusedByTheConcurrencyToken()
    {
        await CreateSchemaAsync();

        // Writer A posts 10, deterministically.
        await PostAndCommitAsync(Receipt(10m, 45m));

        // B reads the projection the way one node of the sync processor would:
        // an autocommit read that pins the version it saw.
        await using SqliteConnection connectionB = await OpenConnectionAsync();
        await using PosDbContext contextB = new(OptionsFor(connectionB));

        InventoryBalance stale = await contextB.InventoryBalances
            .SingleAsync(b => b.LocationId == Main, CancellationToken.None);

        stale.Version.Should().Be(2);

        // Writer C commits +5 for the same bucket while B still believes the
        // projection is at version 2.
        await PostAndCommitAsync(Receipt(5m, 45m));

        // B now writes its staged post.  SQLite will not upgrade a read
        // transaction that predates C's commit (the engine answers with
        // "database is locked" instead), so B comes into the write on a fresh
        // transaction whose tracked original version is forced to the version
        // it actually read.  This is exactly the state a stale writer is in:
        // its concurrency token says 2 while the row is now at 3.
        await using SqliteConnection connectionW = await OpenConnectionAsync();
        await using PosDbContext contextW = new(OptionsFor(connectionW));
        InventoryLedger ledgerW = new(contextW, new FixedClock(Now), new StrictLedgerPolicyProvider());

        await using IDbContextTransaction transactionW =
            await contextW.Database.BeginTransactionAsync(CancellationToken.None);

        InventoryBalance current = await contextW.InventoryBalances
            .SingleAsync(b => b.LocationId == Main, CancellationToken.None);

        contextW.Entry(current).Property(nameof(InventoryBalance.Version)).OriginalValue = stale.Version;

        Result<PostedMovementGroup> staged = await ledgerW.PostAsync(Receipt(7m, 45m), CancellationToken.None);
        staged.IsSuccess.Should().BeTrue();

        // The save targets the version B READ, not the one C committed, so the
        // token refuses it instead of letting B silently overwrite C.
        Func<Task> act = () => contextW.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // Nothing B staged landed; the projection is exactly A plus C.
        await transactionW.RollbackAsync(CancellationToken.None);

        await using SqliteConnection verify = await OpenConnectionAsync();
        await using PosDbContext context = new(OptionsFor(verify));

        (await context.InventoryBalances.SingleAsync(b => b.LocationId == Main, CancellationToken.None))
            .Quantity.Should().Be(15m);
        (await context.InventoryMovements.CountAsync(CancellationToken.None)).Should().Be(4);
    }

    private async Task CreateSchemaAsync()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using PosDbContext context = new(OptionsFor(connection));
        await context.Database.EnsureCreatedAsync();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        SqliteConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        await using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 30000;";
        await pragmas.ExecuteNonQueryAsync();

        return connection;
    }

    private static DbContextOptions<PosDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

    private async Task<Result<PostedMovementGroup>> PostInOwnDatabaseAsync(Barrier startGate, decimal quantity)
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using PosDbContext context = new(OptionsFor(connection));
        InventoryLedger ledger = new(context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        startGate.SignalAndWait();

        return await ledger.PostAsync(Receipt(quantity, 45m), CancellationToken.None);
    }

    private async Task PostAndCommitAsync(MovementGroupSpec spec)
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using PosDbContext context = new(OptionsFor(connection));
        InventoryLedger ledger = new(context, new FixedClock(Now), new StrictLedgerPolicyProvider());

        Result<PostedMovementGroup> result = await ledger.PostAsync(spec, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
    }

    private static MovementGroupSpec Receipt(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-CONCURRENT",
        [
            new MovementLegSpec(
                Coke, null, Supplier, LocationKind.External, InventoryState.External, -quantity, unitCost, false),
            new MovementLegSpec(
                Coke, null, Main, LocationKind.MainWarehouse, InventoryState.Available, quantity, unitCost, false),
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