using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Creates a unit of measure.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="Kind">The measurement kind.</param>
/// <param name="DecimalPlaces">How many decimal places a quantity in this unit may carry.</param>
public sealed record CreateUnitOfMeasureCommand(
    string? Code,
    string? Name,
    UnitKind Kind,
    int DecimalPlaces = 0)
    : ICommand<UnitOfMeasureId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => "uom.manage";
}

/// <summary>Handles <see cref="CreateUnitOfMeasureCommand"/>.</summary>
public sealed class CreateUnitOfMeasureCommandHandler(IMasterDataRepository masterData)
    : ICommandHandler<CreateUnitOfMeasureCommand, UnitOfMeasureId>
{
    /// <inheritdoc />
    public async Task<Result<UnitOfMeasureId>> HandleAsync(
        CreateUnitOfMeasureCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<UnitOfMeasure> created = UnitOfMeasure.Create(
            command.Code, command.Name, command.Kind, command.DecimalPlaces);

        return created.IsFailure
            ? Result<UnitOfMeasureId>.Failure(created.Errors)
            : await masterData.CreateUnitOfMeasureAsync(created.Value, cancellationToken).ConfigureAwait(false);
    }
}