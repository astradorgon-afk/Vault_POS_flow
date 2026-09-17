using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Notifications;

/// <summary>Reads documents that carry unresolved receiving or transfer discrepancies.</summary>
public interface IDiscrepancyAlertRepository
{
    /// <summary>Finds posted goods receipts received since the given instant with at least one unresolved discrepancy.</summary>
    /// <param name="sinceUtc">The oldest receipt instant to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One item per receipt, listing only its unresolved discrepancies.</returns>
    Task<IReadOnlyList<ReceivingDiscrepancyAlert>> GetOpenReceivingDiscrepanciesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>Finds transfers received since the given instant with at least one unresolved arrival shortage.</summary>
    /// <param name="sinceUtc">The oldest arrival instant to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One item per transfer, totalling only its unresolved shortages.</returns>
    Task<IReadOnlyList<TransferShortageAlert>> GetOpenTransferShortagesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}

/// <summary>A goods receipt with unresolved discrepancies.</summary>
/// <param name="ReceiptId">The goods receipt.</param>
/// <param name="ReceiptNumber">The GRN number.</param>
/// <param name="LocationId">The location the goods were received into.</param>
/// <param name="ReceivedAtUtc">When the receipt was recorded.</param>
/// <param name="Discrepancies">The unresolved discrepancies.</param>
public sealed record ReceivingDiscrepancyAlert(
    GoodsReceiptId ReceiptId,
    string ReceiptNumber,
    LocationId LocationId,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<ReceivingDiscrepancyAlertLine> Discrepancies);

/// <summary>One unresolved receiving discrepancy.</summary>
/// <param name="Kind">The discrepancy kind.</param>
/// <param name="Quantity">The quantity in dispute.</param>
/// <param name="ValueImpact">The monetary impact at the receipt unit cost.</param>
public sealed record ReceivingDiscrepancyAlertLine(
    ReceivingDiscrepancyKind Kind,
    decimal Quantity,
    decimal ValueImpact);

/// <summary>A received transfer with unresolved arrival shortages.</summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="TransferNumber">The transfer number.</param>
/// <param name="SourceLocationId">The location the stock left.</param>
/// <param name="DestinationLocationId">The location it arrived short at.</param>
/// <param name="ReceivedAtUtc">When the arrival was recorded.</param>
/// <param name="ShortLineCount">How many unresolved shortage rows there are.</param>
/// <param name="ShortQuantity">The total unresolved short quantity.</param>
public sealed record TransferShortageAlert(
    TransferOrderId TransferId,
    string TransferNumber,
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    DateTimeOffset ReceivedAtUtc,
    int ShortLineCount,
    decimal ShortQuantity);
