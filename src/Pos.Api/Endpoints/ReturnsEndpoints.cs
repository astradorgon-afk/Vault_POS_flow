using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Domain.Inventory;

namespace Pos.Api.Endpoints;

/// <summary>The body of a return-against-a-sale request. Identity fields
/// (<see cref="CreateSalesReturnCommand.ReturnedByUserId"/>) come from the
/// authenticated request.</summary>
public sealed record CreateSalesReturnBody(
    string Number,
    Guid EventId,
    Guid SaleId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    IReadOnlyList<CreateSalesReturnLineBody> Lines);

/// <summary>One line of a return-against-a-sale request.</summary>
public sealed record CreateSalesReturnLineBody(Guid ProductId, decimal Quantity);

/// <summary>The body of a blind-return request (no original sale). Identity
/// fields (<see cref="CreateBlindSalesReturnCommand.ReturnedByUserId"/>) come
/// from the authenticated request.</summary>
public sealed record CreateBlindSalesReturnBody(
    string Number,
    Guid EventId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    string Reason,
    IReadOnlyList<CreateBlindSalesReturnLineBody> Lines);

/// <summary>One line of a blind-return request.</summary>
public sealed record CreateBlindSalesReturnLineBody(Guid ProductId, decimal Quantity);

/// <summary>The body of a refund request. A named <see cref="SaleId"/> issues a
/// refund against the return's original sale; without one the refund follows
/// the return's <see cref="RefundBlindSalesReturnCommand"/> (cash only). Identity
/// fields (<see cref="RefundSalesReturnCommand.RefundedByUserId"/>) come from
/// the authenticated request.</summary>
public sealed record RefundSalesReturnBody(
    Guid? SaleId,
    Guid EventId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference,
    DateTimeOffset RefundedAtUtc);

/// <summary>An inspection decision for one return line. Actor and timestamps come from the server.</summary>
/// <param name="EventId">The retry-safe event identifier.</param>
/// <param name="LocationId">The return's location.</param>
/// <param name="LineNumber">The return line number.</param>
/// <param name="Quantity">The quantity inspected.</param>
/// <param name="Kind">The inspection decision.</param>
/// <param name="ReasonCode">The ledger reason.</param>
/// <param name="Note">The inspection explanation.</param>
public sealed record DisposeSalesReturnBody(Guid EventId, Guid LocationId, int LineNumber,
    decimal Quantity, ReturnDispositionKind Kind, AdjustmentReasonCode ReasonCode, string Note);

/// <summary>A customer return as returned by the detail route.</summary>
public sealed record ReturnDetail(
    Guid Id,
    string Number,
    bool IsBlind,
    Guid? SaleId,
    Guid LocationId,
    Guid CashierShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    Guid ReturnedByUserId,
    decimal RefundableTotal,
    decimal RefundedTotal,
    IReadOnlyList<ReturnLineDetail> Lines,
    IReadOnlyList<ReturnRefundDetail> Refunds);

/// <summary>One return line as returned by the detail route.</summary>
public sealed record ReturnLineDetail(
    int LineNumber,
    Guid? SaleItemId,
    Guid ProductId,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    decimal DispositionedQuantity,
    decimal PendingDispositionQuantity,
    decimal UnitPrice,
    decimal NetAmount,
    decimal RefundableAmount,
    string? BatchCode,
    DateOnly? BatchExpiresOn);

/// <summary>One refund as returned by the detail route.</summary>
public sealed record ReturnRefundDetail(
    Guid Id,
    string Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference,
    DateTimeOffset RefundedAtUtc);

/// <summary>Return and refund endpoints (POS.md §4).</summary>
public static class ReturnsEndpoints
{
    /// <summary>Maps the returns group.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapReturnsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/returns").WithTags("Returns");

        group.MapGet("/{id:guid}", GetReturnDetailAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSalesReturn")
            .WithSummary("Gets a customer return with its lines and refunds.");

        group.MapPost("/{id:guid}/disposition", DisposeReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.Adjust) { Scope = ScopeSource.None })
            .WithName("DisposeSalesReturn")
            .WithSummary("Routes inspected returned goods to restock, quarantine, damaged, supplier-return staging, or waste.");

        group.MapPost("/", CreateReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Return)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateSalesReturn")
            .WithSummary("Accepts a customer return against a completed sale.");

        group.MapPost("/blind", CreateBlindReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ReturnBlind)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateBlindSalesReturn")
            .WithSummary("Accepts a customer return with no original sale, recording a reason.");

        group.MapPost("/{id:guid}/refund", RefundReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Refund)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RefundSalesReturn")
            .WithSummary("Issues a refund against a return; a blind refund is cash only.");

        return app;
    }

    private static async Task<IResult> DisposeReturnAsync(Guid id,
        [FromBody] DisposeSalesReturnBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        Result<Pos.Domain.Common.EventId> result = await dispatcher.SendAsync(new DisposeSalesReturnCommand(
            new Pos.Domain.Common.EventId(body.EventId), new SalesReturnId(id), new LocationId(body.LocationId),
            body.LineNumber, body.Quantity, body.Kind, body.ReasonCode, body.Note), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new { eventId = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    /// <summary>
    /// The permissions that let a caller read a return at its location. The
    /// write flows that operate on a return — refunds and inspections — need the
    /// return's lines and refund history, so any of them suffices to read.
    /// </summary>
    private static readonly string[] ReturnReadingPermissions =
    [
        Permissions.Sales.View,
        Permissions.Sales.Return,
        Permissions.Sales.Refund,
        Permissions.Inventory.Adjust,
    ];

    private static async Task<IResult> GetReturnDetailAsync(
        Guid id,
        [FromServices] ISalesRepository repository,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SalesReturnId returnId = new(id);

        SalesReturn? salesReturn = await repository
            .GetReturnByIdAsync(returnId, cancellationToken)
            .ConfigureAwait(false);

        if (salesReturn is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReturnDetail>.Failure(SalesReturnErrors.Unknown(returnId)),
                currentUser.CorrelationId.Value);
        }

        if (!await CanReadReturnAsync(
                evaluator,
                currentUser.UserId ?? UserId.Empty,
                salesReturn.LocationId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReturnDetail>.Failure(SalesReturnErrors.OutsideScope(returnId)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToDetail(salesReturn));
    }

    private static async Task<bool> CanReadReturnAsync(
        IPermissionEvaluator evaluator,
        UserId userId,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        foreach (string code in ReturnReadingPermissions)
        {
            if (await evaluator
                    .HasPermissionAsync(userId, code, locationId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static ReturnDetail ToDetail(SalesReturn salesReturn) => new(
        salesReturn.Id.Value,
        salesReturn.Number,
        salesReturn.IsBlind,
        salesReturn.SaleId?.Value,
        salesReturn.LocationId.Value,
        salesReturn.CashierShiftId.Value,
        salesReturn.DeviceId.Value,
        salesReturn.CustomerId?.Value,
        salesReturn.BusinessDate,
        salesReturn.ReturnedAtUtc,
        salesReturn.ReturnedByUserId.Value,
        salesReturn.RefundableTotal,
        salesReturn.RefundedTotal,
        salesReturn.Items
            .OrderBy(i => i.LineNumber)
            .Select(i => new ReturnLineDetail(
            i.LineNumber,
            i.SaleItemId?.Value,
            i.ProductId.Value,
            i.ProductName,
            i.Barcode,
            i.Quantity,
            i.DispositionedQuantity,
            i.PendingDispositionQuantity,
            i.UnitPrice,
            i.NetAmount,
            i.RefundableAmount,
            i.BatchCode,
            i.BatchExpiresOn)).ToList(),
        salesReturn.Refunds
            .OrderBy(r => r.RefundedAtUtc)
            .Select(r => new ReturnRefundDetail(
            r.Id.Value,
            r.Method.ToString(),
            r.Amount,
            r.Tendered,
            r.ProviderReference,
            r.RefundedAtUtc)).ToList());

    private static async Task<IResult> CreateReturnAsync(
        [FromBody] CreateSalesReturnBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DocumentNumber> number = DocumentNumber.Parse(body.Number);

        if (number.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SalesReturnId>.Failure(number.Errors),
                currentUser.CorrelationId.Value);
        }

        Result<SalesReturnId> result = await dispatcher
            .SendAsync(
                new CreateSalesReturnCommand(
                    number.Value,
                    new Pos.Domain.Common.EventId(body.EventId),
                    new SaleId(body.SaleId),
                    new LocationId(body.LocationId),
                    new CashierShiftId(body.ShiftId),
                    new DeviceId(body.DeviceId),
                    body.CustomerId.HasValue ? new CustomerId(body.CustomerId.Value) : null,
                    body.BusinessDate,
                    body.ReturnedAtUtc,
                    currentUser.UserId!.Value,
                    body.Lines.Select(l => new SalesReturnLine(new ProductId(l.ProductId), l.Quantity)).ToList()),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/returns/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> CreateBlindReturnAsync(
        [FromBody] CreateBlindSalesReturnBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DocumentNumber> number = DocumentNumber.Parse(body.Number);

        if (number.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SalesReturnId>.Failure(number.Errors),
                currentUser.CorrelationId.Value);
        }

        Result<SalesReturnId> result = await dispatcher
            .SendAsync(
                new CreateBlindSalesReturnCommand(
                    number.Value,
                    new Pos.Domain.Common.EventId(body.EventId),
                    new LocationId(body.LocationId),
                    new CashierShiftId(body.ShiftId),
                    new DeviceId(body.DeviceId),
                    body.CustomerId.HasValue ? new CustomerId(body.CustomerId.Value) : null,
                    body.BusinessDate,
                    body.ReturnedAtUtc,
                    currentUser.UserId!.Value,
                    body.Reason,
                    body.Lines.Select(l => new BlindSalesReturnLine(new ProductId(l.ProductId), l.Quantity)).ToList()),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/returns/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> RefundReturnAsync(
        Guid id,
        [FromBody] RefundSalesReturnBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SalesReturnId returnId = new(id);

        ICommand<RefundId> command = body.SaleId is { } saleId
            ? new RefundSalesReturnCommand(
                new Pos.Domain.Common.EventId(body.EventId),
                new SaleId(saleId),
                returnId,
                new LocationId(body.LocationId),
                new CashierShiftId(body.ShiftId),
                new DeviceId(body.DeviceId),
                body.Method,
                body.Amount,
                body.Tendered,
                body.ProviderReference,
                body.RefundedAtUtc,
                currentUser.UserId!.Value)
            : new RefundBlindSalesReturnCommand(
                new Pos.Domain.Common.EventId(body.EventId),
                returnId,
                new LocationId(body.LocationId),
                new CashierShiftId(body.ShiftId),
                new DeviceId(body.DeviceId),
                body.Method,
                body.Amount,
                body.Tendered,
                body.ProviderReference,
                body.RefundedAtUtc,
                currentUser.UserId!.Value);

        Result<RefundId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}
