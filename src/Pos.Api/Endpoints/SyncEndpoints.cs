using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Infrastructure.Sync;
using Pos.Shared.Sync;

namespace Pos.Api.Endpoints;

/// <summary>Upload and download endpoints for offline devices.</summary>
public static class SyncEndpoints
{
    /// <summary>Maps the sync surface.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/sync").WithTags("Sync");

        group.MapPost("/push", PushAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Create))
            .WithSummary("Uploads a batch of business events from a device.");

        group.MapGet("/pull", PullAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View))
            .WithSummary("Downloads one page of the change feed for the calling device.");

        return app;
    }

    /// <summary>
    /// Takes one batch and answers for every event in it.
    /// </summary>
    /// <remarks>
    /// The response is always 200 with one result per event, including when
    /// every event was refused. A batch is a transport convenience, not a unit
    /// of work: a device needs to know which of its events landed so it can stop
    /// retrying exactly those, and a single status code cannot say that.
    /// </remarks>
    private static async Task<IResult> PushAsync(
        [FromBody] SyncPushRequest request,
        SyncPushProcessor processor,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // A device uploads its own work and nobody else's, whatever the body says.
        if (currentUser.DeviceId is not { } callerDevice || callerDevice.Value != request.DeviceId)
        {
            return Results.Problem(
                title: "The batch names a different device than the one uploading it.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://vaultflow/errors/sync.device_mismatch");
        }

        SyncPushResponse response = await processor.ProcessAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(response);
    }

    /// <summary>
    /// Serves one page of changes to the calling device.
    /// </summary>
    /// <remarks>
    /// The scope is the device's own, taken from its registration rather than
    /// from the query string: a register asking for another store's catalogue is
    /// not a case this route needs to support, and making the scope a parameter
    /// would turn one into a way of asking.
    /// </remarks>
    private static async Task<IResult> PullAsync(
        SyncPullProcessor processor,
        ICurrentUser currentUser,
        CancellationToken cancellationToken,
        long cursor = 0,
        int limit = SyncPullProcessor.MaxPageSize)
    {
        if (currentUser.DeviceId is not { } deviceId)
        {
            return Results.Problem(
                title: "Only an enrolled device can download the change feed.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://vaultflow/errors/sync.device_required");
        }

        SyncPullResult result = await processor
            .PullAsync(deviceId, cursor, limit, cancellationToken)
            .ConfigureAwait(false);

        return result.Refusal switch
        {
            SyncPullRefusal.DeviceUnknown => Results.Problem(
                title: "This device is not registered.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://vaultflow/errors/sync.device_unknown"),

            // 410 rather than a 4xx the device might retry: what it asked for is
            // genuinely gone, and the remedy is a fresh baseline rather than a
            // smaller page or a wait.
            SyncPullRefusal.RebaselineRequired => Results.Json(
                new { action = "rebaseline" },
                statusCode: StatusCodes.Status410Gone),

            _ => Results.Ok(result.Page),
        };
    }
}