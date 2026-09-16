using FluentAssertions;
using Pos.Application.Receipts;
using Pos.Domain.Common;
using Pos.Domain.Receipts;

namespace Pos.Application.Tests;

/// <summary>
/// The printed receipt reads in the branch's own time: a cashier in Manila
/// handing over a receipt at 10:52 must not print 02:52.
/// </summary>
public sealed class ReceiptRendererTests
{
    private static readonly DateTimeOffset IssuedAtUtc = new(2026, 9, 14, 2, 52, 0, TimeSpan.Zero);

    [Fact]
    public void RenderPlainText_PrintsTheBranchWallClockTime()
    {
        string text = ReceiptRenderer.RenderPlainText(Sample(), "Store One", "Asia/Manila", "store1.mgr");

        text.Should().Contain("Issued: 2026-09-14 10:52 (Asia/Manila)\n");
        text.Should().NotContain("UTC");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    public void RenderPlainText_FallsBackToUtc_WhenTheZoneIsUnknown(string? timeZoneId)
    {
        string text = ReceiptRenderer.RenderPlainText(Sample(), "Store One", timeZoneId, "store1.mgr");

        text.Should().Contain("Issued: 2026-09-14 02:52 UTC\n");
    }

    [Fact]
    public void RenderPlainText_KeepsTheDocumentLayout()
    {
        string text = ReceiptRenderer.RenderPlainText(Sample(), "Store One", "Asia/Manila", "store1.mgr");

        text.Should().Be(
            "PAYMENT RECEIPT\n" +
            "===============\n" +
            "RCT-2026-000014\n" +
            "Issued: 2026-09-14 10:52 (Asia/Manila)\n" +
            "Location: Store One\n" +
            "Type: Branch expense\n" +
            "Amount: 480.75\n" +
            "Counterparty: Mercury Hardware\n" +
            "Note: Shelf brackets for aisle 3\n" +
            "Issued by: store1.mgr\n" +
            "\n");
    }

    [Fact]
    public void RenderThermal_UsesFixedWidthRowsAndRightAlignedAmount()
    {
        string text = ReceiptRenderer.Render(Sample(), "Store One", "Asia/Manila", "store1.mgr", ReceiptFormat.Thermal);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().OnlyContain(line => line.Length == 42);
        lines.Should().Contain(line => line.EndsWith("480.75", StringComparison.Ordinal));
        lines.Should().Contain(line => line.Contains("Issued: 2026-09-14 10:52 (Asia/Manila)", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderHtml_ProducesASelfContainedEscapedPrintDocument()
    {
        Receipt receipt = Receipt.Create(
            DocumentNumber.Create(DocumentType.Receipt, 2026, 15),
            ReceiptKind.WalkInSale,
            LocationId.New(),
            10m,
            "<Maria & Co>",
            "<paid>",
            referenceNumber: null,
            UserId.New(),
            IssuedAtUtc).Value;

        string html = ReceiptRenderer.Render(receipt, "<Store>", "Asia/Manila", "<cashier>", ReceiptFormat.Html);

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("@page { size: 80mm auto;");
        html.Should().Contain("&lt;Store&gt;");
        html.Should().Contain("&lt;Maria &amp; Co&gt;");
        html.Should().Contain("&lt;cashier&gt;");
        html.Should().NotContain("<Maria & Co>");
    }

    private static Receipt Sample()
        => Receipt.Create(
            DocumentNumber.Create(DocumentType.Receipt, 2026, 14),
            ReceiptKind.BranchExpense,
            LocationId.New(),
            480.75m,
            "Mercury Hardware",
            "Shelf brackets for aisle 3",
            referenceNumber: null,
            UserId.New(),
            IssuedAtUtc).Value;
}
