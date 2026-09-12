using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Validates <see cref="CreatePurchaseOrderCommand"/>.</summary>
public sealed class CreatePurchaseOrderCommandValidator : AbstractValidator<CreatePurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreatePurchaseOrderCommandValidator()
    {
        RuleFor(c => c.SupplierId)
            .NotEqual(SupplierId.Empty)
            .WithErrorCode(PurchasingErrors.SupplierRequired.Code);

        RuleFor(c => c.DestinationLocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(PurchasingErrors.DestinationRequired.Code);

        RuleFor(c => c.Lines)
            .NotNull()
            .NotEmpty()
            .WithErrorCode(PurchasingErrors.EmptyOrder.Code);

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.OrderedQuantity)
                .GreaterThan(0m)
                .WithErrorCode(PurchasingErrors.InvalidQuantity(1).Code);

            line.RuleFor(l => l.UnitCost)
                .GreaterThanOrEqualTo(0m)
                .WithErrorCode(PurchasingErrors.InvalidUnitCost(1).Code);
        });

        RuleFor(c => c.CurrencyCode)
            .Matches("^[A-Z]{3}$")
            .WithErrorCode(PurchasingErrors.CurrencyInvalid.Code)
            .When(c => c.CurrencyCode is not null);
    }
}

/// <summary>Validates <see cref="SubmitPurchaseOrderCommand"/>.</summary>
public sealed class SubmitPurchaseOrderCommandValidator : AbstractValidator<SubmitPurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SubmitPurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="ApprovePurchaseOrderCommand"/>.</summary>
public sealed class ApprovePurchaseOrderCommandValidator : AbstractValidator<ApprovePurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ApprovePurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="RejectPurchaseOrderCommand"/>.</summary>
public sealed class RejectPurchaseOrderCommandValidator : AbstractValidator<RejectPurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RejectPurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="SendPurchaseOrderCommand"/>.</summary>
public sealed class SendPurchaseOrderCommandValidator : AbstractValidator<SendPurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SendPurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="CancelPurchaseOrderCommand"/>.</summary>
public sealed class CancelPurchaseOrderCommandValidator : AbstractValidator<CancelPurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CancelPurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="WithdrawPurchaseOrderCommand"/>.</summary>
public sealed class WithdrawPurchaseOrderCommandValidator : AbstractValidator<WithdrawPurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public WithdrawPurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}

/// <summary>Validates <see cref="ClosePurchaseOrderCommand"/>.</summary>
public sealed class ClosePurchaseOrderCommandValidator : AbstractValidator<ClosePurchaseOrderCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ClosePurchaseOrderCommandValidator()
        => RuleFor(c => c.OrderId)
            .NotEqual(PurchaseOrderId.Empty)
            .WithErrorCode(PurchasingErrors.OrderIdRequired.Code);
}