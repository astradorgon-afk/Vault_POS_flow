using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Creates a supplier.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="TaxId">Tax registration number, or null.</param>
/// <param name="PaymentTermsDays">Payment terms in days.</param>
/// <param name="LeadTimeDays">Typical lead time in days.</param>
public sealed record CreateSupplierCommand(
    string? Code,
    string? Name,
    string? TaxId,
    int PaymentTermsDays,
    int LeadTimeDays)
    : ICommand<SupplierId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => "supplier.manage";
}

/// <summary>Handles <see cref="CreateSupplierCommand"/>.</summary>
public sealed class CreateSupplierCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CreateSupplierCommand, SupplierId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierId>> HandleAsync(
        CreateSupplierCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Supplier> created = Supplier.Create(
            command.Code, command.Name, command.TaxId, command.PaymentTermsDays, command.LeadTimeDays);

        return created.IsFailure
            ? Result<SupplierId>.Failure(created.Errors)
            : await masterData.CreateSupplierAsync(created.Value, cancellationToken).ConfigureAwait(false);
    }
}