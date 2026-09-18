using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Reports;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Reports;
using Pos.Domain.Sales;
using Pos.Infrastructure.Identity;

namespace Pos.Api.Endpoints;

/// <summary>Reporting endpoints (ROADMAP §Phase 11 "Daily sales summary").</summary>
public static class ReportEndpoints
{
    /// <summary>Maps the report routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/reports").WithTags("Reports");

        group.MapGet("/daily-sales", GetDailySalesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetDailySalesReport")
            .WithSummary("Gets the daily sales summary for a location and a business date.");

        group.MapGet("/sales", GetSalesAnalysisAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSalesAnalysis")
            .WithSummary("Analyses completed sales over a period, by product, category, store or cashier.");

        group.MapGet("/sales/payments", GetPaymentBreakdownAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSalesPaymentBreakdown")
            .WithSummary("Breaks a period's takings down by how they were paid.");

        return app;
    }

    /// <summary>The longest period one report may cover.</summary>
    /// <remarks>
    /// A report is the easiest way to ask a production database for every row it
    /// has ever held. The cap is generous enough for a year-on-year comparison and
    /// small enough that no single request can be the outage.
    /// </remarks>
    private const int MaxPeriodDays = 400;

    /// <summary>The most rows one report returns.</summary>
    private const int MaxRows = 500;

    private static async Task<IResult> GetSalesAnalysisAsync(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromServices] ISalesAnalysisRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] SalesAnalysisGrouping groupBy = SalesAnalysisGrouping.Product,
        [FromQuery] Guid? locationId = null,
        [FromQuery] int limit = 100)
    {
        if (Invalid(from, to) is { } periodProblem)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SalesAnalysisReport>.Failure(periodProblem), currentUser.CorrelationId.Value);
        }

        if (!Enum.IsDefined(groupBy))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SalesAnalysisReport>.Failure(Error.Validation(
                    "report.grouping_unknown", "That is not a way this report can be cut.")),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        if (scope.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SalesAnalysisReport>.Failure(scope.Errors), currentUser.CorrelationId.Value);
        }

        SalesAnalysisReport report = await reports
            .GetSalesAnalysisAsync(
                new SalesAnalysisQuery(from, to, groupBy, scope.Value, Math.Clamp(limit, 1, MaxRows)),
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(report);
    }

    private static async Task<IResult> GetPaymentBreakdownAsync(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromServices] ISalesAnalysisRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null)
    {
        if (Invalid(from, to) is { } periodProblem)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<SalesPaymentMethodRow>>.Failure(periodProblem),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        if (scope.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<SalesPaymentMethodRow>>.Failure(scope.Errors),
                currentUser.CorrelationId.Value);
        }

        IReadOnlyList<SalesPaymentMethodRow> rows = await reports
            .GetPaymentMethodBreakdownAsync(from, to, scope.Value, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(rows);
    }

    private static Error? Invalid(DateOnly from, DateOnly to)
        => to < from || to.DayNumber - from.DayNumber >= MaxPeriodDays
            ? ReportErrors.PeriodInvalid(MaxPeriodDays)
            : null;

    /// <summary>
    /// Resolves which stores this report may cover.
    /// </summary>
    /// <remarks>
    /// Taken from the caller's own authority and never from the query string. A
    /// caller may narrow to one of their stores; there is no parameter that
    /// widens, because on a report that parameter is the whole attack.
    /// </remarks>
    private static async Task<Result<IReadOnlyCollection<LocationId>>> ScopeAsync(
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        Guid? requested,
        CancellationToken cancellationToken)
    {
        // Read from the database rather than from the token. `ICurrentUser` carries
        // the caller's primary location and answers `false` to business-wide
        // authority whatever they hold, which for a report would silently narrow an
        // owner to one store and refuse a regional manager their second.
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (requested is { } id)
        {
            LocationId asked = new(id);

            return !authorization.HasAllLocations && !authorization.Locations.Contains(asked)
                ? Result<IReadOnlyCollection<LocationId>>.Failure(ReportErrors.OutsideScope(asked))
                : Result<IReadOnlyCollection<LocationId>>.Success([asked]);
        }

        if (authorization.HasAllLocations)
        {
            // Business-wide authority, and no narrowing asked for. An empty list
            // means "every location" to the repository, which it reads as no
            // location filter at all.
            return Result<IReadOnlyCollection<LocationId>>.Success([]);
        }

        return authorization.Locations.Count == 0
            ? Result<IReadOnlyCollection<LocationId>>.Failure(ReportErrors.NoScope())
            : Result<IReadOnlyCollection<LocationId>>.Success([.. authorization.Locations]);
    }

    private static async Task<IResult> GetDailySalesAsync(
        [FromQuery] Guid locationId,
        [FromQuery] DateOnly date,
        [FromServices] IDailySalesReportRepository reports,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        LocationId location = new(locationId);

        if (!await evaluator
                .HasPermissionAsync(
                    currentUser.UserId ?? UserId.Empty,
                    Permissions.Administration.ViewReports,
                    location,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<DailySalesReport>.Failure(ReportErrors.OutsideScope(location)),
                currentUser.CorrelationId.Value);
        }

        DailySalesReport? report = await reports
            .GetDailySalesReportAsync(location, date, cancellationToken)
            .ConfigureAwait(false);

        if (report is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<DailySalesReport>.Failure(ReportErrors.LocationUnknown(location)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToResponse(report));
    }

    private static DailySalesReportResponse ToResponse(DailySalesReport report)
        => new(
            report.LocationId.Value,
            report.LocationName,
            report.BusinessDate,
            report.SalesSummary,
            report.PaymentsByMethod,
            report.Shifts);

    private sealed record DailySalesReportResponse(
        Guid LocationId,
        string LocationName,
        DateOnly BusinessDate,
        DailySalesReportSalesSummary SalesSummary,
        IReadOnlyList<DailySalesReportPaymentMethodSummary> PaymentsByMethod,
        IReadOnlyList<DailySalesReportShiftSummary> Shifts);
}