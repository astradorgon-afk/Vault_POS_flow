using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Receipts;
using Pos.Domain.Common;
using Pos.Domain.Receipts;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a payment receipt issue.</summary>
/// <param name="LocationId">The branch the cash event happened at.</param>
/// <param name="Kind">What the receipt documents.</param>
/// <param name="Amount">The amount recorded, greater than zero.</param>
/// <param name="Counterparty">Optional counterparty name, for example a customer or supplier.</param>
/// <param name="Note">Optional purpose note.</param>
/// <param name="ReferenceNumber">Optional number of a related document, such as a sale.</param>
public sealed record IssueReceiptBody(
    Guid LocationId,
    ReceiptKind Kind,
    decimal Amount,
    string? Counterparty = null,
    string? Note = null,
    string? ReferenceNumber = null);

/// <summary>A payment receipt as returned by the detail and print routes.</summary>
public sealed record ReceiptDetail(
    Guid Id,
    string Number,
    string Kind,
    Guid LocationId,
    decimal Amount,
    string? Counterparty,
    string? Note,
    string? ReferenceNumber,
    Guid IssuedByUserId,
    DateTimeOffset IssuedAtUtc);

/// <summary>A payment receipt as returned by the list route.</summary>
public sealed record ReceiptSummary(
    Guid Id,
    string Number,
    string Kind,
    Guid LocationId,
    decimal Amount,
    string? Counterparty,
    Guid IssuedByUserId,
    DateTimeOffset IssuedAtUtc);

/// <summary>Payment receipt endpoints.</summary>
public static class ReceiptEndpoints
{
    /// <summary>Maps the receipt routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapReceiptEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/receipts").WithTags("Receipts");

        group.MapGet("/", ListReceiptsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Receipts.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListReceipts")
            .WithSummary("Lists payment receipts within the caller's scope, newest first.");

        group.MapPost("/", IssueReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Receipts.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("IssueReceipt")
            .WithSummary("Issues an RCT-numbered payment receipt for a cash event.");

        group.MapGet("/{id:guid}", GetReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Receipts.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetReceipt")
            .WithSummary("Gets a payment receipt.");

        group.MapGet("/{id:guid}/print", PrintReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Receipts.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("PrintReceipt")
            .WithSummary("Renders a payment receipt as printable plain text.");

        return app;
    }

    private static async Task<IResult> ListReceiptsAsync(
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromQuery] Guid? locationId,
        [FromQuery] ReceiptKind? kind,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<Receipt> query = context.Receipts.AsNoTracking();

        // Scoped to the caller's assigned locations unless they act business-wide,
        // resolved from the database rather than the token. Filtering on a location
        // outside that scope returns nothing rather than revealing it exists.
        if (!authorization.HasAllLocations)
        {
            query = query.Where(r => authorization.Locations.Contains(r.LocationId));
        }

        if (locationId is { } requested)
        {
            LocationId filter = new(requested);
            query = query.Where(r => r.LocationId == filter);
        }

        if (kind is { } requestedKind)
        {
            query = query.Where(r => r.Kind == requestedKind);
        }

        if (from is { } fromUtc)
        {
            query = query.Where(r => r.IssuedAtUtc >= fromUtc);
        }

        if (to is { } toUtc)
        {
            query = query.Where(r => r.IssuedAtUtc < toUtc);
        }

        List<ReceiptSummary> receipts = await query
            .OrderByDescending(r => r.IssuedAtUtc)
            .ThenByDescending(r => r.Number)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .Select(r => new ReceiptSummary(
                r.Id.Value,
                r.Number,
                r.Kind.ToString(),
                r.LocationId.Value,
                r.Amount,
                r.Counterparty,
                r.IssuedByUserId.Value,
                r.IssuedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(receipts);
    }

    private static async Task<IResult> IssueReceiptAsync(
        [FromBody] IssueReceiptBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<ReceiptId> result = await dispatcher
            .SendAsync(
                new IssueReceiptCommand(
                    body.Kind,
                    new LocationId(body.LocationId),
                    body.Amount,
                    body.Counterparty,
                    body.Note,
                    body.ReferenceNumber),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/receipts/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetReceiptAsync(
        Guid id,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ReceiptId receiptId = new(id);

        Receipt? receipt = await context.Receipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReceiptDetail>.Failure(ReceiptErrors.Unknown(receiptId)),
                currentUser.CorrelationId.Value);
        }

        if (!await CanViewAsync(receipt, evaluator, currentUser, cancellationToken).ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReceiptDetail>.Failure(ReceiptErrors.OutsideScope(receiptId)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToDetail(receipt));
    }

    private static async Task<IResult> PrintReceiptAsync(
        Guid id,
        [FromQuery] ReceiptFormat? format,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ReceiptId receiptId = new(id);

        Receipt? receipt = await context.Receipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReceiptDetail>.Failure(ReceiptErrors.Unknown(receiptId)),
                currentUser.CorrelationId.Value);
        }

        if (!await CanViewAsync(receipt, evaluator, currentUser, cancellationToken).ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ReceiptDetail>.Failure(ReceiptErrors.OutsideScope(receiptId)),
                currentUser.CorrelationId.Value);
        }

        var location = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == receipt.LocationId)
            .Select(l => new { l.Name, l.TimeZoneId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string? issuedByName = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == receipt.IssuedByUserId.Value)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        ReceiptFormat requestedFormat = format ?? ReceiptFormat.Plain;
        string text = ReceiptRenderer.Render(
            receipt, location?.Name ?? string.Empty, location?.TimeZoneId, issuedByName, requestedFormat);

        string contentType = requestedFormat == ReceiptFormat.Html ? "text/html" : "text/plain";
        return TypedResults.Text(text, contentType);
    }

    private static async Task<bool> CanViewAsync(
        Receipt receipt,
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await evaluator
            .HasPermissionAsync(
                currentUser.UserId ?? UserId.Empty,
                Permissions.Receipts.View,
                receipt.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

    private static ReceiptDetail ToDetail(Receipt receipt)
        => new(
            receipt.Id.Value,
            receipt.Number,
            receipt.Kind.ToString(),
            receipt.LocationId.Value,
            receipt.Amount,
            receipt.Counterparty,
            receipt.Note,
            receipt.ReferenceNumber,
            receipt.IssuedByUserId.Value,
            receipt.IssuedAtUtc);
}
