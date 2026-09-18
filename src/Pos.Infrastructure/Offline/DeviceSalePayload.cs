using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// What the server is told about a sale completed on a device. The event is
/// the command input, not a copied sale row: the server replays the shared
/// sale use case and re-derives prices, tax and inventory allocation.
/// </summary>
public sealed record SaleSyncPayload(
    string Number,
    Guid LocationId,
    Guid CashierShiftId,
    Guid DeviceId,
    Guid CashierId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<SaleSyncLine> Lines,
    IReadOnlyList<SaleSyncPayment> Payments);

/// <summary>A sale line carried in a synchronization event.</summary>
public sealed record SaleSyncLine(
    Guid ProductId,
    decimal Quantity,
    Guid UnitOfMeasureId,
    string? Barcode,
    decimal? UnitPriceOverride,
    Guid? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    Guid? DiscountAuthorizedByUserId,
    bool AllowExpiredOverride,
    string? ExpiredOverrideReason);

/// <summary>A payment carried in a synchronization event.</summary>
public sealed record SaleSyncPayment(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);
