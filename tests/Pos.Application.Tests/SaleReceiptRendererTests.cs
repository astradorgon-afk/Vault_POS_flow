using FluentAssertions;
using Pos.Application.Receipts;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Tests;

/// <summary>
/// A sale receipt carries what the branch configured: its trading name, address
/// and TIN on top, its return policy at the bottom, in every format a register
/// prints or shows.
/// </summary>
public sealed class SaleReceiptRendererTests
{
    private const string Header = "Suki Mart Legazpi Village, Makati\nRufino St., Legazpi Village, Makati City\nVAT REG TIN 009-482-715-000-0010";
    private const string Footer = "Salamat po! Keep this receipt for returns within 7 days.\nThis serves as your official receipt.";

    [Fact]
    public void PlainText_PrintsTheHeaderFirstAndTheFooterLast()
    {
        string text = SaleReceiptRenderer.RenderPlainText(Sample(), "Legazpi Village", "Asia/Manila", "Joy Mendoza", header: Header, footer: Footer);
        string[] lines = text.TrimEnd('\n').Split('\n');

        lines[0].Should().Be("Suki Mart Legazpi Village, Makati");
        lines[2].Should().Be("VAT REG TIN 009-482-715-000-0010");
        lines[^2].Should().Be("Salamat po! Keep this receipt for returns within 7 days.");
        lines[^1].Should().Be("This serves as your official receipt.");
        text.Should().NotContain("Thank you.");
    }

    [Fact]
    public void Thermal_WrapsLongLinesToThePaperWidth()
    {
        string text = SaleReceiptRenderer.Render(
            Sample(), "Legazpi Village", "Asia/Manila", "Joy Mendoza", ReceiptFormat.Thermal, header: Header, footer: Footer);

        text.TrimEnd('\n').Split('\n').Should().OnlyContain(line => line.Length <= ReceiptThermal.Width);
        text.Should().Contain("Salamat po! Keep this receipt for returns");
        text.Should().Contain("within 7 days.");
    }

    [Fact]
    public void Html_EscapesTheConfiguredText()
    {
        string html = SaleReceiptRenderer.Render(
            Sample(), "Legazpi Village", "Asia/Manila", "Joy Mendoza", ReceiptFormat.Html, header: "Tom & Jerry's <Store>", footer: "Bye <b>now</b>");

        html.Should().Contain("Tom &amp; Jerry&#39;s &lt;Store&gt;");
        html.Should().Contain("Bye &lt;b&gt;now&lt;/b&gt;");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n  ")]
    public void WithoutConfiguredText_TheReceiptKeepsItsDefaultThanks(string? footer)
    {
        string text = SaleReceiptRenderer.RenderPlainText(Sample(), "Legazpi Village", "Asia/Manila", "Joy Mendoza", header: footer, footer: footer);

        text.Should().StartWith("SALE RECEIPT");
        text.TrimEnd('\n').Split('\n')[^1].Should().Be("Thank you.");
    }

    private static Sale Sample()
    {
        ItemSpec item = new(
            ProductId.New(),
            "Golden Grain Premium Jasmine Rice 5kg",
            Barcode: "4800001000013",
            Quantity: 1m,
            UnitOfMeasureId.New(),
            UnitPrice: 345m,
            PriceVersion: ProductPriceId.New(),
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            Discount: 0m,
            DiscountAuthorizedByUserId: null,
            VatRate: null,
            IsVatExempt: true,
            IsZeroRated: false,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            UnitCost: 290m,
            TracksBatches: false);

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-W02-000812"),
                EventId.New(),
                LocationId.New(),
                CashierShiftId.New(),
                DeviceId.New(),
                customerId: null,
                new DateOnly(2026, 9, 14),
                new DateTimeOffset(2026, 9, 14, 2, 52, 0, TimeSpan.Zero),
                UserId.New(),
                [item],
                [new PaymentSpec(PaymentMethod.Cash, 345m, 500m, ProviderReference: null)])
            .Value;
    }
}
