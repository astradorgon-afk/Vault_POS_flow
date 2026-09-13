using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Catalog;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>
/// Catalog curation of an existing product: edit, activation, barcodes,
/// effective-dated prices, per-location stocking, unit conversions and supplier
/// links. Every change is audited in the same transaction.
/// </summary>
public static class ProductCurationEndpoints
{
    /// <summary>Maps the product curation routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapProductCurationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/catalog/products/{id:guid}").WithTags("Catalog");

        Map(group.MapPut(string.Empty, UpdateAsync), Permissions.Catalog.Edit, "UpdateProduct", "Edits a product's master fields.");
        Map(group.MapPost("/deactivate", DeactivateAsync), Permissions.Catalog.Disable, "DeactivateProduct", "Stops a product being sold or received.");
        Map(group.MapPost("/activate", ActivateAsync), Permissions.Catalog.Disable, "ActivateProduct", "Returns a product to sale and receiving.");

        Map(group.MapGet("/barcodes", ListBarcodesAsync), Permissions.Catalog.View, "ListProductBarcodes", "Lists a product's barcodes, retired codes included.");
        Map(group.MapPost("/barcodes", AddBarcodeAsync), Permissions.Catalog.ManageBarcodes, "AddProductBarcode", "Attaches a barcode to a product.");
        Map(group.MapPost("/barcodes/{barcode}/retire", RetireBarcodeAsync), Permissions.Catalog.ManageBarcodes, "RetireProductBarcode", "Retires a barcode; the value stays reserved.");
        Map(group.MapPost("/barcodes/{barcode}/primary", SetPrimaryBarcodeAsync), Permissions.Catalog.ManageBarcodes, "SetPrimaryProductBarcode", "Makes an active barcode the primary code.");

        Map(group.MapGet("/prices", ListPricesAsync), Permissions.Catalog.View, "ListProductPrices", "Lists a product's price history and schedule.");
        Map(group.MapPost("/prices", SchedulePriceAsync), Permissions.Catalog.ManagePrices, "ScheduleProductPrice", "Schedules an effective-dated price.");

        Map(group.MapGet("/location-settings", ListLocationSettingsAsync), Permissions.Catalog.View, "ListProductLocationSettings", "Lists a product's per-location stocking settings.");
        Map(group.MapPut("/location-settings/{locationId:guid}", SetLocationSettingAsync), Permissions.Catalog.Edit, "SetProductLocationSetting", "Sets how a product is stocked at a location.");

        Map(group.MapGet("/unit-conversions", ListUnitConversionsAsync), Permissions.Catalog.View, "ListProductUnitConversions", "Lists a product's unit conversions.");
        Map(group.MapPost("/unit-conversions", AddUnitConversionAsync), Permissions.Catalog.Edit, "AddProductUnitConversion", "Adds a unit conversion.");
        Map(group.MapDelete("/unit-conversions/{conversionId:guid}", RemoveUnitConversionAsync), Permissions.Catalog.Edit, "RemoveProductUnitConversion", "Removes a unit conversion.");

        Map(group.MapGet("/suppliers", ListSuppliersAsync), Permissions.Catalog.ViewCost, "ListProductSuppliers", "Lists a product's supplier links and last costs.");
        Map(group.MapPut("/suppliers/{supplierId:guid}", LinkSupplierAsync), Permissions.Catalog.Edit, "LinkProductSupplier", "Links a supplier or updates the link's terms.");
        Map(group.MapDelete("/suppliers/{supplierId:guid}", UnlinkSupplierAsync), Permissions.Catalog.Edit, "UnlinkProductSupplier", "Removes a supplier link.");

        return app;
    }

    private static void Map(RouteHandlerBuilder route, string permission, string name, string summary)
        => route
            .WithMetadata(new RequirePermissionAttribute(permission) { Scope = ScopeSource.None })
            .WithName(name)
            .WithSummary(summary);

    private static Task<IResult> UpdateAsync(
        Guid id, [FromBody] UpdateProductBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new UpdateProductCommand(
                new ProductId(id),
                body.Name,
                body.Description,
                new CategoryId(body.CategoryId),
                body.BrandId is { } brandId ? new BrandId(brandId) : null,
                body.PrimarySupplierId is { } supplierId ? new SupplierId(supplierId) : null,
                body.TaxCode,
                body.IsVatExempt,
                body.DefaultPurchaseCost,
                body.ImageRef),
            cancellationToken);

    private static Task<IResult> DeactivateAsync(
        Guid id, [FromBody] ProductActivationBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new SetProductActivationCommand(new ProductId(id), Active: false, body.Reason), cancellationToken);

    private static Task<IResult> ActivateAsync(
        Guid id, [FromBody] ProductActivationBody? body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new SetProductActivationCommand(new ProductId(id), Active: true, body?.Reason), cancellationToken);

    private static async Task<IResult> ListBarcodesAsync(
        Guid id, PosDbContext context, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        if (await UnknownProductAsync(id, context, currentUser, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return unknown;
        }

        List<ProductBarcodeView> barcodes = await context.ProductBarcodes
            .AsNoTracking()
            .Where(b => b.ProductId == new ProductId(id))
            .OrderBy(b => b.RetiredAtUtc != null)
            .ThenByDescending(b => b.IsPrimary)
            .ThenBy(b => b.Value)
            .Select(b => new ProductBarcodeView(
                b.Value, b.Symbology.ToString(), b.UnitOfMeasureId.Value, b.PackQuantity, b.IsPrimary, b.CreatedAtUtc, b.RetiredAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(barcodes);
    }

    private static Task<IResult> AddBarcodeAsync(
        Guid id, [FromBody] AddProductBarcodeBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new AddProductBarcodeCommand(
                new ProductId(id),
                body.Barcode,
                body.UnitOfMeasureId is { } unitId ? new UnitOfMeasureId(unitId) : null,
                body.PackQuantity,
                body.IsPrimary),
            cancellationToken,
            created: true);

    private static Task<IResult> RetireBarcodeAsync(
        Guid id, string barcode, [FromBody] RetireProductBarcodeBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new RetireProductBarcodeCommand(new ProductId(id), barcode, body.Reason), cancellationToken);

    private static Task<IResult> SetPrimaryBarcodeAsync(
        Guid id, string barcode, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new SetPrimaryProductBarcodeCommand(new ProductId(id), barcode), cancellationToken);

    private static async Task<IResult> ListPricesAsync(
        Guid id,
        PosDbContext context,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] Guid? locationId,
        CancellationToken cancellationToken)
    {
        if (await UnknownProductAsync(id, context, currentUser, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return unknown;
        }

        IQueryable<ProductPrice> query = context.Set<ProductPrice>()
            .AsNoTracking()
            .Where(p => p.ProductId == new ProductId(id));

        if (locationId is { } scope)
        {
            query = query.Where(p => p.LocationId == new LocationId(scope));
        }

        // Read into memory before shaping: the amount is a value-converted column
        // and the in-effect flag compares against the server clock.
        List<ProductPrice> rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;

        List<ProductPriceView> prices = [.. rows
            .OrderBy(p => p.LocationId?.Value)
            .ThenByDescending(p => p.EffectiveFromUtc)
            .Select(p => new ProductPriceView(
                p.Id.Value,
                p.LocationId?.Value,
                p.Amount,
                p.EffectiveFromUtc,
                p.EffectiveToUtc,
                p.Reason,
                p.CreatedAtUtc,
                p.EffectiveFromUtc <= now && (p.EffectiveToUtc is null || now < p.EffectiveToUtc)))];

        return TypedResults.Ok(prices);
    }

    private static async Task<IResult> SchedulePriceAsync(
        Guid id, [FromBody] ScheduleProductPriceBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        Result<ProductPriceId> result = await dispatcher
            .SendAsync(
                new ScheduleProductPriceCommand(
                    new ProductId(id),
                    body.LocationId is { } locationId ? new LocationId(locationId) : null,
                    body.Amount,
                    body.EffectiveFromUtc,
                    body.EffectiveToUtc,
                    body.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(PricesPath(id), new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListLocationSettingsAsync(
        Guid id, PosDbContext context, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        if (await UnknownProductAsync(id, context, currentUser, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return unknown;
        }

        List<ProductLocationSettingView> settings = await context.Set<ProductLocationSetting>()
            .AsNoTracking()
            .Where(s => s.ProductId == new ProductId(id))
            .Select(s => new ProductLocationSettingView(
                s.LocationId.Value, s.IsStocked, s.MinimumStock, s.ReorderPoint, s.TargetStock, s.MaximumStock,
                s.PreferredReplenishmentQuantity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(settings);
    }

    private static Task<IResult> SetLocationSettingAsync(
        Guid id, Guid locationId, [FromBody] ProductLocationSettingBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new SetProductLocationSettingCommand(
                new ProductId(id),
                new LocationId(locationId),
                body.IsStocked,
                body.MinimumStock,
                body.ReorderPoint,
                body.TargetStock,
                body.MaximumStock,
                body.PreferredReplenishmentQuantity),
            cancellationToken);

    private static async Task<IResult> ListUnitConversionsAsync(
        Guid id, PosDbContext context, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        if (await UnknownProductAsync(id, context, currentUser, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return unknown;
        }

        List<ProductUnitConversionView> conversions = await context.Set<ProductUnitConversion>()
            .AsNoTracking()
            .Where(c => c.ProductId == new ProductId(id))
            .Select(c => new ProductUnitConversionView(c.Id.Value, c.FromUnitId.Value, c.ToUnitId.Value, c.Factor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(conversions);
    }

    private static async Task<IResult> AddUnitConversionAsync(
        Guid id, [FromBody] AddProductUnitConversionBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        Result<ProductUnitConversionId> result = await dispatcher
            .SendAsync(
                new AddProductUnitConversionCommand(
                    new ProductId(id), new UnitOfMeasureId(body.FromUnitId), new UnitOfMeasureId(body.ToUnitId), body.Factor),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/catalog/products/{id}/unit-conversions"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> RemoveUnitConversionAsync(
        Guid id, Guid conversionId, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new RemoveProductUnitConversionCommand(new ProductId(id), new ProductUnitConversionId(conversionId)),
            cancellationToken);

    private static async Task<IResult> ListSuppliersAsync(
        Guid id, PosDbContext context, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        if (await UnknownProductAsync(id, context, currentUser, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return unknown;
        }

        List<ProductSupplierView> links = await context.Set<ProductSupplier>()
            .AsNoTracking()
            .Where(s => s.ProductId == new ProductId(id))
            .OrderByDescending(s => s.IsPreferred)
            .Select(s => new ProductSupplierView(
                s.SupplierId.Value, s.SupplierSku, s.LastCost, s.LeadTimeDays, s.MinimumOrderQuantity, s.IsPreferred))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(links);
    }

    private static Task<IResult> LinkSupplierAsync(
        Guid id, Guid supplierId, [FromBody] ProductSupplierBody body, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new LinkProductSupplierCommand(
                new ProductId(id),
                new SupplierId(supplierId),
                body.SupplierSku,
                body.LeadTimeDays,
                body.MinimumOrderQuantity,
                body.IsPreferred),
            cancellationToken);

    private static Task<IResult> UnlinkSupplierAsync(
        Guid id, Guid supplierId, IDispatcher dispatcher, ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new UnlinkProductSupplierCommand(new ProductId(id), new SupplierId(supplierId)), cancellationToken);

    private static async Task<IResult> SendAsync(
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        ICommand<ProductId> command,
        CancellationToken cancellationToken,
        bool created = false)
    {
        Result<ProductId> result = await dispatcher.SendAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
        }

        return created
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/catalog/products/{result.Value.Value}/barcodes"),
                new { id = result.Value.Value })
            : TypedResults.NoContent();
    }

    private static async Task<IResult?> UnknownProductAsync(
        Guid id, PosDbContext context, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        bool exists = await context.Products
            .AsNoTracking()
            .AnyAsync(p => p.Id == new ProductId(id), cancellationToken)
            .ConfigureAwait(false);

        return exists
            ? null
            : ProblemDetailsMapping.ToProblem(
                Result<ProductId>.Failure(CatalogErrors.ProductUnknown(new ProductId(id))),
                currentUser.CorrelationId.Value);
    }

    private static string PricesPath(Guid id) => FormattableString.Invariant($"/api/v1/catalog/products/{id}/prices");
}
