using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// One append-only entry of the sale receipt print log (POS.md §4, DATABASE.md §7).
/// The first print of a sale's receipt happens on the device at completion; this
/// log records when a receipt was produced, so a permissioned reprint
/// (<c>sale.reprint</c>) is never silent. A reprint always carries a reason.
/// </summary>
public sealed class SaleReceiptPrint : AggregateRoot<ReceiptPrintId>
{
    /// <summary>The maximum length of a reprint reason.</summary>
    public const int ReasonMaxLength = 200;

    /// <summary>For EF Core.</summary>
    private SaleReceiptPrint()
    {
    }

    private SaleReceiptPrint(
        ReceiptPrintId id,
        SaleId saleId,
        UserId printedByUserId,
        DateTimeOffset printedAtUtc,
        bool isReprint,
        string? reason)
    {
        Id = id;
        SaleId = saleId;
        PrintedByUserId = printedByUserId;
        PrintedAtUtc = printedAtUtc;
        IsReprint = isReprint;
        Reason = reason;
    }

    /// <summary>Gets the sale whose receipt was printed.</summary>
    public SaleId SaleId { get; private set; }

    /// <summary>Gets who produced the receipt.</summary>
    public UserId PrintedByUserId { get; private set; }

    /// <summary>Gets when the receipt was produced.</summary>
    public DateTimeOffset PrintedAtUtc { get; private set; }

    /// <summary>Gets whether this print was a reprint; the first print is not.</summary>
    public bool IsReprint { get; private set; }

    /// <summary>Gets the reason, required for reprints.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Records a receipt print. The reason is mandatory for reprints so every
    /// re-emission is accountable; a first print carries no reason.
    /// </summary>
    /// <param name="saleId">The sale whose receipt was printed.</param>
    /// <param name="printedByUserId">Who produced the receipt.</param>
    /// <param name="printedAtUtc">When it was produced.</param>
    /// <param name="isReprint">Whether this is a reprint.</param>
    /// <param name="reason">The reason for a reprint.</param>
    /// <returns>The new log entry, or a validation failure.</returns>
    public static Result<SaleReceiptPrint> Create(
        SaleId saleId,
        UserId printedByUserId,
        DateTimeOffset printedAtUtc,
        bool isReprint,
        string? reason)
    {
        if (saleId.IsEmpty)
        {
            return Result<SaleReceiptPrint>.Failure(SaleReceiptErrors.SaleRequired);
        }

        if (printedByUserId.IsEmpty)
        {
            return Result<SaleReceiptPrint>.Failure(SaleReceiptErrors.PrintedByRequired);
        }

        if (isReprint)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Result<SaleReceiptPrint>.Failure(SaleReceiptErrors.ReasonRequired);
            }

            if (reason.Length > ReasonMaxLength)
            {
                return Result<SaleReceiptPrint>.Failure(SaleReceiptErrors.ReasonTooLong(ReasonMaxLength));
            }
        }

        return Result<SaleReceiptPrint>.Success(new SaleReceiptPrint(
            ReceiptPrintId.New(),
            saleId,
            printedByUserId,
            printedAtUtc,
            isReprint,
            reason));
    }
}