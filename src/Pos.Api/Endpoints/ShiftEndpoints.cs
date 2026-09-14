using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Api.Endpoints;

/// <summary>The body of a shift open.</summary>
/// <param name="Number">The device-allocated SHF number, for example <c>SHF-2026-D03-0042</c>.</param>
/// <param name="LocationId">The location the shift is opened at.</param>
/// <param name="BusinessDate">The business date the shift opens on.</param>
/// <param name="OpeningFloat">The cash in the drawer when the shift opens, greater than or equal to zero.</param>
public sealed record OpenShiftBody(
    string Number,
    Guid LocationId,
    DateOnly BusinessDate,
    decimal OpeningFloat);

/// <summary>The body of a shift close.</summary>
/// <param name="LocationId">The location the shift belongs to.</param>
/// <param name="DeclaredCash">The cash the cashier declared before counting.</param>
/// <param name="CountedCash">The cash actually counted in the drawer.</param>
public sealed record CloseShiftBody(
    Guid LocationId,
    decimal DeclaredCash,
    decimal CountedCash);

/// <summary>A cashier shift as returned by the summary route.</summary>
public sealed record ShiftSummary(
    Guid Id,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid CashierUserId,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningFloat,
    string Status,
    DateTimeOffset? ClosedAtUtc,
    decimal? DeclaredCash,
    decimal? CountedCash,
    decimal? CashVariance,
    decimal CashSales,
    decimal CashRefunds,
    decimal Payouts,
    decimal ExpectedCash);

/// <summary>Cashier shift endpoints (POS.md §1).</summary>
public static class ShiftEndpoints
{
    /// <summary>Maps the shift routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapShiftEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/shifts").WithTags("Shifts");

        group.MapPost("/open", OpenShiftAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.OpenShift)
            {
                Scope = ScopeSource.None,
            })
            .WithName("OpenShift")
            .WithSummary("Opens a cashier shift on the caller's device.");

        group.MapPost("/{id:guid}/close", CloseShiftAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.CloseShift)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CloseShift")
            .WithSummary("Declares and counts the drawer, then closes the shift.");

        group.MapGet("/{id:guid}/summary", GetShiftSummaryAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.OpenShift)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetShiftSummary")
            .WithSummary("Gets the shift's state, cash totals and computed variance.");

        return app;
    }

    private static async Task<IResult> OpenShiftAsync(
        [FromBody] OpenShiftBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DocumentNumber> number = DocumentNumber.Parse(body.Number);

        if (number.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<CashierShiftId>.Failure(number.Errors),
                currentUser.CorrelationId.Value);
        }

        Result<CashierShiftId> result = await dispatcher
            .SendAsync(
                new OpenShiftCommand(
                    number.Value,
                    new LocationId(body.LocationId),
                    body.BusinessDate,
                    body.OpeningFloat),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/shifts/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> CloseShiftAsync(
        Guid id,
        [FromBody] CloseShiftBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<CashierShiftId> result = await dispatcher
            .SendAsync(
                new CloseShiftCommand(
                    new CashierShiftId(id),
                    new LocationId(body.LocationId),
                    body.DeclaredCash,
                    body.CountedCash),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetShiftSummaryAsync(
        Guid id,
        [FromServices] IShiftRepository shifts,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        CashierShiftId shiftId = new(id);

        CashierShift? shift = await shifts
            .GetShiftAsync(shiftId, cancellationToken)
            .ConfigureAwait(false);

        if (shift is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ShiftSummary>.Failure(ShiftErrors.ShiftUnknown(shiftId)),
                currentUser.CorrelationId.Value);
        }

        if (!await evaluator
                .HasPermissionAsync(
                    currentUser.UserId ?? UserId.Empty,
                    Permissions.Sales.OpenShift,
                    shift.LocationId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ShiftSummary>.Failure(ShiftErrors.OutsideScope(shiftId)),
                currentUser.CorrelationId.Value);
        }

        ShiftCashTotals totals = await shifts
            .GetShiftCashTotalsAsync(shiftId, cancellationToken)
            .ConfigureAwait(false);

        decimal expected = shift.OpeningFloat + totals.CashSales - totals.CashRefunds - totals.Payouts;

        return TypedResults.Ok(new ShiftSummary(
            shift.Id.Value,
            shift.Number,
            shift.LocationId.Value,
            shift.DeviceId.Value,
            shift.CashierUserId.Value,
            shift.BusinessDate,
            shift.OpenedAtUtc,
            shift.OpeningFloat,
            shift.Status.ToString(),
            shift.ClosedAtUtc,
            shift.DeclaredCash,
            shift.CountedCash,
            shift.CashVariance,
            totals.CashSales,
            totals.CashRefunds,
            totals.Payouts,
            expected));
    }
}