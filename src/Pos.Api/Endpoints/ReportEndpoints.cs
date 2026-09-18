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

        group.MapGet("/inventory/on-hand", GetOnHandAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetInventoryOnHand")
            .WithSummary("Lists what is on the shelf, by product and location.");

        group.MapGet("/inventory/valuation", GetValuationAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewFinancialReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetInventoryValuation")
            .WithSummary("Values the stock held, by product and location.");

        group.MapGet("/inventory/ageing", GetAgeingAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetInventoryAgeing")
            .WithSummary("Buckets the stock held by how long it has been standing.");

        group.MapGet("/inventory/dead-stock", GetDeadStockAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetInventoryDeadStock")
            .WithSummary("Lists stock that is standing there and not selling.");

        group.MapGet("/inventory/movement", GetMovementsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetInventoryMovement")
            .WithSummary("Lists the ledger legs recorded in a window.");

        group.MapGet("/transfers", GetTransfersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetTransferReport")
            .WithSummary("Lists transfers raised in a window, longest in flight first.");

        group.MapGet("/transfers/distribution", GetDistributionAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetDistributionReport")
            .WithSummary("Rolls dispatched transfers up by the lane they travelled.");

        group.MapGet("/purchases", GetPurchaseOrdersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetPurchaseOrderReport")
            .WithSummary("Lists purchase orders raised in a window, with what has arrived against them.");

        group.MapGet("/supplier-performance", GetSupplierPerformanceAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSupplierPerformanceReport")
            .WithSummary("Scores each supplier on punctuality, fill rate and quality over a window.");

        group.MapGet("/adjustments", GetAdjustmentsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetAdjustmentReport")
            .WithSummary("Summarises stock written off or corrected, by why.");

        group.MapGet("/shrinkage", GetShrinkageAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewFinancialReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetShrinkageReport")
            .WithSummary("Values the stock lost to damage, spoilage, theft and expiry.");

        group.MapGet("/count-variance", GetCountVariancesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ViewReports)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetCountVarianceReport")
            .WithSummary("Lists the physical count lines that did not match the system.");

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

    /// <summary>
    /// What is on the shelf. Deliberately carries no money: what stock cost is a
    /// financial question with a permission of its own, and a shelf count that
    /// discloses margin is an operational report only head office may open.
    /// </summary>
    private static async Task<IResult> GetOnHandAsync(
        [FromServices] IInventoryReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] bool includeEmpty = false,
        [FromQuery] int limit = 200)
    {
        Result<InventoryReportQuery> query = await QueryAsync(
            evaluator, currentUser, locationId, productId, includeEmpty, limit, cancellationToken)
            .ConfigureAwait(false);

        return query.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<InventoryOnHandRow>>.Failure(query.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetOnHandAsync(query.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetValuationAsync(
        [FromServices] IInventoryReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] bool includeEmpty = false,
        [FromQuery] int limit = 200)
    {
        Result<InventoryReportQuery> query = await QueryAsync(
            evaluator, currentUser, locationId, productId, includeEmpty, limit, cancellationToken)
            .ConfigureAwait(false);

        return query.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<InventoryValuationReport>.Failure(query.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetValuationAsync(query.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// How old the stock is. Measured to today unless a date is given, so the
    /// same window can be re-run against a month end.
    /// </summary>
    private static async Task<IResult> GetAgeingAsync(
        [FromServices] IInventoryReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        CancellationToken cancellationToken,
        [FromQuery] DateOnly? asOf = null,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] int limit = 200)
    {
        Result<InventoryReportQuery> query = await QueryAsync(
            evaluator, currentUser, locationId, productId, includeEmpty: false, limit, cancellationToken)
            .ConfigureAwait(false);

        return query.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<InventoryAgeingRow>>.Failure(query.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetAgeingAsync(
                    asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), query.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// Stock that is not moving, over a window of sales.
    /// </summary>
    /// <remarks>
    /// The window is how far back sales are counted, not a filter on the rows:
    /// a product that has never sold at all has to appear, and it is the worst
    /// case rather than a missing one.
    /// </remarks>
    private static async Task<IResult> GetDeadStockAsync(
        [FromServices] IInventoryReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        CancellationToken cancellationToken,
        [FromQuery] int windowDays = 90,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] int limit = 200)
    {
        if (windowDays < 1 || windowDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<InventoryDeadStockReport>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<InventoryReportQuery> query = await QueryAsync(
            evaluator, currentUser, locationId, productId, includeEmpty: false, limit, cancellationToken)
            .ConfigureAwait(false);

        if (query.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<InventoryDeadStockReport>.Failure(query.Errors), currentUser.CorrelationId.Value);
        }

        DateTimeOffset to = clock.UtcNow;

        return TypedResults.Ok(await reports
            .GetDeadStockAsync(to.AddDays(-windowDays), to, query.Value, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task<IResult> GetMovementsAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IInventoryReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] int limit = 200)
    {
        if (to < from || (to - from).TotalDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<InventoryMovementReport>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<InventoryReportQuery> query = await QueryAsync(
            evaluator, currentUser, locationId, productId, includeEmpty: false, limit, cancellationToken)
            .ConfigureAwait(false);

        return query.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<InventoryMovementReport>.Failure(query.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetMovementsAsync(from, to, query.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetTransfersAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] ITransferReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] bool openOnly = false,
        [FromQuery] int limit = 200)
    {
        if (to < from || (to - from).TotalDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<TransferReport>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<TransferReport>.Failure(scope.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetTransfersAsync(
                    from, to, scope.Value, openOnly, Math.Clamp(limit, 1, MaxRows), cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetDistributionAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] ITransferReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null)
    {
        if (to < from || (to - from).TotalDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<DistributionLaneRow>>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<DistributionLaneRow>>.Failure(scope.Errors),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetDistributionAsync(from, to, scope.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetPurchaseOrdersAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IPurchasingReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] int limit = 200)
    {
        if (to < from || (to - from).TotalDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<PurchaseOrderReport>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<PurchaseOrderReport>.Failure(scope.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetPurchaseOrdersAsync(
                    from,
                    to,
                    scope.Value,
                    supplierId is { } supplier ? new SupplierId(supplier) : null,
                    Math.Clamp(limit, 1, MaxRows),
                    cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetSupplierPerformanceAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IPurchasingReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null)
    {
        if (to < from || (to - from).TotalDays >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<SupplierPerformanceRow>>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays)),
                currentUser.CorrelationId.Value);
        }

        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<SupplierPerformanceRow>>.Failure(scope.Errors),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetSupplierPerformanceAsync(from, to, scope.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// How much stock was written off or corrected. Quantity only: what it cost is
    /// the shrinkage report, which needs the financial permission.
    /// </summary>
    private static async Task<IResult> GetAdjustmentsAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IExceptionReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null)
    {
        Result<IReadOnlyCollection<LocationId>> scope = await WindowScopeAsync(
            evaluator, currentUser, from, to, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<IReadOnlyList<AdjustmentSummaryRow>>.Failure(scope.Errors),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetAdjustmentsAsync(from, to, scope.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetShrinkageAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IExceptionReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null)
    {
        Result<IReadOnlyCollection<LocationId>> scope = await WindowScopeAsync(
            evaluator, currentUser, from, to, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<ShrinkageReport>.Failure(scope.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetShrinkageAsync(from, to, scope.Value, cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<IResult> GetCountVariancesAsync(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] IExceptionReportRepository reports,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken,
        [FromQuery] Guid? locationId = null,
        [FromQuery] bool includeUncounted = false,
        [FromQuery] int limit = 200)
    {
        Result<IReadOnlyCollection<LocationId>> scope = await WindowScopeAsync(
            evaluator, currentUser, from, to, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? ProblemDetailsMapping.ToProblem(
                Result<CountVarianceReport>.Failure(scope.Errors), currentUser.CorrelationId.Value)
            : TypedResults.Ok(await reports
                .GetCountVariancesAsync(
                    from, to, scope.Value, includeUncounted, Math.Clamp(limit, 1, MaxRows), cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// Validates the window and resolves the scope in one step.
    /// </summary>
    /// <remarks>
    /// The period check comes first so a caller asking for fifty years is told
    /// what is wrong with the question rather than what is wrong with them.
    /// </remarks>
    private static async Task<Result<IReadOnlyCollection<LocationId>>> WindowScopeAsync(
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? locationId,
        CancellationToken cancellationToken)
        => to < from || (to - from).TotalDays >= MaxPeriodDays
            ? Result<IReadOnlyCollection<LocationId>>.Failure(ReportErrors.PeriodInvalid(MaxPeriodDays))
            : await ScopeAsync(evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

    private static async Task<Result<InventoryReportQuery>> QueryAsync(
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        Guid? locationId,
        Guid? productId,
        bool includeEmpty,
        int limit,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyCollection<LocationId>> scope = await ScopeAsync(
            evaluator, currentUser, locationId, cancellationToken).ConfigureAwait(false);

        return scope.IsFailure
            ? Result<InventoryReportQuery>.Failure(scope.Errors)
            : Result<InventoryReportQuery>.Success(new InventoryReportQuery(
                scope.Value,
                productId is { } product ? new ProductId(product) : null,
                includeEmpty,
                Math.Clamp(limit, 1, MaxRows)));
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