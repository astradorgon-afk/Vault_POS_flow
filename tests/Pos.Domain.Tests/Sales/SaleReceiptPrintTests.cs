using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>
/// The sale receipt print log's contract (POS.md §4): the append-only entry
/// records who produced a receipt and when; a reprint is mandatory-reasoned so
/// every re-emission is accountable, while the first print carries no reason.
/// </summary>
public sealed class SaleReceiptPrintTests
{
    private static readonly SaleId Sale = SaleId.New();
    private static readonly UserId Cashier = UserId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_FirstPrint_IsNotAReprint_AndCarriesNoReason()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            Cashier,
            Now,
            isReprint: false,
            reason: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsReprint.Should().BeFalse();
        result.Value.Reason.Should().BeNull();
        result.Value.SaleId.Should().Be(Sale);
        result.Value.PrintedByUserId.Should().Be(Cashier);
        result.Value.PrintedAtUtc.Should().Be(Now);
        result.Value.Id.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Create_Reprint_RequiresAReason()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            Cashier,
            Now,
            isReprint: true,
            reason: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleReceiptErrors.ReasonRequired.Code);
    }

    [Fact]
    public void Create_Reprint_RejectsABlankReason()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            Cashier,
            Now,
            isReprint: true,
            reason: "   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleReceiptErrors.ReasonRequired.Code);
    }

    [Fact]
    public void Create_Reprint_RejectsAReasonOverTheMaximum()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            Cashier,
            Now,
            isReprint: true,
            new string('x', SaleReceiptPrint.ReasonMaxLength + 1));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleReceiptErrors.ReasonTooLong(SaleReceiptPrint.ReasonMaxLength).Code);
    }

    [Fact]
    public void Create_AcceptsALongButValidReason()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            Cashier,
            Now,
            isReprint: true,
            new string('x', SaleReceiptPrint.ReasonMaxLength));

        result.IsSuccess.Should().BeTrue();
        result.Value.Reason.Should().HaveLength(SaleReceiptPrint.ReasonMaxLength);
        result.Value.IsReprint.Should().BeTrue();
    }

    [Fact]
    public void Create_RequiresTheSale()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            SaleId.Empty,
            Cashier,
            Now,
            isReprint: false,
            reason: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleReceiptErrors.SaleRequired.Code);
    }

    [Fact]
    public void Create_RequiresWhoPrintedIt()
    {
        Result<SaleReceiptPrint> result = SaleReceiptPrint.Create(
            Sale,
            UserId.Empty,
            Now,
            isReprint: false,
            reason: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SaleReceiptErrors.PrintedByRequired.Code);
    }
}