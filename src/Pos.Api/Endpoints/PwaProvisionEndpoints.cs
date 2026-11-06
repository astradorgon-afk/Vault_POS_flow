using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>Read-only, device-bound identities for preparing employee PWA data.</summary>
public static class PwaProvisionEndpoints
{
    public static IEndpointRouteBuilder MapPwaProvisionEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/v1/pwa").WithTags("PWA provisioning");
        group.MapGet("/employees", EmployeesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageUsers)
            { Scope = ScopeSource.None });
        group.MapPost("/provision-token", ProvisionTokenAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageUsers)
            { Scope = ScopeSource.None });
        return app;
    }

    private static async Task<IResult> EmployeesAsync(
        HttpContext http, PosDbContext context, ICurrentUser currentUser,
        DatabasePermissionEvaluator permissions, CancellationToken ct)
    {
        Device? device = await AuthorizedDeviceAsync(http, context, currentUser, permissions, ct);
        if (device is null)
        {
            return Results.Forbid();
        }

        List<AppUser> users = await context.Users.AsNoTracking()
            .OrderBy(user => user.DisplayName)
            .Take(501)
            .ToListAsync(ct);
        if (users.Count > 500)
        {
            return Results.Problem("The employee list is too large for device preparation.", statusCode: 413);
        }

        List<object> employees = [];
        foreach (AppUser user in users.Where(user => user.CanAuthenticate))
        {
            UserAuthorization authorization = await permissions.GetAuthorizationAsync(new UserId(user.Id), ct);
            if (authorization.HasAllLocations || authorization.Locations.Contains(device.LocationId))
            {
                employees.Add(new { userId = user.Id, userName = user.UserName,
                    user.DisplayName });
            }
        }
        return Results.Ok(employees);
    }

    private static async Task<IResult> ProvisionTokenAsync(
        HttpContext http, ProvisionTarget body, PosDbContext context,
        ICurrentUser currentUser, DatabasePermissionEvaluator permissions,
        ITokenService tokens, IPolicyVersionProvider policyVersion,
        IAuditWriter audit, CancellationToken ct)
    {
        Device? device = await AuthorizedDeviceAsync(http, context, currentUser, permissions, ct);
        if (device is null)
        {
            return Results.Forbid();
        }
        if (body.UserId == Guid.Empty)
        {
            return Results.BadRequest(new { message = "Choose an employee." });
        }

        AppUser? target = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == body.UserId, ct);
        if (target is null || !target.CanAuthenticate)
        {
            return Results.NotFound();
        }
        UserAuthorization authority = await permissions.GetAuthorizationAsync(new UserId(target.Id), ct);
        if (!authority.IsActive ||
            (!authority.HasAllLocations && !authority.Locations.Contains(device.LocationId)))
        {
            return Results.Forbid();
        }

        long version = await policyVersion.GetCurrentAsync(ct);
        AccessToken access = tokens.IssueAccessToken(new UserId(target.Id), target.DisplayName,
            target.SecurityStamp ?? string.Empty, version, target.ApprovalTier,
            "pwa-provision", device.Id, device.LocationId);
        await audit.WriteAsync(new AuditEntry(AuditActions.Devices.PwaEmployeeProvisioned,
            nameof(AppUser), target.Id, Reason: "Offline snapshot preparation requested",
            LocationId: device.LocationId), ct);
        return Results.Ok(new { accessToken = access.Token, accessTokenExpiresAtUtc = access.ExpiresAtUtc,
            userId = target.Id, userName = target.UserName, target.DisplayName,
            permissions = authority.Permissions.Where(permission => Permissions.Find(permission)?.IsOfflineCapable == true),

            // The offline drawer decides which workflows a role may open, so a
            // prepared employee keeps the same roles the online nav reads.
            roles = authority.Roles,
            deviceId = device.Id.Value, locationId = device.LocationId.Value });
    }

    private static async Task<Device?> AuthorizedDeviceAsync(
        HttpContext http, PosDbContext context, ICurrentUser currentUser,
        DatabasePermissionEvaluator permissions, CancellationToken ct)
    {
        if (currentUser.UserId is not { } admin || currentUser.DeviceId is not { } deviceId ||
            http.User.FindFirst(PosClaimTypes.AuthenticationMethod)?.Value != "pwd" ||
            !await permissions.HasPermissionAsync(admin, Permissions.Administration.ManageDevices, null, ct))
        {
            return null;
        }
        Device? device = await context.Devices.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        return device is { IsOperational: true, Platform: DevicePlatform.Pwa } ? device : null;
    }
}

public sealed record ProvisionTarget(Guid UserId);
