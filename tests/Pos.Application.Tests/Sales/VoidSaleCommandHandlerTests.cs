using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Tests <see cref="VoidSaleCommandHandler"/>.</summary>
public sealed class VoidSaleCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly LocationId TestStore = LocationId.New();
    private static readonly DeviceId TestDevice = DeviceId.New();
    private static readonly UserId TestCashier = UserId.New();
    private static readonly CashierShiftId TestShiftId = CashierShiftId.New();
    private static readonly UserId Manager = UserId.New();

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IInventoryLedger _ledger = Substitute.For<IInventoryLedger>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly CashierShift _openShift;
    private readonly VoidSaleCommandHandler _handler;

    public VoidSaleCommandHandlerTests()
    {
        _currentUser.DeviceId.Returns(DeviceId.New());
        _currentUser.CorrelationId.Returns(new CorrelationId(Guid.NewGuid()));

        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 1),
            TestStore,
            TestDevice,
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;

        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);

        _ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Success(new PostedMovementGroup(
                MovementGroupId.New(),
                EventId.New(),
                2,
                false,
                Now)));

        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.UpdateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<SaleId>.Success(((Sale)callInfo[0]).Id));

        _handler = new VoidSaleCommandHandler(
            _repository,
            _shifts,
            _ledger,
            _audit,
            _currentUser);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsSuccess()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(sale.Id);
    }

    [Fact]
    public async Task HappyPath_VoidsTheSaleAndPersistsIt()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sale.Status.Should().Be(SaleStatus.Voided);
        sale.VoidedAtUtc.Should().Be(command.VoidedAtUtc);
        sale.VoidedByUserId.Should().Be(command.VoidedByUserId);
        sale.VoidReason.Should().Be(command.Reason);

        await _repository.Received(1).UpdateAsync(sale, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PostsReversal_WithPositiveLegBackToStoreAvailable()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.MovementType == InventoryMovementType.PosSaleVoid
                && g.ReferenceDocumentType == ReferenceDocumentType.Sale
                && g.EventId == command.EventId
                && g.Legs[0].QuantityDelta > 0m
                && g.Legs[0].LocationId == TestStore
                && g.Legs[0].State == InventoryState.Available),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PostsReversal_WithNegativeLegBackFromExternalCustomer()
    {
        LocationId externalId = LocationId.New();
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(externalId);
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.Legs[1].QuantityDelta < 0m
                && g.Legs[1].LocationId == externalId
                && g.Legs[1].State == InventoryState.External),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_ApprovesAsTheVoidingUser()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.Actor.CreatedBy == command.VoidedByUserId
                && g.Actor.ApprovedBy == command.VoidedByUserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesVoidedAudit()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.SaleVoided
                && e.EntityType == "sale"
                && e.ReferenceDocumentType == ReferenceDocumentType.Sale
                && e.Reason == command.Reason),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Sale facts validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownSale_ReturnsNotFound()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>())
            .Returns((Sale?)null);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(VoidSaleCommandErrors.SaleNotFound(sale.Id));
    }

    [Fact]
    public async Task SaleFromAnotherLocation_ReturnsConflict()
    {
        Sale sale = NewSale();
        LocationId otherLocation = LocationId.New();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale) with { LocationId = otherLocation };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(VoidSaleCommandErrors.LocationMismatch(sale.Id, otherLocation));
    }

    [Fact]
    public async Task SaleFromAnotherDevice_ReturnsConflict()
    {
        Sale sale = NewSale();
        DeviceId otherDevice = DeviceId.New();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale) with { DeviceId = otherDevice };

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(VoidSaleCommandErrors.DeviceMismatch(sale.Id, otherDevice));
    }

    [Fact]
    public async Task AlreadyVoided_ReturnsConflict()
    {
        Sale sale = NewSale();
        sale.Void(TestShiftId, BusinessDate, Now, Manager, "Earlier void");
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.void.only_completed");
    }

    // ------------------------------------------------------------------
    // Shift validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.ShiftUnknown(command.ShiftId).Code);
    }

    [Fact]
    public async Task ClosedShift_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _openShift.DeclareCash(0m);
        _openShift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddMinutes(30));
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.ShiftNotOpen(ShiftStatus.Closed).Code);
    }

    // ------------------------------------------------------------------
    // Reversal failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExternalCustomerLocationMissing_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns((LocationId?)null);
        VoidSaleCommand command = Command(sale);

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ExternalCustomerLocationMissing);
    }

    [Fact]
    public async Task LedgerFailure_ReturnsErrors()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        VoidSaleCommand command = Command(sale);
        Error ledgerError = Error.Conflict("test.ledger", "Ledger refused.");
        _ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Failure(ledgerError));

        Result<SaleId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ledgerError);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Sale NewSale()
    {
        ItemSpec item = new(
            ProductId.New(),
            "Widget",
            Barcode: null,
            Quantity: 1m,
            UnitOfMeasureId.New(),
            UnitPrice: 100m,
            PriceVersion: ProductPriceId.New(),
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

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000001"),
                EventId.New(),
                TestStore,
                TestShiftId,
                TestDevice,
                customerId: null,
                BusinessDate,
                Now,
                TestCashier,
                [item],
                [new PaymentSpec(PaymentMethod.Cash, 100m, 100m, ProviderReference: null)])
            .Value;
    }

    private static VoidSaleCommand Command(Sale sale) => new(
        EventId.New(),
        sale.Id,
        sale.LocationId,
        sale.CashierShiftId,
        sale.DeviceId,
        sale.BusinessDate,
        Manager,
        Now.AddMinutes(5),
        "Wrong item scanned");
}