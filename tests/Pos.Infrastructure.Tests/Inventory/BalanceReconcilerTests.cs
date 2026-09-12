using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// The reconciliation loop: the ledger is replayed into a fresh projection and
/// compared with the stored one, and a rebuild replaces the stored projection
/// with the replay.
/// </summary>
/// <remarks>
/// SQLite has none of the PostgreSQL guards, which is what makes it the right
/// place to corrupt rows freely and observe the reconciler's own behaviour. The
/// rebuild path against the real triggers lives in PostgresBalanceConcurrencyTests.
/// </remarks>
public sealed class BalanceReconcilerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private InventoryLedger _ledger = null!;
    private BalanceReconciler _reconciler = null!;

    /// <inheritdoc />
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
        _reconciler = new BalanceReconciler(_context, NullLogger<BalanceReconciler>.Instance);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AHealthyProjection_ReconcilesClean()
    {
        await PostAndCommitAsync(Receipt(10m, 45m));
        await PostAndCommitAsync(Receipt(8m, 45m));
        await PostAndCommitAsync(Receipt(4m, 55m));

        ReconciliationReport report = await DetectOrThrowAsync();

        report.IsHealthy.Should().BeTrue();
        report.WasRebuilt.Should().BeFalse();
        report.BucketCount.Should().Be(2, because: "the stock bucket and its external counterparty");
        report.DriftedBucketCount.Should().Be(0);
        report.Discrepancies.Should().BeEmpty();

        // The replay must reproduce the ledger's own projection: 22 units, and
        // the weighted average of 18 at 45 and 4 at 55.
        InventoryBalance main = await _context.InventoryBalances
            .SingleAsync(b => b.LocationId == Main);

        main.Quantity.Should().Be(22m);
        main.AverageUnitCost.Should().Be(46.8182m, because: "the column stores four decimal places");
    }

    [Fact]
    public async Task ACorruptedQuantity_IsReported_AndRebuildRepairsIt()
    {
        await PostAndCommitAsync(Receipt(10m, 45m));

        // Corruption: a raw UPDATE, exactly what no application code can reach
        // on PostgreSQL but what a bug in some future feature could do anywhere.
        // Invariant inline SQL rather than parameterised raw SQL, and GUIDs in
        // Microsoft.Data.Sqlite's uppercase text form (EF persists them so).
        await _context.Database.ExecuteSqlRawAsync(
            FormattableString.Invariant(
                $"UPDATE inventory_balance SET quantity = 100 WHERE location_id = '{SqliteGuid(Main.Value)}' AND state = {(int)InventoryState.Available}"));

        ReconciliationReport detected = await DetectOrThrowAsync();

        detected.IsHealthy.Should().BeFalse();
        detected.DriftedBucketCount.Should().Be(1);

        BalanceDiscrepancy discrepancy = detected.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BalanceDiscrepancyKind.Quantity);
        discrepancy.Expected.Should().Be(10m);
        discrepancy.Actual.Should().Be(100m);

        ReconciliationReport rebuilt = await RebuildOrThrowAsync();

        rebuilt.WasRebuilt.Should().BeTrue();
        rebuilt.RebuiltBucketCount.Should().Be(2);
        rebuilt.IsHealthy.Should().BeTrue();
        rebuilt.DriftedBucketCount.Should().Be(1, because: "the report keeps the drift found before the rebuild");
        rebuilt.Discrepancies.Should().BeEmpty();

        (await QuantityAsync(Main, InventoryState.Available)).Should().Be(10m);

        // The ledger is untouched by a rebuild: it is the source of truth.
        (await _context.InventoryMovements.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ADeletedBalanceRow_IsReportedMissing_AndRebuiltFromTheLedger()
    {
        await PostAndCommitAsync(Receipt(10m, 45m));

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM inventory_balance");

        ReconciliationReport detected = await DetectOrThrowAsync();

        // The DELETE removed both the Main and Supplier buckets, so two Missing
        // discrepancies are expected.  We assert on the Main bucket specifically.
        BalanceDiscrepancy discrepancy = detected.Discrepancies
            .Should().ContainSingle(d => d.Kind == BalanceDiscrepancyKind.Missing
                                         && d.Bucket.LocationId == Main.Value).Subject;

        discrepancy.Expected.Should().Be(10m);
        discrepancy.ExpectedLastMovementId.Should().NotBeNull();
        discrepancy.ActualLastMovementId.Should().BeNull();

        ReconciliationReport rebuilt = await RebuildOrThrowAsync();

        rebuilt.WasRebuilt.Should().BeTrue();
        rebuilt.IsHealthy.Should().BeTrue();

        (await QuantityAsync(Main, InventoryState.Available)).Should().Be(10m);
        (await _context.InventoryBalances.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ARowWithNoLedgerBehindIt_IsReportedUnexpected()
    {
        await PostAndCommitAsync(Receipt(10m, 45m));

        // A bucket the ledger has never seen: a stray row from a botched merge.
        // GUIDs must match Microsoft.Data.Sqlite's uppercase text form, which is
        // how EF persists them.
        await _context.Database.ExecuteSqlRawAsync(
            FormattableString.Invariant(
                $"""
                INSERT INTO inventory_balance
                    (location_id, product_id, batch_key, state, quantity, average_unit_cost, total_value, last_movement_id, last_movement_at_utc, version)
                VALUES
                    ('{SqliteGuid(LocationId.New().Value)}', '{SqliteGuid(Coke.Value)}', '{SqliteGuid(Guid.Empty)}', {(int)InventoryState.Available}, 0, 0, 0, '{SqliteGuid(Guid.Empty)}', '2026-03-04 07:00:00+00:00', 1)
                """));

        ReconciliationReport detected = await DetectOrThrowAsync();

        BalanceDiscrepancy discrepancy = detected.Discrepancies
            .Should().ContainSingle(d => d.Kind == BalanceDiscrepancyKind.Unexpected).Subject;

        discrepancy.Expected.Should().Be(0m, because: "the ledger believes this bucket does not exist");

        ReconciliationReport rebuilt = await RebuildOrThrowAsync();

        rebuilt.WasRebuilt.Should().BeTrue();
        rebuilt.IsHealthy.Should().BeTrue();
        (await _context.InventoryBalances.CountAsync()).Should().Be(2, because: "the stray row was dropped");
    }

    [Fact]
    public async Task CostValueDrift_IsRepaired_ByAReplayOfTheWeightedAverage()
    {
        await PostAndCommitAsync(Receipt(10m, 45m));
        await PostAndCommitAsync(Receipt(10m, 55m));

        // Before any corruption: the replay agrees with the ledger's averaging.
        (await DetectOrThrowAsync()).IsHealthy.Should().BeTrue();

        await _context.Database.ExecuteSqlRawAsync(
            FormattableString.Invariant(
                $"UPDATE inventory_balance SET average_unit_cost = 99, total_value = 999 WHERE location_id = '{SqliteGuid(Main.Value)}' AND state = {(int)InventoryState.Available}"));

        ReconciliationReport detected = await DetectOrThrowAsync();

        detected.Discrepancies.Select(d => d.Kind)
            .Should().BeEquivalentTo([BalanceDiscrepancyKind.AverageUnitCost, BalanceDiscrepancyKind.TotalValue]);

        ReconciliationReport rebuilt = await RebuildOrThrowAsync();

        rebuilt.IsHealthy.Should().BeTrue();
        (await QuantityAsync(Main, InventoryState.Available)).Should().Be(20m);

        InventoryBalance bucket = await _context.InventoryBalances
            .SingleAsync(b => b.LocationId == Main);

        bucket.AverageUnitCost.Should().Be(50m, because: "(10*45 + 10*55) / 20");
        bucket.TotalValue.Should().Be(1000m);
    }

    private async Task<decimal> QuantityAsync(LocationId location, InventoryState state)
        => await _ledger.GetQuantityAsync(location, Coke, BatchId.Empty, state, CancellationToken.None);

    /// <summary>
    /// Renders a GUID the way EF's SQLite provider persists it: uppercase text
    /// with dashes. Raw SQL in these tests must inline this form to match rows.
    /// </summary>
    private static string SqliteGuid(Guid value) => value.ToString("D").ToUpperInvariant();

    private async Task<ReconciliationReport> DetectOrThrowAsync()
    {
        Result<ReconciliationReport> result = await _reconciler.DetectAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        return result.Value;
    }

    private async Task<ReconciliationReport> RebuildOrThrowAsync()
    {
        Result<ReconciliationReport> result = await _reconciler.RebuildAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        return result.Value;
    }

    private async Task PostAndCommitAsync(MovementGroupSpec spec)
    {
        Result<PostedMovementGroup> result = await _ledger.PostAsync(spec, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
    }

    private static MovementGroupSpec Receipt(decimal quantity, decimal unitCost) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-RECONCILE",
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