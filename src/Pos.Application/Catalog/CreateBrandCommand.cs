using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Creates a product brand.</summary>
/// <param name="Name">The display name.</param>
public sealed record CreateBrandCommand(string? Name)
    : ICommand<BrandId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => "brand.manage";
}

/// <summary>Handles <see cref="CreateBrandCommand"/>.</summary>
public sealed class CreateBrandCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CreateBrandCommand, BrandId>
{
    /// <inheritdoc />
    public async Task<Result<BrandId>> HandleAsync(
        CreateBrandCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Brand> created = Brand.Create(command.Name);

        return created.IsFailure
            ? Result<BrandId>.Failure(created.Errors)
            : await masterData.CreateBrandAsync(created.Value, cancellationToken).ConfigureAwait(false);
    }
}