using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Notifications;

public sealed class NotificationReader(
    PosDbContext context,
    IPermissionEvaluator permissions) : INotificationReader
{
    public async Task<NotificationFeed> ListAsync(
        UserId userId,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<Notification> visible = await VisibleAsync(userId, cancellationToken).ConfigureAwait(false);
        IQueryable<NotificationReceipt> receipts = context.NotificationReceipts
            .AsNoTracking()
            .Where(receipt => receipt.UserId == userId);

        int unreadCount = await visible
            .CountAsync(
                notification => !receipts.Any(receipt =>
                    receipt.NotificationId == notification.Id && receipt.ReadAtUtc != null),
                cancellationToken)
            .ConfigureAwait(false);

        var query =
            from notification in visible
            join receipt in receipts on notification.Id equals receipt.NotificationId into matches
            from receipt in matches.DefaultIfEmpty()
            where !unreadOnly || receipt == null || receipt.ReadAtUtc == null
            orderby notification.CreatedAtUtc descending, notification.Id descending
            select new NotificationItem(
                notification.Id.Value,
                notification.Kind,
                notification.Severity,
                notification.Title,
                notification.Body,
                notification.LocationId == null ? null : notification.LocationId.Value.Value,
                notification.ProductId == null ? null : notification.ProductId.Value.Value,
                notification.BatchId == null ? null : notification.BatchId.Value.Value,
                notification.ReferenceDocumentType,
                notification.ReferenceDocumentId,
                notification.CreatedAtUtc,
                receipt == null ? null : receipt.ReadAtUtc,
                receipt == null ? null : receipt.AcknowledgedAtUtc);

        List<NotificationItem> items = await query
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NotificationFeed(items, unreadCount);
    }

    public async Task<bool> MarkReadAsync(
        UserId userId,
        NotificationId notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        IQueryable<Notification> visible = await VisibleAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!await visible.AnyAsync(notification => notification.Id == notificationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        NotificationReceipt? receipt = await context.NotificationReceipts
            .FirstOrDefaultAsync(
                value => value.NotificationId == notificationId && value.UserId == userId,
                cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            receipt = NotificationReceipt.Create(notificationId, userId);
            context.NotificationReceipts.Add(receipt);
        }

        receipt.MarkRead(readAtUtc);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> MarkAllReadAsync(
        UserId userId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        List<NotificationId> visibleIds = await (await VisibleAsync(userId, cancellationToken).ConfigureAwait(false))
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (visibleIds.Count == 0)
        {
            return 0;
        }

        Dictionary<NotificationId, NotificationReceipt> receipts = await context.NotificationReceipts
            .Where(receipt => receipt.UserId == userId && visibleIds.Contains(receipt.NotificationId))
            .ToDictionaryAsync(receipt => receipt.NotificationId, cancellationToken)
            .ConfigureAwait(false);

        int marked = 0;
        foreach (NotificationId notificationId in visibleIds)
        {
            if (!receipts.TryGetValue(notificationId, out NotificationReceipt? receipt))
            {
                receipt = NotificationReceipt.Create(notificationId, userId);
                context.NotificationReceipts.Add(receipt);
            }
            else if (receipt.ReadAtUtc is not null)
            {
                continue;
            }

            receipt.MarkRead(readAtUtc);
            marked++;
        }

        if (marked > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return marked;
    }

    private async Task<IQueryable<Notification>> VisibleAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        List<LocationId> assigned = await context.UserLocations
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId)
            .Select(assignment => assignment.LocationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlySet<string> effective = await permissions
            .GetEffectivePermissionsAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<Notification> notifications = context.Notifications.AsNoTracking();
        return effective.Contains(Permissions.Administration.AllLocations)
            ? notifications
            : notifications.Where(notification =>
                notification.LocationId == null || assigned.Contains(notification.LocationId.Value));
    }
}

public sealed class NullNotificationPublisher : INotificationPublisher
{
    public Task PublishAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.CompletedTask;
    }
}
