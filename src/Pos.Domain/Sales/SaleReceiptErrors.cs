using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// Validation failures for the sale receipt print log (POS.md §4).
/// </summary>
public static class SaleReceiptErrors
{
    /// <summary>A receipt print requires the sale its receipt belongs to.</summary>
    public static Error SaleRequired { get; } = Error.Validation(
        "sale.receipt.sale_required",
        "A receipt print requires the sale its receipt belongs to.");

    /// <summary>A receipt print requires who produced it.</summary>
    public static Error PrintedByRequired { get; } = Error.Validation(
        "sale.receipt.printed_by_required",
        "A receipt print requires the user who produced it.");

    /// <summary>A reprint requires a reason.</summary>
    public static Error ReasonRequired { get; } = Error.Validation(
        "sale.receipt.reason_required",
        "A reprint requires a reason.");

    /// <summary>A receipt print requires when it was produced.</summary>
    public static Error PrintedAtRequired { get; } = Error.Validation(
        "sale.receipt.printed_at_required",
        "A receipt print requires when it was produced.");

    /// <summary>A reprint reason is too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error ReasonTooLong(int maxLength) => Error.Validation(
        "sale.receipt.reason_invalid",
        FormattableString.Invariant($"A reprint reason may be at most {maxLength} characters."));
}