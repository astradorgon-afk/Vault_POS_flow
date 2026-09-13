using FluentValidation;
using Pos.Domain.Quarantine;

namespace Pos.Application.Quarantine;

/// <summary>
/// Validates <see cref="CreateQuarantineIncidentCommand"/> before the handler
/// runs. The handler resolves barcodes against the catalogue first, so a line
/// without a barcode must be refused here rather than reach that lookup.
/// </summary>
public sealed class CreateQuarantineIncidentCommandValidator : AbstractValidator<CreateQuarantineIncidentCommand>
{
    /// <summary>Creates the validator.</summary>
    public CreateQuarantineIncidentCommandValidator()
    {
        RuleFor(c => c.Lines)
            .NotEmpty().WithErrorCode(QuarantineErrors.EmptyIncident.Code);

        RuleForEach(c => c.Lines)
            .NotNull().WithErrorCode(QuarantineErrors.BarcodeRequired(1).Code)
            .ChildRules(line =>
            {
                line.RuleFor(l => l.Barcode)
                    .NotEmpty().WithErrorCode(QuarantineErrors.BarcodeRequired(1).Code);

                line.RuleFor(l => l.Quantity)
                    .GreaterThan(0m).WithErrorCode(QuarantineErrors.LineQuantityInvalid(1).Code);

                line.RuleFor(l => l.UnitCost)
                    .GreaterThanOrEqualTo(0m).When(l => l.UnitCost is not null)
                    .WithErrorCode(QuarantineErrors.LineCostInvalid(1).Code);
            });
    }
}
