using FluentValidation;
using Pos.Domain.Common;

namespace Pos.Application.Organizations;

/// <summary>Validates <see cref="CloseLocationCommand"/>.</summary>
public sealed class CloseLocationCommandValidator : AbstractValidator<CloseLocationCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CloseLocationCommandValidator()
    {
        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty).WithErrorCode("location.id_required");

        RuleFor(c => c.ClosingDate)
            .NotEqual(default(DateOnly)).WithErrorCode("location.closing_date_required");
    }
}
