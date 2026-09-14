using FluentValidation;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>Limits shared by the inventory control validators.</summary>
internal static class InventoryControlRules
{
    /// <summary>The most lines one document accepts per request.</summary>
    internal const int MaxLines = 500;

    /// <summary>The longest reason or note accepted.</summary>
    internal const int TextMaxLength = 512;
}

/// <summary>Validates <see cref="CreateStockAdjustmentCommand"/>.</summary>
public sealed class CreateStockAdjustmentCommandValidator : AbstractValidator<CreateStockAdjustmentCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateStockAdjustmentCommandValidator()
    {
        RuleFor(c => c.LocationId).Must(id => !id.IsEmpty).WithErrorCode("inventory_control.location_invalid");
        RuleFor(c => c.Reason).IsInEnum().WithErrorCode("adjustment.reason_not_allowed");
        RuleFor(c => c.Notes).MaximumLength(StockAdjustment.NotesMaxLength).WithErrorCode("adjustment.notes_too_long");

        RuleFor(c => c.Lines)
            .NotEmpty().WithErrorCode("adjustment.empty")
            .Must(lines => lines is null || lines.Count <= InventoryControlRules.MaxLines).WithErrorCode("inventory_control.too_many_lines");

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).Must(id => !id.IsEmpty).WithErrorCode("inventory_control.product_unknown");
            line.RuleFor(l => l.State).IsInEnum().WithErrorCode("adjustment.state_not_allowed");
            line.RuleFor(l => l.QuantityDelta).NotEqual(0m).WithErrorCode("adjustment.quantity_zero");
        });
    }
}

/// <summary>Validates <see cref="RejectStockAdjustmentCommand"/>.</summary>
public sealed class RejectStockAdjustmentCommandValidator : AbstractValidator<RejectStockAdjustmentCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RejectStockAdjustmentCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("inventory_control.reason_required")
            .MinimumLength(5).WithErrorCode("inventory_control.reason_required")
            .MaximumLength(InventoryControlRules.TextMaxLength).WithErrorCode("inventory_control.reason_too_long");
}

/// <summary>Validates <see cref="ReverseStockAdjustmentCommand"/>.</summary>
public sealed class ReverseStockAdjustmentCommandValidator : AbstractValidator<ReverseStockAdjustmentCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ReverseStockAdjustmentCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("inventory_control.reason_required")
            .MinimumLength(5).WithErrorCode("inventory_control.reason_required")
            .MaximumLength(InventoryControlRules.TextMaxLength).WithErrorCode("inventory_control.reason_too_long");
}

/// <summary>Validates <see cref="OpenInventoryCountCommand"/>.</summary>
public sealed class OpenInventoryCountCommandValidator : AbstractValidator<OpenInventoryCountCommand>
{
    /// <summary>Initializes the validator.</summary>
    public OpenInventoryCountCommandValidator()
    {
        RuleFor(c => c.LocationId).Must(id => !id.IsEmpty).WithErrorCode("inventory_control.location_invalid");
        RuleFor(c => c.Kind).IsInEnum().WithErrorCode("count.scope_invalid");
        RuleFor(c => c.Note).MaximumLength(InventoryControlRules.TextMaxLength).WithErrorCode("count.note_too_long");
        RuleFor(c => c.CategoryIds).NotNull().WithErrorCode("count.scope_invalid");
        RuleFor(c => c.ProductIds).NotNull().WithErrorCode("count.scope_invalid");
    }
}

/// <summary>Validates <see cref="RecordCountLinesCommand"/>.</summary>
public sealed class RecordCountLinesCommandValidator : AbstractValidator<RecordCountLinesCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RecordCountLinesCommandValidator()
    {
        RuleFor(c => c.Lines)
            .NotEmpty().WithErrorCode("count.lines_required")
            .Must(lines => lines is null || lines.Count <= InventoryControlRules.MaxLines).WithErrorCode("inventory_control.too_many_lines");

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).Must(id => !id.IsEmpty).WithErrorCode("inventory_control.product_unknown");
            line.RuleFor(l => l.PhysicalQuantity).GreaterThanOrEqualTo(0m).WithErrorCode("count.quantity_negative");
        });
    }
}

/// <summary>Validates <see cref="RejectInventoryCountCommand"/>.</summary>
public sealed class RejectInventoryCountCommandValidator : AbstractValidator<RejectInventoryCountCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RejectInventoryCountCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("inventory_control.reason_required")
            .MinimumLength(5).WithErrorCode("inventory_control.reason_required")
            .MaximumLength(InventoryControlRules.TextMaxLength).WithErrorCode("inventory_control.reason_too_long");
}

/// <summary>Validates <see cref="CancelInventoryCountCommand"/>.</summary>
public sealed class CancelInventoryCountCommandValidator : AbstractValidator<CancelInventoryCountCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CancelInventoryCountCommandValidator()
        => RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("inventory_control.reason_required")
            .MinimumLength(5).WithErrorCode("inventory_control.reason_required")
            .MaximumLength(InventoryControlRules.TextMaxLength).WithErrorCode("inventory_control.reason_too_long");
}
