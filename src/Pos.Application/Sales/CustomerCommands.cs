using FluentValidation;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Creates a new customer record.</summary>
/// <param name="DisplayName">The customer's display name.</param>
/// <param name="Phone">The phone number (optional).</param>
/// <param name="Email">The email address (optional).</param>
/// <param name="Tin">The tax identification number (optional).</param>
/// <param name="Note">A free-text note (optional).</param>
public sealed record CreateCustomerCommand(
    string DisplayName,
    string? Phone,
    string? Email,
    string? Tin,
    string? Note)
    : ICommand<CustomerId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.ManageCustomers;
}

/// <summary>Validates the create-customer request.</summary>
public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    /// <summary>Creates the validation rules.</summary>
    public CreateCustomerCommandValidator()
    {
        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(Customer.DisplayNameMaxLength);

        RuleFor(c => c.Phone)
            .MaximumLength(Customer.PhoneMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Phone));

        RuleFor(c => c.Email)
            .MaximumLength(Customer.EmailMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Email))
            .EmailAddress()
            .When(c => !string.IsNullOrWhiteSpace(c.Email))
            .WithErrorCode(CustomerErrors.EmailInvalid.Code);

        RuleFor(c => c.Tin)
            .MaximumLength(Customer.TinMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Tin));

        RuleFor(c => c.Note)
            .MaximumLength(Customer.NoteMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Note));
    }
}

/// <summary>Updates an existing customer's details.</summary>
/// <param name="CustomerId">The customer to update.</param>
/// <param name="DisplayName">The customer's display name.</param>
/// <param name="Phone">The phone number (optional).</param>
/// <param name="Email">The email address (optional).</param>
/// <param name="Tin">The tax identification number (optional).</param>
/// <param name="Note">A free-text note (optional).</param>
public sealed record UpdateCustomerCommand(
    CustomerId CustomerId,
    string DisplayName,
    string? Phone,
    string? Email,
    string? Tin,
    string? Note)
    : ICommand<CustomerId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.ManageCustomers;
}

/// <summary>Validates the update-customer request.</summary>
public sealed class UpdateCustomerCommandValidator : AbstractValidator<UpdateCustomerCommand>
{
    /// <summary>Creates the validation rules.</summary>
    public UpdateCustomerCommandValidator()
    {
        RuleFor(c => c.CustomerId).NotEqual(CustomerId.Empty);

        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .MaximumLength(Customer.DisplayNameMaxLength);

        RuleFor(c => c.Phone)
            .MaximumLength(Customer.PhoneMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Phone));

        RuleFor(c => c.Email)
            .MaximumLength(Customer.EmailMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Email))
            .EmailAddress()
            .When(c => !string.IsNullOrWhiteSpace(c.Email))
            .WithErrorCode(CustomerErrors.EmailInvalid.Code);

        RuleFor(c => c.Tin)
            .MaximumLength(Customer.TinMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Tin));

        RuleFor(c => c.Note)
            .MaximumLength(Customer.NoteMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.Note));
    }
}

/// <summary>Deactivates a customer.</summary>
/// <param name="CustomerId">The customer to deactivate.</param>
/// <param name="Reason">The deactivation reason.</param>
public sealed record DeactivateCustomerCommand(
    CustomerId CustomerId,
    string Reason)
    : ICommand<CustomerId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.ManageCustomers;
}

/// <summary>Validates the deactivate-customer request.</summary>
public sealed class DeactivateCustomerCommandValidator : AbstractValidator<DeactivateCustomerCommand>
{
    /// <summary>Creates the validation rules.</summary>
    public DeactivateCustomerCommandValidator()
    {
        RuleFor(c => c.CustomerId).NotEqual(CustomerId.Empty);
        RuleFor(c => c.Reason)
            .NotEmpty()
            .MaximumLength(Customer.DeactivationReasonMaxLength);
    }
}

/// <summary>Reactivates a deactivated customer.</summary>
/// <param name="CustomerId">The customer to reactivate.</param>
public sealed record ReactivateCustomerCommand(
    CustomerId CustomerId)
    : ICommand<CustomerId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.ManageCustomers;
}

/// <summary>Validates the reactivate-customer request.</summary>
public sealed class ReactivateCustomerCommandValidator : AbstractValidator<ReactivateCustomerCommand>
{
    /// <summary>Creates the validation rules.</summary>
    public ReactivateCustomerCommandValidator()
    {
        RuleFor(c => c.CustomerId).NotEqual(CustomerId.Empty);
    }
}