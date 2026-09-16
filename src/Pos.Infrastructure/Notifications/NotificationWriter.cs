using Microsoft.EntityFrameworkCore;
using Pos.Application.Notifications;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Notifications;

public sealed class NotificationWriter(PosDbContext context) : INotificationWriter
{
    public async Task<bool> WriteOnceAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        bool exists = await context.Notifications.AsNoTracking()
            .AnyAsync(n => n.DeduplicationKey == notification.DeduplicationKey, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        context.Notifications.Add(notification);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
