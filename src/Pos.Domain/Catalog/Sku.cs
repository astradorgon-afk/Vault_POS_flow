using System.Text.RegularExpressions;
using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A stock-keeping unit code. Normalised to upper case so lookups and
/// uniqueness are case-insensitive without relying on database collation.
/// </summary>
public readonly partial record struct Sku
{
    /// <summary>Maximum permitted length.</summary>
    public const int MaxLength = 32;

    private Sku(string value) => Value = value;

    /// <summary>Gets the normalised code.</summary>
    public string Value { get; }

    /// <summary>Parses and validates a SKU.</summary>
    /// <param name="value">The raw input.</param>
    /// <returns>The SKU, or a validation failure.</returns>
    public static Result<Sku> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<Sku>.Failure(
                Error.Validation("catalog.sku_required", "A SKU is required."));
        }

        string normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length > MaxLength)
        {
            return Result<Sku>.Failure(Error.Validation(
                "catalog.sku_too_long",
                FormattableString.Invariant($"A SKU may be at most {MaxLength} characters.")));
        }

        if (!SkuPattern().IsMatch(normalised))
        {
            return Result<Sku>.Failure(Error.Validation(
                "catalog.sku_invalid_characters",
                "A SKU may contain only letters, digits, dot, underscore and hyphen."));
        }

        return Result<Sku>.Success(new Sku(normalised));
    }

    /// <summary>
    /// Rehydrates a value already validated on the way in, such as from the database.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The SKU.</returns>
    public static Sku FromTrustedSource(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Z0-9._-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}
