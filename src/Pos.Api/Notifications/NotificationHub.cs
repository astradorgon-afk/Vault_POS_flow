using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Notifications;

[Authorize]
public sealed class NotificationHub(
    PosDbContext context,
    IPermissionEvaluator permissions) : Hub
{
    public const string Route = "/hubs/notifications";
    public const string ClientEvent = "notificationReceived";

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out Guid rawUserId))
        {
            Context.Abort();
            return;
        }

        UserId userId = new(rawUserId);
        await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.User(userId))
            .ConfigureAwait(false);
        await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.Global)
            .ConfigureAwait(false);

        IReadOnlySet<string> effective = await permissions
            .GetEffectivePermissionsAsync(userId, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (effective.Contains(Permissions.Administration.AllLocations))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.AllLocations)
                .ConfigureAwait(false);
        }
        else
        {
            List<LocationId> locations = await context.UserLocations
                .AsNoTracking()
                .Where(assignment => assignment.UserId == userId)
                .Select(assignment => assignment.LocationId)
                .ToListAsync(Context.ConnectionAborted)
                .ConfigureAwait(false);

            foreach (LocationId locationId in locations)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, NotificationGroups.Location(locationId))
                    .ConfigureAwait(false);
            }
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}

internal static class NotificationGroups
{
    internal const string Global = "notifications:global";
    internal const string AllLocations = "notifications:all-locations";

    internal static string User(UserId userId) => $"notifications:user:{userId.Value:D}";

    internal static string Location(LocationId locationId) => $"notifications:location:{locationId.Value:D}";
}

public sealed class SignalRNotificationPublisher(
    IHubContext<NotificationHub> hub) : INotificationPublisher
{
    public async Task PublishAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        NotificationItem item = new(
            notification.Id.Value,
            notification.Kind,
            notification.Severity,
            notification.Title,
            notification.Body,
            notification.LocationId?.Value,
            notification.ProductId?.Value,
            notification.BatchId?.Value,
            notification.ReferenceDocumentType,
            notification.ReferenceDocumentId,
            notification.CreatedAtUtc,
            ReadAtUtc: null,
            AcknowledgedAtUtc: null);

        if (notification.LocationId is { } locationId)
        {
            await hub.Clients
                .Groups(NotificationGroups.Location(locationId), NotificationGroups.AllLocations)
                .SendAsync(NotificationHub.ClientEvent, item, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await hub.Clients.Group(NotificationGroups.Global)
            .SendAsync(NotificationHub.ClientEvent, item, cancellationToken)
            .ConfigureAwait(false);
    }
}
