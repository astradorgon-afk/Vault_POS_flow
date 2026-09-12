using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Application.Organizations;

/// <summary>
/// Creates a physical location (Main Warehouse or Store). External counterparty
/// locations are created by the system, never by users.
/// </summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The human-readable name.</param>
/// <param name="Kind">The kind; not <see cref="LocationKind.External"/>.</param>
/// <param name="TimeZoneId">IANA timezone identifier.</param>
/// <param name="Settings">Operational settings; defaults to the strictest.</param>
public sealed record CreateLocationCommand(
    string? Code,
    string? Name,
    LocationKind Kind,
    string? TimeZoneId,
    LocationSettings? Settings = null)
    : ICommand<LocationId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => "location.manage";

    /// <inheritdoc />
    public LocationId LocationId => LocationId.Empty;
}

/// <summary>Handles <see cref="CreateLocationCommand"/>.</summary>
public sealed class CreateLocationCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CreateLocationCommand, LocationId>
{
    /// <inheritdoc />
    public async Task<Result<LocationId>> HandleAsync(
        CreateLocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Location> located = Location.Create(
            Organization.DefaultId,
            command.Code,
            command.Name,
            command.Kind,
            command.TimeZoneId,
            command.Settings);

        if (located.IsFailure)
        {
            return Result<LocationId>.Failure(located.Errors);
        }

        return await masterData.CreateLocationAsync(located.Value, cancellationToken).ConfigureAwait(false);
    }
}