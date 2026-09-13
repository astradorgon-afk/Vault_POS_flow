using Pos.Domain.Common;

namespace Pos.Domain.Receipts;

/// <summary>What the payment receipt documents.</summary>
public enum ReceiptKind
{
    /// <summary>Cash taken for a walk-in sale with no sale document.</summary>
    WalkInSale = 1,

    /// <summary>Cash paid out for a branch expense.</summary>
    BranchExpense = 2,

    /// <summary>Cash withdrawn by the owner.</summary>
    OwnerWithdrawal = 3,
}

/// <summary>
/// A payment receipt: money taken in or paid out, recorded as an RCT-numbered
/// document. Receipts are standalone — they record that a cash event happened,
/// not a resulting balance — and carry no ledger entry. Temporary by decision
/// (ADR-0026): there is no subscription or invoice around them.
/// </summary>
public sealed class Receipt
{
    /// <summary>Maximum length of a counterparty name.</summary>
    public const int CounterpartyMaxLength = 128;

    /// <summary>Maximum length of the purpose note.</summary>
    public const int NoteMaxLength = 512;

    private Receipt(
        ReceiptId id,
        DocumentNumber number,
        ReceiptKind kind,
        LocationId locationId,
        decimal amount,
        string? counterparty,
        string? note,
        string? referenceNumber,
        UserId issuedByUserId,
        DateTimeOffset issuedAtUtc)
    {
        Id = id;
        Number = number.Value;
        Kind = kind;
        LocationId = locationId;
        Amount = amount;
        Counterparty = counterparty;
        Note = note;
        ReferenceNumber = referenceNumber;
        IssuedByUserId = issuedByUserId;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Receipt()
    {
        Number = string.Empty;
        LocationId = LocationId.Empty;
        IssuedByUserId = UserId.Empty;
    }

    /// <summary>Gets the receipt identifier.</summary>
    public ReceiptId Id { get; }

    /// <summary>Gets the human-readable RCT document number.</summary>
    public string Number { get; }

    /// <summary>Gets what the receipt documents.</summary>
    public ReceiptKind Kind { get; }

    /// <summary>Gets the branch the cash event happened at.</summary>
    public LocationId LocationId { get; }

    /// <summary>Gets the amount recorded, stored at four decimal places.</summary>
    public decimal Amount { get; }

    /// <summary>Gets the optional counterparty name, for example a customer or supplier.</summary>
    public string? Counterparty { get; }

    /// <summary>Gets the optional purpose note.</summary>
    public string? Note { get; }

    /// <summary>Gets the optional number of a related document, such as a sale.</summary>
    public string? ReferenceNumber { get; }

    /// <summary>Gets the user who issued the receipt.</summary>
    public UserId IssuedByUserId { get; }

    /// <summary>Gets when the receipt was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>
    /// Creates a receipt. The RCT number is allocated by the caller so the
    /// counter advances exactly once, in the same transaction as the receipt.
    /// </summary>
    /// <param name="number">The allocated RCT number.</param>
    /// <param name="kind">What the receipt documents.</param>
    /// <param name="locationId">The branch the cash event happened at.</param>
    /// <param name="amount">The amount recorded, greater than zero.</param>
    /// <param name="counterparty">Optional counterparty name.</param>
    /// <param name="note">Optional purpose note.</param>
    /// <param name="referenceNumber">Optional related document number.</param>
    /// <param name="issuedByUserId">The issuing user.</param>
    /// <param name="issuedAtUtc">When the receipt was issued.</param>
    /// <returns>The new receipt, or a validation failure.</returns>
    public static Result<Receipt> Create(
        DocumentNumber number,
        ReceiptKind kind,
        LocationId locationId,
        decimal amount,
        string? counterparty,
        string? note,
        string? referenceNumber,
        UserId issuedByUserId,
        DateTimeOffset issuedAtUtc)
    {
        if (!Enum.IsDefined(kind))
        {
            return Result<Receipt>.Failure(ReceiptErrors.KindUnknown(kind));
        }

        if (locationId.IsEmpty)
        {
            return Result<Receipt>.Failure(ReceiptErrors.LocationRequired);
        }

        if (amount <= 0m)
        {
            return Result<Receipt>.Failure(ReceiptErrors.AmountInvalid(amount));
        }

        if (counterparty is { Length: > CounterpartyMaxLength })
        {
            return Result<Receipt>.Failure(ReceiptErrors.CounterpartyTooLong(CounterpartyMaxLength));
        }

        if (note is { Length: > NoteMaxLength })
        {
            return Result<Receipt>.Failure(ReceiptErrors.NoteTooLong(NoteMaxLength));
        }

        // A reference names another business document, so it must parse as one.
        // Normalising it here keeps the stored value stable regardless of how
        // the caller typed it.
        string? normalizedReference = null;

        if (referenceNumber is not null)
        {
            Result<DocumentNumber> parsedReference = DocumentNumber.Parse(referenceNumber);

            if (parsedReference.IsFailure)
            {
                return Result<Receipt>.Failure(ReceiptErrors.ReferenceNumberInvalid);
            }

            normalizedReference = parsedReference.Value.Value;
        }

        if (issuedByUserId.IsEmpty)
        {
            return Result<Receipt>.Failure(ReceiptErrors.IssuerRequired);
        }

        return Result<Receipt>.Success(new Receipt(
            ReceiptId.New(),
            number,
            kind,
            locationId,
            amount,
            counterparty?.Trim(),
            note?.Trim(),
            normalizedReference,
            issuedByUserId,
            issuedAtUtc));
    }
}