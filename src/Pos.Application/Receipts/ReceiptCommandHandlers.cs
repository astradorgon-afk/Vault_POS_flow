using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Receipts;

namespace Pos.Application.Receipts;

/// <summary>
/// Handles <see cref="IssueReceiptCommand"/>. Resolves the location, allocates
/// the RCT number, and persists the receipt — all inside the unit-of-work
/// behaviour's transaction, so a receipt is never stored without its number.
/// </summary>
public sealed class IssueReceiptCommandHandler(
    IReceiptRepository receipts,
    IDocumentNumberGenerator numbers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<IssueReceiptCommand, ReceiptId>
{
    /// <inheritdoc />
    public async Task<Result<ReceiptId>> HandleAsync(
        IssueReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;

        ReceiptLocationInfo? locationInfo = await receipts
            .GetLocationInfoAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (locationInfo is null)
        {
            return Result<ReceiptId>.Failure(ReceiptErrors.LocationUnknown(command.LocationId));
        }

        if (locationInfo.Kind == LocationKind.External)
        {
            return Result<ReceiptId>.Failure(ReceiptErrors.LocationExternal);
        }

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.Receipt, cancellationToken)
            .ConfigureAwait(false);

        Result<Receipt> created = Receipt.Create(
            number,
            command.Kind,
            command.LocationId,
            command.Amount,
            command.Counterparty,
            command.Note,
            command.ReferenceNumber,
            actor,
            now);

        if (created.IsFailure)
        {
            return Result<ReceiptId>.Failure(created.Errors);
        }

        return await receipts
            .AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}