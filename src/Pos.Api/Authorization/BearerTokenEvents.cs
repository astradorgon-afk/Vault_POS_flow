using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Authorization;

/// <summary>
/// Re-checks, on every request, the things a token cannot be trusted to still
/// reflect.
/// </summary>
/// <remarks>
/// <para>
/// A signed token proves only what was true when it was issued. Between then and
/// now the account may have been disabled, the password changed, or the terminal
/// reported stolen. A ten-minute access token would otherwise keep working for
/// ten minutes after any of those, which is exactly the window an attacker wants.
/// </para>
/// <para>
/// Two cheap checks close it: Identity's security stamp, which changes whenever
/// the account materially changes, and the device's status. Both are indexed
/// primary-key lookups, and the permission cache means the heavier authorization
/// work still happens once per policy version.
/// </para>
/// </remarks>
public sealed class PosJwtBearerEvents : JwtBearerEvents
{
    /// <summary>Initializes the events.</summary>
    public PosJwtBearerEvents()
    {
        OnMessageReceived = ReadHubTokenAsync;
        OnTokenValidated = ValidateAsync;
    }

    private static Task ReadHubTokenAsync(MessageReceivedContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments(
                Notifications.NotificationHub.Route,
                StringComparison.Ordinal)
            && context.Request.Query.TryGetValue("access_token", out Microsoft.Extensions.Primitives.StringValues token)
            && token.Count == 1)
        {
            context.Token = token[0];
        }

        return Task.CompletedTask;
    }

    private static async Task ValidateAsync(TokenValidatedContext context)
    {
        PosDbContext database = context.HttpContext.RequestServices.GetRequiredService<PosDbContext>();
        ILogger<PosJwtBearerEvents> logger = context.HttpContext.RequestServices
            .GetRequiredService<ILogger<PosJwtBearerEvents>>();

        CancellationToken cancellationToken = context.HttpContext.RequestAborted;
        ClaimsPrincipal principal = context.Principal
            ?? throw new InvalidOperationException("A validated token must carry a principal.");

        if (!TryReadGuid(principal, System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, out Guid userId))
        {
            context.Fail("The token does not identify a user.");
            return;
        }

        AppUser? user = await database.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.CanAuthenticate)
        {
            AuthLog.AccountNotActive(logger, userId);
            context.Fail("The account is no longer active.");
            return;
        }

        string? stamp = principal.FindFirst(PosClaimTypes.SecurityStamp)?.Value;

        if (!string.Equals(stamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            // The stamp changes on password change, role change, disable, and on
            // an administrative revocation. A mismatch means one of those
            // happened after this token was minted.
            AuthLog.SecurityStampChanged(logger, userId);
            context.Fail("This session is no longer valid.");
            return;
        }

        if (TryReadGuid(principal, PosClaimTypes.DeviceId, out Guid deviceGuid))
        {
            DeviceId deviceId = new(deviceGuid);

            Device? device = await database.Devices
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
                .ConfigureAwait(false);

            if (device is null || !device.IsOperational)
            {
                AuthLog.DeviceNotOperational(logger, deviceGuid, device?.Status.ToString() ?? "unknown");

                context.Fail("This device is no longer permitted.");
                return;
            }
        }

        // Roles are attached here rather than baked into the token so that a role
        // change takes effect at once. They are used only for the audit snapshot;
        // authorization always asks for a permission.
        List<Guid> roleIds = await database.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (roleIds.Count > 0 && principal.Identity is ClaimsIdentity identity)
        {
            List<string> roleNames = await database.Roles
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .Select(r => r.Name!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (string role in roleNames)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }
    }

    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value)
        => Guid.TryParse(principal.FindFirst(claimType)?.Value, out value);
}
