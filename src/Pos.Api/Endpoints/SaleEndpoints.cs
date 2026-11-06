using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Receipts;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a complete-sale request. Fields mirror the command; identity
/// fields (<see cref="CompleteSaleCommand.CashierId"/> and
/// <see cref="CompleteSaleCommand.DeviceId"/>) come from the authenticated request.</summary>
public sealed record CompleteSaleBody(
    string Number,
    Guid EventId,
    Guid LocationId,
    Guid CashierShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<CompleteSaleLineBody> Lines,
    IReadOnlyList<CompleteSalePaymentBody> Payments);

/// <summary>One line of a completed sale.</summary>
public sealed record CompleteSaleLineBody(
    Guid ProductId,
    decimal Quantity,
    Guid UnitOfMeasureId,
    string? Barcode,
    decimal? UnitPriceOverride,
    Guid? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    Guid? DiscountAuthorizedByUserId,
    bool AllowExpiredOverride,
    string? ExpiredOverrideReason);

/// <summary>One payment that settles a sale.</summary>
public sealed record CompleteSalePaymentBody(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>A sale line as returned by the detail route.</summary>
public sealed record SaleLineDetail(
    int LineNumber,
    Guid ProductId,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal GrossAmount,
    decimal NetAmount,
    decimal? VatRate,
    decimal Vat,
    bool IsVatExempt,
    bool IsZeroRated);

/// <summary>A payment as returned by the detail route.</summary>
public sealed record SalePaymentDetail(
    string Method,
    decimal Amount,
    decimal? Tendered,
    decimal? Change,
    string? ProviderReference);

/// <summary>The body of a void-sale request. Identity fields
/// (<see cref="VoidSaleCommand.VoidedByUserId"/>) come from the authenticated
/// request.</summary>
public sealed record VoidSaleBody(
    Guid EventId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    DateOnly BusinessDate,
    DateTimeOffset VoidedAtUtc,
    string Reason);

/// <summary>One completed sale as returned by the search route.</summary>
public sealed record SaleSummary(
    Guid Id,
    string Number,
    string Status,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    decimal GrossTotal,
    decimal NetTotal);

/// <summary>
/// The body of a receipt-reprint request. Identity fields
/// (<see cref="ReprintSaleReceiptCommand.ReprintedByUserId"/>) come from the
/// authenticated request; a reprint is a read-side emission and needs no shift.
/// </summary>
public sealed record ReprintSaleReceiptBody(
    Guid LocationId,
    Guid DeviceId,
    string Reason,
    DateTimeOffset ReprintedAtUtc);

/// <summary>A completed sale as returned by the detail route.</summary>
public sealed record SaleDetail(
    Guid Id,
    string Number,
    string Status,
    Guid EventId,
    Guid LocationId,
    Guid CashierShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    Guid CompletedByUserId,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    decimal VatTotal,
    decimal VatExemptTotal,
    decimal ZeroRatedTotal,
    decimal TaxableBaseTotal,
    IReadOnlyList<SaleLineDetail> Lines,
    IReadOnlyList<SalePaymentDetail> Payments);

/// <summary>Sale endpoints: complete, read and receipt render (POS.md §3).</summary>
public static class SaleEndpoints
{
    /// <summary>Sets the maximum number of items accepted per sale.</summary>
    private const int MaxItems = 50;

    /// <summary>Sets the maximum number of payments accepted per sale.</summary>
    private const int MaxPayments = 5;

    /// <summary>Maps the sale routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/sales").WithTags("Sales");

        group.MapPost("/", CompleteSaleAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CompleteSale")
            .WithSummary("Completes a point-of-sale transaction.");

        group.MapGet("/", SearchSalesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View)
            {
                Scope = ScopeSource.QueryValue,
            })
            .WithName("SearchSales")
            .WithSummary("Finds completed sales by location, business-date window and cashier.");

        group.MapGet("/{id:guid}", GetSaleDetailAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSaleDetail")
            .WithSummary("Gets a completed sale with its lines and payments.");

        group.MapGet("/{id:guid}/receipt", PrintSaleReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("PrintSaleReceipt")
            .WithSummary("Renders a completed sale as printable plain text.");

        group.MapPost("/{id:guid}/reprint", ReprintSaleReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Reprint)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReprintSaleReceipt")
            .WithSummary("Logs a permissioned reprint of a sale receipt, appending to the print log.");

        group.MapPost("/{id:guid}/void", VoidSaleAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.Void)
            {
                Scope = ScopeSource.None,
            })
            .WithName("VoidSale")
            .WithSummary("Voids a completed sale, reversing its stock movement.");

        return app;
    }

    private static async Task<IResult> CompleteSaleAsync(
        [FromBody] CompleteSaleBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DocumentNumber> number = DocumentNumber.Parse(body.Number);

        if (number.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SaleId>.Failure(number.Errors),
                currentUser.CorrelationId.Value);
        }

        Result<SaleId> result = await dispatcher
            .SendAsync(
                new CompleteSaleCommand(
                    number.Value,
                    new Pos.Domain.Common.EventId(body.EventId),
                    new LocationId(body.LocationId),
                    new CashierShiftId(body.CashierShiftId),
                    new DeviceId(body.DeviceId),
                    currentUser.UserId!.Value,
                    body.CustomerId.HasValue ? new CustomerId(body.CustomerId.Value) : null,
                    body.BusinessDate,
                    body.CompletedAtUtc,
                    MapLines(body.Lines),
                    MapPayments(body.Payments)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/sales/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private const int DefaultSalesPage = 200;
    private const int MaxSalesPage = 1000;

    private static async Task<IResult> SearchSalesAsync(
        [FromQuery] Guid locationId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? cashierId,
        [FromQuery] string? number,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] PosDbContext context,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        LocationId scope = new(locationId);

        // A page of the newest sales. The total goes in X-Total-Count so a list
        // can say how many there are and page through them, instead of quietly
        // stopping at the first page.
        int skip = Math.Max(0, offset ?? 0);
        int take = Math.Clamp(limit ?? DefaultSalesPage, 1, MaxSalesPage);

        IQueryable<Sale> query = context.Sales
            .AsNoTracking()
            .Where(s => s.LocationId == scope);

        if (from is { } fromDate)
        {
            query = query.Where(s => s.BusinessDate >= fromDate);
        }

        if (to is { } toDate)
        {
            query = query.Where(s => s.BusinessDate <= toDate);
        }

        if (cashierId is { } cashier)
        {
            UserId cashierAccount = new(cashier);
            query = query.Where(s => s.CompletedByUserId == cashierAccount);
        }

        // Part of a receipt number, as a cashier types or scans it.
        if (!string.IsNullOrWhiteSpace(number))
        {
            string term = number.Trim().ToUpperInvariant();
            query = query.Where(s => s.Number.Contains(term));
        }

        int total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var rows = await query
            .OrderByDescending(s => s.CompletedAtUtc)
            .ThenByDescending(s => s.Number)
            .Skip(skip)
            .Take(take)
            .Select(s => new
            {
                s.Id,
                s.Number,
                s.Status,
                s.BusinessDate,
                s.CompletedAtUtc,
                s.GrossTotal,
                s.NetTotal,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<SaleSummary> results = rows
            .Select(s => new SaleSummary(
                s.Id.Value,
                s.Number,
                s.Status.ToString(),
                s.BusinessDate,
                s.CompletedAtUtc,
                s.GrossTotal,
                s.NetTotal))
            .ToList();

        return TypedResults.Ok(results);
    }

    private static async Task<IResult> ReprintSaleReceiptAsync(
        Guid id,
        [FromBody] ReprintSaleReceiptBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SaleId saleId = new(id);

        Result<SaleId> result = await dispatcher
            .SendAsync(
                new ReprintSaleReceiptCommand(
                    saleId,
                    new LocationId(body.LocationId),
                    new DeviceId(body.DeviceId),
                    body.Reason,
                    body.ReprintedAtUtc,
                    currentUser.UserId!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetSaleDetailAsync(
        Guid id,
        [FromServices] ISalesRepository repository,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SaleId saleId = new(id);

        Sale? sale = await repository
            .GetByIdAsync(saleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SaleDetail>.Failure(SaleErrors.Unknown(saleId)),
                currentUser.CorrelationId.Value);
        }

        if (!await evaluator
                .HasPermissionAsync(
                    currentUser.UserId ?? UserId.Empty,
                    Permissions.Sales.View,
                    sale.LocationId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<SaleDetail>.Failure(SaleErrors.OutsideScope(saleId)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToDetail(sale));
    }

    private static async Task<IResult> PrintSaleReceiptAsync(
        Guid id,
        [FromQuery] ReceiptFormat? format,
        [FromServices] ISalesRepository repository,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] PosDbContext context,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SaleId saleId = new(id);

        Sale? sale = await repository
            .GetByIdAsync(saleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<string>.Failure(SaleErrors.Unknown(saleId)),
                currentUser.CorrelationId.Value);
        }

        if (!await evaluator
                .HasPermissionAsync(
                    currentUser.UserId ?? UserId.Empty,
                    Permissions.Sales.View,
                    sale.LocationId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<string>.Failure(SaleErrors.OutsideScope(saleId)),
                currentUser.CorrelationId.Value);
        }

        var location = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == sale.LocationId)
            .Select(l => new { l.Name, l.TimeZoneId, l.Settings })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // The cashier's name as the rest of the system shows it, not their login.
        string? cashierName = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == sale.CompletedByUserId.Value)
            .Select(u => string.IsNullOrEmpty(u.DisplayName) ? u.UserName : u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // The branch's own header and footer, as configured on the Locations page.
        ReceiptFormat requestedFormat = format ?? ReceiptFormat.Plain;
        string text = SaleReceiptRenderer.Render(
            sale,
            location?.Name ?? string.Empty,
            location?.TimeZoneId,
            cashierName,
            requestedFormat,
            header: location?.Settings.ReceiptHeader,
            footer: location?.Settings.ReceiptFooter);

        Result<SaleReceiptPrint> print = SaleReceiptPrint.Create(
            saleId,
            currentUser.UserId ?? UserId.Empty,
            DateTimeOffset.UtcNow,
            isReprint: false,
            reason: null);

        if (print.IsSuccess)
        {
            await repository
                .AddReceiptPrintAsync(print.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        string contentType = requestedFormat == ReceiptFormat.Html ? "text/html" : "text/plain";
        return TypedResults.Text(text, contentType);
    }

    private static async Task<IResult> VoidSaleAsync(
        Guid id,
        [FromBody] VoidSaleBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SaleId saleId = new(id);

        Result<SaleId> result = await dispatcher
            .SendAsync(
                new VoidSaleCommand(
                    new Pos.Domain.Common.EventId(body.EventId),
                    saleId,
                    new LocationId(body.LocationId),
                    new CashierShiftId(body.ShiftId),
                    new DeviceId(body.DeviceId),
                    body.BusinessDate,
                    currentUser.UserId!.Value,
                    body.VoidedAtUtc,
                    body.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static List<CompleteSaleLine> MapLines(IReadOnlyList<CompleteSaleLineBody> lines)
        => lines.Select(l => new CompleteSaleLine(
            new ProductId(l.ProductId),
            l.Quantity,
            new UnitOfMeasureId(l.UnitOfMeasureId),
            l.Barcode,
            l.UnitPriceOverride,
            l.PriceOverrideAuthorizedByUserId.HasValue
                ? new UserId(l.PriceOverrideAuthorizedByUserId.Value) : null,
            l.Discount,
            l.DiscountAuthorizedByUserId.HasValue
                ? new UserId(l.DiscountAuthorizedByUserId.Value) : null,
            l.AllowExpiredOverride,
            l.ExpiredOverrideReason)).ToList();

    private static List<CompleteSalePayment> MapPayments(IReadOnlyList<CompleteSalePaymentBody> payments)
        => payments.Select(p => new CompleteSalePayment(
            p.Method, p.Amount, p.Tendered, p.ProviderReference)).ToList();

    private static SaleDetail ToDetail(Sale sale) => new(
        sale.Id.Value,
        sale.Number,
        sale.Status.ToString(),
        sale.EventId.Value,
        sale.LocationId.Value,
        sale.CashierShiftId.Value,
        sale.DeviceId.Value,
        sale.CustomerId?.Value,
        sale.BusinessDate,
        sale.CompletedAtUtc,
        sale.CompletedByUserId.Value,
        sale.GrossTotal,
        sale.DiscountTotal,
        sale.NetTotal,
        sale.VatTotal,
        sale.VatExemptTotal,
        sale.ZeroRatedTotal,
        sale.TaxableBaseTotal,
        sale.Items.Select(i => new SaleLineDetail(
            i.LineNumber,
            i.ProductId.Value,
            i.ProductName,
            i.Barcode,
            i.Quantity,
            i.UnitPrice,
            i.Discount,
            i.GrossAmount,
            i.NetAmount,
            i.VatRate,
            i.Vat,
            i.IsVatExempt,
            i.IsZeroRated)).ToList(),
        sale.Payments.Select(p => new SalePaymentDetail(
            p.Method.ToString(),
            p.Amount,
            p.Tendered,
            p.Change,
            p.ProviderReference)).ToList());
}
