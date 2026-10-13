using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Application.Organizations;

/// <summary>
/// Changes a location's descriptive details: code, name, kind and timezone.
/// Settings live in <see cref="UpdateLocationSettingsCommand"/>, and a location
/// is never deleted — it is closed by <see cref="CloseLocationCommand"/>.
/// </summary>
/// <param name="LocationId">The location to change.</param>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The human-readable name.</param>
/// <param name="Kind">The kind; not <see cref="LocationKind.External"/>.</param>
/// <param name="TimeZoneId">IANA timezone identifier.</param>
public sealed record UpdateLocationCommand(
    LocationId LocationId,
    string? Code,
    string? Name,
    LocationKind Kind,
    string? TimeZoneId)
    : ICommand<LocationId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => "location.manage";
}

/// <summary>Handles <see cref="UpdateLocationCommand"/>.</summary>
public sealed class UpdateLocationCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<UpdateLocationCommand, LocationId>
{
    /// <inheritdoc />
    public async Task<Result<LocationId>> HandleAsync(
        UpdateLocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await masterData
            .UpdateLocationAsync(
                command.LocationId,
                command.Code,
                command.Name,
                command.Kind,
                command.TimeZoneId,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
