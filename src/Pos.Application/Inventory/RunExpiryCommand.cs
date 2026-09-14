using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Triggers an expiry run at a specific location: finds all available stock
/// whose batches are past expiry and posts ExpiryQuarantine movements to
/// shift them into the Expired state.
/// </summary>
/// <param name="LocationId">The location to process.</param>
public sealed record RunExpiryCommand(LocationId LocationId)
    : ICommand<ExpiryRunResult>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.RunExpiry;
}
