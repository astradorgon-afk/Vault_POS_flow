using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Reports;
using Pos.Domain.Sales;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>Owner dashboard endpoints (ROADMAP §Phase 16).</summary>
public static class DashboardEndpoints
{
    /// <summary>Maps the dashboard routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/dashboard").WithTags("Dashboard");

        group.MapGet("/overview", GetOverviewAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetDashboardOverview")
            .WithSummary("The period's headline numbers, the store comparison and what the stock looks like now.");

        group.MapGet("/exceptions", GetExceptionsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetDashboardExceptions")
            .WithSummary("Everything that needs somebody, worst first.");

        group.MapGet("/timeline", GetTimelineAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetDocumentTimeline")
            .WithSummary("Everything that happened to one document, and the ledger chain it caused.");

        return app;
    }

    /// <summary>
    /// The drill-down: one document's timeline and its movement chain.
    /// </summary>
    /// <remarks>
    /// A document nothing in the caller's stores touched comes back 404 rather
    /// than an empty timeline. "You may not see this" and "nothing happened" are
    /// different answers, and an empty one would quietly tell a manager the second.
    /// </remarks>
    private static async Task<IResult> GetTimelineAsync(
        [FromQuery] ReferenceDocumentType referenceType,
        [FromQuery] Guid referenceId,
        [FromServices] IDashboardRepository dashboard,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(referenceType))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<DocumentTimeline>.Failure(Error.Validation(
                    "dashboard.reference_type_unknown", "That is not a kind of document this system records.")),
                currentUser.CorrelationId.Value);
        }

        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        Result<IReadOnlyCollection<LocationId>> scope = Scope(authorization, null);

        if (scope.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<DocumentTimeline>.Failure(scope.Errors), currentUser.CorrelationId.Value);
        }

        DocumentTimeline? timeline = await dashboard
            .GetTimelineAsync(
                referenceType,
                referenceId,
                scope.Value,
                authorization.Permissions.Contains(Permissions.Administration.ViewFinancialReports),
                cancellationToken)
            .ConfigureAwait(false);

        return timeline is null
            ? ProblemDetailsMapping.ToProblem(
                Result<DocumentTimeline>.Failure(Error.NotFound(
                    "dashboard.document_not_found", "There is nothing recorded against that document here.")),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(timeline);
    }

    /// <summary>
    /// Builds the exception board.
    /// </summary>
    /// <remarks>
    /// Takes the same range and scope as the overview, so the two halves of the
    /// dashboard are always talking about the same period and the same stores.
    /// Some panels are a standing state and some count the period; the payload
    /// carries both the window and the instant it was read so a reader never has
    /// to guess which a number is.
    /// </remarks>
    private static async Task<IResult> GetExceptionsAsync(
        [FromServices] IDashboardRepository dashboard,
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        [FromServices] IOptions<DashboardOptions> options,
        CancellationToken cancellationToken,
        [FromQuery] DashboardRange range = DashboardRange.Last30,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? locationId = null)
    {
        Result<DashboardQuery> query = await QueryAsync(
            context, evaluator, currentUser, clock, range, from, to, locationId, cancellationToken)
            .ConfigureAwait(false);

        if (query.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<DashboardExceptions>.Failure(query.Errors), currentUser.CorrelationId.Value);
        }

        DashboardOptions settings = options.Value;

        return TypedResults.Ok(await dashboard
            .GetExceptionsAsync(
                query.Value,
                settings.HighValueAdjustmentThreshold,
                settings.OfflineAfter,
                settings.ExceptionSampleSize,
                cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Resolves the scope, the timezone and the period once, for either half.
    /// </summary>
    /// <remarks>
    /// Shared so the overview and the exception board cannot drift apart on what
    /// "this month" means or on which stores a caller may see.
    /// </remarks>
    private static async Task<Result<DashboardQuery>> QueryAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        ISystemClock clock,
        DashboardRange range,
        DateOnly? from,
        DateOnly? to,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        Result<IReadOnlyCollection<LocationId>> scope = Scope(authorization, locationId);

        if (scope.IsFailure)
        {
            return Result<DashboardQuery>.Failure(scope.Errors);
        }

        string timeZoneId = await TimeZoneAsync(context, locationId, cancellationToken).ConfigureAwait(false);

        Result<(DateOnly From, DateOnly To)> period = DashboardPeriod.Resolve(
            range, clock.BusinessDateFor(timeZoneId), from, to);

        return period.IsFailure
            ? Result<DashboardQuery>.Failure(period.Errors)
            : Result<DashboardQuery>.Success(new DashboardQuery(
                range,
                period.Value.From,
                period.Value.To,
                timeZoneId,
                scope.Value,
                authorization.Permissions.Contains(Permissions.Administration.ViewFinancialReports)));
    }

    /// <summary>
    /// Builds the owner's overview for a period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The financial half — cost, margin, stock value — is included only for a
    /// caller who holds <c>report.view.financial</c>, and comes back null for
    /// everybody else rather than zero. One dashboard serves both, because a
    /// second route would be a second place for the sales arithmetic to drift.
    /// </para>
    /// <para>
    /// Named ranges are resolved in a real timezone. Asking one store means that
    /// store's; asking the business means the organization's. Resolving in UTC
    /// would show a Manila store, at nine in the morning, a fraction of the day it
    /// had already had.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetOverviewAsync(
        [FromServices] IDashboardRepository dashboard,
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        CancellationToken cancellationToken,
        [FromQuery] DashboardRange range = DashboardRange.Today,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? locationId = null)
    {
        Result<DashboardQuery> query = await QueryAsync(
            context, evaluator, currentUser, clock, range, from, to, locationId, cancellationToken)
            .ConfigureAwait(false);

        return query.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<DashboardOverview>.Failure(query.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await dashboard
                .GetOverviewAsync(query.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// The timezone a named range is resolved in.
    /// </summary>
    /// <remarks>
    /// One store's own when a store was named, the organization's otherwise. An
    /// owner spanning timezones asking for "today" is asking one question that has
    /// several answers, and the business's own day is the only one that is the
    /// same question for every store on the page.
    /// </remarks>
    private static async Task<string> TimeZoneAsync(
        PosDbContext context,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        if (locationId is { } id)
        {
            string? own = await context.Locations
                .AsNoTracking()
                .Where(l => l.Id == new LocationId(id))
                .Select(l => l.TimeZoneId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(own))
            {
                return own;
            }
        }

        return await context.Organizations
            .AsNoTracking()
            .Select(o => o.DefaultTimeZoneId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "UTC";
    }

    /// <summary>
    /// Resolves which stores the dashboard may cover.
    /// </summary>
    /// <remarks>
    /// Read from the caller's database authority, never from the token, and
    /// <c>locationId</c> may only narrow within it. The same rule the reports
    /// follow, for the same reason: on a report, a widening parameter is the whole
    /// attack.
    /// </remarks>
    private static Result<IReadOnlyCollection<LocationId>> Scope(
        UserAuthorization authorization,
        Guid? requested)
    {
        if (requested is { } id)
        {
            LocationId asked = new(id);

            return !authorization.HasAllLocations && !authorization.Locations.Contains(asked)
                ? Result<IReadOnlyCollection<LocationId>>.Failure(ReportErrors.OutsideScope(asked))
                : Result<IReadOnlyCollection<LocationId>>.Success([asked]);
        }

        if (authorization.HasAllLocations)
        {
            return Result<IReadOnlyCollection<LocationId>>.Success([]);
        }

        return authorization.Locations.Count == 0
            ? Result<IReadOnlyCollection<LocationId>>.Failure(ReportErrors.NoScope())
            : Result<IReadOnlyCollection<LocationId>>.Success([.. authorization.Locations]);
    }
}
