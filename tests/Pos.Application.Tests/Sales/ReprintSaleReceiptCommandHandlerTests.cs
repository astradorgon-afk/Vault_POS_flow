using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Tests <see cref="ReprintSaleReceiptCommandHandler"/>.</summary>
public sealed class ReprintSaleReceiptCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);
    private static readonly LocationId TestStore = LocationId.New();
    private static readonly DeviceId TestDevice = DeviceId.New();
    private static readonly CashierShiftId TestShiftId = CashierShiftId.New();
    private static readonly UserId TestCashier = UserId.New();
    private static readonly UserId Manager = UserId.New();

    private readonly ISalesRepository _repository = Substitute.For<ISalesRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private readonly ReprintSaleReceiptCommandHandler _handler;

    public ReprintSaleReceiptCommandHandlerTests()
    {
        _repository.AddReceiptPrintAsync(Arg.Any<SaleReceiptPrint>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<ReceiptPrintId>.Success(((SaleReceiptPrint)callInfo[0]).Id));

        _handler = new ReprintSaleReceiptCommandHandler(_repository, _audit);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsTheSaleId()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        Result<SaleId> result = await _handler.HandleAsync(Command(sale), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(sale.Id);
    }

    [Fact]
    public async Task HappyPath_AppendsAPrintLogEntry_WithReason()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.HandleAsync(Command(sale), CancellationToken.None);

        await _repository.Received(1).AddReceiptPrintAsync(
            Arg.Is<SaleReceiptPrint>(p =>
                p.SaleId == sale.Id
                && p.IsReprint
                && p.Reason == "Printer jam; re-emitting the customer copy"
                && p.PrintedByUserId == Manager),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesTheReceiptReprintedAudit()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.HandleAsync(Command(sale), CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a =>
                a.Action == AuditActions.Sales.ReceiptReprinted
                && a.EntityId == sale.Id.Value
                && a.LocationId == TestStore
                && a.Reason == "Printer jam; re-emitting the customer copy"
                && a.ReferenceDocumentType == ReferenceDocumentType.Sale
                && a.ReferenceDocumentId == sale.Id.Value),
            Arg.Any<CancellationToken>());

        // The reprint is read-side only: no sale mutation, no ledger movement.
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // Failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaleNotFound_ReturnsNotFound()
    {
        _repository.GetByIdAsync(Arg.Any<SaleId>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        Result<SaleId> result = await _handler.HandleAsync(
            Command(NewSale()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReprintCommandErrors.SaleNotFound(SaleId.Empty).Code);
    }

    [Fact]
    public async Task LocationMismatch_ReturnsConflict()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        Result<SaleId> result = await _handler.HandleAsync(
            Command(sale) with { LocationId = LocationId.New() },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReprintCommandErrors.LocationMismatch(sale.Id, locationId: LocationId.Empty).Code);
    }

    [Fact]
    public async Task VoidedSale_HasNoReceipt_ReturnsConflict()
    {
        Sale sale = NewSale();
        sale.Void(
            TestShiftId,
            sale.BusinessDate,
            Now.AddHours(1),
            Manager,
            "Same-day cancellation");
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        Result<SaleId> result = await _handler.HandleAsync(Command(sale), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReprintCommandErrors.UnreprintableState(sale.Id, SaleStatus.Voided).Code);
        await _repository.DidNotReceive().AddReceiptPrintAsync(Arg.Any<SaleReceiptPrint>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryFailure_ReturnsErrors()
    {
        Sale sale = NewSale();
        _repository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);
        Error saveError = Error.Conflict("test.print", "The print log refused the entry.");
        _repository.AddReceiptPrintAsync(Arg.Any<SaleReceiptPrint>(), Arg.Any<CancellationToken>())
            .Returns(Result<ReceiptPrintId>.Failure(saveError));

        Result<SaleId> result = await _handler.HandleAsync(Command(sale), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(saveError.Code);
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
                DocumentNumber.FromTrustedSource("SAL-2026-000100"),
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

    private static ReprintSaleReceiptCommand Command(Sale sale) => new(
        sale.Id,
        sale.LocationId,
        sale.DeviceId,
        "Printer jam; re-emitting the customer copy",
        Now.AddMinutes(5),
        Manager);
}