using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>
/// Tests <see cref="RefundSalesReturnCommandHandler"/>: the refund first checks
/// idempotency, then verifies the return belongs to the sale, location and
/// device, confirms the shift is open on the same device, loads the location's
/// rounding increment, lets the aggregate enforce the per-method and per-return
/// caps, and persists the refund with its audit entry.
/// </summary>
public sealed class RefundSalesReturnCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly LocationId TestStore = LocationId.New();
    private static readonly DeviceId TestDevice = DeviceId.New();
    private static readonly UserId TestCashier = UserId.New();
    private static readonly CashierShiftId TestShiftId = CashierShiftId.New();
    private static readonly UserId ReturnedBy = UserId.New();
    private static readonly ProductId Product = ProductId.New();
    private static readonly UnitOfMeasureId Each = UnitOfMeasureId.New();
    private static readonly ProductPriceId PriceVersion = ProductPriceId.New();

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private readonly CashierShift _openShift;
    private readonly Sale _sale;
    private readonly SalesReturn _salesReturn;
    private readonly RefundSalesReturnCommandHandler _handler;

    public RefundSalesReturnCommandHandlerTests()
    {
        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D03", 1),
            TestStore,
            TestDevice,
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;

        _sale = Sale.Create(
            DocumentNumber.FromTrustedSource("SAL-2026-000001"),
            EventId.New(),
            TestStore,
            TestShiftId,
            TestDevice,
            customerId: null,
            BusinessDate,
            Now,
            TestCashier,
            [Item(Product, "Widget")],
            [new PaymentSpec(PaymentMethod.Cash, 100m, 100m, ProviderReference: null)])
            .Value;

        _salesReturn = SalesReturn.Create(
            DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 5),
            EventId.New(),
            _sale.Id,
            _sale.LocationId,
            _sale.CashierShiftId,
            _sale.DeviceId,
            customerId: null,
            _sale.BusinessDate,
            Now.AddMinutes(30),
            ReturnedBy,
            [new ReturnItemSpec(_sale.Items[0], 1m)],
            _sale.Items.ToDictionary(i => i.Id, i => i.ReturnedQuantity)).Value;

        _repository.GetRefundByEventAsync(Arg.Any<EventId>(), Arg.Any<CancellationToken>())
            .Returns((Refund?)null);
        _repository.GetReturnByIdAsync(Arg.Any<SalesReturnId>(), Arg.Any<CancellationToken>())
            .Returns(_salesReturn);
        _repository.GetByIdAsync(Arg.Any<SaleId>(), Arg.Any<CancellationToken>())
            .Returns(_sale);
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);
        _shifts.GetRefundedAmountsByMethodAsync(Arg.Any<SaleId>(), Arg.Any<CancellationToken>(), _salesReturn.Id)
            .Returns(new Dictionary<PaymentMethod, decimal>());
        _repository.GetLocationAsync(Arg.Any<LocationId>(), Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(
                LocationKind.Store,
                new LocationSettings { CashRoundingIncrement = 0.05m }));
        _repository.AddRefundAsync(Arg.Any<SalesReturnId>(), Arg.Any<Refund>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<RefundId>.Success(((Refund)callInfo[1]).Id));

        _handler = new RefundSalesReturnCommandHandler(_repository, _shifts, _audit);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsTheRefundId()
    {
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(RefundId.Empty);
        await _shifts.Received(1).GetRefundedAmountsByMethodAsync(
            _sale.Id, Arg.Any<CancellationToken>(), _salesReturn.Id);
    }

    [Fact]
    public async Task HappyPath_PersistsTheRefund()
    {
        RefundSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _repository.Received(1).AddRefundAsync(
            _salesReturn.Id,
            Arg.Is<Refund>(r =>
                r.Method == PaymentMethod.Cash
                && r.Amount == 50m
                && r.Tendered == 50m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesRefundIssuedAudit()
    {
        RefundSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.RefundIssued
                && e.EntityType == "sales_return"
                && e.ReferenceDocumentType == ReferenceDocumentType.SalesReturn),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Idempotency
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReplayEvent_ReturnsStoredRefund()
    {
        Refund existing = _salesReturn.IssueRefund(
            EventId.New(),
            TestShiftId,
            TestDevice,
            PaymentMethod.Cash,
            20m,
            tendered: 20m,
            providerReference: null,
            Now.AddMinutes(10),
            ReturnedBy,
            SalePaidByMethod(),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m).Value;
        _repository.GetRefundByEventAsync(existing.EventId, Arg.Any<CancellationToken>())
            .Returns(existing);
        RefundSalesReturnCommand command = Command() with { EventId = existing.EventId };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existing.Id);
        await _repository.DidNotReceive().GetReturnByIdAsync(Arg.Any<SalesReturnId>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddRefundAsync(Arg.Any<SalesReturnId>(), Arg.Any<Refund>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Return and sale validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownReturn_ReturnsNotFound()
    {
        _repository.GetReturnByIdAsync(_salesReturn.Id, Arg.Any<CancellationToken>())
            .Returns((SalesReturn?)null);
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RefundCommandErrors.ReturnNotFound(_salesReturn.Id));
    }

    [Fact]
    public async Task ReturnForDifferentSale_ReturnsConflict()
    {
        RefundSalesReturnCommand command = Command() with { SaleId = SaleId.New() };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.sale_mismatch");
    }

    [Fact]
    public async Task ReturnAtDifferentLocation_ReturnsConflict()
    {
        RefundSalesReturnCommand command = Command() with { LocationId = LocationId.New() };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.location_mismatch");
    }

    [Fact]
    public async Task ReturnOnDifferentDevice_ReturnsConflict()
    {
        RefundSalesReturnCommand command = Command() with { DeviceId = DeviceId.New() };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.device_mismatch");
    }

    // ------------------------------------------------------------------
    // Sale loading
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownSale_ReturnsNotFound()
    {
        _repository.GetByIdAsync(_sale.Id, Arg.Any<CancellationToken>())
            .Returns((Sale?)null);
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RefundCommandErrors.SaleNotFound(_sale.Id));
    }

    // ------------------------------------------------------------------
    // Shift validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(RefundCommandErrors.ShiftUnknown(command.ShiftId).Code);
    }

    [Fact]
    public async Task ClosedShift_ReturnsConflict()
    {
        _openShift.DeclareCash(0m);
        _openShift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddMinutes(30));
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(RefundCommandErrors.ShiftNotOpen(ShiftStatus.Closed).Code);
    }

    [Fact]
    public async Task ShiftOpenedOnDifferentDevice_ReturnsConflict()
    {
        CashierShift otherShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D03", 2),
            TestStore,
            DeviceId.New(),
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(otherShift);
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RefundCommandErrors.ShiftDeviceMismatch);
    }

    // ------------------------------------------------------------------
    // Location loading
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownLocation_ReturnsNotFound()
    {
        _repository.GetLocationAsync(_salesReturn.LocationId, Arg.Any<CancellationToken>())
            .Returns((SaleLocationFacts?)null);
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.LocationUnknown(_salesReturn.LocationId).Code);
    }

    // ------------------------------------------------------------------
    // Refund caps (aggregate errors propagated)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CashRefundWithoutTendered_Fails()
    {
        RefundSalesReturnCommand command = Command() with { Tendered = null };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.tendered_required");
    }

    [Fact]
    public async Task RefundExceedingMethodCap_Fails()
    {
        RefundSalesReturnCommand command = Command() with { Amount = 150m, Tendered = 150m };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_paid_for_method");
    }

    [Fact]
    public async Task PriorRefundsFromOtherDocuments_CountAgainstMethodCap()
    {
        _shifts.GetRefundedAmountsByMethodAsync(_sale.Id, Arg.Any<CancellationToken>(), _salesReturn.Id)
            .Returns(new Dictionary<PaymentMethod, decimal> { [PaymentMethod.Cash] = 60m });
        RefundSalesReturnCommand command = Command() with { Amount = 50m, Tendered = 50m };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_paid_for_method");
    }

    // ------------------------------------------------------------------
    // Repository failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddRefundFailure_PropagatesError()
    {
        Error persistenceError = Error.Conflict("test.persist", "Save failed.");
        _repository.AddRefundAsync(Arg.Any<SalesReturnId>(), Arg.Any<Refund>(), Arg.Any<CancellationToken>())
            .Returns(Result<RefundId>.Failure(persistenceError));
        RefundSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(persistenceError);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ItemSpec Item(ProductId productId, string productName, decimal quantity = 1m, decimal price = 100m)
        => new(
            productId,
            productName,
            Barcode: null,
            quantity,
            Each,
            price,
            PriceVersion,
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            Discount: 0m,
            DiscountAuthorizedByUserId: null,
            VatRate: 0.12m,
            IsVatExempt: false,
            IsZeroRated: false,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            UnitCost: 50m,
            TracksBatches: false);

    private RefundSalesReturnCommand Command() => new(
        EventId.New(),
        _sale.Id,
        _salesReturn.Id,
        _salesReturn.LocationId,
        _openShift.Id,
        _salesReturn.DeviceId,
        PaymentMethod.Cash,
        50m,
        Tendered: 50m,
        ProviderReference: null,
        Now.AddHours(1),
        ReturnedBy);

    private Dictionary<PaymentMethod, decimal> SalePaidByMethod() =>
        _sale.Payments
            .GroupBy(p => p.Method)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

    private static Dictionary<PaymentMethod, decimal> RefundedByMethod() => new();
}
