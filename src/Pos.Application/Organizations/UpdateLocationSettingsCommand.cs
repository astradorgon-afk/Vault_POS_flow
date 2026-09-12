using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Application.Organizations;

/// <summary>
/// Replaces the operational settings of a physical location: negative-stock
/// policy, direct-supplier delivery, offline grace period and receipt text.
/// System counterparty locations have no settings and are refused.
/// </summary>
/// <param name="LocationId">The location to change.</param>
/// <param name="Settings">The new settings, or null for the conservative defaults.</param>
public sealed record UpdateLocationSettingsCommand(LocationId LocationId, LocationSettings? Settings)
    : ICommand<LocationId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => "settings.manage";
}

/// <summary>Handles <see cref="UpdateLocationSettingsCommand"/>.</summary>
public sealed class UpdateLocationSettingsCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<UpdateLocationSettingsCommand, LocationId>
{
    /// <inheritdoc />
    public async Task<Result<LocationId>> HandleAsync(
        UpdateLocationSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await masterData
            .UpdateLocationSettingsAsync(
                command.LocationId,
                command.Settings ?? LocationSettings.Default,
                cancellationToken)
            .ConfigureAwait(false);
    }
}