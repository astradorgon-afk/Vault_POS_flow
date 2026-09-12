using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A grouping of products. Categories form a self-referencing hierarchy; the
/// Main Warehouse curates them centrally.
/// </summary>
public sealed class ProductCategory : AggregateRoot<CategoryId>
{
    /// <summary>Maximum length of a category name.</summary>
    public const int NameMaxLength = 64;

    /// <summary>Maximum length of a category code.</summary>
    public const int CodeMaxLength = 32;

    private ProductCategory(
        CategoryId id,
        CategoryId? parentId,
        string code,
        string name,
        int sortOrder)
    {
        Id = id;
        ParentId = parentId;
        Code = code;
        Name = name;
        SortOrder = sortOrder;
        IsActive = true;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductCategory()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Gets the parent category, or null for a top-level category.</summary>
    public CategoryId? ParentId { get; private set; }

    /// <summary>Gets the short unique code.</summary>
    public string Code { get; private set; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets ordering within the same parent, for deterministic UI grouping.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Gets whether this category may still be assigned to products.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Creates a category.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="parentId">The parent category, or null for a top-level category.</param>
    /// <param name="sortOrder">Ordering within the parent.</param>
    /// <returns>The new category, or a validation failure.</returns>
    public static Result<ProductCategory> Create(string? code, string? name, CategoryId? parentId, int sortOrder = 0)
    {
        string normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalisedCode.Length == 0 || normalisedCode.Length > CodeMaxLength)
        {
            return Result<ProductCategory>.Failure(Error.Validation(
                "category.code_invalid",
                FormattableString.Invariant(
                    $"A category code is required and may be at most {CodeMaxLength} characters.")));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result<ProductCategory>.Failure(Error.Validation(
                "category.name_invalid",
                FormattableString.Invariant(
                    $"A category name is required and may be at most {NameMaxLength} characters.")));
        }

        if (parentId is { IsEmpty: true })
        {
            return Result<ProductCategory>.Failure(Error.Validation(
                "category.parent_same_as_self",
                "A category cannot be its own parent."));
        }

        return Result<ProductCategory>.Success(new ProductCategory(
            CategoryId.New(),
            parentId,
            normalisedCode,
            name.Trim(),
            Math.Max(0, sortOrder)));
    }
}