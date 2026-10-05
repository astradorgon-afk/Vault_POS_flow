using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Handles <see cref="CreateTransferCommand"/>. Validates the locations, derives
/// the transfer kind from the route, and when a pre-approval token is named
/// validates its lifecycle, scope and value ceiling before staging the transfer
/// as pre-approved.
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

        TransferKind kind = KindFor(source.Kind, destination.Kind);
        TransferMode mode = TransferMode.Normal;
        PreApprovalToken? token = null;

        if (command.Mode == TransferMode.StoreRestock)
        {
            if (kind != TransferKind.WarehouseToStore || command.PreApprovalTokenId is not null ||
                !currentUser.AssignedLocations.Contains(command.DestinationLocationId))
            {
                return Result<TransferOrderId>.Failure(Error.Forbidden(
                    "restock.invalid_route",
                    "Restock requests must go from the main warehouse to one of your assigned stores."));
            }

            mode = TransferMode.StoreRestock;
        }
        else if (command.Mode != TransferMode.Normal)
        {
            return Result<TransferOrderId>.Failure(Error.Validation(
                "transfer.invalid_mode", "This transfer mode cannot be requested here."));
        }

        if (command.PreApprovalTokenId is { } tokenId)
        {
            token = await transfers
                .GetPreApprovalTokenAsync(tokenId, cancellationToken)
                .ConfigureAwait(false);

            if (token is null)
            {
                return Result<TransferOrderId>.Failure(PreApprovalTokenErrors.TokenUnknown(tokenId));
            }

            Result availability = ValidateTokenAvailability(token, clock.UtcNow);

            if (availability.IsFailure)
            {
                return Result<TransferOrderId>.Failure(availability.Errors);
            }

            if (token.SourceLocationId != command.SourceLocationId
                || token.DestinationLocationId != command.DestinationLocationId)
            {
                return Result<TransferOrderId>.Failure(PreApprovalTokenErrors.ScopeMismatch);
            }

            ProductId[] productIds = [.. command.Lines.Select(l => l.ProductId).Distinct()];

            if (token.Products.Count > 0 && productIds.Any(p => !token.Products.Contains(p)))
            {
                return Result<TransferOrderId>.Failure(PreApprovalTokenErrors.ScopeMismatch);
            }

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

            decimal estimatedValue = command.Lines
                .Sum(l => productById[l.ProductId].DefaultPurchaseCost * l.RequestedQuantity);

            if (token.ValueExceeds(estimatedValue))
            {
                return Result<TransferOrderId>.Failure(PreApprovalTokenErrors.ValueExceeded);
            }

            mode = TransferMode.PreApproved;
        }

        Result<Transfer> created = Transfer.Create(
            command.SourceLocationId,
            command.DestinationLocationId,
            command.Lines,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow,
            kind,
            mode,
            token?.Id);

        if (created.IsFailure)
        {
            return Result<TransferOrderId>.Failure(created.Errors);
        }

        Transfer transfer = created.Value;

        return await transfers.AddAsync(transfer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the token's lifecycle at the current instant, distinguishing
    /// consumed, revoked, and not-yet-valid tokens from plain expiry.
    /// </summary>
    private static Result ValidateTokenAvailability(PreApprovalToken token, DateTimeOffset now)
        => token.Status == PreApprovalTokenStatus.Consumed
            ? Result.Failure(PreApprovalTokenErrors.AlreadyConsumed(token.Id))
            : token.Status == PreApprovalTokenStatus.Revoked
                ? Result.Failure(PreApprovalTokenErrors.Revoked(token.Id))
                : now < token.ValidFromUtc
                    ? Result.Failure(PreApprovalTokenErrors.NotYetValid)
                    : now > token.ValidUntilUtc
                        ? Result.Failure(PreApprovalTokenErrors.Expired)
                        : Result.Success();

    /// <summary>Derives the transfer kind from the route's location kinds.</summary>
    private static TransferKind KindFor(LocationKind source, LocationKind destination)
        => source == LocationKind.MainWarehouse && destination == LocationKind.Store
            ? TransferKind.WarehouseToStore
            : source == LocationKind.Store && destination == LocationKind.MainWarehouse
                ? TransferKind.StoreToWarehouse
                : TransferKind.StoreToStore;
}

/// <summary>
/// Handles <see cref="SubmitTransferCommand"/>. Pre-approved transfers are
/// submitted under the token issuer's authority: the token is consumed
/// atomically in the same save as the state change, which is the single-use gate.
/// </summary>
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

        if (transfer.Mode == TransferMode.StoreRestock &&
            transfer.CreatedByUserId != (currentUser.UserId ?? UserId.Empty))
        {
            return Result<TransferOrderId>.Failure(RestockOwnerGuard.CreatorRequired);
        }

        if (transfer.Mode == TransferMode.PreApproved)
        {
            return await SubmitPreApprovedAsync(transfer, cancellationToken).ConfigureAwait(false);
        }

        Result submitted = transfer.Submit(currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        return submitted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(submitted.Errors);
    }

    private async Task<Result<TransferOrderId>> SubmitPreApprovedAsync(
        Transfer transfer,
        CancellationToken cancellationToken)
    {
        if (transfer.PreApprovalTokenId is not { } tokenId)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SubmitPreApprovedTokenMissing);
        }

        PreApprovalToken? token = await transfers
            .GetPreApprovalTokenAsync(tokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return Result<TransferOrderId>.Failure(PreApprovalTokenErrors.TokenUnknown(tokenId));
        }

        Result preApproved = transfer.SubmitPreApproved(token.CreatedByUserId, clock.UtcNow);

        if (preApproved.IsFailure)
        {
            return Result<TransferOrderId>.Failure(preApproved.Errors);
        }

        Result consumed = token.Consume(transfer.Id, clock.UtcNow);

        if (consumed.IsFailure)
        {
            return Result<TransferOrderId>.Failure(consumed.Errors);
        }

        return Result<TransferOrderId>.Success(transfer.Id);
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

        if (transfer.Mode == TransferMode.StoreRestock && !RestockOwnerGuard.IsOwner(currentUser))
        {
            return Result<TransferOrderId>.Failure(RestockOwnerGuard.OwnerRequired);
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

        if (transfer.Mode == TransferMode.StoreRestock && !RestockOwnerGuard.IsOwner(currentUser))
        {
            return Result<TransferOrderId>.Failure(RestockOwnerGuard.OwnerRequired);
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

        if (transfer.Mode == TransferMode.StoreRestock && !RestockOwnerGuard.IsOwner(currentUser))
        {
            return Result<TransferOrderId>.Failure(RestockOwnerGuard.OwnerRequired);
        }

        Result rejected = transfer.Reject(currentUser.UserId ?? UserId.Empty, clock.UtcNow, command.Note);

        return rejected.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(rejected.Errors);
    }
}

internal static class RestockOwnerGuard
{
    public static Error OwnerRequired { get; } = Error.Forbidden(
        "restock.owner_required", "Only the owner can review or decide a store restock request.");

    public static Error CreatorRequired { get; } = Error.Forbidden(
        "restock.creator_required", "Only the store user who created this request can submit it.");

    public static bool IsOwner(ICurrentUser user) => user.RoleSnapshot?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Contains(Roles.Owner, StringComparer.Ordinal) == true;
}
