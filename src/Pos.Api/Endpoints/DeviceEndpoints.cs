using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Devices;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a device registration.</summary>
/// <param name="ShortCode">The code embedded in the device's offline document numbers.</param>
/// <param name="Name">A human-readable name.</param>
/// <param name="LocationId">The location the device belongs to.</param>
/// <param name="Platform">The platform it runs on.</param>
public sealed record RegisterDeviceBody(
    string ShortCode,
    string Name,
    Guid LocationId,
    DevicePlatform Platform);

/// <summary>The body of a device enrolment.</summary>
/// <param name="EnrolmentCode">The one-time code issued by an administrator.</param>
/// <param name="PublicKeyThumbprint">Thumbprint of the key the device generated locally.</param>
/// <param name="Platform">The platform it runs on.</param>
/// <param name="AppVersion">The application version.</param>
/// <param name="OsVersion">The operating system version.</param>
public sealed record EnrolDeviceBody(
    string EnrolmentCode,
    string PublicKeyThumbprint,
    DevicePlatform Platform,
    string? AppVersion,
    string? OsVersion);

/// <summary>The body of a status change that needs a reason.</summary>
/// <param name="Reason">Why the change is being made.</param>
public sealed record DeviceStatusChangeBody(string Reason);

/// <summary>A device as listed for administrators.</summary>
/// <param name="Id">The device identifier.</param>
/// <param name="ShortCode">Its document-number short code.</param>
/// <param name="Name">Its name.</param>
/// <param name="LocationId">Its location.</param>
/// <param name="Platform">Its platform.</param>
/// <param name="Status">Its lifecycle state.</param>
/// <param name="AppVersion">The last reported application version.</param>
/// <param name="LastSeenAtUtc">When it last contacted the server.</param>
/// <param name="LastSyncAtUtc">When it last synchronized.</param>
/// <param name="ClockSkewSeconds">The last observed clock difference.</param>
/// <param name="StatusReason">Why it was suspended or revoked.</param>
public sealed record DeviceSummary(
    Guid Id,
    string ShortCode,
    string Name,
    Guid LocationId,
    DevicePlatform Platform,
    DeviceStatus Status,
    string? AppVersion,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    decimal? ClockSkewSeconds,
    string? StatusReason);

/// <summary>Device administration and enrolment endpoints.</summary>
public static class DeviceEndpoints
{
    /// <summary>Maps the device routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/devices").WithTags("Devices");

        group.MapGet("/", ListAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices))
            .WithName("ListDevices")
            .WithSummary("Lists registered devices.");

        group.MapPost("/", RegisterAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RegisterDevice")
            .WithSummary("Registers a device and issues its first enrolment code.");

        group.MapPost("/{id:guid}/enrolment-code", ReissueCodeAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices))
            .WithName("ReissueDeviceEnrolmentCode")
            .WithSummary("Issues a fresh enrolment code, burning any outstanding one.");

        group.MapPost("/enrol", EnrolAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth-login")
            .WithName("EnrolDevice")
            .WithSummary("Completes enrolment against a one-time code.")
            .WithMetadata(new PublicEndpointAttribute(
                "A device has no credentials until it enrols; the one-time code is the credential."));

        group.MapPost("/{id:guid}/suspend", SuspendAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices))
            .WithName("SuspendDevice")
            .WithSummary("Temporarily blocks a device and ends its sessions.");

        group.MapPost("/{id:guid}/reactivate", ReactivateAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices))
            .WithName("ReactivateDevice")
            .WithSummary("Lifts a suspension.");

        group.MapPost("/{id:guid}/revoke", RevokeAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageDevices))
            .WithName("RevokeDevice")
            .WithSummary("Permanently blocks a device. Not reversible.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PosDbContext context,
        [FromQuery] Guid? locationId,
        CancellationToken cancellationToken)
    {
        IQueryable<Device> query = context.Devices.AsNoTracking();

        if (locationId is { } scope)
        {
            LocationId id = new(scope);
            query = query.Where(d => d.LocationId == id);
        }

        List<DeviceSummary> devices = await query
            .OrderBy(d => d.ShortCode)
            .Select(d => new DeviceSummary(
                d.Id.Value,
                d.ShortCode,
                d.Name,
                d.LocationId.Value,
                d.Platform,
                d.Status,
                d.AppVersion,
                d.LastSeenAtUtc,
                d.LastSyncAtUtc,
                d.ClockSkewSeconds,
                d.StatusReason))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(devices);
    }

    private static async Task<IResult> RegisterAsync(
        [FromBody] RegisterDeviceBody body,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DeviceRegistration> result = await devices
            .RegisterAsync(body.ShortCode, body.Name, new LocationId(body.LocationId), body.Platform, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/devices/{result.Value.DeviceId.Value}"),
                Describe(result.Value))
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ReissueCodeAsync(
        Guid id,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DeviceRegistration> result = await devices
            .ReissueEnrolmentCodeAsync(new DeviceId(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(Describe(result.Value))
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> EnrolAsync(
        [FromBody] EnrolDeviceBody body,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DeviceId> result = await devices
            .EnrolAsync(
                new DeviceEnrolmentRequest(
                    body.EnrolmentCode, body.PublicKeyThumbprint, body.Platform, body.AppVersion, body.OsVersion),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { deviceId = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> SuspendAsync(
        Guid id,
        [FromBody] DeviceStatusChangeBody body,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => devices.SuspendAsync(new DeviceId(id), body.Reason, cancellationToken), currentUser);

    private static Task<IResult> ReactivateAsync(
        Guid id,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => ExecuteAsync(() => devices.ReactivateAsync(new DeviceId(id), cancellationToken), currentUser);

    private static Task<IResult> RevokeAsync(
        Guid id,
        [FromBody] DeviceStatusChangeBody body,
        IDeviceService devices,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            () => devices.RevokeAsync(new DeviceId(id), body.Reason, cancellationToken), currentUser);

    private static async Task<IResult> ExecuteAsync(Func<Task<Result>> action, ICurrentUser currentUser)
    {
        Result result = await action().ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static object Describe(DeviceRegistration registration) => new
    {
        deviceId = registration.DeviceId.Value,
        shortCode = registration.ShortCode,

        // Returned exactly once: only a hash is stored, so a lost code is
        // reissued rather than recovered.
        enrolmentCode = registration.EnrolmentCode,
        expiresAtUtc = registration.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
    };
}
