using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Application.Organizations;

/// <summary>Validates <see cref="UpdateLocationSettingsCommand"/>.</summary>
public sealed class UpdateLocationSettingsCommandValidator : AbstractValidator<UpdateLocationSettingsCommand>
{
    /// <summary>Initializes the validator.</summary>
    public UpdateLocationSettingsCommandValidator()
    {
        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty).WithErrorCode("location.id_required");

        RuleFor(c => c.Settings)
            .Must(s => s is null || s.ReceiptHeader.Length <= LocationSettings.ReceiptTextMaxLength)
            .WithErrorCode("location.receipt_header_too_long")
            .Must(s => s is null || s.ReceiptFooter.Length <= LocationSettings.ReceiptTextMaxLength)
            .WithErrorCode("location.receipt_footer_too_long");
    }
}