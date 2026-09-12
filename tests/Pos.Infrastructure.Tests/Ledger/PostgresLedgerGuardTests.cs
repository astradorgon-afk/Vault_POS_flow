using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;
using Testcontainers.PostgreSql;

namespace Pos.Infrastructure.Tests.Ledger;

/// <summary>
/// Proves the database-level guards on a real PostgreSQL instance: the ledger
/// cannot be edited, and a balance cannot be changed by anything other than
/// movements posted in the same transaction.
/// </summary>
/// <remarks>
/// These are the guarantees that survive a compromised or bypassed application,
/// so they are tested against the real engine rather than a fake. The suite
/// skips itself when no Docker daemon is reachable, so a developer without
/// Docker still gets a green local run; CI always has one.
/// </remarks>
[Collection("postgres")]
public sealed class PostgresLedgerGuardTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private PostgreSqlContainer? _container;
    private PosDbContext? _context;
    private InventoryLedger? _ledger;

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
            // No Docker on this machine. Every test in this class self-skips.
            _container = null;
            return;
        }

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        _context = new PosDbContext(options);

        // Migrations, not EnsureCreated: the triggers live in a migration, and
        // running them here is what proves the migration itself works.
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
    public async Task Migrations_ApplyCleanly_AndInstallTheGuards()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        int triggers = await ScalarAsync(
            """
            SELECT count(*) FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname IN ('trg_inventory_movement_immutable',
                             'trg_inventory_movement_no_truncate',
                             'trg_inventory_balance_guard',
                             'trg_inventory_balance_no_delete',
                             'trg_audit_log_immutable',
                             'trg_audit_log_no_truncate',
                             'trg_login_attempt_immutable')
            """);

        triggers.Should().Be(7);

        // The balance guard must be deferred: it has to see the transaction's
        // final state, not an arbitrary moment part-way through it.
        int deferred = await ScalarAsync(
            """
            SELECT count(*) FROM pg_trigger
            WHERE tgname = 'trg_inventory_balance_guard' AND tgdeferrable AND tginitdeferred
            """);

        deferred.Should().Be(1);
    }

    [SkippableFact]
    public async Task DirectUpdateOfAMovement_IsRejectedByTheDatabase()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndSaveAsync(Receipt(10m, 45m));

        // Raw SQL: the route that bypasses the domain type and the interceptor.
        Func<Task> act = async () => await _context!.Database.ExecuteSqlRawAsync(
            "UPDATE inventory.inventory_movement SET quantity_delta = 9999");

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("append-only", StringComparison.OrdinalIgnoreCase)
                        || e.InnerException!.Message.Contains("append-only", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task DirectDeleteOfAMovement_IsRejectedByTheDatabase()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndSaveAsync(Receipt(10m, 45m));

        Func<Task> act = async () => await _context!.Database.ExecuteSqlRawAsync(
            "DELETE FROM inventory.inventory_movement");

        await act.Should().ThrowAsync<Exception>();
    }

    [SkippableFact]
    public async Task SettingAStockQuantityDirectly_IsRejectedByTheBalanceGuard()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndSaveAsync(Receipt(10m, 45m));

        // This is the exact statement the architecture exists to make impossible:
        // a stock quantity assigned with no movement to explain it.
        Func<Task> act = async () => await _context!.Database.ExecuteSqlRawAsync(
            "UPDATE inventory.inventory_balance SET quantity = 100 WHERE state = 0");

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("inventory ledger", StringComparison.OrdinalIgnoreCase)
                        || e.InnerException!.Message.Contains("inventory ledger", StringComparison.OrdinalIgnoreCase));

        decimal unchanged = await _ledger!.GetQuantityAsync(
            Main, Coke, BatchId.Empty, InventoryState.Available, CancellationToken.None);

        unchanged.Should().Be(10m);
    }

    [SkippableFact]
    public async Task DeletingABalanceRow_IsRejected()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndSaveAsync(Receipt(10m, 45m));

        Func<Task> act = async () => await _context!.Database.ExecuteSqlRawAsync(
            "DELETE FROM inventory.inventory_balance");

        await act.Should().ThrowAsync<Exception>();
    }

    [SkippableFact]
    public async Task LegitimatePostsPassTheGuard_IncludingSeveralInOneTransaction()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        // Two posts to the same bucket inside one transaction is the case a naive
        // guard gets wrong, so it is asserted explicitly.
        await using (var transaction = await _context!.Database.BeginTransactionAsync())
        {
            await _ledger!.PostAsync(Receipt(10m, 45m), CancellationToken.None);
            await _context.SaveChangesAsync();

            await _ledger.PostAsync(Receipt(5m, 45m), CancellationToken.None);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();
        }

        decimal quantity = await _ledger!.GetQuantityAsync(
            Main, Coke, BatchId.Empty, InventoryState.Available, CancellationToken.None);

        quantity.Should().Be(15m);
    }

    [SkippableFact]
    public async Task WholeLedger_SumsToZero()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        await PostAndSaveAsync(Receipt(10m, 45m));

        int nonZero = await ScalarAsync(
            "SELECT CASE WHEN COALESCE(SUM(quantity_delta),0) = 0 THEN 0 ELSE 1 END FROM inventory.inventory_movement");

        nonZero.Should().Be(0);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a literal assertion query; no user input reaches this helper.")]
    private async Task<int> ScalarAsync(string sql)
    {
        System.Data.Common.DbConnection connection = _context!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private async Task PostAndSaveAsync(MovementGroupSpec spec)
    {
        Result<PostedMovementGroup> result = await _ledger!.PostAsync(spec, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        await _context!.SaveChangesAsync();
    }

    private static MovementGroupSpec Receipt(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-000001",
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
