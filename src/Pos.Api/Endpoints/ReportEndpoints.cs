using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

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

        return app;
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