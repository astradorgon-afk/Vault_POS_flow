using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Sales;

/// <summary>
/// Repository behaviour against a real relational provider: the cashier shift
/// persists, round-trips with NoTracking, and the cash-totals query is correct.
/// </summary>
/// <remarks>
/// Runs on SQLite in memory so it executes anywhere, including a machine without
/// Docker.
/// </remarks>
public sealed class ShiftRepositoryTests : IAsyncLifetime
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Cashier = UserId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly UnitOfMeasureId Unit = UnitOfMeasureId.New();
    private static readonly DocumentNumber Number =
        DocumentNumber.FromTrustedSource("SHF-2026-D01-0001");

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private ShiftRepository _repository = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new PosDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _repository = new ShiftRepository(_context);

        if (Location.Create(Organization.DefaultId, "MAIN", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila")
                is { IsSuccess: true } main
            && Location.Create(Organization.DefaultId, "STORE01", "Store One", LocationKind.Store, "Asia/Manila")
                is { IsSuccess: true } store)
        {
            _context.Locations.Add(main.Value);
            _context.Locations.Add(store.Value);
            await _context.SaveChangesAsync();
        }
        else
        {
            throw new InvalidOperationException("Could not seed test locations.");
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AddAsync_PersistsShift_WithAllColumns()
    {
        CashierShift shift = NewShift();
        LocationId locationId = await GetStoreIdAsync();

        Result<CashierShiftId> saved = await _repository.AddAsync(shift, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(shift.Id);

        CashierShift stored = await _context.CashierShifts.SingleAsync(s => s.Id == shift.Id, CancellationToken.None);

        stored.Number.Should().Be(shift.Number);
        stored.LocationId.Should().Be(shift.LocationId);
        stored.DeviceId.Should().Be(shift.DeviceId);
        stored.CashierUserId.Should().Be(shift.CashierUserId);
        stored.OpeningFloat.Should().Be(shift.OpeningFloat);
        stored.BusinessDate.Should().Be(shift.BusinessDate);
        stored.OpenedAtUtc.Should().Be(shift.OpenedAtUtc);
        stored.Status.Should().Be(ShiftStatus.Open);
        stored.ClosedAtUtc.Should().BeNull();
        stored.DeclaredCash.Should().BeNull();
        stored.CountedCash.Should().BeNull();
        stored.CashVariance.Should().BeNull();
    }

    [Fact]
    public async Task GetShiftAsync_ExistingShift_ReturnsIt()
    {
        CashierShift shift = NewShift();

        await _repository.AddAsync(shift, CancellationToken.None);

        CashierShift? loaded = await _repository.GetShiftAsync(shift.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(shift.Id);
        loaded.Number.Should().Be(shift.Number);
    }

    [Fact]
    public async Task GetShiftAsync_MissingShift_ReturnsNull()
    {
        CashierShift? loaded = await _repository.GetShiftAsync(CashierShiftId.New(), CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetOpenShiftForDeviceAsync_WithOpenShift_ReturnsIt()
    {
        CashierShift shift = NewShift(status: ShiftStatus.Open);

        await _repository.AddAsync(shift, CancellationToken.None);

        CashierShift? loaded = await _repository.GetOpenShiftForDeviceAsync(shift.DeviceId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(shift.Id);
        loaded.Status.Should().Be(ShiftStatus.Open);
    }

    [Fact]
    public async Task GetOpenShiftForDeviceAsync_WithSuspendedShift_ReturnsIt()
    {
        CashierShift shift = NewShift(status: ShiftStatus.Suspended);

        await _repository.AddAsync(shift, CancellationToken.None);

        CashierShift? loaded = await _repository.GetOpenShiftForDeviceAsync(shift.DeviceId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(shift.Id);
        loaded.Status.Should().Be(ShiftStatus.Suspended);
    }

    [Fact]
    public async Task GetOpenShiftForDeviceAsync_WithClosedShift_ReturnsNull()
    {
        CashierShift shift = NewShift(status: ShiftStatus.Closed);

        await _repository.AddAsync(shift, CancellationToken.None);

        CashierShift? loaded = await _repository.GetOpenShiftForDeviceAsync(shift.DeviceId, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetOpenShiftForDeviceAsync_MissingDevice_ReturnsNull()
    {
        CashierShift? loaded = await _repository.GetOpenShiftForDeviceAsync(DeviceId.New(), CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_AttachesAndSaves_UpdatedFields()
    {
        CashierShift shift = NewShift();
        await _repository.AddAsync(shift, CancellationToken.None);

        shift.DeclareCash(100m);
        shift.Close(countedCash: 1005m, cashSales: 0m, cashRefunds: 0m, payouts: 0m,
            closedAtUtc: new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.Zero));

        Result<CashierShiftId> result = await _repository.UpdateAsync(shift, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        CashierShift stored = await _context.CashierShifts
            .AsNoTracking()
            .SingleAsync(s => s.Id == shift.Id, CancellationToken.None);

        stored.Status.Should().Be(ShiftStatus.Closed);
        stored.DeclaredCash.Should().Be(100m);
        stored.CountedCash.Should().Be(1005m);
        stored.CashVariance.Should().Be(5m);
        stored.ClosedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetLocationFactsAsync_ExistingLocation_ReturnsKindAndSettings()
    {
        LocationId locationId = await GetStoreIdAsync();

        ShiftLocationFacts? facts = await _repository.GetLocationFactsAsync(locationId, CancellationToken.None);

        facts.Should().NotBeNull();
        facts!.Kind.Should().Be(LocationKind.Store);
        facts.Settings.Should().Be(LocationSettings.Default);
    }

    [Fact]
    public async Task GetLocationFactsAsync_MissingLocation_ReturnsNull()
    {
        ShiftLocationFacts? facts = await _repository.GetLocationFactsAsync(LocationId.New(), CancellationToken.None);

        facts.Should().BeNull();
    }

    [Fact]
    public async Task GetShiftCashTotalsAsync_WithCashPayments_ReturnsCorrectTotals()
    {
        CashierShift shift = NewShift();
        LocationId locationId = await GetStoreIdAsync();

        await _repository.AddAsync(shift, CancellationToken.None);

        ItemSpec item = new(
            ProductId.New(),
            "Widget",
            Barcode: null,
            Quantity: 2m,
            Unit,
            UnitPrice: 100m,
            PriceVersion: ProductPriceId.New(),
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            Discount: 0m,
            DiscountAuthorizedByUserId: null,
            VatRate: null,
            IsVatExempt: false,
            IsZeroRated: true,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            UnitCost: 0m,
            TracksBatches: false);

        PaymentSpec payment = new(PaymentMethod.Cash, 200m, 200m, ProviderReference: null);

        Sale sale = Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000001"),
                EventId.New(),
                locationId,
                shift.Id,
                Device,
                customerId: null,
                shift.BusinessDate,
                shift.OpenedAtUtc,
                Cashier,
                [item],
                [payment])
            .Value;

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync();

        ShiftCashTotals totals = await _repository.GetShiftCashTotalsAsync(shift.Id, CancellationToken.None);

        totals.CashSales.Should().Be(200m);
        totals.CashRefunds.Should().Be(0m);
        totals.Payouts.Should().Be(0m);
    }

    [Fact]
    public async Task GetShiftCashTotalsAsync_NoPayments_ReturnsZeros()
    {
        CashierShift shift = NewShift();

        await _repository.AddAsync(shift, CancellationToken.None);

        ShiftCashTotals totals = await _repository.GetShiftCashTotalsAsync(shift.Id, CancellationToken.None);

        totals.CashSales.Should().Be(0m);
        totals.CashRefunds.Should().Be(0m);
        totals.Payouts.Should().Be(0m);
    }

    private static CashierShift NewShift(ShiftStatus status = ShiftStatus.Open)
    {
        DateTimeOffset now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

        Result<CashierShift> result = CashierShift.Open(
            Number,
            Store,
            Device,
            Cashier,
            openingFloat: 1000m,
            new DateOnly(2026, 9, 15),
            now);

        if (result.IsFailure)
        {
            throw new InvalidOperationException("Could not create test shift.");
        }

        CashierShift shift = result.Value;

        if (status == ShiftStatus.Closed)
        {
            shift.DeclareCash(950m);
            shift.Close(countedCash: 960m, cashSales: 0m, cashRefunds: 0m, payouts: 0m,
                closedAtUtc: now.AddHours(10));
        }
        else if (status == ShiftStatus.Suspended)
        {
            shift.Suspend();
        }

        return shift;
    }

    private async Task<LocationId> GetStoreIdAsync()
        => (await _context.Locations.SingleAsync(l => l.Code == "STORE01", CancellationToken.None)).Id;
}