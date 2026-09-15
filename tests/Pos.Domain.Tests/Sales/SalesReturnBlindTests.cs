using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>
/// The blind customer return's contract (POS.md §4): goods accepted back with
/// no original sale under <c>sale.return_blind</c>. The factory freezes the
/// catalog facts the handler resolved instead of a sale line's snapshot — value
/// at today's shelf price — requires the exception reason that makes the
/// acceptance accountable, and sets the refundable total like a referenced
/// return, so a later refund against a blind return remains capped by what the
/// acceptance accepted.
/// </summary>
public sealed class SalesReturnBlindTests
{
    private static readonly UserId ReturnedBy = UserId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly CashierShiftId Shift = CashierShiftId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly ProductId Product = ProductId.New();
    private static readonly UnitOfMeasureId Each = UnitOfMeasureId.New();
    private static readonly DateOnly BusinessDate = new(2026, 9, 15);
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static DocumentNumber NewRetNumber() => DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 1);

    private static BlindReturnItemSpec Item(
        decimal price = 100m,
        decimal quantity = 1m,
        decimal unitCost = 50m,
        decimal? vatRate = 0.12m,
        bool isVatExempt = false,
        bool isZeroRated = false)
        => new(
            Product,
            "Sample SKU",
            Barcode: "4800111252222",
            quantity,
            Each,
            price,
            vatRate,
            isVatExempt,
            isZeroRated,
            unitCost);

    private static Result<SalesReturn> CreateBlind(
        string? reason = "Customer could not produce the original receipt.",
        IReadOnlyList<BlindReturnItemSpec>? items = null)
        => SalesReturn.CreateBlind(
            NewRetNumber(),
            EventId.New(),
            Store,
            Shift,
            Device,
            customerId: null,
            BusinessDate,
            Now,
            ReturnedBy,
            reason!,
            items ?? [Item()]);

    [Fact]
    public void CreateBlind_WithoutSale_AcceptsBackAtTodaySPrice()
    {
        Result<SalesReturn> created = CreateBlind();

        created.IsSuccess.Should().BeTrue();

        SalesReturn salesReturn = created.Value;
        salesReturn.SaleId.Should().BeNull();
        salesReturn.IsBlind.Should().BeTrue();
        salesReturn.Number.Should().Be("RET-2026-D03-000001");

        SalesReturnItem line = salesReturn.Items.Should().ContainSingle().Subject;
        line.SaleItemId.Should().BeNull();
        line.ProductId.Should().Be(Product);
        line.ProductName.Should().Be("Sample SKU");
        line.Barcode.Should().Be("4800111252222");
        line.UnitPrice.Should().Be(100m);
        line.Quantity.Should().Be(1m);
        line.UnitCost.Should().Be(50m);
    }

    [Fact]
    public void CreateBlind_VatableLine_SplitsTaxInclusiveLikeASale()
    {
        Result<SalesReturn> created = CreateBlind(items: [Item(price: 112m, quantity: 2m, vatRate: 0.12m)]);

        created.IsSuccess.Should().BeTrue();

        SalesReturnItem line = created.Value.Items.Single();
        line.GrossAmount.Should().Be(224m);
        line.NetAmount.Should().Be(224m);
        line.VatRate.Should().Be(0.12m);
        line.VatBase.Should().Be(200m);
        line.Vat.Should().Be(24m);
        line.RefundableAmount.Should().Be(224m);
        created.Value.RefundableTotal.Should().Be(224m);
    }

    [Fact]
    public void CreateBlind_ExemptLine_ZeroBaseAndZeroVat()
    {
        Result<SalesReturn> created = CreateBlind(items: [Item(vatRate: null, isVatExempt: true)]);

        created.IsSuccess.Should().BeTrue();

        SalesReturnItem line = created.Value.Items.Single();
        line.IsVatExempt.Should().BeTrue();
        line.VatRate.Should().BeNull();
        line.VatBase.Should().Be(0m);
        line.Vat.Should().Be(0m);
        line.RefundableAmount.Should().Be(100m);
    }

    [Fact]
    public void CreateBlind_ZeroRatedLine_VatBaseIsTheNet()
    {
        Result<SalesReturn> created = CreateBlind(items: [Item(vatRate: null, isZeroRated: true)]);

        created.IsSuccess.Should().BeTrue();

        SalesReturnItem line = created.Value.Items.Single();
        line.IsZeroRated.Should().BeTrue();
        line.VatRate.Should().BeNull();
        line.VatBase.Should().Be(100m);
        line.Vat.Should().Be(0m);
    }

    [Fact]
    public void CreateBlind_MultipleLines_AccumulatesRefundableTotalInLineOrder()
    {
        ProductId secondProduct = ProductId.New();

        Result<SalesReturn> created = CreateBlind(items:
        [
            Item(price: 100m, quantity: 1m),
            new BlindReturnItemSpec(
                secondProduct,
                "Second SKU",
                Barcode: null,
                3m,
                Each,
                50m,
                VatRate: null,
                IsVatExempt: true,
                IsZeroRated: false,
                UnitCost: 20m),
        ]);

        created.IsSuccess.Should().BeTrue();

        SalesReturn salesReturn = created.Value;
        salesReturn.Items.Should().HaveCount(2);
        salesReturn.Items[0].LineNumber.Should().Be(1);
        salesReturn.Items[1].LineNumber.Should().Be(2);
        salesReturn.Items[1].Barcode.Should().BeNull();
        salesReturn.RefundableTotal.Should().Be(250m);
    }

    [Fact]
    public void CreateBlind_BlankReason_Rejects()
    {
        Result<SalesReturn> created = CreateBlind(reason: "   ");

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("sale.return.blind.reason_required");
    }

    [Fact]
    public void CreateBlind_LongReason_Rejects()
    {
        Result<SalesReturn> created = CreateBlind(reason: new string('x', SalesReturn.BlindReasonMaxLength + 1));

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("sale.return.blind.reason_invalid");
    }

    [Fact]
    public void CreateBlind_ExactlyMaxLengthReason_Accepts()
    {
        Result<SalesReturn> created = CreateBlind(reason: new string('x', SalesReturn.BlindReasonMaxLength));

        created.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CreateBlind_NoLines_Rejects()
    {
        Result<SalesReturn> created = CreateBlind(items: []);

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("sale.return.items_required");
    }

    [Fact]
    public void CreateBlind_EmptyProduct_Rejects()
    {
        BlindReturnItemSpec anonymousLine = new(
            ProductId.Empty,
            "No product",
            Barcode: null,
            1m,
            Each,
            100m,
            VatRate: null,
            IsVatExempt: false,
            IsZeroRated: false,
            UnitCost: 10m);

        Result<SalesReturn> created = CreateBlind(items: [anonymousLine]);

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("sale.return.blind.product_required");
    }

    [Fact]
    public void CreateBlind_NonPositiveQuantity_Rejects()
    {
        Result<SalesReturn> created = CreateBlind(items: [Item(quantity: 0m)]);

        created.IsFailure.Should().BeTrue();
        created.Error.Code.Should().Be("sale.return.item.quantity_invalid");
    }

    // ------------------------------------------------------------------
    // IssueBlindRefund: the refund a later return of accepted goods hands
    // back. A blind return has no original sale, so its refund is cash-only
    // and capped only by the return's own refundable total.
    // ------------------------------------------------------------------

    [Fact]
    public void IssueBlindRefund_CashRefund_AppliesAgainstRefundableTotal()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, amount: 50m, tendered: 50m);

        issued.IsSuccess.Should().BeTrue();
        issued.Value.Method.Should().Be(PaymentMethod.Cash);
        issued.Value.Amount.Should().Be(50m);
        issued.Value.Tendered.Should().Be(50m);
        issued.Value.SalesReturnId.Should().Be(salesReturn.Id);
        issued.Value.CashierShiftId.Should().Be(Shift);
        issued.Value.DeviceId.Should().Be(Device);
        salesReturn.Refunds.Should().ContainSingle();
        salesReturn.RefundedTotal.Should().Be(50m);
    }

    [Fact]
    public void IssueBlindRefund_CardMethod_RejectsAsCashOnly()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, method: PaymentMethod.Card, amount: 50m, tendered: null);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.blind.cash_only");
    }

    [Fact]
    public void IssueBlindRefund_EWalletMethod_RejectsAsCashOnly()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, method: PaymentMethod.EWallet, amount: 50m, tendered: null);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.blind.cash_only");
    }

    [Fact]
    public void IssueBlindRefund_OnReferencedReturn_RejectsAsBlindOnly()
    {
        SalesReturn referenced = NewReferencedReturn();

        Result<Refund> issued = BlindRefund(referenced, amount: 50m, tendered: 50m);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.blind_only");
    }

    [Fact]
    public void IssueBlindRefund_CashWithoutTendered_Rejects()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, tendered: null);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.tendered_required");
    }

    [Fact]
    public void IssueBlindRefund_TenderedShort_Rejects()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, amount: 50m, tendered: 40m);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.tendered_insufficient");
    }

    [Fact]
    public void IssueBlindRefund_NonPositiveIncrement_Rejects()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, increment: 0m);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.increment_invalid");
    }

    [Fact]
    public void IssueBlindRefund_ExceedingRefundableTotal_Rejects()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = BlindRefund(salesReturn, amount: 112m, tendered: 112m);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.exceeds_refundable");
        salesReturn.Refunds.Should().BeEmpty();
    }

    [Fact]
    public void IssueBlindRefund_MultipleRefunds_AccumulateToRefundableTotal()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> first = BlindRefund(salesReturn, amount: 60m, tendered: 60m);
        Result<Refund> second = BlindRefund(salesReturn, amount: 40m, tendered: 40m);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        salesReturn.RefundedTotal.Should().Be(100m);

        Result<Refund> over = BlindRefund(salesReturn, amount: 1m, tendered: 1m);

        over.IsFailure.Should().BeTrue();
        over.Error.Code.Should().Be("sale.refund.exceeds_refundable");
    }

    [Fact]
    public void IssueBlindRefund_EmptyEvent_Rejects()
    {
        SalesReturn salesReturn = CreateBlind().Value;

        Result<Refund> issued = salesReturn.IssueBlindRefund(
            EventId.Empty,
            Shift,
            Device,
            PaymentMethod.Cash,
            50m,
            tendered: 50m,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            0.05m);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Code.Should().Be("sale.refund.event_required");
    }

    private static Result<Refund> BlindRefund(
        SalesReturn salesReturn,
        PaymentMethod method = PaymentMethod.Cash,
        decimal amount = 50m,
        decimal? tendered = 50m,
        decimal increment = 0.05m)
        => salesReturn.IssueBlindRefund(
            EventId.New(),
            Shift,
            Device,
            method,
            amount,
            tendered,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            increment);

    private static SalesReturn NewReferencedReturn()
    {
        DateOnly businessDate = new(2026, 9, 15);
        DateTimeOffset now = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
        UserId cashier = UserId.New();

        ItemSpec item = new(
            Product,
            "Sample SKU",
            Barcode: null,
            Quantity: 1m,
            Each,
            100m,
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

        Sale sale = Sale.Create(
            DocumentNumber.FromTrustedSource("SAL-2026-000001"),
            EventId.New(),
            Store,
            Shift,
            Device,
            customerId: null,
            businessDate,
            now,
            cashier,
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
            now.AddMinutes(30),
            ReturnedBy,
            [new ReturnItemSpec(sale.Items[0], 1m)],
            sale.Items.ToDictionary(i => i.Id, _ => 0m))
            .Value;
    }
}
