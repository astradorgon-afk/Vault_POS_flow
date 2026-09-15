using FluentValidation;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Routes inspected goods out of ReturnPending under location-scoped inventory authority.</summary>
/// <param name="EventId">The retry-safe event identifier.</param>
/// <param name="SalesReturnId">The return being inspected.</param>
/// <param name="LocationId">The return's location.</param>
/// <param name="LineNumber">The return line.</param>
/// <param name="Quantity">The quantity to route.</param>
/// <param name="Kind">The inspection decision.</param>
/// <param name="ReasonCode">The ledger reason.</param>
/// <param name="Note">The inspection explanation.</param>
public sealed record DisposeSalesReturnCommand(EventId EventId, SalesReturnId SalesReturnId,
    LocationId LocationId, int LineNumber, decimal Quantity, ReturnDispositionKind Kind,
    AdjustmentReasonCode ReasonCode, string Note)
    : ICommand<EventId>, IIdempotentCommand, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Adjust;
}

/// <summary>Validates the inspection request before executing the transaction.</summary>
public sealed class DisposeSalesReturnCommandValidator : AbstractValidator<DisposeSalesReturnCommand>
{
    /// <summary>Creates the inspection validation rules.</summary>
    public DisposeSalesReturnCommandValidator()
    {
        RuleFor(c => c.EventId).NotEqual(EventId.Empty);
        RuleFor(c => c.SalesReturnId).NotEqual(SalesReturnId.Empty);
        RuleFor(c => c.LocationId).NotEqual(LocationId.Empty);
        RuleFor(c => c.LineNumber).GreaterThan(0);
        RuleFor(c => c.Quantity).GreaterThan(0m).Must(q => decimal.Round(q, Quantity.Scale) == q);
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.ReasonCode).IsInEnum();
        RuleFor(c => c.Note).NotEmpty().MaximumLength(512);
    }
}

/// <summary>Persists inspection decisions and loads return lines with concurrency tracking.</summary>
public interface IReturnDispositionRepository
{
    /// <summary>Loads a tracked return and its lines.</summary>
    /// <param name="id">The return identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The return, or null.</returns>
    Task<SalesReturn?> GetReturnAsync(SalesReturnId id, CancellationToken cancellationToken);
    /// <summary>Reads the immutable outcome of an earlier event.</summary>
    /// <param name="id">The event identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision, or null.</returns>
    Task<SalesReturnDisposition?> GetEventAsync(EventId id, CancellationToken cancellationToken);
    /// <summary>Saves the decision and tracked line changes inside the command transaction.</summary>
    /// <param name="disposition">The decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when staged changes have been saved.</returns>
    Task AddAsync(SalesReturnDisposition disposition, CancellationToken cancellationToken);
}
