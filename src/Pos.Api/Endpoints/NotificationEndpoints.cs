using Microsoft.AspNetCore.Mvc;
using Pos.Application.Common.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Common;

namespace Pos.Api.Endpoints;

/// <summary>Durable notification feed endpoints for the authenticated operator.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListNotifications")
            .WithSummary("Lists the caller's newest location-scoped notifications.");
        group.MapPost("/{id:guid}/read", MarkReadAsync)
            .WithName("MarkNotificationRead")
            .WithSummary("Marks one visible notification read.");
        group.MapPost("/read-all", MarkAllReadAsync)
            .WithName("MarkAllNotificationsRead")
            .WithSummary("Marks every notification visible to the caller read.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        INotificationReader reader,
        ICurrentUser currentUser,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        NotificationFeed feed = await reader
            .ListAsync(userId, unreadOnly, Math.Clamp(limit, 1, 100), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(feed);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        INotificationReader reader,
        ICurrentUser currentUser,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        bool marked = await reader
            .MarkReadAsync(userId, new NotificationId(id), clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return marked ? TypedResults.Ok(new { id }) : TypedResults.NotFound();
    }

    private static async Task<IResult> MarkAllReadAsync(
        INotificationReader reader,
        ICurrentUser currentUser,
        ISystemClock clock,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        int markedRead = await reader
            .MarkAllReadAsync(userId, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(new { markedRead });
    }
}
