using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Handles <see cref="SetProductLocationSettingCommand"/>.</summary>
public sealed class SetProductLocationSettingCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<SetProductLocationSettingCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        SetProductLocationSettingCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Error? missing = await masterData
            .FindMissingReferenceAsync(new ProductReferences(Location: command.LocationId), cancellationToken)
            .ConfigureAwait(false);

        if (missing is not null)
        {
            return Result<ProductId>.Failure(missing);
        }

        Product product = loaded.Value;
        ProductLocationSetting? existing = product.LocationSettings.FirstOrDefault(s => s.LocationId == command.LocationId);
        object? previous = existing is null ? null : Snapshot(existing);

        Result set = product.SetLocationSetting(
            command.LocationId,
            command.IsStocked,
            command.MinimumStock,
            command.ReorderPoint,
            command.TargetStock,
            command.MaximumStock,
            command.PreferredReplenishmentQuantity);

        if (set.IsFailure)
        {
            return Result<ProductId>.Failure(set.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id, previous,
                Snapshot(product.LocationSettings.Single(s => s.LocationId == command.LocationId)),
                reason: null, cancellationToken, command.LocationId)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }

    private static object Snapshot(ProductLocationSetting setting) => new
    {
        LocationSetting = setting.LocationId.Value,
        setting.IsStocked,
        setting.MinimumStock,
        setting.ReorderPoint,
        setting.TargetStock,
        setting.MaximumStock,
        setting.PreferredReplenishmentQuantity,
    };
}

/// <summary>Handles <see cref="AddProductUnitConversionCommand"/>.</summary>
public sealed class AddProductUnitConversionCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<AddProductUnitConversionCommand, ProductUnitConversionId>
{
    /// <inheritdoc />
    public async Task<Result<ProductUnitConversionId>> HandleAsync(
        AddProductUnitConversionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductUnitConversionId>.Failure(loaded.Errors);
        }

        Error? missing = await masterData
            .FindMissingReferenceAsync(
                new ProductReferences(Units: [command.FromUnitId, command.ToUnitId]),
                cancellationToken)
            .ConfigureAwait(false);

        if (missing is not null)
        {
            return Result<ProductUnitConversionId>.Failure(missing);
        }

        Product product = loaded.Value;

        Result<ProductUnitConversionId> added = product.AddUnitConversion(
            command.FromUnitId, command.ToUnitId, command.Factor);

        if (added.IsFailure)
        {
            return added;
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id, previous: null,
                new
                {
                    UnitConversionAdded = added.Value.Value,
                    FromUnitId = command.FromUnitId.Value,
                    ToUnitId = command.ToUnitId.Value,
                    command.Factor,
                },
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return added;
    }
}

/// <summary>Handles <see cref="RemoveProductUnitConversionCommand"/>.</summary>
public sealed class RemoveProductUnitConversionCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<RemoveProductUnitConversionCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        RemoveProductUnitConversionCommand command,
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
        ProductUnitConversion? conversion = product.UnitConversions.FirstOrDefault(c => c.Id == command.ConversionId);

        Result removed = product.RemoveUnitConversion(command.ConversionId);

        if (removed.IsFailure)
        {
            return Result<ProductId>.Failure(removed.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id,
                new
                {
                    UnitConversionRemoved = command.ConversionId.Value,
                    FromUnitId = conversion!.FromUnitId.Value,
                    ToUnitId = conversion.ToUnitId.Value,
                    conversion.Factor,
                },
                next: null, reason: null, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}

/// <summary>Handles <see cref="LinkProductSupplierCommand"/>.</summary>
public sealed class LinkProductSupplierCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<LinkProductSupplierCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        LinkProductSupplierCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Product> loaded = await ProductCuration.LoadAsync(masterData, command.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<ProductId>.Failure(loaded.Errors);
        }

        Error? missing = await masterData
            .FindMissingReferenceAsync(new ProductReferences(Supplier: command.SupplierId), cancellationToken)
            .ConfigureAwait(false);

        if (missing is not null)
        {
            return Result<ProductId>.Failure(missing);
        }

        Product product = loaded.Value;
        ProductSupplier? existing = product.Suppliers.FirstOrDefault(s => s.SupplierId == command.SupplierId);
        object? previous = existing is null ? null : Snapshot(existing);

        Result linked = product.LinkSupplier(
            command.SupplierId,
            command.SupplierSku,
            command.LeadTimeDays,
            command.MinimumOrderQuantity,
            command.IsPreferred);

        if (linked.IsFailure)
        {
            return Result<ProductId>.Failure(linked.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id, previous,
                Snapshot(product.Suppliers.Single(s => s.SupplierId == command.SupplierId)),
                reason: null, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }

    private static object Snapshot(ProductSupplier link) => new
    {
        SupplierLink = link.SupplierId.Value,
        link.SupplierSku,
        link.LeadTimeDays,
        link.MinimumOrderQuantity,
        link.IsPreferred,
    };
}

/// <summary>Handles <see cref="UnlinkProductSupplierCommand"/>.</summary>
public sealed class UnlinkProductSupplierCommandHandler(IMasterDataRepository masterData, IAuditWriter audit)
    : ICommandHandler<UnlinkProductSupplierCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        UnlinkProductSupplierCommand command,
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
        Result unlinked = product.UnlinkSupplier(command.SupplierId);

        if (unlinked.IsFailure)
        {
            return Result<ProductId>.Failure(unlinked.Errors);
        }

        await ProductCuration.AuditAsync(
                audit, AuditActions.Catalog.ProductUpdated, product.Id,
                new { SupplierUnlinked = command.SupplierId.Value },
                next: null, reason: null, cancellationToken)
            .ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}
