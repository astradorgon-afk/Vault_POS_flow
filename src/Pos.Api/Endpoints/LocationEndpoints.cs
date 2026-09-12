using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a location creation.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The human-readable name.</param>
/// <param name="Kind">The kind; not external.</param>
/// <param name="TimeZoneId">The IANA timezone identifier.</param>
/// <param name="Settings">Operational settings; defaults to the strictest.</param>
public sealed record CreateLocationBody(
    string Code,
    string Name,
    LocationKind Kind,
    string TimeZoneId,
    LocationSettings? Settings = null);

/// <summary>A location as listed for the organization.</summary>
/// <param name="Id">The location identifier.</param>
/// <param name="Code">Its short unique code.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="TimeZoneId">Its IANA timezone.</param>
/// <param name="IsActive">Whether it accepts operations.</param>
/// <param name="IsSystemCreated">Whether it is a system counterparty.</param>
/// <param name="OpenedOn">When it opened.</param>
/// <param name="ClosedOn">When it closed, if ever.</param>
/// <param name="Settings">Its operational settings.</param>
public sealed record LocationSummary(
    Guid Id,
    string Code,
    string Name,
    LocationKind Kind,
    string TimeZoneId,
    bool IsActive,
    bool IsSystemCreated,
    DateOnly OpenedOn,
    DateOnly? ClosedOn,
    LocationSettings Settings);

/// <summary>Location administration endpoints.</summary>
public static class LocationEndpoints
{
    /// <summary>Maps the location routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/locations").WithTags("Locations");

        group.MapGet("/", ListAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListLocations")
            .WithSummary("Lists the physical locations of the organization.");

        group.MapPost("/", CreateAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageLocations)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateLocation")
            .WithSummary("Creates a physical location (Main Warehouse or Store).");

        group.MapPut("/{id:guid}/settings", UpdateSettingsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageSettings)
            {
                Scope = ScopeSource.RouteValue,
            })
            .WithName("UpdateLocationSettings")
            .WithSummary("Replaces a location's operational settings.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PosDbContext context,
        CancellationToken cancellationToken)
    {
        List<LocationSummary> locations = await context.Locations
            .AsNoTracking()
            .Where(l => l.IsActive && !l.IsSystemCreated)
            .OrderBy(l => l.Code)
            .Select(l => new LocationSummary(
                l.Id.Value,
                l.Code,
                l.Name,
                l.Kind,
                l.TimeZoneId,
                l.IsActive,
                l.IsSystemCreated,
                l.OpenedOn,
                l.ClosedOn,
                l.Settings))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(locations);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateLocationBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<LocationId> result = await dispatcher
            .SendAsync(
                new CreateLocationCommand(
                    body.Code,
                    body.Name,
                    body.Kind,
                    body.TimeZoneId,
                    body.Settings),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/locations/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> UpdateSettingsAsync(
        Guid id,
        [FromBody] LocationSettings? settings,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<LocationId> result = await dispatcher
            .SendAsync(new UpdateLocationSettingsCommand(new LocationId(id), settings), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}