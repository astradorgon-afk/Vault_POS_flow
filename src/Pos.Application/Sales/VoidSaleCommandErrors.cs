using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Errors raised while voiding a sale. Domain-shape errors live on
/// <see cref="Pos.Domain.Sales.SaleErrors"/> under the <c>sale.void.*</c>
/// prefix; these cover the facts the handler verifies before the domain runs —
/// that the sale exists and that the request matches the sale's location and
/// device.
/// </summary>
internal static class VoidSaleCommandErrors
{
    /// <summary>The void names no sale.</summary>
    public static Error SaleRequired => Error.Validation(
        "sale.void.sale_required",
        "A void must name the sale to void.");

    /// <summary>The sale named by the command does not exist.</summary>
    /// <param name="saleId">The missing sale.</param>
    /// <returns>The error.</returns>
    public static Error SaleNotFound(SaleId saleId) => Error.NotFound(
        "sale.void.sale_not_found",
        FormattableString.Invariant($"No sale has the identifier {saleId}."));

    /// <summary>The void acts on a sale that does not belong to the location.</summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="locationId">The location the void was requested at.</param>
    /// <returns>The error.</returns>
    public static Error LocationMismatch(SaleId saleId, LocationId locationId) => Error.Conflict(
        "sale.void.location_mismatch",
        FormattableString.Invariant($"Sale {saleId} does not belong to location {locationId.Value}."));

    /// <summary>The void acts on a sale not completed on the same device.</summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="deviceId">The device the void was requested on.</param>
    /// <returns>The error.</returns>
    public static Error DeviceMismatch(SaleId saleId, DeviceId deviceId) => Error.Conflict(
        "sale.void.device_mismatch",
        FormattableString.Invariant($"Sale {saleId} was not completed on device {deviceId.Value}."));
}