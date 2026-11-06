using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>
/// Tests <see cref="CreateSalesReturnCommandHandler"/>: the return accepts goods
/// back from a completed sale of the same location and device, matches the
/// requested products FIFO across the sale's lines, posts the CustomerReturn
/// movement from EXT-CUSTOMER's External bucket into the store's ReturnPending
/// bucket, and persists the return with the updated sale.
/// </summary>
public sealed class CreateSalesReturnCommandHandlerTests
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
    private static readonly BatchId Batch1 = BatchId.New();
    private static readonly BatchId Batch2 = BatchId.New();

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IInventoryLedger _ledger = Substitute.For<IInventoryLedger>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly CashierShift _openShift;
    private readonly CreateSalesReturnCommandHandler _handler;

    public CreateSalesReturnCommandHandlerTests()
    {
        _currentUser.DeviceId.Returns(DeviceId.New());
        _currentUser.CorrelationId.Returns(new CorrelationId(Guid.NewGuid()));

        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D03", 1),
            TestStore,
            TestDevice,
            TestCashier,
            0m,
            BusinessDate,
            Now).Value;

        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns(_openShift);
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D03"));

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
        _repository.AddReturnAsync(Arg.Any<SalesReturn>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<SalesReturnId>.Success(((SalesReturn)callInfo[0]).Id));

        _handler = new CreateSalesReturnCommandHandler(
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
    public async Task HappyPath_ReturnsTheCreatedReturnId()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(SalesReturnId.Empty);
    }

    [Fact]
    public async Task HappyPath_AccumulatesReturnedQuantityOnTheSaleLine()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sale.Items[0].ReturnedQuantity.Should().Be(1m);
        await _repository.Received(1).UpdateAsync(sale, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PostsCustomerReturn_FromExternalToReturnPending()
    {
        LocationId externalId = LocationId.New();
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(externalId);
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.MovementType == InventoryMovementType.CustomerReturn
                && g.ReferenceDocumentType == ReferenceDocumentType.SalesReturn
                && g.EventId == command.EventId
                && g.BusinessDate == command.BusinessDate
                && g.OccurredAtUtc == command.ReturnedAtUtc
                && g.Legs[0].QuantityDelta == 1m
                && g.Legs[0].LocationId == TestStore
                && g.Legs[0].State == InventoryState.ReturnPending
                && g.Legs[1].QuantityDelta == -1m
                && g.Legs[1].LocationId == externalId
                && g.Legs[1].State == InventoryState.External),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PostsWithTheReturningUserAsCreator()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _ledger.Received(1).PostAsync(
            Arg.Is<MovementGroupSpec>(g =>
                g.Actor.CreatedBy == command.ReturnedByUserId
                && g.Actor.ApprovedBy == null
                && g.Actor.Device == _currentUser.DeviceId
                && g.Actor.Correlation == _currentUser.CorrelationId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesReturnCreatedAudit()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ReturnCreated
                && e.EntityType == "sales_return"
                && e.ReferenceDocumentType == ReferenceDocumentType.SalesReturn),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PersistsTheReturn()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _repository.Received(1).AddReturnAsync(
            Arg.Is<SalesReturn>(r =>
                r.Number.StartsWith("RET-")
                && r.SaleId == sale.Id
                && r.RefundableTotal == 100m
                && r.Items.Should().ContainSingle().Subject.Quantity == 1m),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // FIFO allocation and line caps
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fifo_TakesEarliestBatchSlicesFirst()
    {
        // Two sale lines of the same product sold from two batches. Returning
        // 1.5 units must consume the first line fully and half of the second.
        Sale sale = NewSale(
            [Item(Product, "Widget", quantity: 1m, batchId: Batch1), Item(Product, "Widget", quantity: 1m, batchId: Batch2)],
            [new PaymentSpec(PaymentMethod.Cash, 200m, 200m, ProviderReference: null)]);
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale, [new SalesReturnLine(Product, 1.5m)]);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));

        await _repository.Received(1).AddReturnAsync(
            Arg.Is<SalesReturn>(r =>
                r.Items.Count == 2
                && r.Items[0].BatchId == Batch1 && r.Items[0].Quantity == 1m
                && r.Items[1].BatchId == Batch2 && r.Items[1].Quantity == 0.5m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lines_AlreadyReturnedUnitsAreNotReturnableAgain()
    {
        Sale sale = NewSale([Item(Product, "Widget", quantity: 2m)], [new PaymentSpec(PaymentMethod.Cash, 200m, 200m, ProviderReference: null)]);
        sale.RecordReturn(sale.Items[0].Id, 0.5m).IsSuccess.Should().BeTrue();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale, [new SalesReturnLine(Product, 2m)]);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.quantity_exceeds_available");
        result.Error.Message.Should().Contain("1.5");
    }

    [Fact]
    public async Task Lines_RequestingMoreThanSold_Fails()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale, [new SalesReturnLine(Product, 2m)]);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.quantity_exceeds_available");
    }

    [Fact]
    public async Task Lines_ProductNotOnTheSale_Fails()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        ProductId other = ProductId.New();
        CreateSalesReturnCommand command = Command(sale, [new SalesReturnLine(other, 1m)]);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.no_returnable_lines");
    }

    // ------------------------------------------------------------------
    // Sale facts validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownSale_ReturnsNotFound()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns((Sale?)null);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ReturnCommandErrors.SaleNotFound(sale.Id));
    }

    [Fact]
    public async Task SaleFromAnotherLocation_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale) with { LocationId = LocationId.New() };

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.location_mismatch");
    }

    [Fact]
    public async Task ASaleRungAtAnotherCounterInTheStore_CanBeReturnedHere()
    {
        Sale sale = NewSale(device: DeviceId.New());
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        // This register's own shift takes the return.
        CreateSalesReturnCommand command = Command(sale) with { DeviceId = TestDevice, ShiftId = _openShift.Id };

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // RET number and shift validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task NumberWithoutDeviceCode_ReturnsInvalid()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale) with
        {
            Number = DocumentNumber.Create(DocumentType.SalesReturn, 2026, 5),
        };

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ReturnCommandErrors.NumberInvalid);
    }

    [Fact]
    public async Task NumberFromAnotherDevice_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("ZZ9"));
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ReturnCommandErrors.NumberDeviceMismatch);
    }

    [Fact]
    public async Task UnknownDevice_ReturnsNotFound()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns((ShiftDeviceFacts?)null);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.device_unknown");
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _shifts.GetShiftAsync(Arg.Any<CashierShiftId>(), Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

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
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.shift_not_open");
    }

    [Fact]
    public async Task ShiftOpenedOnAnotherDevice_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

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
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.shift_device_mismatch");
    }

    // ------------------------------------------------------------------
    // Movement failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExternalCustomerLocationMissing_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns((LocationId?)null);
        CreateSalesReturnCommand command = Command(sale);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SaleCommandErrors.ExternalCustomerLocationMissing);
    }

    [Fact]
    public async Task LedgerFailure_ReturnsErrors()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        CreateSalesReturnCommand command = Command(sale);
        Error ledgerError = Error.Conflict("test.ledger", "Ledger refused.");
        _ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Failure(ledgerError));

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ledgerError);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ItemSpec Item(
        ProductId productId,
        string productName,
        decimal quantity = 1m,
        decimal price = 100m,
        decimal unitCost = 50m,
        BatchId? batchId = null,
        string? batchCode = null)
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
            BatchId: batchId,
            BatchCode: batchCode,
            BatchExpiresOn: null,
            unitCost,
            TracksBatches: batchId is not null);

    private static Sale NewSale(
        IReadOnlyList<ItemSpec>? items = null,
        IReadOnlyList<PaymentSpec>? payments = null,
        DeviceId? device = null)
        => Sale.Create(
            DocumentNumber.FromTrustedSource("SAL-2026-000001"),
            EventId.New(),
            TestStore,
            TestShiftId,
            device ?? TestDevice,
            customerId: null,
            BusinessDate,
            Now,
            TestCashier,
            items ?? [Item(Product, "Widget")],
            payments ?? [new PaymentSpec(PaymentMethod.Cash, 100m, 100m, ProviderReference: null)])
            .Value;

    private static CreateSalesReturnCommand Command(Sale sale, IReadOnlyList<SalesReturnLine>? lines = null) => new(
        DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 5),
        EventId.New(),
        sale.Id,
        sale.LocationId,
        sale.CashierShiftId,
        sale.DeviceId,
        CustomerId: null,
        sale.BusinessDate,
        Now.AddMinutes(30),
        ReturnedBy,
        lines ?? [new SalesReturnLine(Product, 1m)]);
}