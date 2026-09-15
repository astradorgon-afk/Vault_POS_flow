using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Sales;

/// <summary>
/// Repository behaviour for C2 (POS.md §4): the sales return persists with its
/// lines, a refund attaches to the return and is read back, and both idempotent
/// and aggregate reads surface the stored facts the refund handler trusts.
/// </summary>
/// <remarks>
/// Runs on SQLite in memory so it executes anywhere, including a machine without
/// Docker. The PostgreSQL guards are separate; this suite proves the mapping,
/// the NoTracking reads, and the refund-to-return navigation.
/// </remarks>
public sealed class SalesReturnRepositoryTests : IAsyncLifetime
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Cashier = UserId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly UnitOfMeasureId Unit = UnitOfMeasureId.New();
    private static readonly ProductId Widget = ProductId.New();
    private static readonly DocumentNumber SaleNumber =
        DocumentNumber.FromTrustedSource("SAL-2026-000001");
    private static readonly DocumentNumber ReturnNumber =
        DocumentNumber.FromTrustedSource("RET-2026-STORE01-0001");

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private SalesRepository _repository = null!;
    private ShiftRepository _shiftRepository = null!;

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

        _repository = new SalesRepository(_context);
        _shiftRepository = new ShiftRepository(_context);

        // Every test needs the counterparty the sold goods are posted to and a
        // store the sale and the return belong to.
        if (Location.Create(Organization.DefaultId, "MAIN", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila")
                is { IsSuccess: true } main
            && Location.Create(Organization.DefaultId, "STORE01", "Store One", LocationKind.Store, "Asia/Manila")
                is { IsSuccess: true } store
            && Location.CreateSystemExternal(Organization.DefaultId, SystemLocationCodes.ExternalCustomer, "External Customer")
                is { IsSuccess: true } customer)
        {
            _context.Locations.Add(main.Value);
            _context.Locations.Add(store.Value);
            _context.Locations.Add(customer.Value);
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
    public async Task AddReturnAsync_PersistsReturnAndLines_AndReadsThemBack()
    {
        Sale sale = await NewSaleAsync(quantity: 2m, unitPrice: 100m);
        SalesReturn salesReturn = NewReturn(sale, quantity: 1m);

        Result<SalesReturnId> saved = await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(salesReturn.Id);

        SalesReturn stored = await _context.SalesReturns
            .Include(r => r.Items)
            .SingleAsync(r => r.Id == salesReturn.Id, CancellationToken.None);

        stored.Number.Should().Be(salesReturn.Number);
        stored.SaleId.Should().Be(sale.Id);
        stored.LocationId.Should().Be(sale.LocationId);
        stored.CashierShiftId.Should().Be(sale.CashierShiftId);
        stored.DeviceId.Should().Be(sale.DeviceId);
        stored.BusinessDate.Should().Be(sale.BusinessDate);
        stored.ReturnedByUserId.Should().Be(sale.CompletedByUserId);
        stored.RefundableTotal.Should().Be(100m);
        stored.Items.Should().ContainSingle();
        stored.Items[0].Quantity.Should().Be(1m);
        stored.Items[0].RefundableAmount.Should().Be(100m);
    }

    [Fact]
    public async Task GetReturnByIdAsync_LoadsTheReturn_WithItsRefunds()
    {
        Sale sale = await NewSaleAsync(quantity: 1m, unitPrice: 200m);
        SalesReturn salesReturn = NewReturn(sale, quantity: 1m);

        await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        EventId refundEvent = EventId.New();
        Refund refund = IssueCashRefund(salesReturn, refundEvent, amount: 200m);

        Result<RefundId> saved = await _repository.AddRefundAsync(salesReturn.Id, refund, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(refund.Id);

        SalesReturn? stored = await _repository.GetReturnByIdAsync(salesReturn.Id, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.Refunds.Should().ContainSingle();
        stored.Refunds[0].Id.Should().Be(refund.Id);
        stored.Refunds[0].EventId.Should().Be(refundEvent);
        stored.Refunds[0].Method.Should().Be(PaymentMethod.Cash);
        stored.Refunds[0].Amount.Should().Be(200m);
        stored.Refunds[0].Tendered.Should().Be(200m);
        stored.Refunds[0].CashierShiftId.Should().Be(sale.CashierShiftId);
        stored.Refunds[0].DeviceId.Should().Be(sale.DeviceId);
    }

    [Fact]
    public async Task GetRefundByEventAsync_ExistingEvent_ReturnsTheRefund()
    {
        Sale sale = await NewSaleAsync(quantity: 1m, unitPrice: 100m);
        SalesReturn salesReturn = NewReturn(sale, quantity: 1m);
        await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        EventId refundEvent = EventId.New();
        Refund refund = IssueCashRefund(salesReturn, refundEvent, amount: 100m);
        await _repository.AddRefundAsync(salesReturn.Id, refund, CancellationToken.None);

        Refund? loaded = await _repository.GetRefundByEventAsync(refundEvent, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(refund.Id);
        loaded.SalesReturnId.Should().Be(salesReturn.Id);
        loaded.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task GetRefundByEventAsync_MissingEvent_ReturnsNull()
    {
        Refund? loaded = await _repository.GetRefundByEventAsync(EventId.New(), CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetReturnByIdAsync_MissingReturn_ReturnsNull()
    {
        SalesReturn? loaded = await _repository.GetReturnByIdAsync(SalesReturnId.New(), CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task AddReceiptPrintAsync_PersistsReprint_WithReasonAndSaleFk()
    {
        Sale sale = await NewSaleAsync(quantity: 1m, unitPrice: 100m);

        Result<SaleReceiptPrint> print = SaleReceiptPrint.Create(
            sale.Id,
            Cashier,
            new DateTimeOffset(2026, 9, 15, 9, 45, 0, TimeSpan.Zero),
            isReprint: true,
            "Customer copy lost; re-issued at till");

        print.IsSuccess.Should().BeTrue();

        Result<ReceiptPrintId> saved =
            await _repository.AddReceiptPrintAsync(print.Value, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();

        SaleReceiptPrint? loaded = await _context.ReceiptPrints
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == saved.Value);

        loaded.Should().NotBeNull();
        loaded!.SaleId.Should().Be(sale.Id);
        loaded.PrintedByUserId.Should().Be(Cashier);
        loaded.IsReprint.Should().BeTrue();
        loaded.Reason.Should().Be("Customer copy lost; re-issued at till");
        loaded.PrintedAtUtc.Should().Be(new DateTimeOffset(2026, 9, 15, 9, 45, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task AddReceiptPrintAsync_FirstPrint_PersistsWithoutReason()
    {
        Sale sale = await NewSaleAsync(quantity: 1m, unitPrice: 100m);

        Result<SaleReceiptPrint> print = SaleReceiptPrint.Create(
            sale.Id,
            Cashier,
            new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero),
            isReprint: false,
            reason: null);

        Result<ReceiptPrintId> saved =
            await _repository.AddReceiptPrintAsync(print.Value, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();

        SaleReceiptPrint? loaded = await _context.ReceiptPrints
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == saved.Value);

        loaded!.IsReprint.Should().BeFalse();
        loaded.Reason.Should().BeNull();
    }

    [Fact]
    public async Task AddBlindReturnAsync_PersistsWithoutSale_AndReadsThemBack()
    {
        SalesReturn salesReturn = NewBlindReturn();

        Result<SalesReturnId> saved = await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(salesReturn.Id);

        SalesReturn stored = await _context.SalesReturns
            .Include(r => r.Items)
            .SingleAsync(r => r.Id == salesReturn.Id, CancellationToken.None);

        stored.Number.Should().Be(salesReturn.Number);
        stored.IsBlind.Should().BeTrue();
        stored.SaleId.Should().BeNull();
        stored.LocationId.Should().Be(Store);
        stored.RefundableTotal.Should().Be(100m);
        stored.Items.Should().ContainSingle();
        stored.Items[0].SaleItemId.Should().BeNull();
        stored.Items[0].LineNumber.Should().Be(1);
        stored.Items[0].ProductId.Should().Be(Widget);
        stored.Items[0].Quantity.Should().Be(1m);
        stored.Items[0].UnitPrice.Should().Be(100m);
        stored.Items[0].RefundableAmount.Should().Be(100m);
    }

    [Fact]
    public async Task GetReturnByIdAsync_LoadsABlindReturn()
    {
        SalesReturn salesReturn = NewBlindReturn();
        await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        SalesReturn? stored = await _repository.GetReturnByIdAsync(salesReturn.Id, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.IsBlind.Should().BeTrue();
        stored.SaleId.Should().BeNull();
        stored.Items.Should().ContainSingle();
        stored.Items[0].SaleItemId.Should().BeNull();
    }

    [Fact]
    public async Task AddRefundAsync_PersistsBlindRefund_AndReadsItBack()
    {
        SalesReturn salesReturn = NewBlindReturn();
        await _repository.AddReturnAsync(salesReturn, CancellationToken.None);

        EventId refundEvent = EventId.New();
        Result<Refund> issued = salesReturn.IssueBlindRefund(
            refundEvent,
            salesReturn.CashierShiftId,
            salesReturn.DeviceId,
            PaymentMethod.Cash,
            100m,
            tendered: 100m,
            providerReference: null,
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero),
            Cashier,
            cashRoundingIncrement: 0.05m);

        issued.IsSuccess.Should().BeTrue();

        Result<RefundId> saved = await _repository.AddRefundAsync(salesReturn.Id, issued.Value, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(issued.Value.Id);

        SalesReturn? stored = await _repository.GetReturnByIdAsync(salesReturn.Id, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.Refunds.Should().ContainSingle();
        stored.Refunds[0].Id.Should().Be(issued.Value.Id);
        stored.Refunds[0].EventId.Should().Be(refundEvent);
        stored.Refunds[0].Method.Should().Be(PaymentMethod.Cash);
        stored.Refunds[0].Amount.Should().Be(100m);
        stored.Refunds[0].Tendered.Should().Be(100m);
        stored.Refunds[0].CashierShiftId.Should().Be(salesReturn.CashierShiftId);
        stored.Refunds[0].DeviceId.Should().Be(salesReturn.DeviceId);

        Refund? byEvent = await _repository.GetRefundByEventAsync(refundEvent, CancellationToken.None);

        byEvent.Should().NotBeNull();
        byEvent!.Id.Should().Be(issued.Value.Id);
        byEvent.SalesReturnId.Should().Be(salesReturn.Id);
    }

    private async Task<Sale> NewSaleAsync(decimal quantity, decimal unitPrice)
    {
        CashierShift shift = NewShift();
        await _shiftRepository.AddAsync(shift, CancellationToken.None);

        ItemSpec item = new(
            Widget,
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

        Sale sale = Sale.Create(
                SaleNumber,
                EventId.New(),
                Store,
                shift.Id,
                Device,
                customerId: null,
                shift.BusinessDate,
                shift.OpenedAtUtc,
                Cashier,
                [item],
                [payment])
            .Value;

        await _repository.AddAsync(sale, CancellationToken.None);
        return sale;
    }

    private static SalesReturn NewReturn(Sale sale, decimal quantity)
    {
        DateOnly businessDate = sale.BusinessDate;
        DateTimeOffset now = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<SaleItemId, decimal> alreadyReturned =
            sale.Items.ToDictionary(i => i.Id, _ => 0m);

        Result<SalesReturn> result = SalesReturn.Create(
            ReturnNumber,
            EventId.New(),
            sale.Id,
            sale.LocationId,
            sale.CashierShiftId,
            sale.DeviceId,
            customerId: null,
            businessDate,
            now,
            sale.CompletedByUserId,
            [new ReturnItemSpec(sale.Items[0], quantity)],
            alreadyReturned);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Could not create test return: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }

        return result.Value;
    }

    private static SalesReturn NewBlindReturn()
    {
        DateOnly businessDate = new(2026, 9, 15);
        DateTimeOffset now = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

        Result<SalesReturn> result = SalesReturn.CreateBlind(
            ReturnNumber,
            EventId.New(),
            Store,
            CashierShiftId.New(),
            Device,
            customerId: null,
            businessDate,
            now,
            Cashier,
            "Customer is outside the store; goods returned without a sale number",
            [new BlindReturnItemSpec(
                Widget,
                "Widget",
                Barcode: null,
                Quantity: 1m,
                Unit,
                UnitPrice: 100m,
                VatRate: null,
                IsVatExempt: false,
                IsZeroRated: true,
                UnitCost: 0m)]);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Could not create test blind return: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }

        return result.Value;
    }

    private static Refund IssueCashRefund(SalesReturn salesReturn, EventId eventId, decimal amount)
    {
        IReadOnlyDictionary<PaymentMethod, decimal> paidByMethod = new Dictionary<PaymentMethod, decimal>
        {
            [PaymentMethod.Cash] = amount,
        };

        Result<Refund> result = salesReturn.IssueRefund(
            eventId,
            salesReturn.CashierShiftId,
            salesReturn.DeviceId,
            PaymentMethod.Cash,
            amount,
            tendered: amount,
            providerReference: null,
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero),
            Cashier,
            paidByMethod,
            priorRefundedByMethod: new Dictionary<PaymentMethod, decimal>(),
            cashRoundingIncrement: 0.01m);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Could not create test refund: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }

        return result.Value;
    }

    private static CashierShift NewShift()
    {
        DateTimeOffset now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

        Result<CashierShift> result = CashierShift.Open(
            DocumentNumber.FromTrustedSource("SHF-2026-D01-0001"),
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

        return result.Value;
    }
}