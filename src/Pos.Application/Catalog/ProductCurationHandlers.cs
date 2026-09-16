using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>
/// Shared steps of every product curation handler: load the product tracked, and
/// record the change in the audit log inside the same unit of work.
/// </summary>
internal static class ProductCuration
{
    /// <summary>Loads a product for change, or fails with <c>catalog.product_unknown</c>.</summary>
    /// <param name="masterData">The repository.</param>
    /// <param name="productId">The product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked product, or a not-found failure.</returns>
    internal static async Task<Result<Product>> LoadAsync(
        IMasterDataRepository masterData,
        ProductId productId,
        CancellationToken cancellationToken)
    {
        Product? product = await masterData
            .GetProductForUpdateAsync(productId, cancellationToken)
            .ConfigureAwait(false);

        return product is null
            ? Result<Product>.Failure(CatalogErrors.ProductUnknown(productId))
            : Result<Product>.Success(product);
    }

    /// <summary>Stages an audit entry for a product change.</summary>
    /// <param name="audit">The audit writer.</param>
    /// <param name="action">The stable action code.</param>
    /// <param name="productId">The product.</param>
    /// <param name="previous">The prior state, or null.</param>
    /// <param name="next">The new state, or null.</param>
    /// <param name="reason">The actor's reason, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="locationId">The location the change applies to, if any.</param>
    /// <returns>A task that completes when the entry is staged.</returns>
    internal static Task AuditAsync(
        IAuditWriter audit,
        string action,
        ProductId productId,
        object? previous,
        object? next,
        string? reason,
        CancellationToken cancellationToken,
        LocationId? locationId = null)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                nameof(Product),
                productId.Value,
                previous is null ? null : JsonSerializer.Serialize(previous),
                next is null ? null : JsonSerializer.Serialize(next),
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                LocationId: locationId),
            cancellationToken);

    /// <summary>Captures the editable master fields for the audit log.</summary>
    /// <param name="product">The product.</param>
    /// <returns>A serialisable snapshot.</returns>
    internal static object Details(Product product) => new
    {
        product.Name,
        product.Description,
        CategoryId = product.CategoryId.Value,
        BrandId = product.BrandId?.Value,
        PrimarySupplierId = product.PrimarySupplierId?.Value,
        product.TaxCode,
        product.IsVatExempt,
        product.ImageRef,
    };
}

/// <summary>Handles <see cref="UpdateProductCommand"/>.</summary>
public sealed class UpdateProductCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<UpdateProductCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Error? missing = await masterData
            .FindMissingReferenceAsync(
                new ProductReferences(command.CategoryId, command.BrandId, command.PrimarySupplierId),
                cancellationToken)
            .ConfigureAwait(false);

        if (missing is not null)
        {
            return Result<ProductId>.Failure(missing);
        }

        Product product = loaded.Value;
        object previous = ProductCuration.Details(product);
        decimal previousCost = product.DefaultPurchaseCost;

        Result updated = product.UpdateDetails(
            command.Name,
            command.Description,
            command.CategoryId,
            command.BrandId,
            command.PrimarySupplierId,
            command.TaxCode,
            command.IsVatExempt,
            command.DefaultPurchaseCost,
            command.ImageRef);

        if (updated.IsFailure)
        {
            return Result<ProductId>.Failure(updated.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id, previous, ProductCuration.Details(product),
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        // Cost is audited on its own action code so a margin review can find
        // every cost change without reading every product edit.
        if (previousCost != product.DefaultPurchaseCost)
        {
            await ProductCuration.AuditAsync(
                    audit, AuditActions.Catalog.CostChanged, product.Id,
                    new { DefaultPurchaseCost = previousCost },
                    new { product.DefaultPurchaseCost },
                    reason: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>Handles <see cref="SetProductActivationCommand"/>.</summary>
public sealed class SetProductActivationCommandHandler(
    IMasterDataRepository masterData,
    IAuditWriter audit,
    ISystemClock clock) : ICommandHandler<SetProductActivationCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        SetProductActivationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Product product = loaded.Value;
        object previous = new { product.IsActive, product.DiscontinuedOn };

        Result changed = command.Active
            ? product.Activate()
            : product.Deactivate(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));

        if (changed.IsFailure)
        {
            return Result<ProductId>.Failure(changed.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductActivationChanged, product.Id, previous,
                new { product.IsActive, product.DiscontinuedOn }, command.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>
/// Handles <see cref="AddProductBarcodeCommand"/>. The aggregate refuses a code
/// it already holds; the repository refuses one any other product holds,
/// including retired codes, which stay reserved.
/// </summary>
public sealed class AddProductBarcodeCommandHandler(
    IMasterDataRepository masterData,
    IAuditWriter audit,
    ICurrentUser currentUser) : ICommandHandler<AddProductBarcodeCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(AddProductBarcodeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Product product = loaded.Value;
        UnitOfMeasureId unitId = command.UnitOfMeasureId ?? product.BaseUnitOfMeasureId;

        Error? missing = await masterData
            .FindMissingReferenceAsync(new ProductReferences(Units: [unitId]), cancellationToken)
            .ConfigureAwait(false);

        if (missing is not null)
        {
            return Result<ProductId>.Failure(missing);
        }

        Result added = product.AddBarcode(
            command.Barcode,
            unitId,
            command.PackQuantity,
            command.IsPrimary,
            currentUser.UserId ?? UserId.Empty,
            requireChecksum: false);

        if (added.IsFailure)
        {
            return Result<ProductId>.Failure(added.Errors);
        }

        ProductBarcode barcode = product.Barcodes[^1];

        if (await masterData.BarcodeInUseAsync(barcode.Value, cancellationToken).ConfigureAwait(false))
        {
            return Result<ProductId>.Failure(CatalogErrors.DuplicateBarcode(barcode.Value));
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.BarcodeChanged, product.Id, previous: null,
                new
                {
                    Operation = "attached",
                    Barcode = barcode.Value,
                    UnitOfMeasureId = barcode.UnitOfMeasureId.Value,
                    barcode.PackQuantity,
                    barcode.IsPrimary,
                },
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>Handles <see cref="RetireProductBarcodeCommand"/>.</summary>
public sealed class RetireProductBarcodeCommandHandler(
    IMasterDataRepository masterData,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RetireProductBarcodeCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        RetireProductBarcodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Product product = loaded.Value;

        Result retired = product.RetireBarcode(command.Barcode, currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (retired.IsFailure)
        {
            return Result<ProductId>.Failure(retired.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.BarcodeChanged, product.Id, previous: null,
                new
                {
                    Operation = "retired",
                    Barcode = Barcode.Create(command.Barcode).Value.Value,
                    PrimaryBarcode = product.Barcodes.FirstOrDefault(b => b.IsPrimary)?.Value,
                },
                command.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>Handles <see cref="SetPrimaryProductBarcodeCommand"/>.</summary>
public sealed class SetPrimaryProductBarcodeCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<SetPrimaryProductBarcodeCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        SetPrimaryProductBarcodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Product product = loaded.Value;
        string? previousPrimary = product.Barcodes.FirstOrDefault(b => b.IsPrimary)?.Value;

        Result changed = product.SetPrimaryBarcode(command.Barcode);

        if (changed.IsFailure)
        {
            return Result<ProductId>.Failure(changed.Errors);
        }

        string? primary = product.Barcodes.FirstOrDefault(b => b.IsPrimary)?.Value;

        if (primary != previousPrimary)
        {
            await ProductCuration.AuditAsync(
                    audit, AuditActions.Catalog.BarcodeChanged, product.Id,
                    new { PrimaryBarcode = previousPrimary },
                    new { Operation = "primary", PrimaryBarcode = primary },
                    reason: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>Handles <see cref="ScheduleProductPriceCommand"/>.</summary>
public sealed class ScheduleProductPriceCommandHandler(
    IMasterDataRepository masterData,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ScheduleProductPriceCommand, ProductPriceId>
{
    /// <inheritdoc />
    public async Task<Result<ProductPriceId>> HandleAsync(
        ScheduleProductPriceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductPriceId>.Failure(loaded.Errors);
        }

        if (command.LocationId is { } locationId)
        {
            Error? missing = await masterData
                .FindMissingReferenceAsync(new ProductReferences(Location: locationId), cancellationToken)
                .ConfigureAwait(false);

            if (missing is not null)
            {
                return Result<ProductPriceId>.Failure(missing);
            }
        }

        Product product = loaded.Value;
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset from = command.EffectiveFromUtc ?? now;

        // The row in effect in this exact scope when the new price starts, if any.
        ProductPrice? superseded = product.Prices.FirstOrDefault(p =>
            p.LocationId == command.LocationId && p.Overlaps(from, from.AddTicks(1)));

        Result<ProductPriceId> scheduled = product.SchedulePrice(
            command.LocationId,
            command.Amount,
            from,
            command.EffectiveToUtc,
            currentUser.UserId ?? UserId.Empty,
            command.Reason,
            now);

        if (scheduled.IsFailure)
        {
            return scheduled;
        }

        ProductPrice created = product.Prices.Single(p => p.Id == scheduled.Value);

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.PriceChanged, product.Id,
                superseded is null ? null : new { PriceId = superseded.Id.Value, superseded.Amount },
                new
                {
                    PriceId = created.Id.Value,
                    LocationId = created.LocationId?.Value,
                    created.Amount,
                    created.EffectiveFromUtc,
                    created.EffectiveToUtc,
                },
                command.Reason, cancellationToken, command.LocationId)
            .ConfigureAwait(false);

        return scheduled;
    }
}

/// <summary>Handles <see cref="CancelScheduledProductPriceCommand"/>.</summary>
public sealed class CancelScheduledProductPriceCommandHandler(
    IMasterDataRepository masterData,
    IAuditWriter audit,
    ISystemClock clock) : ICommandHandler<CancelScheduledProductPriceCommand, ProductPriceId>
{
    /// <inheritdoc />
    public async Task<Result<ProductPriceId>> HandleAsync(
        CancelScheduledProductPriceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductPriceId>.Failure(loaded.Errors);
        }

        Product product = loaded.Value;
        ProductPrice? cancelled = product.Prices.FirstOrDefault(p => p.Id == command.PriceId);

        if (cancelled is null)
        {
            return Result<ProductPriceId>.Failure(CatalogErrors.PriceUnknown(command.PriceId));
        }

        // Snapshot the row before removing it: cancellation deletes the row, so
        // the audit carries the only copy of what was cancelled.
        var before = new
        {
            PriceId = cancelled.Id.Value,
            LocationId = cancelled.LocationId?.Value,
            cancelled.Amount,
            cancelled.EffectiveFromUtc,
            cancelled.EffectiveToUtc,
        };

        Result<ProductPriceId> cancelledPrice = product.CancelScheduledPrice(
            command.PriceId,
            clock.UtcNow);

        if (cancelledPrice.IsFailure)
        {
            return cancelledPrice;
        }

        // The row now covering the cancelled period, if any: the predecessor
        // restored to carry the old amount through it.
        ProductPrice? restored = product.Prices.FirstOrDefault(p =>
            p.LocationId == cancelled.LocationId
            && p.Overlaps(cancelled.EffectiveFromUtc, cancelled.EffectiveToUtc));

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.PriceCancelled, product.Id,
                before,
                restored is null
                    ? null
                    : new
                    {
                        PriceId = restored.Id.Value,
                        LocationId = restored.LocationId?.Value,
                        restored.Amount,
                        restored.EffectiveFromUtc,
                        restored.EffectiveToUtc,
                    },
                command.Reason, cancellationToken, cancelled.LocationId)
            .ConfigureAwait(false);

        return cancelledPrice;
    }
}
