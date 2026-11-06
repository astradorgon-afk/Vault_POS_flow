using FluentValidation;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Limits shared by the supply request validators.</summary>
internal static class SupplyRequestRules
{
    /// <summary>The most lines one request or order accepts.</summary>
    internal const int MaxLines = 200;
}

/// <summary>Validates <see cref="CreateSupplyRequestCommand"/>.</summary>
public sealed class CreateSupplyRequestCommandValidator : AbstractValidator<CreateSupplyRequestCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateSupplyRequestCommandValidator()
    {
        RuleFor(c => c.DestinationLocationId)
            .Must(id => !id.IsEmpty)
            .WithErrorCode("purchasing.supply_request_destination_invalid")
            .WithMessage("Choose the warehouse the goods are wanted at.");

        RuleFor(c => c.Note)
            .MaximumLength(SupplyRequest.TextMaxLength)
            .WithErrorCode("purchasing.supply_request_note_too_long");

        RuleFor(c => c.Lines)
            .NotEmpty()
            .WithErrorCode("purchasing.supply_request_empty")
            .WithMessage("Add at least one product to the request.")
            .Must(lines => lines is null || lines.Count <= SupplyRequestRules.MaxLines)
            .WithErrorCode("purchasing.supply_request_too_many_lines")
            .WithMessage($"A request carries at most {SupplyRequestRules.MaxLines} products. Split it into separate requests.");

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId)
                .Must(id => !id.IsEmpty)
                .WithErrorCode("purchasing.supply_request_product_invalid");
            line.RuleFor(l => l.Quantity)
                .GreaterThan(0m)
                .WithErrorCode("purchasing.supply_request_quantity_invalid");
            line.RuleFor(l => l.Note)
                .MaximumLength(SupplyRequest.TextMaxLength)
                .WithErrorCode("purchasing.supply_request_note_too_long");
        });
    }
}

/// <summary>Validates <see cref="OrderSupplyRequestCommand"/>.</summary>
public sealed class OrderSupplyRequestCommandValidator : AbstractValidator<OrderSupplyRequestCommand>
{
    /// <summary>Initializes the validator.</summary>
    public OrderSupplyRequestCommandValidator()
    {
        RuleFor(c => c.RequestId)
            .Must(id => !id.IsEmpty)
            .WithErrorCode("purchasing.supply_request_unknown");

        RuleFor(c => c.Lines)
            .NotEmpty()
            .WithErrorCode("purchasing.supply_request_empty")
            .WithMessage("Choose at least one line to put on the order.")
            .Must(lines => lines is null || lines.Count <= SupplyRequestRules.MaxLines)
            .WithErrorCode("purchasing.supply_request_too_many_lines")
            .WithMessage($"An order carries at most {SupplyRequestRules.MaxLines} lines. Raise a second order for the rest.");

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineNo)
                .GreaterThan(0)
                .WithErrorCode("purchasing.supply_request_line_unknown");
            line.RuleFor(l => l.Quantity)
                .GreaterThan(0m)
                .WithErrorCode("purchasing.supply_request_quantity_invalid");
            line.RuleFor(l => l.UnitCost)
                .GreaterThanOrEqualTo(0m)
                .WithErrorCode("purchasing.line_unit_cost_invalid")
                .WithMessage("A unit cost cannot be negative.");
        });
    }
}

/// <summary>Validates <see cref="RejectSupplyRequestCommand"/>.</summary>
public sealed class RejectSupplyRequestCommandValidator : AbstractValidator<RejectSupplyRequestCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RejectSupplyRequestCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty()
            .MinimumLength(5)
            .MaximumLength(SupplyRequest.TextMaxLength)
            .WithErrorCode("purchasing.supply_request_reason_required")
            .WithMessage("Give a reason of at least five characters, so the warehouse knows where it stands.");
}

/// <summary>Validates <see cref="CancelSupplyRequestCommand"/>.</summary>
public sealed class CancelSupplyRequestCommandValidator : AbstractValidator<CancelSupplyRequestCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CancelSupplyRequestCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty()
            .MinimumLength(5)
            .MaximumLength(SupplyRequest.TextMaxLength)
            .WithErrorCode("purchasing.supply_request_reason_required")
            .WithMessage("Give a reason of at least five characters, so the warehouse knows where it stands.");
}
