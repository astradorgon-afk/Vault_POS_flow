using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;

namespace Pos.Api.Endpoints;

/// <summary>The body of an account creation.</summary>
public sealed record CreateUserBody(
    string UserName,
    string DisplayName,
    string Password,
    string? Email = null,
    string? EmployeeCode = null,
    ApprovalTier ApprovalTier = ApprovalTier.None,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<UserLocationSpec>? Locations = null);

/// <summary>The body of an account update.</summary>
public sealed record UpdateUserBody(
    string DisplayName,
    string? Email = null,
    string? EmployeeCode = null,
    ApprovalTier ApprovalTier = ApprovalTier.None);

/// <summary>A body that only records why.</summary>
/// <param name="Reason">Why the change is being made.</param>
public sealed record ReasonBody(string Reason);

/// <summary>The roles an account should hold.</summary>
/// <param name="Roles">Role names.</param>
public sealed record UserRolesBody(IReadOnlyList<string> Roles);

/// <summary>The locations an account should be assigned to.</summary>
/// <param name="Locations">The assignments.</param>
public sealed record UserLocationsBody(IReadOnlyList<UserLocationSpec> Locations);

/// <summary>A permission granted to or withheld from one account.</summary>
public sealed record UserOverrideBody(
    string PermissionCode,
    PermissionEffect Effect,
    string Reason,
    DateTimeOffset? ExpiresAtUtc = null,
    Guid? LocationId = null);

/// <summary>A new password set by an administrator.</summary>
/// <param name="NewPassword">The password.</param>
public sealed record ResetPasswordBody(string NewPassword);

/// <summary>A cashier PIN set by an administrator.</summary>
/// <param name="Pin">The PIN.</param>
public sealed record SetPinBody(string Pin);

/// <summary>The permissions a role should bundle.</summary>
/// <param name="Permissions">Permission codes.</param>
/// <param name="Reason">Why the role is changing.</param>
public sealed record RolePermissionsBody(IReadOnlyList<string> Permissions, string Reason);

/// <summary>User, role and permission administration endpoints (ADR-0028).</summary>
public static class AdministrationEndpoints
{
    /// <summary>Maps the administration routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder users = app.MapGroup("/api/v1/users").WithTags("Users");

        users.MapGet("/", ListUsersAsync).WithMetadata(ManageUsers()).WithName("ListUsers")
            .WithSummary("Lists accounts.");
        users.MapPost("/", CreateUserAsync).WithMetadata(ManageUsers()).WithName("CreateUser")
            .WithSummary("Creates an account with roles and locations.");
        users.MapGet("/{id:guid}", GetUserAsync).WithMetadata(ManageUsers()).WithName("GetUser")
            .WithSummary("Gets an account with its assignments, overrides and effective permissions.");
        users.MapPut("/{id:guid}", UpdateUserAsync).WithMetadata(ManageUsers()).WithName("UpdateUser")
            .WithSummary("Changes an account's name, e-mail, employee code or approval tier.");
        users.MapPost("/{id:guid}/disable", DisableUserAsync).WithMetadata(ManageUsers()).WithName("DisableUser")
            .WithSummary("Disables an account and ends its sessions.");
        users.MapPost("/{id:guid}/enable", EnableUserAsync).WithMetadata(ManageUsers()).WithName("EnableUser")
            .WithSummary("Re-enables a disabled account.");
        users.MapPut("/{id:guid}/roles", SetRolesAsync).WithMetadata(ManageUsers()).WithName("SetUserRoles")
            .WithSummary("Replaces an account's roles.");
        users.MapPut("/{id:guid}/locations", SetLocationsAsync).WithMetadata(ManageUsers()).WithName("SetUserLocations")
            .WithSummary("Replaces an account's location assignments.");
        users.MapPost("/{id:guid}/overrides", GrantOverrideAsync).WithMetadata(ManageUsers()).WithName("GrantUserOverride")
            .WithSummary("Grants or withholds one permission for one account.");
        users.MapPost("/{id:guid}/overrides/{overrideId:guid}/remove", RemoveOverrideAsync).WithMetadata(ManageUsers())
            .WithName("RemoveUserOverride").WithSummary("Removes a permission override.");
        users.MapPut("/{id:guid}/password", ResetPasswordAsync).WithMetadata(ManageUsers()).WithName("ResetUserPassword")
            .WithSummary("Sets a new password and ends the account's sessions.");
        users.MapPut("/{id:guid}/pin", SetPinAsync).WithMetadata(ManageUsers()).WithName("SetUserPin")
            .WithSummary("Sets the cashier PIN used at a registered terminal.");
        users.MapPost("/{id:guid}/two-factor/reset", ResetTwoFactorAsync).WithMetadata(ManageUsers())
            .WithName("ResetUserTwoFactor").WithSummary("Clears an account's authenticator so it must enrol again.");

        RouteGroupBuilder roles = app.MapGroup("/api/v1").WithTags("Roles");

        roles.MapGet("/roles", ListRolesAsync).WithMetadata(ManageRoles()).WithName("ListRoles")
            .WithSummary("Lists roles with their permissions and member counts.");
        roles.MapPut("/roles/{id:guid}/permissions", SetRolePermissionsAsync).WithMetadata(ManageRoles())
            .WithName("SetRolePermissions").WithSummary("Replaces the permissions a role bundles.");
        roles.MapGet("/permissions", ListPermissions).WithMetadata(ManageRoles()).WithName("ListPermissions")
            .WithSummary("Lists the permission catalogue, marking governance permissions.");

        return app;
    }

    private static RequirePermissionAttribute ManageUsers()
        => new(Permissions.Administration.ManageUsers) { Scope = ScopeSource.None };

    private static RequirePermissionAttribute ManageRoles()
        => new(Permissions.Administration.ManageRoles) { Scope = ScopeSource.None };

    private static async Task<IResult> ListUsersAsync(
        [FromServices] IUserAdministration administration,
        [FromQuery] string? search,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
        => TypedResults.Ok(await administration
            .ListAsync(search, includeInactive, offset, limit, cancellationToken)
            .ConfigureAwait(false));

    private static async Task<IResult> GetUserAsync(
        Guid id, [FromServices] IUserAdministration administration, [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => ProblemDetailsMapping.ToHttpResult(
            await administration.GetAsync(new UserId(id), cancellationToken).ConfigureAwait(false),
            currentUser.CorrelationId.Value);

    private static async Task<IResult> CreateUserAsync(
        [FromBody] CreateUserBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        Result<UserId> result = await administration.CreateAsync(
            new CreateUserRequest(
                body.UserName, body.DisplayName, body.Password, body.Email, body.EmployeeCode, body.ApprovalTier,
                body.Roles ?? [], body.Locations ?? []),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(FormattableString.Invariant($"/api/v1/users/{result.Value.Value}"), new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> UpdateUserAsync(
        Guid id, [FromBody] UpdateUserBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.UpdateAsync(
                new UserId(id), new UpdateUserRequest(body.DisplayName, body.Email, body.EmployeeCode, body.ApprovalTier),
                cancellationToken),
            currentUser);

    private static Task<IResult> DisableUserAsync(
        Guid id, [FromBody] ReasonBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(administration.DisableAsync(new UserId(id), body.Reason, cancellationToken), currentUser);

    private static Task<IResult> EnableUserAsync(
        Guid id, [FromServices] IUserAdministration administration, [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => NoContentAsync(administration.EnableAsync(new UserId(id), cancellationToken), currentUser);

    private static Task<IResult> SetRolesAsync(
        Guid id, [FromBody] UserRolesBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(administration.SetRolesAsync(new UserId(id), body.Roles ?? [], cancellationToken), currentUser);

    private static Task<IResult> SetLocationsAsync(
        Guid id, [FromBody] UserLocationsBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.SetLocationsAsync(new UserId(id), body.Locations ?? [], cancellationToken), currentUser);

    private static async Task<IResult> GrantOverrideAsync(
        Guid id, [FromBody] UserOverrideBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        Result<Guid> result = await administration.GrantOverrideAsync(
            new UserId(id),
            new GrantOverrideRequest(body.PermissionCode, body.Effect, body.Reason, body.ExpiresAtUtc, body.LocationId),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(FormattableString.Invariant($"/api/v1/users/{id}"), new { id = result.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> RemoveOverrideAsync(
        Guid id, Guid overrideId, [FromBody] ReasonBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.RemoveOverrideAsync(new UserId(id), overrideId, body.Reason, cancellationToken), currentUser);

    private static Task<IResult> ResetPasswordAsync(
        Guid id, [FromBody] ResetPasswordBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.ResetPasswordAsync(new UserId(id), body.NewPassword, cancellationToken), currentUser);

    private static Task<IResult> SetPinAsync(
        Guid id, [FromBody] SetPinBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(administration.SetPinAsync(new UserId(id), body.Pin, cancellationToken), currentUser);

    private static Task<IResult> ResetTwoFactorAsync(
        Guid id, [FromBody] ReasonBody body, [FromServices] IUserAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.ResetTwoFactorAsync(new UserId(id), body.Reason, cancellationToken), currentUser);

    private static async Task<IResult> ListRolesAsync(
        [FromServices] IRoleAdministration administration, CancellationToken cancellationToken)
        => TypedResults.Ok(await administration.ListRolesAsync(cancellationToken).ConfigureAwait(false));

    private static Task<IResult> SetRolePermissionsAsync(
        Guid id, [FromBody] RolePermissionsBody body, [FromServices] IRoleAdministration administration,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => NoContentAsync(
            administration.SetPermissionsAsync(id, body.Permissions ?? [], body.Reason, cancellationToken), currentUser);

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<PermissionView>> ListPermissions(
        [FromServices] IRoleAdministration administration)
        => TypedResults.Ok(administration.ListPermissions());

    private static async Task<IResult> NoContentAsync(Task<Result> operation, ICurrentUser currentUser)
    {
        Result result = await operation.ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}
