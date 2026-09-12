using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A conversion between two units of measure for one product, for example
/// <c>1 Case = 24 Piece</c>.
/// </summary>
public sealed class ProductUnitConversion
{
    internal ProductUnitConversion(
        ProductUnitConversionId id,
        ProductId productId,
        UnitOfMeasureId fromUnitId,
        UnitOfMeasureId toUnitId,
        decimal factor)
    {
        Id = id;
        ProductId = productId;
        FromUnitId = fromUnitId;
        ToUnitId = toUnitId;
        Factor = factor;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductUnitConversion()
    {
    }

    /// <summary>Gets the conversion row identifier.</summary>
    public ProductUnitConversionId Id { get; }

    /// <summary>Gets the owning product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the source unit.</summary>
    public UnitOfMeasureId FromUnitId { get; }

    /// <summary>Gets the target unit.</summary>
    public UnitOfMeasureId ToUnitId { get; }

    /// <summary>Gets how many source units make one target unit.</summary>
    public decimal Factor { get; }

    /// <summary>Creates a unit conversion row.</summary>
    /// <param name="productId">The owning product.</param>
    /// <param name="fromUnitId">The source unit.</param>
    /// <param name="toUnitId">The target unit.</param>
    /// <param name="factor">The multiplier; must be greater than zero.</param>
    /// <returns>The conversion, or a validation failure.</returns>
    public static Result<ProductUnitConversion> Create(
        ProductId productId,
        UnitOfMeasureId fromUnitId,
        UnitOfMeasureId toUnitId,
        decimal factor)
    {
        if (fromUnitId.IsEmpty || toUnitId.IsEmpty)
        {
            return Result<ProductUnitConversion>.Failure(
                Error.Validation("conversion.units_required", "Both units of a conversion are required."));
        }

        if (fromUnitId == toUnitId)
        {
            return Result<ProductUnitConversion>.Failure(
                Error.Validation("conversion.same_units", "A conversion must be between two different units."));
        }

        if (factor <= 0m)
        {
            return Result<ProductUnitConversion>.Failure(
                Error.Validation("conversion.factor_positive", "A conversion factor must be greater than zero."));
        }

        return Result<ProductUnitConversion>.Success(new ProductUnitConversion(
            ProductUnitConversionId.New(),
            productId,
            fromUnitId,
            toUnitId,
            factor));
    }
}