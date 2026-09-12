using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Creates a product category.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="ParentId">The parent category, or null for a top-level category.</param>
/// <param name="SortOrder">Ordering within the parent.</param>
public sealed record CreateCategoryCommand(
    string? Code,
    string? Name,
    CategoryId? ParentId,
    int SortOrder = 0)
    : ICommand<CategoryId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => "category.manage";
}

/// <summary>Handles <see cref="CreateCategoryCommand"/>.</summary>
public sealed class CreateCategoryCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CreateCategoryCommand, CategoryId>
{
    /// <inheritdoc />
    public async Task<Result<CategoryId>> HandleAsync(
        CreateCategoryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<ProductCategory> created = ProductCategory.Create(
            command.Code, command.Name, command.ParentId, command.SortOrder);

        return created.IsFailure
            ? Result<CategoryId>.Failure(created.Errors)
            : await masterData.CreateCategoryAsync(created.Value, cancellationToken).ConfigureAwait(false);
    }
}