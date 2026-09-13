using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Receipts;

namespace Pos.Application.Receipts;

/// <summary>Validates <see cref="IssueReceiptCommand"/>.</summary>
public sealed class IssueReceiptCommandValidator : AbstractValidator<IssueReceiptCommand>
{
    /// <summary>Creates the validator.</summary>
    public IssueReceiptCommandValidator()
    {
        RuleFor(c => c.Kind)
            .IsInEnum().WithErrorCode("receipt.kind_unknown");

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty).WithErrorCode("receipt.location_required");

        RuleFor(c => c.Amount)
            .GreaterThan(0m).WithErrorCode("receipt.amount_invalid");

        RuleFor(c => c.Counterparty)
            .MaximumLength(Receipt.CounterpartyMaxLength).WithErrorCode("receipt.counterparty_too_long");

        RuleFor(c => c.Note)
            .MaximumLength(Receipt.NoteMaxLength).WithErrorCode("receipt.note_too_long");

        // A reference names another business document, so it must parse as one.
        // The text form is accepted here so the caller's exact flavour of spaces
        // does not matter; the aggregate normalises the stored value.
        RuleFor(c => c.ReferenceNumber)
            .MaximumLength(DocumentNumber.MaxLength).WithErrorCode("receipt.reference_number_invalid")
            .Must(reference => string.IsNullOrWhiteSpace(reference) || DocumentNumber.Parse(reference).IsSuccess)
                .WithErrorCode("receipt.reference_number_invalid");
    }
}