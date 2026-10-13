using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Application.Organizations;

/// <summary>Validates <see cref="UpdateLocationCommand"/>.</summary>
public sealed class UpdateLocationCommandValidator : AbstractValidator<UpdateLocationCommand>
{
    /// <summary>Initializes the validator.</summary>
    public UpdateLocationCommandValidator()
    {
        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty).WithErrorCode("location.id_required");

        RuleFor(c => c.Code)
            .NotEmpty().WithErrorCode("location.code_required")
            .MaximumLength(Location.CodeMaxLength).WithErrorCode("location.code_invalid");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("location.name_required")
            .MaximumLength(Location.NameMaxLength).WithErrorCode("location.name_invalid");

        RuleFor(c => c.TimeZoneId)
            .NotEmpty().WithErrorCode("location.timezone_required");

        RuleFor(c => c.Kind)
            .NotEqual(LocationKind.External)
            .WithErrorCode("location.external_not_user_creatable");
    }
}
