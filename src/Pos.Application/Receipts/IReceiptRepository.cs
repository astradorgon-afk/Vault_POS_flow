using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Receipts;

namespace Pos.Application.Receipts;

/// <summary>
/// Persists payment receipts. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
public interface IReceiptRepository
{
    /// <summary>Stages a new receipt on the context.</summary>
    /// <param name="receipt">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<ReceiptId>> AddAsync(Receipt receipt, CancellationToken cancellationToken);

    /// <summary>Loads a location's kind for the issue-time checks.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location info, or <see langword="null"/> when the location does not exist.</returns>
    Task<ReceiptLocationInfo?> GetLocationInfoAsync(LocationId locationId, CancellationToken cancellationToken);
}

/// <summary>What a receipt handler needs to know about one location.</summary>
/// <param name="Kind">The location kind, used to refuse external counterparties.</param>
public sealed record ReceiptLocationInfo(LocationKind Kind);