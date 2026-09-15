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

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private ShiftRepository _repository = null!;
    private SalesRepository _salesRepository = null!;

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
        _salesRepository = new SalesRepository(_context);

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

    [Fact]
    public async Task GetShiftCashTotalsAsync_WithCashRefunds_CountsThem()
    {
        CashierShift shift = NewShift();
        LocationId locationId = await GetStoreIdAsync();

        await _repository.AddAsync(shift, CancellationToken.None);

        Sale sale = NewSale(shift, locationId, quantity: 2m, unitPrice: 100m);
        await _salesRepository.AddAsync(sale, CancellationToken.None);

        SalesReturn salesReturn = NewReturn(sale, quantity: 1m);
        await _salesRepository.AddReturnAsync(salesReturn, CancellationToken.None);

        Refund refund = IssueCashRefund(salesReturn, amount: 40m);
        await _salesRepository.AddRefundAsync(salesReturn.Id, refund, CancellationToken.None);

        ShiftCashTotals totals = await _repository.GetShiftCashTotalsAsync(shift.Id, CancellationToken.None);

        totals.CashSales.Should().Be(200m);
        totals.CashRefunds.Should().Be(40m);
        totals.Payouts.Should().Be(0m);
    }

    [Fact]
    public async Task GetRefundedAmountsByMethodAsync_AcrossReturns_SumsPerMethod()
    {
        CashierShift shift = NewShift();
        LocationId locationId = await GetStoreIdAsync();

        await _repository.AddAsync(shift, CancellationToken.None);

        // 2 lines of 200 each, paid half by cash and half by card: each returned
        // line is refundable for 200, and each method may be refunded up to 200.
        Sale sale = NewTwoLineSale(shift, locationId, unitPrice: 200m);
        await _salesRepository.AddAsync(sale, CancellationToken.None);

        SalesReturn firstReturn = NewReturn(sale, quantity: 1m, lineIndex: 0);
        await _salesRepository.AddReturnAsync(firstReturn, CancellationToken.None);

        Refund firstRefund = IssueCashRefund(firstReturn, amount: 80m);
        await _salesRepository.AddRefundAsync(firstReturn.Id, firstRefund, CancellationToken.None);

        SalesReturn secondReturn = NewReturn(sale, quantity: 1m, lineIndex: 1);
        await _salesRepository.AddReturnAsync(secondReturn, CancellationToken.None);

        // The handler would supply prior refunds from this query; the aggregate
        // enforces the caps, so mirror them here to keep the test inside the rules.
        IReadOnlyDictionary<PaymentMethod, decimal> prior = new Dictionary<PaymentMethod, decimal>
        {
            [PaymentMethod.Cash] = 80m,
        };

        Result<Refund> cash = secondReturn.IssueRefund(
            EventId.New(),
            secondReturn.CashierShiftId,
            secondReturn.DeviceId,
            PaymentMethod.Cash,
            amount: 60m,
            tendered: 60m,
            providerReference: null,
            new DateTimeOffset(2026, 9, 15, 9, 45, 0, TimeSpan.Zero),
            Cashier,
            OriginalPaidByMethod(200m, 200m),
            prior,
            cashRoundingIncrement: 0.01m);
        cash.IsSuccess.Should().BeTrue(because: string.Join("; ", cash.Errors.Select(e => e.Code)));
        await _salesRepository.AddRefundAsync(secondReturn.Id, cash.Value, CancellationToken.None);

        Result<Refund> card = secondReturn.IssueRefund(
            EventId.New(),
            secondReturn.CashierShiftId,
            secondReturn.DeviceId,
            PaymentMethod.Card,
            amount: 100m,
            tendered: null,
            providerReference: "REF-2001",
            new DateTimeOffset(2026, 9, 15, 9, 46, 0, TimeSpan.Zero),
            Cashier,
            OriginalPaidByMethod(200m, 200m),
            prior,
            cashRoundingIncrement: 0.01m);
        card.IsSuccess.Should().BeTrue(because: string.Join("; ", card.Errors.Select(e => e.Code)));
        await _salesRepository.AddRefundAsync(secondReturn.Id, card.Value, CancellationToken.None);

        IReadOnlyDictionary<PaymentMethod, decimal> refunded =
            await _repository.GetRefundedAmountsByMethodAsync(sale.Id, CancellationToken.None);

        refunded.Should().HaveCount(2);
        refunded[PaymentMethod.Cash].Should().Be(140m);
        refunded[PaymentMethod.Card].Should().Be(100m);

        IReadOnlyDictionary<PaymentMethod, decimal> otherReturns =
            await _repository.GetRefundedAmountsByMethodAsync(sale.Id, CancellationToken.None, secondReturn.Id);

        otherReturns.Should().ContainSingle();
        otherReturns[PaymentMethod.Cash].Should().Be(80m);
    }

    [Fact]
    public async Task GetRefundedAmountsByMethodAsync_NoRefunds_ReturnsEmpty()
    {
        Sale sale = NewSale(NewShift(), await GetStoreIdAsync(), quantity: 1m, unitPrice: 100m);
        await _salesRepository.AddAsync(sale, CancellationToken.None);

        IReadOnlyDictionary<PaymentMethod, decimal> refunded =
            await _repository.GetRefundedAmountsByMethodAsync(sale.Id, CancellationToken.None);

        refunded.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ForceClose_PersistsFlag()
    {
        CashierShift shift = NewShift();
        await _repository.AddAsync(shift, CancellationToken.None);
        DateTimeOffset now = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

        shift.ForceClose(now);

        await _repository.UpdateAsync(shift, CancellationToken.None);

        CashierShift stored = await _context.CashierShifts.SingleAsync(s => s.Id == shift.Id, CancellationToken.None);
        stored.Status.Should().Be(ShiftStatus.Closed);
        stored.IsForceClosed.Should().BeTrue();
        stored.ClosedAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task UpdateAsync_ForceCloseWithoutReconcile_SurvivesRoundTrip()
    {
        CashierShift shift = NewShift();
        await _repository.AddAsync(shift, CancellationToken.None);
        shift.ForceClose(new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.Zero));
        await _repository.UpdateAsync(shift, CancellationToken.None);

        CashierShift? loaded = await _context.CashierShifts.AsNoTracking()
            .SingleAsync(s => s.Id == shift.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsForceClosed.Should().BeTrue();
        loaded.Status.Should().Be(ShiftStatus.Closed);
        loaded.CashVariance.Should().BeNull();

        shift.Reconcile(varianceThreshold: 0m, reason: "Abandoned past MaxShiftHours");
        await _repository.UpdateAsync(shift, CancellationToken.None);

        CashierShift? reconciled = await _context.CashierShifts.AsNoTracking()
            .SingleAsync(s => s.Id == shift.Id, CancellationToken.None);
        reconciled!.Status.Should().Be(ShiftStatus.Reconciled);
        reconciled.IsForceClosed.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_SuspendResume_RoundTrips()
    {
        CashierShift shift = NewShift();
        await _repository.AddAsync(shift, CancellationToken.None);

        shift.Suspend();
        await _repository.UpdateAsync(shift, CancellationToken.None);

        CashierShift? suspended = await _context.CashierShifts.AsNoTracking()
            .SingleAsync(s => s.Id == shift.Id, CancellationToken.None);
        suspended!.Status.Should().Be(ShiftStatus.Suspended);

        shift.Resume();
        await _repository.UpdateAsync(shift, CancellationToken.None);

        CashierShift? resumed = await _context.CashierShifts.AsNoTracking()
            .SingleAsync(s => s.Id == shift.Id, CancellationToken.None);
        resumed!.Status.Should().Be(ShiftStatus.Open);
    }

    [Fact]
    public async Task GetForceCloseCandidatesAsync_ReturnsOpenAndSuspended_WithDefaultMaxHours()
    {
        LocationId store = await GetStoreIdAsync();
        CashierShift open = NewShift(status: ShiftStatus.Open, sequence: 1, locationId: store);
        CashierShift suspended = NewShift(status: ShiftStatus.Suspended, sequence: 2, locationId: store);
        await _repository.AddAsync(open, CancellationToken.None);
        await _repository.AddAsync(suspended, CancellationToken.None);

        IReadOnlyList<ShiftForceCloseCandidate> candidates =
            await _repository.GetForceCloseCandidatesAsync(CancellationToken.None);

        candidates.Should().HaveCount(2);
        candidates.Select(c => c.Shift.Id).Should().Contain(open.Id).And.Contain(suspended.Id);
        candidates.Should().OnlyContain(c => c.MaxShiftHours == LocationSettings.Default.MaxShiftHours);
    }

    [Fact]
    public async Task GetForceCloseCandidatesAsync_ExcludesClosedAndReconciledShifts()
    {
        LocationId store = await GetStoreIdAsync();
        CashierShift open = NewShift(status: ShiftStatus.Open, sequence: 1, locationId: store);
        CashierShift closed = NewShift(status: ShiftStatus.Closed, sequence: 2, locationId: store);
        await _repository.AddAsync(open, CancellationToken.None);
        await _repository.AddAsync(closed, CancellationToken.None);

        IReadOnlyList<ShiftForceCloseCandidate> candidates =
            await _repository.GetForceCloseCandidatesAsync(CancellationToken.None);

        candidates.Should().ContainSingle(c => c.Shift.Id == open.Id);
    }

    [Fact]
    public async Task GetForceCloseCandidatesAsync_UsesLocationSpecificMaxShiftHours()
    {
        Result<Location> created = Location.Create(
            Organization.DefaultId,
            "STORE02",
            "Store Two",
            LocationKind.Store,
            "Asia/Manila",
            new LocationSettings { CashVarianceThreshold = 10m, MaxShiftHours = TimeSpan.FromHours(8) });

        if (created.IsFailure)
        {
            throw new InvalidOperationException("Could not create test location.");
        }

        Location storeTwo = created.Value;
        _context.Locations.Add(storeTwo);
        await _context.SaveChangesAsync();

        CashierShift host = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D02", 1),
            storeTwo.Id,
            DeviceId.New(),
            UserId.New(),
            1000m,
            new DateOnly(2026, 9, 15),
            new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero)).Value;
        await _repository.AddAsync(host, CancellationToken.None);

        IReadOnlyList<ShiftForceCloseCandidate> candidates =
            await _repository.GetForceCloseCandidatesAsync(CancellationToken.None);

        ShiftForceCloseCandidate candidate = candidates.Single(c => c.Shift.Id == host.Id);
        candidate.MaxShiftHours.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task GetForceCloseCandidatesAsync_NoShifts_ReturnsEmpty()
    {
        IReadOnlyList<ShiftForceCloseCandidate> candidates =
            await _repository.GetForceCloseCandidatesAsync(CancellationToken.None);

        candidates.Should().BeEmpty();
    }

    private static Dictionary<PaymentMethod, decimal> OriginalPaidByMethod(
        decimal cash,
        decimal card)
        => new Dictionary<PaymentMethod, decimal>
        {
            [PaymentMethod.Cash] = cash,
            [PaymentMethod.Card] = card,
        };

    private static Sale NewSale(CashierShift shift, LocationId locationId, decimal quantity, decimal unitPrice)
    {
        ItemSpec item = new(
            ProductId.New(),
            "Widget",
            Barcode: null,
            Quantity: quantity,
            Unit,
            unitPrice,
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

        decimal total = unitPrice * quantity;
        PaymentSpec payment = new(PaymentMethod.Cash, total, total, ProviderReference: null);

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000002"),
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
    }

    private static Sale NewTwoLineSale(CashierShift shift, LocationId locationId, decimal unitPrice)
    {
        ItemSpec item = new(
            ProductId.New(),
            "Widget",
            Barcode: null,
            Quantity: 1m,
            Unit,
            unitPrice,
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

        PaymentSpec cash = new(PaymentMethod.Cash, unitPrice, unitPrice, ProviderReference: null);
        PaymentSpec card = new(PaymentMethod.Card, unitPrice, unitPrice, ProviderReference: "REF-0001");

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000002"),
                EventId.New(),
                locationId,
                shift.Id,
                Device,
                customerId: null,
                shift.BusinessDate,
                shift.OpenedAtUtc,
                Cashier,
                [item, item],
                [cash, card])
            .Value;
    }

    private static SalesReturn NewReturn(Sale sale, decimal quantity, int lineIndex = 0)
    {
        DateOnly businessDate = sale.BusinessDate;
        DateTimeOffset now = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);

        IReadOnlyDictionary<SaleItemId, decimal> alreadyReturned =
            sale.Items.ToDictionary(i => i.Id, _ => 0m);

        Result<SalesReturn> result = SalesReturn.Create(
            DocumentNumber.FromTrustedSource($"RET-2026-STORE01-{lineIndex + 1:D4}"),
            EventId.New(),
            sale.Id,
            sale.LocationId,
            sale.CashierShiftId,
            sale.DeviceId,
            customerId: null,
            businessDate,
            now,
            sale.CompletedByUserId,
            [new ReturnItemSpec(sale.Items[lineIndex], quantity)],
            alreadyReturned);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Could not create test return: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }

        return result.Value;
    }

    private static Refund IssueCashRefund(SalesReturn salesReturn, decimal amount)
    {
        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            salesReturn.CashierShiftId,
            salesReturn.DeviceId,
            PaymentMethod.Cash,
            amount,
            tendered: amount,
            providerReference: null,
            new DateTimeOffset(2026, 9, 15, 9, 45, 0, TimeSpan.Zero),
            Cashier,
            OriginalPaidByMethod(amount, 0m),
            priorRefundedByMethod: new Dictionary<PaymentMethod, decimal>(),
            cashRoundingIncrement: 0.01m);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Could not create test refund: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }

        return result.Value;
    }

    private static CashierShift NewShift(ShiftStatus status = ShiftStatus.Open, int sequence = 1, LocationId? locationId = null)
    {
        DateTimeOffset now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

        Result<CashierShift> result = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", sequence),
            locationId ?? Store,
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
