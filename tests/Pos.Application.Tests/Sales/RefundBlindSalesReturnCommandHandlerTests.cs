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
/// Tests <see cref="RefundBlindSalesReturnCommandHandler"/>: the blind refund
/// first checks idempotency, then verifies the return exists, is a blind return
/// and belongs to the same location and device, confirms the shift is open on
/// the same device, loads the location's rounding increment, lets the aggregate
/// enforce the per-return cap, and persists the refund with its audit entry.
/// Because a blind return accepted goods back with no original sale, no sale is
/// loaded and no per-method cap applies.
/// </summary>
public sealed class RefundBlindSalesReturnCommandHandlerTests
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

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private readonly CashierShift _openShift;
    private readonly SalesReturn _blindReturn;
    private readonly RefundBlindSalesReturnCommandHandler _handler;

    public RefundBlindSalesReturnCommandHandlerTests()
    {
        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D03", 1),
            TestStore,
            TestDevice,
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;

        _blindReturn = SalesReturn.CreateBlind(
            DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 5),
            EventId.New(),
            TestStore,
            _openShift.Id,
            TestDevice,
            customerId: null,
            BusinessDate,
            Now.AddMinutes(20),
            ReturnedBy,
            "Customer could not produce the original receipt.",
            [Item(Product, "Widget")]).Value;

        _repository.GetRefundByEventAsync(Arg.Any<EventId>(), Arg.Any<CancellationToken>())
            .Returns((Refund?)null);
        _repository.GetReturnByIdAsync(Arg.Any<SalesReturnId>(), Arg.Any<CancellationToken>())
            .Returns(_blindReturn);
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);
        _repository.GetLocationAsync(Arg.Any<LocationId>(), Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(
                LocationKind.Store,
                new LocationSettings { CashRoundingIncrement = 0.05m }));
        _repository.AddRefundAsync(Arg.Any<SalesReturnId>(), Arg.Any<Refund>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<RefundId>.Success(((Refund)callInfo[1]).Id));

        _handler = new RefundBlindSalesReturnCommandHandler(_repository, _shifts, _audit);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsTheRefundId()
    {
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(RefundId.Empty);
    }

    [Fact]
    public async Task HappyPath_PersistsTheRefund()
    {
        RefundBlindSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _repository.Received(1).AddRefundAsync(
            _blindReturn.Id,
            Arg.Is<Refund>(r =>
                r.Method == PaymentMethod.Cash
                && r.Amount == 50m
                && r.Tendered == 50m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesRefundIssuedAudit()
    {
        RefundBlindSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.RefundIssued
                && e.EntityType == "sales_return"
                && e.ReferenceDocumentType == ReferenceDocumentType.SalesReturn),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_DoesNotLoadAnySale()
    {
        RefundBlindSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _repository.DidNotReceive().GetByIdAsync(Arg.Any<SaleId>(), Arg.Any<CancellationToken>());
        await _shifts.DidNotReceive().GetRefundedAmountsByMethodAsync(Arg.Any<SaleId>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Idempotency
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReplayEvent_ReturnsStoredRefund()
    {
        Refund existing = _blindReturn.IssueBlindRefund(
            EventId.New(),
            _openShift.Id,
            TestDevice,
            PaymentMethod.Cash,
            20m,
            tendered: 20m,
            providerReference: null,
            Now.AddMinutes(10),
            ReturnedBy,
            cashRoundingIncrement: 0.05m).Value;
        _repository.GetRefundByEventAsync(existing.EventId, Arg.Any<CancellationToken>())
            .Returns(existing);
        RefundBlindSalesReturnCommand command = Command() with { EventId = existing.EventId };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existing.Id);
        await _repository.DidNotReceive().GetReturnByIdAsync(Arg.Any<SalesReturnId>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddRefundAsync(Arg.Any<SalesReturnId>(), Arg.Any<Refund>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Return validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownReturn_ReturnsNotFound()
    {
        _repository.GetReturnByIdAsync(_blindReturn.Id, Arg.Any<CancellationToken>())
            .Returns((SalesReturn?)null);
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(RefundCommandErrors.ReturnNotFound(_blindReturn.Id));
    }

    [Fact]
    public async Task ReferencedReturn_ReturnsConflict()
    {
        _repository.GetReturnByIdAsync(_blindReturn.Id, Arg.Any<CancellationToken>())
            .Returns(NewReferencedReturn());
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.return_not_blind");
    }

    [Fact]
    public async Task ReturnAtDifferentLocation_ReturnsConflict()
    {
        RefundBlindSalesReturnCommand command = Command() with { LocationId = LocationId.New() };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.location_mismatch");
    }

    [Fact]
    public async Task ReturnOnDifferentDevice_ReturnsConflict()
    {
        RefundBlindSalesReturnCommand command = Command() with { DeviceId = DeviceId.New() };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.refund.device_mismatch");
    }

    // ------------------------------------------------------------------
    // Shift validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(RefundCommandErrors.ShiftUnknown(command.ShiftId).Code);
    }

    [Fact]
    public async Task ClosedShift_ReturnsConflict()
    {
        _openShift.DeclareCash(0m);
        _openShift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddMinutes(30));
        RefundBlindSalesReturnCommand command = Command();

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
        RefundBlindSalesReturnCommand command = Command();

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
        _repository.GetLocationAsync(_blindReturn.LocationId, Arg.Any<CancellationToken>())
            .Returns((SaleLocationFacts?)null);
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.LocationUnknown(_blindReturn.LocationId).Code);
    }

    // ------------------------------------------------------------------
    // Refund caps (aggregate errors propagated)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CardRefund_FailsAsCashOnly()
    {
        RefundBlindSalesReturnCommand command = Command()
            with
            {
                Method = PaymentMethod.Card,
                Tendered = null,
            };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.blind.cash_only");
    }

    [Fact]
    public async Task CashRefundWithoutTendered_Fails()
    {
        RefundBlindSalesReturnCommand command = Command() with { Tendered = null };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.tendered_required");
    }

    [Fact]
    public async Task RefundExceedingRefundableTotal_Fails()
    {
        RefundBlindSalesReturnCommand command = Command() with { Amount = 150m, Tendered = 150m };

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_refundable");
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
        RefundBlindSalesReturnCommand command = Command();

        Result<RefundId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(persistenceError);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private RefundBlindSalesReturnCommand Command() => new(
        EventId.New(),
        _blindReturn.Id,
        _blindReturn.LocationId,
        _openShift.Id,
        _blindReturn.DeviceId,
        PaymentMethod.Cash,
        50m,
        Tendered: 50m,
        ProviderReference: null,
        Now.AddHours(1),
        ReturnedBy);

    private static BlindReturnItemSpec Item(ProductId productId, string productName)
        => new(
            productId,
            productName,
            Barcode: null,
            Quantity: 1m,
            Each,
            100m,
            VatRate: null,
            IsVatExempt: false,
            IsZeroRated: true,
            UnitCost: 50m);

    private static SalesReturn NewReferencedReturn()
    {
        ProductPriceId priceVersion = ProductPriceId.New();
        CashierShiftId shiftId = CashierShiftId.New();

        ItemSpec item = new(
            Product,
            "Widget",
            Barcode: null,
            Quantity: 1m,
            Each,
            100m,
            priceVersion,
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

        Sale sale = Sale.Create(
            DocumentNumber.FromTrustedSource("SAL-2026-000001"),
            EventId.New(),
            TestStore,
            shiftId,
            TestDevice,
            customerId: null,
            BusinessDate,
            Now,
            TestCashier,
            [item],
            [new PaymentSpec(PaymentMethod.Cash, 100m, 100m, ProviderReference: null)])
            .Value;

        return SalesReturn.Create(
            DocumentNumber.FromTrustedSource("RET-2026-STORE01-0001"),
            EventId.New(),
            sale.Id,
            sale.LocationId,
            sale.CashierShiftId,
            sale.DeviceId,
            customerId: null,
            sale.BusinessDate,
            Now.AddMinutes(30),
            ReturnedBy,
            [new ReturnItemSpec(sale.Items[0], 1m)],
            sale.Items.ToDictionary(i => i.Id, _ => 0m))
            .Value;
    }
}