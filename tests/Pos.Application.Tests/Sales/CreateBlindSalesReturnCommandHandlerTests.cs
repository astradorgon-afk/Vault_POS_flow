using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>
/// Tests <see cref="CreateBlindSalesReturnCommandHandler"/>: a manager accepts
/// goods back with no original sale under <c>sale.return_blind</c>. The handler
/// resolves every line's price and VAT class from the catalog, posts the
/// CustomerReturn movement from EXT-CUSTOMER's External bucket into the store's
/// ReturnPending bucket, and writes both the return audit and the mandatory
/// blind-acceptance exception audit, all persisted atomically.
/// </summary>
public sealed class CreateBlindSalesReturnCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);
    private static readonly LocationId TestStore = LocationId.New();
    private static readonly DeviceId TestDevice = DeviceId.New();
    private static readonly UserId TestCashier = UserId.New();
    private static readonly CashierShiftId TestShiftId = CashierShiftId.New();
    private static readonly UserId ReturnedBy = UserId.New();
    private static readonly UserId Manager = UserId.New();
    private static readonly UnitOfMeasureId Each = UnitOfMeasureId.New();

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IInventoryLedger _ledger = Substitute.For<IInventoryLedger>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly CashierShift _openShift;
    private readonly CreateBlindSalesReturnCommandHandler _handler;
    private readonly Product _product;

    public CreateBlindSalesReturnCommandHandlerTests()
    {
        _currentUser.DeviceId.Returns(DeviceId.New());
        _currentUser.CorrelationId.Returns(new CorrelationId(Guid.NewGuid()));

        _product = NewPricedProduct();

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

        _repository.GetSaleProductsAsync(Arg.Any<IReadOnlyCollection<ProductId>>(), Arg.Any<CancellationToken>())
            .Returns([_product]);
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(LocationId.New());
        _repository.GetLocationAsync(TestStore, Arg.Any<CancellationToken>())
            .Returns(new SaleLocationFacts(LocationKind.Store, LocationSettings.Default));
        _repository.AddReturnAsync(Arg.Any<SalesReturn>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<SalesReturnId>.Success(((SalesReturn)callInfo[0]).Id));

        _handler = new CreateBlindSalesReturnCommandHandler(
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
        CreateBlindSalesReturnCommand command = Command();

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        result.Value.Should().NotBe(SalesReturnId.Empty);
    }

    [Fact]
    public async Task HappyPath_PostsCustomerReturn_FromExternalToReturnPending()
    {
        LocationId externalId = LocationId.New();
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns(externalId);
        CreateBlindSalesReturnCommand command = Command();

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
                && g.Legs[0].LocationKind == LocationKind.Store
                && g.Legs[1].QuantityDelta == -1m
                && g.Legs[1].LocationId == externalId
                && g.Legs[1].State == InventoryState.External
                && g.Legs[1].LocationKind == LocationKind.External),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesTheExceptionAuditWithTheReason()
    {
        CreateBlindSalesReturnCommand command = Command();

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ReturnCreated
                && e.EntityType == "sales_return"
                && e.ReferenceDocumentType == ReferenceDocumentType.SalesReturn),
            Arg.Any<CancellationToken>());

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.BlindReturnAccepted
                && e.EntityType == "sales_return"
                && e.Reason == command.Reason
                && e.ReferenceDocumentType == ReferenceDocumentType.SalesReturn),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_PersistsABlindReturnAtTodaySPrice()
    {
        SalesReturn? saved = null;
        _repository.AddReturnAsync(Arg.Do<SalesReturn>(r => saved = r), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<SalesReturnId>.Success(((SalesReturn)callInfo[0]).Id));

        await _handler.HandleAsync(Command(), CancellationToken.None);

        saved!.Number.Should().StartWith("RET-");
        saved.IsBlind.Should().BeTrue();
        saved.SaleId.Should().BeNull();
        saved.CashierShiftId.Should().Be(TestShiftId);
        saved.RefundableTotal.Should().Be(100m);
        saved.Items.Should().ContainSingle();
        saved.Items[0].Quantity.Should().Be(1m);
        saved.Items[0].UnitPrice.Should().Be(100m);
        saved.Items[0].VatRate.Should().Be(0.12m);
        saved.Items[0].UnitCost.Should().Be(40m);
    }

    [Fact]
    public async Task PrimaryBarcode_IsFrozenOnTheLine()
    {
        _product.AddBarcode("4800111252222", Each, 1m, isPrimary: true, Manager).IsSuccess.Should().BeTrue();

        await _handler.HandleAsync(Command(), CancellationToken.None);

        await _repository.Received(1).AddReturnAsync(
            Arg.Is<SalesReturn>(r => r.Items[0].Barcode == "4800111252222"),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Catalog facts
    // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownProduct_ReturnsNotFound()
    {
        _repository.GetSaleProductsAsync(Arg.Any<IReadOnlyCollection<ProductId>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        CreateBlindSalesReturnCommand command = Command();

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.blind.product_unknown");
    }

    [Fact]
    public async Task ProductWithoutSellingPrice_ReturnsConflict()
    {
        Product unpriced = Product.Create(
            "OUT-OF-STOCK-01",
            "Unpriced",
            CategoryId.New(),
            Each,
            Manager).Value;
        _repository.GetSaleProductsAsync(Arg.Any<IReadOnlyCollection<ProductId>>(), Arg.Any<CancellationToken>())
            .Returns([unpriced]);
        CreateBlindSalesReturnCommand command = Command(unpriced.Id);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.blind.price_missing");
    }

    [Fact]
    public async Task UnknownLocation_ReturnsConflict()
    {
        LocationId other = LocationId.New();
        _repository.GetLocationAsync(other, Arg.Any<CancellationToken>())
            .Returns((SaleLocationFacts?)null);
        CreateBlindSalesReturnCommand command = Command() with { LocationId = other };

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.location_unknown");
    }

    [Fact]
    public async Task NonPositiveQuantity_FailsBeforeTheAggregate()
    {
        CreateBlindSalesReturnCommand command = Command(lines: [new BlindSalesReturnLine(_product.Id, 0m)]);

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.return.item.quantity_invalid");
    }

    // ------------------------------------------------------------------
    // RET number and shift validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task NumberWithoutDeviceCode_ReturnsInvalid()
    {
        CreateBlindSalesReturnCommand command = Command() with
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
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("ZZ9"));
        CreateBlindSalesReturnCommand command = Command();

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ReturnCommandErrors.NumberDeviceMismatch);
    }

    [Fact]
    public async Task ClosedShift_ReturnsConflict()
    {
        _openShift.DeclareCash(0m);
        _openShift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddMinutes(30));
        CreateBlindSalesReturnCommand command = Command();

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("sale.shift_not_open");
    }

    // ------------------------------------------------------------------
    // Movement failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExternalCustomerLocationMissing_ReturnsConflict()
    {
        _repository.GetExternalCustomerLocationIdAsync(Arg.Any<CancellationToken>())
            .Returns((LocationId?)null);
        CreateBlindSalesReturnCommand command = Command();

        Result<SalesReturnId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleCommandErrors.ExternalCustomerLocationMissing.Code);
    }

    [Fact]
    public async Task LedgerFailure_ReturnsErrors()
    {
        CreateBlindSalesReturnCommand command = Command();
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

    private static Product NewProduct(string sku, string name)
    {
        return Product.Create(
            sku,
            name,
            CategoryId.New(),
            Each,
            Manager,
            defaultPurchaseCost: 40m).Value;
    }

    /// <summary>Creates a product with an open-ended 100m price effective now.</summary>
    private static Product NewPricedProduct()
    {
        Product product = NewProduct("BLIND-001", "Widget");
        product.SchedulePrice(null, 100m, Now, null, Manager, "Launch", Now).IsSuccess.Should().BeTrue();
        return product;
    }

    private CreateBlindSalesReturnCommand Command(
        ProductId? productId = null,
        IReadOnlyList<BlindSalesReturnLine>? lines = null) => new(
        DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 5),
        EventId.New(),
        TestStore,
        TestShiftId,
        TestDevice,
        CustomerId: null,
        BusinessDate,
        Now.AddMinutes(30),
        ReturnedBy,
        "Customer could not produce the original receipt.",
        lines ?? [new BlindSalesReturnLine(productId ?? _product.Id, 1m)]);
}