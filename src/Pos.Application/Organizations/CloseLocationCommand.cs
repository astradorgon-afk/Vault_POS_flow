using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;

namespace Pos.Application.Organizations;

/// <summary>
/// Closes a location. Locations are never deleted: their codes, movements and
/// receipts are part of the historical ledger, so retirement means marking the
/// location inactive from a closing business date. Refused while stock or open
/// documents remain, and never available for the system counterparty rows.
/// </summary>
/// <param name="LocationId">The location to close.</param>
/// <param name="ClosingDate">The effective closing business date.</param>
public sealed record CloseLocationCommand(LocationId LocationId, DateOnly ClosingDate)
    : ICommand<LocationId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => "location.manage";
}

/// <summary>Handles <see cref="CloseLocationCommand"/>.</summary>
public sealed class CloseLocationCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CloseLocationCommand, LocationId>
{
    /// <inheritdoc />
    public async Task<Result<LocationId>> HandleAsync(
        CloseLocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await masterData
            .CloseLocationAsync(command.LocationId, command.ClosingDate, cancellationToken)
            .ConfigureAwait(false);
    }
}
