using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Api.Middleware;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Api.Endpoints;

/// <summary>The body of a password sign-in.</summary>
/// <param name="UserName">Username or e-mail address.</param>
/// <param name="Password">The password.</param>
/// <param name="TwoFactorCode">The authenticator code, where the account needs one.</param>
public sealed record SignInBody(string UserName, string Password, string? TwoFactorCode = null);

/// <summary>The body of a cashier PIN sign-in.</summary>
/// <param name="EmployeeCode">The cashier's short code.</param>
/// <param name="Pin">The PIN.</param>
/// <param name="AppVersion">The client application version.</param>
public sealed record PinSignInBody(string EmployeeCode, string Pin, string? AppVersion = null);

/// <summary>The body of a token exchange.</summary>
/// <param name="RefreshToken">The token the client holds.</param>
public sealed record RefreshBody(string RefreshToken);

/// <summary>What the caller is told about themselves.</summary>
/// <param name="UserId">Their identifier.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Roles">The roles they hold.</param>
/// <param name="Permissions">
/// Their effective permissions, so the client can hide what it cannot use. The
/// server re-checks every one of them on every request.
/// </param>
/// <param name="Locations">The locations they may act in.</param>
/// <param name="HasAllLocations">Whether they may act business-wide.</param>
/// <param name="PolicyVersion">The authorization version these were resolved under.</param>
public sealed record CurrentUserResponse(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> Locations,
    bool HasAllLocations,
    long PolicyVersion);

/// <summary>Authentication and session endpoints.</summary>
public static class AuthEndpoints
{
    /// <summary>Maps the authentication routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", SignInAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth-login")
            .WithName("SignIn")
            .WithSummary("Signs in with a username and password.")
            .WithMetadata(new PublicEndpointAttribute("Sign-in cannot require an existing session."));

        group.MapPost("/login/pin", SignInWithPinAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth-login")
            .WithName("SignInWithPin")
            .WithSummary("Signs a cashier in at a registered terminal.")
            .WithMetadata(new PublicEndpointAttribute("Sign-in cannot require an existing session."));

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth-refresh")
            .WithName("RefreshToken")
            .WithSummary("Exchanges a refresh token for a new pair.")
            .WithMetadata(new PublicEndpointAttribute(
                "The access token is expected to have expired, so this route authenticates on the refresh token alone."));

        group.MapPost("/logout", SignOutAsync)
            .AllowAnonymous()
            .WithName("SignOut")
            .WithSummary("Ends the session the refresh token belongs to.")
            .WithMetadata(new PublicEndpointAttribute(
                "Signing out must work even when the access token has already expired."));

        group.MapGet("/me", GetCurrentUserAsync)
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .WithSummary("Describes the caller and their effective authority.");

        return app;
    }

    private static async Task<IResult> SignInAsync(
        [FromBody] SignInBody body,
        HttpContext http,
        IAuthenticationService authentication,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        PasswordSignInRequest request = new(
            body.UserName,
            body.Password,
            body.TwoFactorCode,
            ReadDeviceHeader(http),
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString());

        Result<AuthenticationResult> result = await authentication
            .SignInAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, currentUser);
    }

    private static async Task<IResult> SignInWithPinAsync(
        [FromBody] PinSignInBody body,
        HttpContext http,
        IAuthenticationService authentication,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        DeviceId? deviceId = ReadDeviceHeader(http);

        if (deviceId is not { } device)
        {
            // A PIN is only meaningful at a known terminal. Without one there is
            // no location to check the cashier against and no device to throttle.
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Validation(
                    "auth.device_header_required",
                    "PIN sign-in requires the X-Device-Id header of an enrolled terminal.")),
                currentUser.CorrelationId.Value);
        }

        PinSignInRequest request = new(
            body.EmployeeCode,
            body.Pin,
            device,
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString(),
            body.AppVersion);

        Result<AuthenticationResult> result = await authentication
            .SignInWithPinAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, currentUser);
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshBody body,
        HttpContext http,
        IAuthenticationService authentication,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        RefreshRequest request = new(
            body.RefreshToken,
            ReadDeviceHeader(http),
            http.Connection.RemoteIpAddress?.ToString());

        Result<AuthenticationResult> result = await authentication
            .RefreshAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, currentUser);
    }

    private static async Task<IResult> SignOutAsync(
        [FromBody] RefreshBody body,
        IAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        await authentication.SignOutAsync(body.RefreshToken, cancellationToken).ConfigureAwait(false);

        // Always the same answer, whether or not the token was live. Telling the
        // caller which it was would confirm a stolen token still works.
        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetCurrentUserAsync(
        ICurrentUser currentUser,
        IPermissionEvaluator permissions,
        Infrastructure.Identity.DatabasePermissionEvaluator evaluator,
        Infrastructure.Identity.IPolicyVersionProvider policyVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(AuthenticationErrors.InvalidCredentials),
                currentUser.CorrelationId.Value);
        }

        Infrastructure.Identity.UserAuthorization authorization =
            await evaluator.GetAuthorizationAsync(userId, cancellationToken).ConfigureAwait(false);

        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new CurrentUserResponse(
            userId.Value,
            string.Empty,
            authorization.Roles,
            [.. authorization.Permissions.Order(StringComparer.Ordinal)],
            [.. authorization.Locations.Select(l => l.Value)],
            authorization.HasAllLocations,
            version));
    }

    private static DeviceId? ReadDeviceHeader(HttpContext http)
        => http.Items.TryGetValue(RequestContextMiddleware.DeviceItemKey, out object? raw) && raw is Guid device
            ? new DeviceId(device)
            : null;

    private static IResult ToResponse(Result<AuthenticationResult> result, ICurrentUser currentUser)
    {
        if (result.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
        }

        AuthenticationResult value = result.Value;

        return TypedResults.Ok(new
        {
            accessToken = value.AccessToken,
            accessTokenExpiresAtUtc = value.AccessTokenExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
            refreshToken = value.RefreshToken,
            refreshTokenExpiresAtUtc = value.RefreshTokenExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
            user = new CurrentUserResponse(
                value.UserId.Value,
                value.DisplayName,
                value.Roles,
                value.Permissions,
                [.. value.Locations.Select(l => l.Value)],
                value.HasAllLocations,
                value.PolicyVersion),
        });
    }
}
