using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Handles <see cref="CreateTransferCommand"/>. Validates the locations and
/// stages the draft transfer.
/// </summary>
public sealed class CreateTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        CreateTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<Transfer> created = Transfer.Create(
            command.SourceLocationId,
            command.DestinationLocationId,
            command.Lines,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<TransferOrderId>.Failure(created.Errors);
        }

        Transfer transfer = created.Value;

        TransferLocationInfo? source = await transfers
            .GetLocationInfoAsync(command.SourceLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceLocationUnknown(command.SourceLocationId));
        }

        TransferLocationInfo? destination = await transfers
            .GetLocationInfoAsync(command.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (destination is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationLocationUnknown(command.DestinationLocationId));
        }

        if (source.Kind == LocationKind.External)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceLocationExternal(command.SourceLocationId));
        }

        if (destination.Kind == LocationKind.External)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationLocationExternal(command.DestinationLocationId));
        }

        return await transfers.AddAsync(transfer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="SubmitTransferCommand"/>.</summary>
public sealed class SubmitTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<SubmitTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        SubmitTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        Result submitted = transfer.Submit(currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        return submitted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(submitted.Errors);
    }
}

/// <summary>Handles <see cref="ReviewTransferCommand"/>.</summary>
public sealed class ReviewTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ReviewTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ReviewTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        Result reviewed = transfer.Review(currentUser.UserId ?? UserId.Empty, clock.UtcNow, command.Note);

        return reviewed.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(reviewed.Errors);
    }
}

/// <summary>
/// Handles <see cref="ApproveTransferCommand"/>. The approval gate evaluates the
/// estimated value from product master costs because picking costs are not known
/// until the stock is chosen.
/// </summary>
public sealed class ApproveTransferCommandHandler(
    ITransferRepository transfers,
    IApprovalGate gate,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApproveTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ApproveTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<TransferOrderId>.Failure(TransferErrors.ProductUnknown(productId));
            }
        }

        IReadOnlyDictionary<ProductId, decimal> unitCosts = productById.ToDictionary(
            p => p.Key,
            p => p.Value.DefaultPurchaseCost);

        UserId approver = currentUser.UserId ?? UserId.Empty;

        Result gateResult = await gate
            .RequireAsync(
                Permissions.Transfer.Approve,
                transfer.EstimatedTotalValue(unitCosts),
                approver,
                transfer.CreatedByUserId,
                transfer.SourceLocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (gateResult.IsFailure)
        {
            return Result<TransferOrderId>.Failure(gateResult.Errors);
        }

        Result approved = transfer.Approve(approver, clock.UtcNow, command.Amendments, command.Note);

        return approved.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(approved.Errors);
    }
}

/// <summary>Handles <see cref="RejectTransferCommand"/>.</summary>
public sealed class RejectTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        RejectTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        Result rejected = transfer.Reject(currentUser.UserId ?? UserId.Empty, clock.UtcNow, command.Note);

        return rejected.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(rejected.Errors);
    }
}