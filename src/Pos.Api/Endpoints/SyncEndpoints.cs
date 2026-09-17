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
}
