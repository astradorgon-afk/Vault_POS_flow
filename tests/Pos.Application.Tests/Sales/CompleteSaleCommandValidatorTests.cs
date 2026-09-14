using FluentAssertions;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Validates <see cref="CompleteSaleCommandValidator"/>.</summary>
public sealed class CompleteSaleCommandValidatorTests
{
    private readonly CompleteSaleCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyEventId_IsRejected()
    {
        var command = ValidCommand() with { EventId = EventId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.EventRequired.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.LocationRequired.Code);
    }

    [Fact]
    public void EmptyCashierShiftId_IsRejected()
    {
        var command = ValidCommand() with { CashierShiftId = CashierShiftId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ShiftRequired.Code);
    }

    [Fact]
    public void EmptyDeviceId_IsRejected()
    {
        var command = ValidCommand() with { DeviceId = DeviceId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.DeviceRequired.Code);
    }

    [Fact]
    public void EmptyCashierId_IsRejected()
    {
        var command = ValidCommand() with { CashierId = UserId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.CashierRequired.Code);
    }

    [Fact]
    public void ZeroBusinessDate_IsRejected()
    {
        var command = ValidCommand() with { BusinessDate = default };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.BusinessDateRequired.Code);
    }

    [Fact]
    public void ZeroCompletedAt_IsRejected()
    {
        var command = ValidCommand() with { CompletedAtUtc = default };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.CompletedAtRequired.Code);
    }

    [Fact]
    public void EmptyLines_IsRejected()
    {
        var command = ValidCommand() with { Lines = [] };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemsRequired.Code);
    }

    [Fact]
    public void EmptyPayments_IsRejected()
    {
        var command = ValidCommand() with { Payments = [] };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.PaymentsRequired.Code);
    }

    [Fact]
    public void LineWithEmptyProductId_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { ProductId = ProductId.Empty }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemProductRequired.Code);
    }

    [Fact]
    public void LineWithZeroQuantity_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Quantity = 0m }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemQuantityInvalid.Code);
    }

    [Fact]
    public void LineWithNegativeQuantity_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Quantity = -1m }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemQuantityInvalid.Code);
    }

    [Fact]
    public void LineWithNegativeDiscount_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Discount = -5m }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemDiscountInvalid.Code);
    }

    [Fact]
    public void DiscountWithoutAuthorizer_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Discount = 5m, DiscountAuthorizedByUserId = null }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemDiscountAuthorizerRequired.Code);
    }

    [Fact]
    public void PriceOverrideWithoutAuthorizer_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { UnitPriceOverride = 90m, PriceOverrideAuthorizedByUserId = null }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemPriceOverrideAuthorizerRequired.Code);
    }

    [Fact]
    public void NegativePriceOverride_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { UnitPriceOverride = -1m }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemPriceNegative.Code);
    }

    [Fact]
    public void CashPaymentWithoutTendered_IsRejected()
    {
        var command = ValidCommand() with
        {
            Payments = [new CompleteSalePayment(PaymentMethod.Cash, 100m, null, null)],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.PaymentTenderedRequired.Code);
    }

    [Fact]
    public void PaymentWithZeroAmount_IsRejected()
    {
        var command = ValidCommand() with
        {
            Payments = [new CompleteSalePayment(PaymentMethod.Cash, 0m, 0m, null)],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.PaymentAmountInvalid.Code);
    }

    [Fact]
    public void BarcodeAtMaxLength_IsAccepted()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Barcode = new string('1', SaleItem.BarcodeMaxLength) }],
        };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void BarcodeOverMaxLength_IsRejected()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Barcode = new string('1', SaleItem.BarcodeMaxLength + 1) }],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ItemBarcodeTooLong(SaleItem.BarcodeMaxLength).Code);
    }

    [Fact]
    public void DiscountWithZeroAmount_PassesWithoutAuthorizer()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { Discount = 0m, DiscountAuthorizedByUserId = null }],
        };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void PriceOverrideWithAuthorizer_IsAccepted()
    {
        var command = ValidCommand() with
        {
            Lines = [ValidLine() with { UnitPriceOverride = 90m, PriceOverrideAuthorizedByUserId = UserId.New() }],
        };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CardPaymentWithoutTendered_IsAccepted()
    {
        var command = ValidCommand() with
        {
            Payments = [new CompleteSalePayment(PaymentMethod.Card, 100m, null, "REF-123")],
        };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ProviderReferenceOverMaxLength_IsRejected()
    {
        var command = ValidCommand() with
        {
            Payments = [new CompleteSalePayment(PaymentMethod.Card, 100m, null, new string('x', Payment.ProviderReferenceMaxLength + 1))],
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.PaymentReferenceTooLong(Payment.ProviderReferenceMaxLength).Code);
    }

    [Fact]
    public void EmptyNumber_IsRejected()
    {
        var command = ValidCommand() with { Number = default };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleCommandErrors.NumberInvalid.Code);
    }

    [Fact]
    public void CentralNumberedSale_IsRejected()
    {
        var command = ValidCommand() with
        {
            Number = DocumentNumber.Create(DocumentType.Sale, 2026, 1),
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleCommandErrors.NumberInvalid.Code);
    }

    private static CompleteSaleCommand ValidCommand() => new(
        DocumentNumber.CreateForDevice(DocumentType.Sale, 2026, "D01", 1),
        EventId.New(),
        LocationId.New(),
        CashierShiftId.New(),
        DeviceId.New(),
        UserId.New(),
        null,
        new DateOnly(2026, 9, 14),
        new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero),
        [ValidLine()],
        [new CompleteSalePayment(PaymentMethod.Cash, 100m, 100m, null)]);

    private static CompleteSaleLine ValidLine() => new(
        ProductId.New(),
        1m,
        UnitOfMeasureId.New(),
        null,
        null,
        null,
        0m,
        null,
        false);
}