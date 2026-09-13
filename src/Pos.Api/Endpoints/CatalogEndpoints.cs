using System.Linq.Expressions;
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

/// <summary>Catalog and purchasing master-data endpoints.</summary>
public static class CatalogEndpoints
{
    /// <summary>Maps the catalog routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/catalog").WithTags("Catalog");

        group.MapGet("/products", ListProductsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListProducts")
            .WithSummary("Searches the product catalogue.");

        group.MapGet("/products/by-barcode/{barcode}", GetProductByBarcodeAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetProductByBarcode")
            .WithSummary("Looks up a product by one of its barcodes.");

        group.MapGet("/products/{id:guid}", GetProductAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetProduct")
            .WithSummary("Gets one product by identifier.");

        group.MapPost("/products", CreateProductAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateProduct")
            .WithSummary("Creates a product master record.");

        group.MapGet("/categories", ListCategoriesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListCategories")
            .WithSummary("Lists product categories.");

        group.MapPost("/categories", CreateCategoryAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.ManageCategories)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateCategory")
            .WithSummary("Creates a product category.");

        group.MapGet("/brands", ListBrandsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListBrands")
            .WithSummary("Lists product brands.");

        group.MapPost("/brands", CreateBrandAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.ManageBrands)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateBrand")
            .WithSummary("Creates a product brand.");

        group.MapGet("/units", ListUnitsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListUnitsOfMeasure")
            .WithSummary("Lists units of measure.");

        group.MapPost("/units", CreateUnitAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.ManageUnits)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateUnitOfMeasure")
            .WithSummary("Creates a unit of measure.");

        group.MapGet("/suppliers", ListSuppliersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.ViewSuppliers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListSuppliers")
            .WithSummary("Lists suppliers.");

        group.MapPost("/suppliers", CreateSupplierAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.ManageSuppliers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateSupplier")
            .WithSummary("Creates a supplier.");

        return app;
    }

    private static async Task<IResult> ListProductsAsync(
        PosDbContext context,
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        [FromQuery] string? q,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        int boundedOffset = Math.Max(0, offset);
        int boundedLimit = Math.Clamp(limit, 1, 200);

        IQueryable<Product> query = context.Products.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            string term = q.Trim();

            // Name matches run through LIKE (case-insensitive on SQLite, which is
            // the offline store's embedded database); SKU matches are exact after
            // normalization, because the SKU is a value-converted key and partial
            // string functions do not translate through the converter. Retired
            // barcodes no longer find their product.
            query = query.Where(p =>
                EF.Functions.Like(p.Name, $"%{term}%")
                || p.Sku == Sku.FromTrustedSource(term.ToUpperInvariant())
                || context.ProductBarcodes.Any(b =>
                    b.ProductId == p.Id && b.RetiredAtUtc == null && b.Value.Contains(term)));
        }

        List<ProductSummary> products = await query
            .OrderBy(p => p.Name)
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .Select(ToSummary(context))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await CanViewCostAsync(evaluator, currentUser, cancellationToken).ConfigureAwait(false))
        {
            products = [.. products.Select(p => p with { DefaultPurchaseCost = null })];
        }

        return TypedResults.Ok(products);
    }

    private static async Task<IResult> GetProductAsync(
        Guid id,
        PosDbContext context,
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ProductSummary? product = await context.Products
            .AsNoTracking()
            .Where(p => p.Id == new ProductId(id))
            .Select(ToSummary(context))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<ProductSummary>.Failure(CatalogErrors.ProductUnknown(new ProductId(id))),
                currentUser.CorrelationId.Value);
        }

        return await CanViewCostAsync(evaluator, currentUser, cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(product)
            : TypedResults.Ok(product with { DefaultPurchaseCost = null });
    }

    private static async Task<IResult> GetProductByBarcodeAsync(
        string barcode,
        PosDbContext context,
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        string normalisedBarcode = barcode.Trim().ToUpperInvariant();

        // A retired code answers exactly like an unknown one, so the POS and
        // receiving clients route it to the quarantine workflow.
        Guid? productId = await context.ProductBarcodes
            .AsNoTracking()
            .Where(b => b.Value == normalisedBarcode && b.RetiredAtUtc == null)
            .Select(b => (Guid?)b.ProductId.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return productId is null
            ? ProblemDetailsMapping.ToProblem(
                Result<ProductSummary>.Failure(CatalogErrors.BarcodeUnknown(normalisedBarcode)),
                currentUser.CorrelationId.Value)
            : await GetProductAsync(productId.Value, context, evaluator, currentUser, cancellationToken)
                .ConfigureAwait(false);
    }

    private static Expression<Func<Product, ProductSummary>> ToSummary(PosDbContext context)
        => p => new ProductSummary(
            p.Id.Value,
            p.Sku.Value,
            p.Name,
            p.Description,
            p.CategoryId.Value,
            p.BrandId != null ? p.BrandId.Value.Value : null,
            p.PrimarySupplierId != null ? p.PrimarySupplierId.Value.Value : null,
            p.BaseUnitOfMeasureId.Value,
            p.TaxCode,
            p.IsVatExempt,
            p.DefaultPurchaseCost,
            p.TracksBatches,
            p.TracksExpiry,
            p.ShelfLifeDays,
            p.ImageRef,
            p.IsActive,
            p.DiscontinuedOn,
            p.CreatedAtUtc,
            p.UpdatedAtUtc,
            context.ProductBarcodes
                .Where(b => b.ProductId == p.Id && b.RetiredAtUtc == null)
                .OrderBy(b => b.IsPrimary ? 0 : 1)
                .ThenBy(b => b.Value)
                .Select(b => b.Value)
                .ToList());

    private static Task<bool> CanViewCostAsync(
        IPermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => evaluator.HasPermissionAsync(
            currentUser.UserId ?? UserId.Empty,
            Permissions.Catalog.ViewCost,
            locationId: null,
            cancellationToken);

    private static async Task<IResult> CreateProductAsync(
        [FromBody] CreateProductBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<ProductId> result = await dispatcher
            .SendAsync(
                new CreateProductCommand(
                    body.Sku,
                    body.Name,
                    new CategoryId(body.CategoryId),
                    new UnitOfMeasureId(body.BaseUnitOfMeasureId),
                    body.Description,
                    body.BrandId is { } brandId ? new BrandId(brandId) : null,
                    body.PrimarySupplierId is { } supplierId ? new SupplierId(supplierId) : null,
                    body.TaxCode,
                    body.IsVatExempt,
                    body.DefaultPurchaseCost,
                    body.TracksBatches,
                    body.TracksExpiry,
                    body.ShelfLifeDays,
                    body.InitialBarcode),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/catalog/products/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListCategoriesAsync(
        PosDbContext context,
        CancellationToken cancellationToken)
    {
        List<CategorySummary> categories = await context.Categories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Code)
            .Select(c => new CategorySummary(
                c.Id.Value,
                c.ParentId != null ? c.ParentId.Value.Value : null,
                c.Code,
                c.Name,
                c.SortOrder,
                c.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(categories);
    }

    private static async Task<IResult> CreateCategoryAsync(
        [FromBody] CreateCategoryBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<CategoryId> result = await dispatcher
            .SendAsync(
                new CreateCategoryCommand(
                    body.Code,
                    body.Name,
                    body.ParentId is { } parentId ? new CategoryId(parentId) : null,
                    body.SortOrder),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListBrandsAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        List<BrandSummary> brands = await context.Brands
            .AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.Name)
            .Select(b => new BrandSummary(b.Id.Value, b.Name, b.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(brands);
    }

    private static async Task<IResult> CreateBrandAsync(
        [FromBody] CreateBrandBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<BrandId> result = await dispatcher
            .SendAsync(new CreateBrandCommand(body.Name), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListUnitsAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        List<UnitOfMeasureSummary> units = await context.UnitsOfMeasure
            .AsNoTracking()
            .OrderBy(u => u.Code)
            .Select(u => new UnitOfMeasureSummary(u.Id.Value, u.Code, u.Name, u.Kind, u.DecimalPlaces))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(units);
    }

    private static async Task<IResult> CreateUnitAsync(
        [FromBody] CreateUnitOfMeasureBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<UnitOfMeasureId> result = await dispatcher
            .SendAsync(
                new CreateUnitOfMeasureCommand(body.Code, body.Name, body.Kind, body.DecimalPlaces),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListSuppliersAsync(
        PosDbContext context,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Supplier> query = context.Suppliers.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        List<SupplierSummary> suppliers = await query
            .OrderBy(s => s.Code)
            .Select(s => new SupplierSummary(
                s.Id.Value,
                s.Code,
                s.Name,
                s.TaxId,
                s.PaymentTermsDays,
                s.LeadTimeDays,
                s.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(suppliers);
    }

    private static async Task<IResult> CreateSupplierAsync(
        [FromBody] CreateSupplierBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SupplierId> result = await dispatcher
            .SendAsync(
                new CreateSupplierCommand(
                    body.Code,
                    body.Name,
                    body.TaxId,
                    body.PaymentTermsDays,
                    body.LeadTimeDays),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}