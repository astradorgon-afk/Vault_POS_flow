using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>
/// The completed sale's creation contract (POS.md §2 and §3): the factory
/// freezes the resolved price and batch, computes per-line VAT and document
/// totals, and requires payments to cover the total due exactly. The SAL number
/// is allocated before the factory runs, by the application layer.
/// </summary>
public sealed class SaleTests
{
    private static readonly UserId Cashier = UserId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly CashierShiftId Shift = CashierShiftId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly ProductId Product = ProductId.New();
    private static readonly UnitOfMeasureId Each = UnitOfMeasureId.New();
    private static readonly ProductPriceId PriceVersion = ProductPriceId.New();
    private static readonly BatchId Batch = BatchId.New();
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DocumentNumber NewNumber()
        => DocumentNumber.Create(DocumentType.Sale, 2026, 1);

    private static ItemSpec Item(
        decimal price = 100m,
        decimal quantity = 1m,
        decimal discount = 0m,
        UserId? discountAuthorizedBy = null,
        bool priceWasOverridden = false,
        UserId? overrideAuthorizedBy = null,
        decimal? vatRate = 0.12m,
        bool isVatExempt = false,
        bool isZeroRated = false,
        BatchId? batchId = null,
        string? batchCode = null,
        DateOnly? batchExpiresOn = null,
        decimal unitCost = 50m,
        bool tracksBatches = false)
        => new(
            Product,
            "Sample SKU",
            Barcode: "4800111252222",
            quantity,
            Each,
            price,
            PriceVersion,
            priceWasOverridden,
            overrideAuthorizedBy,
            discount,
            discountAuthorizedBy,
            vatRate,
            isVatExempt,
            isZeroRated,
            batchId,
            batchCode,
            batchExpiresOn,
            unitCost,
            tracksBatches);

    private static PaymentSpec Cash(decimal amount, decimal tendered)
        => new(PaymentMethod.Cash, amount, tendered, ProviderReference: null);

    private static PaymentSpec Card(decimal amount, string? reference = "TXN-001")
        => new(PaymentMethod.Card, amount, Tendered: null, reference);

    private static Result<Sale> Create(
        IReadOnlyList<ItemSpec>? items = null,
        IReadOnlyList<PaymentSpec>? payments = null,
        EventId? eventId = null,
        LocationId? locationId = null,
        CashierShiftId? shift = null,
        DeviceId? device = null,
        decimal cashIncrement = Sale.DefaultCashRoundingIncrement)
        => Sale.Create(
            NewNumber(),
            eventId ?? EventId.New(),
            locationId ?? Store,
            shift ?? Shift,
            device ?? Device,
            customerId: null,
            BusinessDate,
            Now,
            Cashier,
            items ?? [Item()],
            payments ?? [Cash(100m, 100m)],
            cashIncrement);

    [Fact]
    public void Create_ValidCashSale_IsAcceptedWithComputedTotals()
    {
        // One vatable 100-peso item at 12%: the price is tax-inclusive, so the
        // total due is 100.00 with 10.7143 of that being VAT.
        Result<Sale> sale = Create(items: [Item()], payments: [Cash(100m, 100m)]);

        sale.IsSuccess.Should().BeTrue(because: string.Join("; ", sale.Errors.Select(e => e.Code)));
        Sale value = sale.Value;
        value.Id.Should().NotBe(SaleId.Empty);
        value.Number.Should().StartWith("SAL-");

        value.Status.Should().Be(SaleStatus.Completed);
        value.EventId.Should().NotBe(EventId.Empty);
        value.LocationId.Should().Be(Store);
        value.CashierShiftId.Should().Be(Shift);
        value.DeviceId.Should().Be(Device);
        value.BusinessDate.Should().Be(BusinessDate);
        value.CompletedAtUtc.Should().Be(Now);
        value.CompletedByUserId.Should().Be(Cashier);

        value.GrossTotal.Should().Be(100m);
        value.DiscountTotal.Should().Be(0m);
        value.NetTotal.Should().Be(100m);
        value.VatTotal.Should().Be(10.7143m);
        value.TaxableBaseTotal.Should().Be(89.2857m);
        value.VatExemptTotal.Should().Be(0m);
        value.ZeroRatedTotal.Should().Be(0m);

        SaleItem line = value.Items.Should().ContainSingle().Subject;
        line.LineNumber.Should().Be(1);
        line.Quantity.Should().Be(1m);
        line.UnitPrice.Should().Be(100m);
        line.PriceVersion.Should().Be(PriceVersion);
        line.GrossAmount.Should().Be(100m);
        line.NetAmount.Should().Be(100m);
        line.VatBase.Should().Be(89.2857m);
        line.Vat.Should().Be(10.7143m);
        line.VatRate.Should().Be(0.12m);

        Payment payment = value.Payments.Should().ContainSingle().Subject;
        payment.Method.Should().Be(PaymentMethod.Cash);
        payment.Amount.Should().Be(100m);
        payment.Tendered.Should().Be(100m);
        payment.Change.Should().Be(0m);
    }

    [Fact]
    public void Create_BatchTrackedItem_FreezesBatchIdentity()
    {
        DateOnly expiresOn = new(2026, 12, 31);
        Result<Sale> sale = Create(
            items:
            [
                Item(
                    batchId: Batch,
                    batchCode: "LOT-2026-A",
                    batchExpiresOn: expiresOn,
                    unitCost: 42.5m,
                    tracksBatches: true),
            ],
            payments: [Cash(100m, 100m)]);

        sale.IsSuccess.Should().BeTrue(because: string.Join("; ", sale.Errors.Select(e => e.Code)));
        SaleItem line = sale.Value.Items.Should().ContainSingle().Subject;
        line.BatchId.Should().Be(Batch);
        line.BatchCode.Should().Be("LOT-2026-A");
        line.BatchExpiresOn.Should().Be(expiresOn);
        line.UnitCost.Should().Be(42.5m);
    }

    [Fact]
    public void Create_ItemSnapshots_AreTrimmed()
    {
        Result<Sale> sale = Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            customerId: null,
            BusinessDate,
            Now,
            Cashier,
            items: [Item() with { ProductName = "  Sample SKU  ", Barcode = "  4800111252222  " }],
            payments: [Cash(100m, 100m)]);

        sale.IsSuccess.Should().BeTrue();
        SaleItem line = sale.Value.Items.Should().ContainSingle().Subject;
        line.ProductName.Should().Be("Sample SKU");
        line.Barcode.Should().Be("4800111252222");
    }

    [Fact]
    public void Create_DiscountLine_RecordsAuthorizerAndReducesTotal()
    {
        Result<Sale> sale = Create(
            items:
            [
                Item(discount: 10m, discountAuthorizedBy: Cashier),
            ],
            payments: [Cash(90m, 90m)]);

        sale.IsSuccess.Should().BeTrue(because: string.Join("; ", sale.Errors.Select(e => e.Code)));
        Sale value = sale.Value;
        value.GrossTotal.Should().Be(100m);
        value.DiscountTotal.Should().Be(10m);
        value.NetTotal.Should().Be(90m);

        SaleItem line = value.Items.Should().ContainSingle().Subject;
        line.Discount.Should().Be(10m);
        line.DiscountAuthorizedByUserId.Should().Be(Cashier);
    }

    [Fact]
    public void Create_PriceOverride_RecordsAuthorizingUser()
    {
        Result<Sale> sale = Create(
            items:
            [
                Item(price: 120m, priceWasOverridden: true, overrideAuthorizedBy: Cashier),
            ],
            payments: [Cash(120m, 120m)]);

        sale.IsSuccess.Should().BeTrue();
        SaleItem line = sale.Value.Items.Should().ContainSingle().Subject;
        line.PriceWasOverridden.Should().BeTrue();
        line.PriceOverrideAuthorizedByUserId.Should().Be(Cashier);
    }

    [Fact]
    public void Create_ExemptLine_IsTrackedSeparatelyWithNoTax()
    {
        Result<Sale> sale = Create(
            items: [Item(isVatExempt: true)],
            payments: [Cash(100m, 100m)]);

        sale.IsSuccess.Should().BeTrue();
        Sale value = sale.Value;
        value.NetTotal.Should().Be(100m);
        value.VatTotal.Should().Be(0m);
        value.VatExemptTotal.Should().Be(100m);
        value.TaxableBaseTotal.Should().Be(0m);

        SaleItem line = value.Items.Should().ContainSingle().Subject;
        line.Vat.Should().Be(0m);
        line.VatBase.Should().Be(0m);
        line.VatRate.Should().BeNull();
    }

    [Fact]
    public void Create_ZeroRatedLine_IsTrackedSeparatelyWithNoTax()
    {
        Result<Sale> sale = Create(
            items: [Item(isZeroRated: true, vatRate: null)],
            payments: [Cash(100m, 100m)]);

        sale.IsSuccess.Should().BeTrue();
        Sale value = sale.Value;
        value.NetTotal.Should().Be(100m);
        value.VatTotal.Should().Be(0m);
        value.ZeroRatedTotal.Should().Be(100m);
        value.TaxableBaseTotal.Should().Be(0m);

        SaleItem line = value.Items.Should().ContainSingle().Subject;
        line.Vat.Should().Be(0m);
        line.VatBase.Should().Be(100m);
        line.VatRate.Should().BeNull();
    }

    [Fact]
    public void Create_SplitPayments_MustCoverTheTotalExactly()
    {
        Result<Sale> sale = Create(
            items: [Item(price: 100m, quantity: 2m)],
            payments: [Cash(100m, 200m), Card(100m, "TXN-002")]);

        sale.IsSuccess.Should().BeTrue();
        IReadOnlyList<Payment> payments = sale.Value.Payments;
        payments.Should().HaveCount(2);
        payments[0].Amount.Should().Be(100m);
        payments[0].Change.Should().Be(100m);
        payments[1].Method.Should().Be(PaymentMethod.Card);
        payments[1].Amount.Should().Be(100m);
        payments[1].ProviderReference.Should().Be("TXN-002");
    }

    [Fact]
    public void Create_CashChange_RoundsToConfiguredIncrement()
    {
        // The sale is 100.03 (the tax-inclusive price of one item); the customer
        // tenders 1,000. Change on a 5-centavo increment is 899.95, not 899.97.
        Result<Sale> sale = Create(
            items: [Item(price: 100.03m)],
            payments: [Cash(100.03m, 1000m)],
            cashIncrement: 0.05m);

        sale.IsSuccess.Should().BeTrue();
        Payment cash = sale.Value.Payments.Should().ContainSingle().Subject;
        cash.Tendered.Should().Be(1000m);
        cash.Change.Should().Be(899.95m);
    }

    [Fact]
    public void Create_CashTenderedBelowAmount_IsRejected()
    {
        Result<Sale> sale = Create(payments: [Cash(112m, 100m)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment.tendered_insufficient");
    }

    [Fact]
    public void Create_CashPaymentWithoutTender_IsRejected()
    {
        Result<Sale> sale = Create(payments: [new PaymentSpec(PaymentMethod.Cash, 112m, Tendered: null, null)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment.tendered_required");
    }

    [Fact]
    public void Create_PaymentsThatDoNotCoverTheTotal_AreRejected()
    {
        Result<Sale> sale = Create(payments: [Cash(90m, 90m)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment_mismatch");
        sale.Error.Metadata!["expected"].Should().Be(100m);
    }

    [Fact]
    public void Create_PaymentOverTheTotal_IsRejected()
    {
        Result<Sale> sale = Create(payments: [Cash(120m, 120m)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment_mismatch");
    }

    [Fact]
    public void Create_CardPaymentWithLongReference_IsRejected()
    {
        Result<Sale> sale = Create(payments: [Card(112m, new string('x', Payment.ProviderReferenceMaxLength + 1))]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment.reference_too_long");
    }

    [Fact]
    public void Create_UnknownPaymentMethod_IsRejected()
    {
        Result<Sale> sale = Create(payments: [new PaymentSpec((PaymentMethod)99, 112m, null, null)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment.method_unknown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_NonPositivePaymentAmount_IsRejected(decimal amount)
    {
        Result<Sale> sale = Create(payments: [Cash(amount, 10m)]);

        sale.IsFailure.Should().BeTrue();
        sale.Error.Code.Should().Be("sale.payment.amount_invalid");
    }

    [Fact]
    public void Create_InvalidHeader_IsRejected()
    {
        Sale.Create(
            NewNumber(),
            EventId.Empty,
            Store,
            Shift,
            Device,
            null,
            BusinessDate,
            Now,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.event_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            LocationId.Empty,
            Shift,
            Device,
            null,
            BusinessDate,
            Now,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.location_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            CashierShiftId.Empty,
            Device,
            null,
            BusinessDate,
            Now,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.shift_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            DeviceId.Empty,
            null,
            BusinessDate,
            Now,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.device_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            null,
            default,
            Now,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.business_date_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            null,
            BusinessDate,
            default,
            Cashier,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.completed_at_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            null,
            BusinessDate,
            Now,
            UserId.Empty,
            [Item()],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.cashier_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            null,
            BusinessDate,
            Now,
            Cashier,
            [],
            [Cash(112m, 112m)]).Error.Code.Should().Be("sale.items_required");

        Sale.Create(
            NewNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            null,
            BusinessDate,
            Now,
            Cashier,
            [Item()],
            []).Error.Code.Should().Be("sale.payments_required");
    }

    [Fact]
    public void Create_InvalidItem_IsRejected()
    {
        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item() with { ProductId = ProductId.Empty }], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.product_required");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item() with { ProductName = new string('x', SaleItem.ProductNameMaxLength + 1) }], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.product_name_invalid");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item() with { Barcode = new string('x', SaleItem.BarcodeMaxLength + 1) }], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.barcode_too_long");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(quantity: 0m)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.quantity_invalid");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(price: -1m)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.price_negative");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(price: 100m, discount: 200m)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.discount_invalid");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(price: 100m, discount: 10m)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.discount_authorizer_required");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(price: 100m, priceWasOverridden: true)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.price_override_authorizer_required");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item() with { PriceVersion = ProductPriceId.Empty }], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.price_version_required");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(vatRate: null)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.vat_rate_missing");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(isVatExempt: true, isZeroRated: true)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.vat_class_conflict");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(tracksBatches: true)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.batch_missing");

        Sale.Create(
            NewNumber(), EventId.New(), Store, Shift, Device, null, BusinessDate, Now, Cashier,
            [Item(batchId: Batch)], [Cash(112m, 112m)])
            .Error.Code.Should().Be("sale.item.batch_unexpected");
    }

    // ------------------------------------------------------------------
    // Voids (POS.md §4.1)
    // ------------------------------------------------------------------

    [Fact]
    public void Void_CompletedSale_FlipsStatusAndStampsTheVoid()
    {
        UserId manager = UserId.New();
        DateTimeOffset voidedAt = new(2026, 9, 14, 12, 30, 0, TimeSpan.Zero);
        Result<Sale> created = Create();

        Result voided = created.Value.Void(Shift, BusinessDate, voidedAt, manager, "Wrong item scanned");

        voided.IsSuccess.Should().BeTrue();
        created.Value.Status.Should().Be(SaleStatus.Voided);
        created.Value.VoidedAtUtc.Should().Be(voidedAt);
        created.Value.VoidedByUserId.Should().Be(manager);
        created.Value.VoidReason.Should().Be("Wrong item scanned");
    }

    [Fact]
    public void Void_TrimsTheReason()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(Shift, BusinessDate, Now, UserId.New(), "  wrong item  ");

        voided.IsSuccess.Should().BeTrue();
        created.Value.VoidReason.Should().Be("wrong item");
    }

    [Fact]
    public void Void_AlreadyVoided_ReturnsConflict()
    {
        UserId manager = UserId.New();
        Result<Sale> created = Create();
        created.Value.Void(Shift, BusinessDate, Now, manager, "First void");

        Result second = created.Value.Void(Shift, BusinessDate, Now, manager, "Second void");

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("sale.void.only_completed");
    }

    [Fact]
    public void Void_DifferentShift_ReturnsConflict()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(CashierShiftId.New(), BusinessDate, Now, UserId.New(), "Why");

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be("sale.void.shift_mismatch");
    }

    [Fact]
    public void Void_DifferentBusinessDate_ReturnsConflict()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(
            Shift, BusinessDate.AddDays(1), Now, UserId.New(), "Why");

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be("sale.void.business_date_mismatch");
    }

    [Fact]
    public void Void_DefaultStamp_ReturnsValidation()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(Shift, BusinessDate, default, UserId.New(), "Why");

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be("sale.void.stamp_required");
    }

    [Fact]
    public void Void_EmptyUser_ReturnsValidation()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(Shift, BusinessDate, Now, UserId.Empty, "Why");

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be("sale.void.by_required");
    }

    [Fact]
    public void Void_BlankReason_ReturnsValidation()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(Shift, BusinessDate, Now, UserId.New(), "   ");

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be(SaleErrors.VoidReasonInvalid(Sale.VoidReasonMaxLength).Code);
    }

    [Fact]
    public void Void_TooLongReason_ReturnsValidation()
    {
        Result<Sale> created = Create();

        Result voided = created.Value.Void(
            Shift,
            BusinessDate,
            Now,
            UserId.New(),
            new string('x', Sale.VoidReasonMaxLength + 1));

        voided.IsFailure.Should().BeTrue();
        voided.Error.Code.Should().Be(SaleErrors.VoidReasonInvalid(Sale.VoidReasonMaxLength).Code);
    }
}