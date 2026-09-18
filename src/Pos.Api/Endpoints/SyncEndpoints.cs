using Microsoft.AspNetCore.Mvc;
using Pos.Api.Common;
using Pos.Api.Authorization;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Infrastructure.Sync;

namespace Pos.Api.Endpoints;

/// <summary>Device synchronization transport endpoints.</summary>
public static class SyncEndpoints
{
    /// <summary>Maps the synchronization routes.</summary>
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/sync/push", PushAsync)
            .RequireAuthorization()
            .WithTags("Synchronization")
            .WithName("PushDeviceEvents")
            .WithSummary("Uploads device events with per-event idempotency.");

        app.MapGet("/api/v1/sync/pull", PullAsync)
            .RequireAuthorization()
            .WithTags("Synchronization")
            .WithName("PullDeviceChanges")
            .WithSummary("Downloads the scoped master-data change feed.");

        app.MapGet("/api/v1/sync/baseline", BaselineAsync)
            .RequireAuthorization()
            .WithTags("Synchronization")
            .WithName("GetSyncBaseline")
            .WithSummary("Downloads a fresh scoped master-data baseline.");

        app.MapGet("/api/v1/sync/status", StatusAsync)
            .RequireAuthorization()
            .WithTags("Synchronization")
            .WithName("GetSyncStatus")
            .WithSummary("Returns the authenticated device's synchronization health.");

        app.MapGet("/api/v1/sync/failures", ListFailuresAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageSync) { Scope = ScopeSource.None })
            .WithTags("Synchronization")
            .WithName("ListSyncFailures")
            .WithSummary("Lists synchronization failures awaiting action.");

        app.MapPost("/api/v1/sync/failures/{failureId:guid}/retry", RetryFailureAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageSync) { Scope = ScopeSource.None })
            .WithTags("Synchronization")
            .WithName("RetrySyncFailure")
            .WithSummary("Schedules a synchronization failure for retry.");

        app.MapPost("/api/v1/sync/failures/{failureId:guid}/dismiss", DismissFailureAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageSync) { Scope = ScopeSource.None })
            .WithTags("Synchronization")
            .WithName("DismissSyncFailure")
            .WithSummary("Dismisses a synchronization failure with a required note.");

        return app;
    }

    private static async Task<IResult> PushAsync(
        [FromBody] SyncPushRequest request,
        SyncPushService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SyncPushResponse response = await sync.PushAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.ErrorCode is not null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Forbidden(response.ErrorCode, response.Message ?? "The synchronization request was refused.")),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(response);
    }

    private static async Task<IResult> PullAsync(
        [FromQuery] long cursor,
        [FromQuery] int limit,
        SyncPullService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SyncPullResponse response = await sync.PullAsync(cursor, limit == 0 ? 500 : limit, cancellationToken)
            .ConfigureAwait(false);

        if (response.RebaselineRequired)
        {
            return TypedResults.Json(
                new
                {
                    errorCode = response.ErrorCode,
                    message = response.Message,
                    action = "rebaseline",
                    requestedCursor = response.FromCursor,
                    earliestRetainedCursor = response.EarliestRetainedCursor,
                },
                statusCode: StatusCodes.Status410Gone);
        }

        if (response.ErrorCode is not null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Forbidden(response.ErrorCode, response.Message ?? "The synchronization request was refused.")),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(response);
    }

    private static async Task<IResult> ListFailuresAsync(
        [FromQuery] int limit,
        SyncFailureService sync,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await sync.ListAsync(limit, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> StatusAsync(
        SyncStatusService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SyncStatusResponse response = await sync.GetAsync(cancellationToken).ConfigureAwait(false);
        return response.ErrorCode is null
            ? TypedResults.Ok(response)
            : ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Forbidden(response.ErrorCode, response.Message ?? "The synchronization status request was refused.")),
                currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> BaselineAsync(
        SyncBaselineService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SyncBaselineResponse response = await sync.GetAsync(cancellationToken).ConfigureAwait(false);
        return response.ErrorCode is null
            ? TypedResults.Ok(response)
            : ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Forbidden(response.ErrorCode, response.Message ?? "The baseline request was refused.")),
                currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> RetryFailureAsync(
        Guid failureId,
        SyncFailureService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SyncFailureView> result = await sync.RetryAsync(failureId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> DismissFailureAsync(
        Guid failureId,
        [FromBody] DismissSyncFailureRequest request,
        SyncFailureService sync,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SyncFailureView> result = await sync
            .DismissAsync(failureId, request.Note, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}

public sealed record DismissSyncFailureRequest(string Note);
