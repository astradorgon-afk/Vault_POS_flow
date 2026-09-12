using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>
/// Creates a product. The Main Warehouse is the only place a product master is
/// registered; holding <c>product.create</c> is enforced on the command itself.
/// </summary>
/// <param name="Sku">The stock-keeping unit code.</param>
/// <param name="Name">The display name.</param>
/// <param name="CategoryId">The category.</param>
/// <param name="BaseUnitOfMeasureId">The unit the ledger counts in.</param>
/// <param name="Description">Free-text description, or null.</param>
/// <param name="BrandId">The brand, or null.</param>
/// <param name="PrimarySupplierId">The primary supplier, or null.</param>
/// <param name="TaxCode">The VAT/tax code, or null.</param>
/// <param name="IsVatExempt">Whether the product is VAT-exempt.</param>
/// <param name="DefaultPurchaseCost">The default purchase cost per base unit.</param>
/// <param name="TracksBatches">Whether the product is batch-tracked.</param>
/// <param name="TracksExpiry">Whether the product carries an expiry date.</param>
/// <param name="ShelfLifeDays">Shelf life in days, where expiry is tracked.</param>
/// <param name="InitialBarcode">A barcode to attach at creation, or null.</param>
/// <param name="InitialPrice">An initial selling price amount, or null.</param>
public sealed record CreateProductCommand(
    string? Sku,
    string? Name,
    CategoryId CategoryId,
    UnitOfMeasureId BaseUnitOfMeasureId,
    string? Description = null,
    BrandId? BrandId = null,
    SupplierId? PrimarySupplierId = null,
    string? TaxCode = null,
    bool IsVatExempt = false,
    decimal DefaultPurchaseCost = 0m,
    bool TracksBatches = false,
    bool TracksExpiry = false,
    int? ShelfLifeDays = null,
    string? InitialBarcode = null,
    decimal? InitialPrice = null)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => "product.create";
}

/// <summary>Handles <see cref="CreateProductCommand"/>.</summary>
public sealed class CreateProductCommandHandler(IMasterDataRepository masterData, ICurrentUser currentUser)
    : ICommandHandler<CreateProductCommand, ProductId>
{
    /// <inheritdoc />
    public async Task<Result<ProductId>> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        UserId actor = currentUser.UserId ?? UserId.Empty;

        Result<Product> created = Product.Create(
            command.Sku,
            command.Name,
            command.CategoryId,
            command.BaseUnitOfMeasureId,
            actor,
            command.Description,
            command.BrandId,
            command.PrimarySupplierId,
            command.TaxCode,
            command.IsVatExempt,
            command.DefaultPurchaseCost,
            command.TracksBatches,
            command.TracksExpiry,
            command.ShelfLifeDays);

        if (created.IsFailure)
        {
            return Result<ProductId>.Failure(created.Errors);
        }

        Product product = created.Value;

        if (!string.IsNullOrWhiteSpace(command.InitialBarcode))
        {
            Result attached = product.AddBarcode(
                command.InitialBarcode,
                command.BaseUnitOfMeasureId,
                1m,
                isPrimary: true,
                actor,
                requireChecksum: false);

            if (attached.IsFailure)
            {
                return Result<ProductId>.Failure(attached.Errors);
            }
        }

        return await masterData.CreateProductAsync(product, cancellationToken).ConfigureAwait(false);
    }
}