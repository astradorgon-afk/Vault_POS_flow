using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>The nature of a unit of measure.</summary>
public enum UnitKind
{
    /// <summary>A countable unit such as piece or carton.</summary>
    Count = 0,

    /// <summary>A mass unit such as gram or kilogram.</summary>
    Weight = 1,

    /// <summary>A volume unit such as litre or millilitre.</summary>
    Volume = 2,
}

/// <summary>
/// A unit of measure in which products are counted, weighed or measured.
/// The ledger always counts in a product's base unit; other units are expressed
/// through <see cref="ProductUnitConversion"/>.
/// </summary>
public sealed class UnitOfMeasure : AggregateRoot<UnitOfMeasureId>
{
    /// <summary>Maximum length of a unit code or name.</summary>
    public const int CodeMaxLength = 12;

    /// <summary>Maximum length of a unit name.</summary>
    public const int NameMaxLength = 32;

    private UnitOfMeasure(UnitOfMeasureId id, string code, string name, UnitKind kind, int decimalPlaces)
    {
        Id = id;
        Code = code;
        Name = name;
        Kind = kind;
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private UnitOfMeasure()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Gets the short unique code, for example <c>PC</c> or <c>KG</c>.</summary>
    public string Code { get; private set; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the measurement kind.</summary>
    public UnitKind Kind { get; private set; }

    /// <summary>Gets how many decimal places a quantity in this unit may carry.</summary>
    public int DecimalPlaces { get; private set; }

    /// <summary>Creates a unit of measure.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="kind">The measurement kind.</param>
    /// <param name="decimalPlaces">Display/rounding precision for quantities in this unit.</param>
    /// <returns>The new unit, or a validation failure.</returns>
    public static Result<UnitOfMeasure> Create(string? code, string? name, UnitKind kind, int decimalPlaces = 0)
    {
        string normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalisedCode.Length == 0 || normalisedCode.Length > CodeMaxLength)
        {
            return Result<UnitOfMeasure>.Failure(Error.Validation(
                "uom.code_invalid",
                FormattableString.Invariant(
                    $"A unit code is required and may be at most {CodeMaxLength} characters.")));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result<UnitOfMeasure>.Failure(Error.Validation(
                "uom.name_invalid",
                FormattableString.Invariant(
                    $"A unit name is required and may be at most {NameMaxLength} characters.")));
        }

        if (decimalPlaces is < 0 or > 6)
        {
            return Result<UnitOfMeasure>.Failure(Error.Validation(
                "uom.decimal_places_invalid",
                "Decimal places must be between 0 and 6."));
        }

        return Result<UnitOfMeasure>.Success(new UnitOfMeasure(
            UnitOfMeasureId.New(),
            normalisedCode,
            name.Trim(),
            kind,
            decimalPlaces));
    }
}