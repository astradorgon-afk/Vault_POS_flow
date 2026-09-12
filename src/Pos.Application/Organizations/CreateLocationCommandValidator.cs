using FluentValidation;
using Pos.Domain.Organizations;

namespace Pos.Application.Organizations;

/// <summary>Validates <see cref="CreateLocationCommand"/>.</summary>
public sealed class CreateLocationCommandValidator : AbstractValidator<CreateLocationCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateLocationCommandValidator()
    {
        RuleFor(c => c.Code)
            .NotEmpty().WithErrorCode("location.code_required")
            .MaximumLength(Location.CodeMaxLength).WithErrorCode("location.code_invalid");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("location.name_required")
            .MaximumLength(Location.NameMaxLength).WithErrorCode("location.name_invalid");

        RuleFor(c => c.TimeZoneId)
            .NotEmpty().WithErrorCode("location.timezone_required");

        RuleFor(c => c.Kind)
            .NotEqual(Pos.Domain.Locations.LocationKind.External)
            .WithErrorCode("location.external_not_user_creatable");
    }
}