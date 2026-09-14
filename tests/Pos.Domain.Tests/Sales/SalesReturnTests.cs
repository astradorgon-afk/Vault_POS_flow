using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>
/// The customer return's contract (POS.md §4): the factory freezes a
/// proportional snapshot of each returned sale line, caps every line at what
/// the sale still holds unreturned, and sets the refundable total that refunds
/// may draw against. A refund is capped per method by what the original sale
/// paid that way, and in total by the return's refundable total.
/// </summary>
public sealed class SalesReturnTests
{
    private static readonly UserId Cashier = UserId.New();
    private static readonly UserId ReturnedBy = UserId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly CashierShiftId Shift = CashierShiftId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly CashierShiftId RefundShift = CashierShiftId.New();
    private static readonly ProductId Product = ProductId.New();
    private static readonly UnitOfMeasureId Each = UnitOfMeasureId.New();
    private static readonly ProductPriceId PriceVersion = ProductPriceId.New();
    private static readonly BatchId Batch = BatchId.New();
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    private static DocumentNumber NewRetNumber() => DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D03", 1);

    private static ItemSpec Item(
        decimal price = 100m,
        decimal quantity = 1m,
        decimal discount = 0m,
        UserId? discountAuthorizedBy = null,
        decimal unitCost = 50m)
        => new(
            Product,
            "Sample SKU",
            Barcode: "4800111252222",
            quantity,
            Each,
            price,
            PriceVersion,
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            discount,
            DiscountAuthorizedByUserId: discountAuthorizedBy,
            VatRate: 0.12m,
            IsVatExempt: false,
            IsZeroRated: false,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            unitCost,
            TracksBatches: false);

    private static PaymentSpec Cash(decimal amount, decimal tendered)
        => new(PaymentMethod.Cash, amount, tendered, ProviderReference: null);

    private static PaymentSpec Card(decimal amount, string? reference = "TXN-001")
        => new(PaymentMethod.Card, amount, Tendered: null, reference);

    private static Sale SaleWith(
        IReadOnlyList<ItemSpec> items,
        IReadOnlyList<PaymentSpec> payments) => Sale.Create(
            DocumentNumber.Create(DocumentType.Sale, 2026, 1),
            EventId.New(),
            Store,
            Shift,
            Device,
            customerId: null,
            BusinessDate,
            Now,
            Cashier,
            items,
            payments,
            Sale.DefaultCashRoundingIncrement).Value;

    private static Result<SalesReturn> Create(
        Sale sale,
        IReadOnlyList<ReturnItemSpec>? items = null,
        IReadOnlyDictionary<SaleItemId, decimal>? alreadyReturned = null)
        => SalesReturn.Create(
            NewRetNumber(),
            EventId.New(),
            sale.Id,
            sale.LocationId,
            sale.CashierShiftId,
            sale.DeviceId,
            customerId: null,
            sale.BusinessDate,
            Now.AddMinutes(30),
            ReturnedBy,
            items ?? [new ReturnItemSpec(sale.Items[0], 1m)],
            alreadyReturned ?? sale.Items.ToDictionary(i => i.Id, i => i.ReturnedQuantity));

    // ------------------------------------------------------------------
    // SalesReturn.Create
    // ------------------------------------------------------------------

    [Fact]
    public void Create_ValidReturn_FreezesProportionalSnapshot()
    {
        // A 100-peso, 12% line at 2 units: net 200.00. Returning one unit must
        // freeze a snapshot worth half of it — 100.00 net, 10.7143 VAT-split
        // halves — and a refundable total of 100.00.
        Sale sale = SaleWith([Item(quantity: 2m)], [Cash(200m, 200m)]);
        SaleItem line = sale.Items[0];

        Result<SalesReturn> result = Create(sale, [new ReturnItemSpec(line, 1m)]);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        SalesReturn value = result.Value;
        value.Id.Should().NotBe(SalesReturnId.Empty);
        value.Number.Should().StartWith("RET-");
        value.EventId.Should().NotBe(EventId.Empty);
        value.SaleId.Should().Be(sale.Id);
        value.RefundableTotal.Should().Be(100m);

        SalesReturnItem item = value.Items.Should().ContainSingle().Subject;
        item.LineNumber.Should().Be(1);
        item.SaleItemId.Should().Be(line.Id);
        item.Quantity.Should().Be(1m);
        item.UnitPrice.Should().Be(100m);
        item.GrossAmount.Should().Be(100m);
        item.NetAmount.Should().Be(100m);
        item.RefundableAmount.Should().Be(100m);
        item.Vat.Should().Be(10.7143m);
        item.VatBase.Should().Be(89.2857m);
        item.UnitCost.Should().Be(50m);
    }

    [Fact]
    public void Create_InvalidQuantity_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);

        Result<SalesReturn> result = Create(sale, [new ReturnItemSpec(sale.Items[0], 0m)]);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.return.item.quantity_invalid");
    }

    [Fact]
    public void Create_ExceedingRemaining_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SaleItem line = sale.Items[0];
        line.AccumulateReturnedQuantity(0.5m);

        Result<SalesReturn> result = Create(sale, [new ReturnItemSpec(line, 0.6m)]);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.return.item.quantity_exceeds_remaining");
    }

    [Fact]
    public void Create_UnknownItem_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SaleItem foreign = SaleItem.Create(
            sale.Id,
            lineNumber: 99,
            new ItemSpec(
                ProductId.New(),
                "Other",
                Barcode: null,
                1m,
                Each,
                10m,
                ProductPriceId.New(),
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
                UnitCost: 5m,
                TracksBatches: false));

        Result<SalesReturn> result = Create(sale, [new ReturnItemSpec(foreign, 1m)]);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.return.item_unknown");
    }

    [Fact]
    public void Create_EmptyEvent_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);

        Result<SalesReturn> result = SalesReturn.Create(
            NewRetNumber(),
            EventId.Empty,
            sale.Id,
            sale.LocationId,
            sale.CashierShiftId,
            sale.DeviceId,
            customerId: null,
            sale.BusinessDate,
            Now,
            ReturnedBy,
            [new ReturnItemSpec(sale.Items[0], 1m)],
            sale.Items.ToDictionary(i => i.Id, i => i.ReturnedQuantity));

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.return.event_required");
    }

    [Fact]
    public void Create_DiscountedLine_RefundsProportionalNet()
    {
        // A 200-peso line with 50 written off: net 350 for 2 units. Returning
        // one unit refunds 175.00 — the discount is proportionally shared.
        Sale sale = SaleWith(
            [Item(price: 200m, quantity: 2m, discount: 50m, discountAuthorizedBy: Cashier)],
            [Cash(350m, 350m)]);

        Result<SalesReturn> result = Create(sale, [new ReturnItemSpec(sale.Items[0], 1m)]);

        result.IsSuccess.Should().BeTrue();
        SalesReturnItem item = result.Value.Items.Should().ContainSingle().Subject;
        item.Discount.Should().Be(25m);
        item.NetAmount.Should().Be(175m);
        item.RefundableAmount.Should().Be(175m);
    }

    // ------------------------------------------------------------------
    // Sale.RecordReturn (the server-side cap)
    // ------------------------------------------------------------------

    [Fact]
    public void RecordReturn_AccumulatesUpToTheSoldQuantity()
    {
        // Fractional returns must accumulate at quantity scale (3 dp); a
        // previous bug rounded at the decimal value's own scale, collapsing
        // 0.5-unit returns to zero.
        Sale sale = SaleWith([Item(quantity: 3m)], [Cash(300m, 300m)]);
        SaleItem line = sale.Items[0];

        sale.RecordReturn(line.Id, 0.5m).IsSuccess.Should().BeTrue();
        sale.RecordReturn(line.Id, 1m).IsSuccess.Should().BeTrue();
        sale.RecordReturn(line.Id, 1.5m).IsSuccess.Should().BeTrue();

        line.ReturnedQuantity.Should().Be(3m);
    }

    [Fact]
    public void RecordReturn_ExceedingTheSoldQuantity_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SaleItem line = sale.Items[0];

        Result result = sale.RecordReturn(line.Id, 1.5m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.item.return_exceeds_remaining");
    }

    [Fact]
    public void RecordReturn_OnUnknownLine_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);

        Result result = sale.RecordReturn(SaleItemId.New(), 1m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.item.return_unknown");
    }

    [Fact]
    public void RecordReturn_OnVoidedSale_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        sale.Void(Shift, BusinessDate, Now.AddMinutes(5), Cashier, "Voided");
        SaleItem line = sale.Items[0];

        Result result = sale.RecordReturn(line.Id, 1m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.return.only_completed");
    }

    // ------------------------------------------------------------------
    // SalesReturn.IssueRefund
    // ------------------------------------------------------------------

    private static SalesReturn ReturnedReturn(Sale sale, decimal quantity = 1m)
    {
        SalesReturn created = Create(sale, [new ReturnItemSpec(sale.Items[0], quantity)]).Value;
        return created;
    }

    [Fact]
    public void IssueRefund_Cash_IsAcceptedAndRounds()
    {
        Sale sale = SaleWith([Item(quantity: 2m)], [Cash(200m, 200m)]);
        SalesReturn salesReturn = ReturnedReturn(sale, quantity: 1m);

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Cash,
            100m,
            tendered: 100m,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        Refund refund = result.Value;
        refund.SalesReturnId.Should().Be(salesReturn.Id);
        refund.Method.Should().Be(PaymentMethod.Cash);
        refund.Amount.Should().Be(100m);
        refund.Tendered.Should().Be(100m);
        salesReturn.RefundedTotal.Should().Be(100m);
    }

    [Fact]
    public void IssueRefund_CashWithoutTendered_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Cash,
            50m,
            tendered: null,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.tendered_required");
    }

    [Fact]
    public void IssueRefund_CashTenderedShort_Fails()
    {
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Cash,
            100m,
            tendered: 99.50m,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.tendered_insufficient");
    }

    [Fact]
    public void IssueRefund_NonOriginalMethod_Fails()
    {
        // The sale was paid 100 cash; refunding card is refused because card was
        // never used to pay the original sale.
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            50m,
            tendered: null,
            providerReference: "TXN-002",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.method_not_original");
    }

    [Fact]
    public void IssueRefund_ExceedingMethodCap_Fails()
    {
        // The sale paid 150 cash and 50 card. Two returns refund the 50 card in
        // full; a third refund against card must be refused.
        Sale sale = SaleWith([Item(price: 200m)], [Cash(150m, 150m), Card(50m)]);

        SalesReturn first = ReturnedReturn(sale);
        first.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            25m,
            tendered: null,
            providerReference: "TXN-001",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m).IsSuccess.Should().BeTrue();

        Result<Refund> second = first.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            30m,
            tendered: null,
            providerReference: "TXN-002",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        second.IsFailure.Should().BeTrue();
        second.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_paid_for_method");
    }

    [Fact]
    public void IssueRefund_InMemoryRefunds_CountAgainstMethodCap()
    {
        // The sale was paid 100 card. Two refunds issued against the same
        // return push the cumulative card refunds to 110 — refused because
        // the in-memory _refunds accumulation is checked against the original
        // card payment.
        Sale sale = SaleWith([Item()], [Card(100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            40m,
            tendered: null,
            providerReference: "TXN-001",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m).IsSuccess.Should().BeTrue();

        Result<Refund> second = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            70m,
            tendered: null,
            providerReference: "TXN-002",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        second.IsFailure.Should().BeTrue();
        second.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_paid_for_method");
    }

    [Fact]
    public void IssueRefund_ExceedingRefundable_Fails()
    {
        // The sale paid 100 cash and 100 card; the return is worth 100. After
        // 90 cash is refunded, a 20 card refund stays under the card cap (20 of
        // 100) but pushes the total to 110 — refused on the refundable total.
        Sale sale = SaleWith([Item(quantity: 2m)], [Cash(100m, 100m), Card(100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Cash,
            90m,
            tendered: 90m,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m).IsSuccess.Should().BeTrue();

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Card,
            20m,
            tendered: null,
            providerReference: "TXN-002",
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(),
            cashRoundingIncrement: 0.05m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_refundable");
    }

    [Fact]
    public void IssueRefund_PriorReturnsFromOtherDocuments_CountAgainstMethod()
    {
        // The sale paid 100 cash. A prior RET already refunded 60 cash against
        // the same sale; refunding 50 more would push cumulative cash refunds
        // to 110 — refused by the cross-document priorRefundedByMethod map.
        Sale sale = SaleWith([Item()], [Cash(100m, 100m)]);
        SalesReturn salesReturn = ReturnedReturn(sale);

        Result<Refund> result = salesReturn.IssueRefund(
            EventId.New(),
            RefundShift,
            Device,
            PaymentMethod.Cash,
            50m,
            tendered: 50m,
            providerReference: null,
            Now.AddHours(1),
            ReturnedBy,
            SalePaidByMethod(sale),
            RefundedByMethod(PaymentMethod.Cash, 60m),
            cashRoundingIncrement: 0.05m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Code).Should().Contain("sale.refund.exceeds_paid_for_method");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<PaymentMethod, decimal> SalePaidByMethod(Sale sale)
        => sale.Payments
            .GroupBy(p => p.Method)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

    private static Dictionary<PaymentMethod, decimal> RefundedByMethod(
        PaymentMethod? method = null,
        decimal amount = 0m)
        => method is null
            ? new Dictionary<PaymentMethod, decimal>()
            : new Dictionary<PaymentMethod, decimal> { [method.Value] = amount };
}