using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// The application-level failures of a receipt reprint, on top of what the
/// domain (<see cref="Pos.Domain.Sales.SaleReceiptErrors"/>) and the pipeline
/// behaviours already reject.
/// </summary>
public static class ReprintCommandErrors
{
    /// <summary>A reprint requires the sale it refers to.</summary>
    public static Error SaleRequired { get; } = Error.Validation(
        "sale.receipt.sale_required",
        "A reprint requires the sale it refers to.");

    /// <summary>The sale to reprint does not exist.</summary>
    /// <param name="saleId">The requested sale.</param>
    /// <returns>The error.</returns>
    public static Error SaleNotFound(SaleId saleId) => Error.NotFound(
        "sale.receipt.sale_not_found",
        FormattableString.Invariant($"Sale {saleId} does not exist."));

    /// <summary>The reprint location does not match the sale's.</summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="locationId">The location the reprint was attempted at.</param>
    /// <returns>The error.</returns>
    public static Error LocationMismatch(SaleId saleId, LocationId locationId) => Error.Conflict(
        "sale.receipt.location_mismatch",
        FormattableString.Invariant($"Sale {saleId} belongs to another location; reprints must happen at its own location."),
        new Dictionary<string, object?>
        {
            ["saleId"] = saleId.Value,
            ["expectedLocationId"] = locationId.Value,
        });

    /// <summary>A receipt can only be reprinted while there is a sale to reprint.</summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="status">The sale's current state.</param>
    /// <returns>The error.</returns>
    public static Error UnreprintableState(SaleId saleId, SaleStatus status) => Error.Conflict(
        "sale.receipt.sale_not_completed",
        FormattableString.Invariant($"Sale {saleId} is {status}; only completed sales carry a receipt."),
        new Dictionary<string, object?>
        {
            ["saleId"] = saleId.Value,
            ["status"] = status,
        });
}