using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A product brand. Pure master data; brands are shared across the catalogue.
/// </summary>
public sealed class Brand : AggregateRoot<BrandId>
{
    /// <summary>Maximum length of a brand name.</summary>
    public const int NameMaxLength = 64;

    private Brand(BrandId id, string name)
    {
        Id = id;
        Name = name;
        IsActive = true;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Brand()
    {
        Name = string.Empty;
    }

    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets whether this brand may still be assigned to products.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Creates a brand.</summary>
    /// <param name="name">The display name.</param>
    /// <returns>The new brand, or a validation failure.</returns>
    public static Result<Brand> Create(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result<Brand>.Failure(Error.Validation(
                "brand.name_invalid",
                FormattableString.Invariant(
                    $"A brand name is required and may be at most {NameMaxLength} characters.")));
        }

        return Result<Brand>.Success(new Brand(BrandId.New(), name.Trim()));
    }
}