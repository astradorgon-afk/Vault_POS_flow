using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Receipts;

namespace Pos.Domain.Tests.Receipts;

/// <summary>
/// The payment receipt's creation contract.
/// </summary>
/// <remarks>
/// <para>
/// A receipt is an RCT-numbered cash document issued at a real branch, by a real
/// cashier, for a positive amount. The RCT number is allocated before the factory
/// runs, by the application layer, so the document counter advances exactly once,
/// in the same transaction as the receipt.
/// </para>
/// <para>
/// The factory validates the shape of the document: kind is known, the location
/// is present, the amount is positive, the counterparty and note respect their
/// limits, the reference number is a normalised business-document number, and
/// the issuing user is identified. Whether the location is internal (not an
/// external counterparty) is decided by the application layer, which owns the
/// location-kind lookup; the domain factory deliberately does not need the
/// repository to create a receipt.
/// </para>
/// </remarks>
public sealed class ReceiptTests
{
    private static readonly UserId Cashier = UserId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DocumentNumber NewNumber()
        => DocumentNumber.Create(DocumentType.Receipt, 2026, 1);

    [Fact]
    public void Create_ValidWalkInSaleReceipt_IsAccepted()
    {
        DocumentNumber number = NewNumber();

        Result<Receipt> receipt = Receipt.Create(
            number,
            ReceiptKind.WalkInSale,
            Store,
            150.25m,
            counterparty: "Maria Santos",
            note: "Payment for the register open.",
            referenceNumber: null,
            issuedByUserId: Cashier,
            issuedAtUtc: Now);

        receipt.IsSuccess.Should().BeTrue(because: string.Join("; ", receipt.Errors.Select(e => e.Code)));
        receipt.Value.Id.Should().NotBe(ReceiptId.Empty);
        receipt.Value.Number.Should().Be(number.ToString());
        receipt.Value.Kind.Should().Be(ReceiptKind.WalkInSale);
        receipt.Value.LocationId.Should().Be(Store);
        receipt.Value.Amount.Should().Be(150.25m);
        receipt.Value.Counterparty.Should().Be("Maria Santos");
        receipt.Value.Note.Should().Be("Payment for the register open.");
        receipt.Value.ReferenceNumber.Should().BeNull();
        receipt.Value.IssuedByUserId.Should().Be(Cashier);
        receipt.Value.IssuedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData((int)ReceiptKind.OwnerWithdrawal + 1)]
    [InlineData(0)]
    public void Create_UnknownKind_IsRejected(int unknownKind)
    {
        Result<Receipt> receipt = Receipt.Create(
            NewNumber(),
            (ReceiptKind)unknownKind,
            Store,
            10m,
            null,
            null,
            null,
            Cashier,
            Now);

        receipt.IsFailure.Should().BeTrue();
        receipt.Error.Code.Should().Be("receipt.kind_unknown");
    }

    [Fact]
    public void Create_EmptyLocation_IsRejected()
    {
        Result<Receipt> receipt = Receipt.Create(
            NewNumber(),
            ReceiptKind.WalkInSale,
            LocationId.Empty,
            10m,
            null,
            null,
            null,
            Cashier,
            Now);

        receipt.IsFailure.Should().BeTrue();
        receipt.Error.Code.Should().Be("receipt.location_required");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Create_AmountMustBePositive(decimal invalidAmount)
    {
        Result<Receipt> receipt = Receipt.Create(
            NewNumber(),
            ReceiptKind.WalkInSale,
            Store,
            invalidAmount,
            null,
            null,
            null,
            Cashier,
            Now);

        receipt.IsFailure.Should().BeTrue();
        receipt.Error.Code.Should().Be("receipt.amount_invalid");
    }

    [Fact]
    public void Create_CounterpartyAndNoteRespectMaximumLength()
    {
        Result<Receipt> counterparty = Receipt.Create(
            NewNumber(),
            ReceiptKind.WalkInSale,
            Store,
            10m,
            new string('x', Receipt.CounterpartyMaxLength + 1),
            null,
            null,
            Cashier,
            Now);

        counterparty.IsFailure.Should().BeTrue();
        counterparty.Error.Code.Should().Be("receipt.counterparty_too_long");

        Result<Receipt> note = Receipt.Create(
            NewNumber(),
            ReceiptKind.WalkInSale,
            Store,
            10m,
            null,
            new string('x', Receipt.NoteMaxLength + 1),
            null,
            Cashier,
            Now);

        note.IsFailure.Should().BeTrue();
        note.Error.Code.Should().Be("receipt.note_too_long");
    }

    [Fact]
    public void Create_ReferenceNumberIsParsedAndNormalised()
    {
        Result<Receipt> receipt = Receipt.Create(
            NewNumber(),
            ReceiptKind.BranchExpense,
            Store,
            10m,
            null,
            null,
            "spl-2026-000017",
            Cashier,
            Now);

        // The reference is normalised to the business-document form, matching the
        // stored value regardless of how the caller typed it.
        receipt.IsSuccess.Should().BeTrue();
        receipt.Value.ReferenceNumber.Should().Be("SPL-2026-000017");

        // Some shapes are not business-document numbers and are refused.
        Result<Receipt> invalid = Receipt.Create(
            NewNumber(),
            ReceiptKind.BranchExpense,
            Store,
            10m,
            null,
            null,
            "not-a-document-number",
            Cashier,
            Now);

        invalid.IsFailure.Should().BeTrue();
        invalid.Error.Code.Should().Be("receipt.reference_number_invalid");
    }

    [Fact]
    public void Create_EmptyIssuer_IsRejected()
    {
        Result<Receipt> receipt = Receipt.Create(
            NewNumber(),
            ReceiptKind.WalkInSale,
            Store,
            10m,
            null,
            null,
            null,
            UserId.Empty,
            Now);

        receipt.IsFailure.Should().BeTrue();
        receipt.Error.Code.Should().Be("receipt.issuer_required");
    }
}
