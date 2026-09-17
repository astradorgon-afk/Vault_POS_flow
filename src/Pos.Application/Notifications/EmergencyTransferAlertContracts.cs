using Pos.Domain.Common;

namespace Pos.Application.Notifications;

/// <summary>Reads committed emergency transfers that should raise operational alerts.</summary>
public interface IEmergencyTransferAlertRepository
{
    /// <summary>Finds emergency transfers created since the given instant whose stock movement was posted.</summary>
    /// <param name="sinceUtc">The oldest creation instant to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The emergency transfers, ordered by creation time.</returns>
    Task<IReadOnlyList<EmergencyTransferAlert>> GetRecentAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}

/// <summary>A committed emergency transfer to announce to both endpoint locations.</summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="TransferNumber">The TRF number.</param>
/// <param name="SourceLocationId">The store the stock left.</param>
/// <param name="DestinationLocationId">The store that received the stock.</param>
/// <param name="CreatedAtUtc">When the emergency transfer was raised.</param>
/// <param name="LineCount">The number of product lines moved.</param>
/// <param name="TotalQuantity">The total quantity moved.</param>
public sealed record EmergencyTransferAlert(
    TransferOrderId TransferId,
    string TransferNumber,
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    DateTimeOffset CreatedAtUtc,
    int LineCount,
    decimal TotalQuantity);
