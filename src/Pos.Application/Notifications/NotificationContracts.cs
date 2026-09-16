using Pos.Domain.Notifications;

namespace Pos.Application.Notifications;

/// <summary>Writes durable, deduplicated operational notifications.</summary>
public interface INotificationWriter
{
    /// <summary>Writes the notification unless its deduplication key already exists.</summary>
    Task<bool> WriteOnceAsync(Notification notification, CancellationToken cancellationToken);
}
