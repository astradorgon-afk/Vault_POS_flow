using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Handles <see cref="IssuePreApprovalTokenCommand"/>. Validates the route,
/// allocates the PAT number and stages the token.
/// </summary>
public sealed class IssuePreApprovalTokenCommandHandler(
    ITransferRepository transfers,
    IDocumentNumberGenerator numbers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<IssuePreApprovalTokenCommand, PreApprovalTokenId>
{
    /// <inheritdoc />
    public async Task<Result<PreApprovalTokenId>> HandleAsync(
        IssuePreApprovalTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        TransferLocationInfo? source = await transfers
            .GetLocationInfoAsync(command.SourceLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Result<PreApprovalTokenId>.Failure(
                PreApprovalTokenErrors.SourceLocationUnknown(command.SourceLocationId));
        }

        TransferLocationInfo? destination = await transfers
            .GetLocationInfoAsync(command.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (destination is null)
        {
            return Result<PreApprovalTokenId>.Failure(
                PreApprovalTokenErrors.DestinationLocationUnknown(command.DestinationLocationId));
        }

        if (source.Kind == LocationKind.External)
        {
            return Result<PreApprovalTokenId>.Failure(
                PreApprovalTokenErrors.SourceLocationExternal(command.SourceLocationId));
        }

        if (destination.Kind == LocationKind.External)
        {
            return Result<PreApprovalTokenId>.Failure(
                PreApprovalTokenErrors.DestinationLocationExternal(command.DestinationLocationId));
        }

        if (command.Products.Count > 0)
        {
            IReadOnlyList<Product> found = await transfers
                .GetProductsAsync(command.Products, cancellationToken)
                .ConfigureAwait(false);

            HashSet<ProductId> foundIds = found.Select(p => p.Id).ToHashSet();

            foreach (ProductId productId in command.Products)
            {
                if (!foundIds.Contains(productId))
                {
                    return Result<PreApprovalTokenId>.Failure(
                        PreApprovalTokenErrors.ProductUnknown(productId));
                }
            }
        }

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.PreApprovalToken, cancellationToken)
            .ConfigureAwait(false);

        Result<PreApprovalToken> created = PreApprovalToken.Create(
            number,
            command.SourceLocationId,
            command.DestinationLocationId,
            command.Products,
            command.MaxValue,
            command.ValidFromUtc,
            command.ValidUntilUtc,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<PreApprovalTokenId>.Failure(created.Errors);
        }

        return await transfers
            .AddPreApprovalTokenAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="RevokePreApprovalTokenCommand"/>.</summary>
public sealed class RevokePreApprovalTokenCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RevokePreApprovalTokenCommand, PreApprovalTokenId>
{
    /// <inheritdoc />
    public async Task<Result<PreApprovalTokenId>> HandleAsync(
        RevokePreApprovalTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PreApprovalToken? token = await transfers
            .GetPreApprovalTokenAsync(command.TokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return Result<PreApprovalTokenId>.Failure(PreApprovalTokenErrors.TokenUnknown(command.TokenId));
        }

        Result revoked = token.Revoke(currentUser.UserId ?? UserId.Empty, clock.UtcNow, command.Reason);

        return revoked.IsSuccess
            ? Result<PreApprovalTokenId>.Success(token.Id)
            : Result<PreApprovalTokenId>.Failure(revoked.Errors);
    }
}

/// <summary>
/// Handles <see cref="InitiateEmergencyTransferCommand"/>. Verifies both store
/// managers, gates the monthly budget, allocates the TRF number, creates the
/// pending-central-review transfer and posts the stock movement. The unit of
/// work's ambient transaction makes the transfer record and its ledger legs one
/// atomic save, so a failed post rolls the whole emergency back.
/// </summary>
public sealed class InitiateEmergencyTransferCommandHandler(
    ITransferRepository transfers,
    IDocumentNumberGenerator numbers,
    IInventoryLedger ledger,
    IAuthenticationService authentication,
    ICurrentUser currentUser,
    ISystemClock clock,
    EmergencyTransfersOptions options) : ICommandHandler<InitiateEmergencyTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        InitiateEmergencyTransferCommand command,
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

        if (source.Kind != LocationKind.Store || destination.Kind != LocationKind.Store)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.EmergencyInvalidRoute);
        }

        if (source.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceTimeZoneMissing(command.SourceLocationId));
        }

        UserId bearer = currentUser.UserId ?? UserId.Empty;

        if (options.MonthlyCapPerStore > 0)
        {
            int committed = await transfers
                .CountEmergencyCreatedInMonthAsync(
                    command.SourceLocationId,
                    MonthStartUtc(clock.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed >= options.MonthlyCapPerStore)
            {
                return Result<TransferOrderId>.Failure(TransferErrors.EmergencyMonthlyCapExceeded);
            }
        }

        Result<AuthenticationResult> coSigner = await authentication
            .SignInAsync(
                new PasswordSignInRequest(command.CoAuthorization.UserName, command.CoAuthorization.Password),
                cancellationToken)
            .ConfigureAwait(false);

        if (coSigner.IsFailure)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.EmergencyCoSignerUnauthorized);
        }

        AuthenticationResult coSignerResult = coSigner.Value;

        if (coSignerResult.UserId == bearer)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.EmergencyCoSignerSameAsBearer);
        }

        bool coSignerIsAuthorized =
            coSignerResult.Permissions.Contains(Permissions.Transfer.Emergency, StringComparer.Ordinal)
            && coSignerResult.Locations.Contains(command.DestinationLocationId);

        if (!coSignerIsAuthorized)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.EmergencyCoSignerUnauthorized);
        }

        ProductId[] productIds = [.. command.Lines.Select(l => l.ProductId).Distinct()];

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

        for (int i = 0; i < command.Lines.Count; i++)
        {
            if (productById[command.Lines[i].ProductId].TracksBatches)
            {
                return Result<TransferOrderId>.Failure(TransferErrors.EmergencyBatchNotSupported(i + 1));
            }
        }

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.TransferOrder, cancellationToken)
            .ConfigureAwait(false);

        Result<Transfer> created = Transfer.CreateEmergency(
            command.SourceLocationId,
            command.DestinationLocationId,
            command.Lines,
            number,
            bearer,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<TransferOrderId>.Failure(created.Errors);
        }

        Transfer transfer = created.Value;

        Result<TransferOrderId> saved = await transfers
            .AddAsync(transfer, cancellationToken)
            .ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return saved;
        }

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.TransferEmergency,
            ReferenceDocumentType: ReferenceDocumentType.TransferOrder,
            ReferenceDocumentId: transfer.Id.Value,
            ReferenceNumber: transfer.Number,
            Legs: BuildEmergencyLegs(transfer, source, destination, productById),
            Actor: new LedgerActor(
                bearer,
                coSignerResult.UserId,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(source.TimeZoneId),
            ReasonCode: AdjustmentReasonCode.EmergencyTransfer,
            Notes: command.Note);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<TransferOrderId>.Failure(posted.Errors);
        }

        Result linked = transfer.RecordEmergencyLedgerPost(posted.Value.MovementGroupId);

        if (linked.IsFailure)
        {
            return Result<TransferOrderId>.Failure(linked.Errors);
        }

        return Result<TransferOrderId>.Success(transfer.Id);
    }

    /// <summary>
    /// Builds the zero-sum legs: each unit leaves the source's available bucket
    /// and lands in the destination's available bucket at the product's master
    /// cost. Emergency lines are restricted to non-batch products, so the batch
    /// key is always empty.
    /// </summary>
    private static List<MovementLegSpec> BuildEmergencyLegs(
        Transfer transfer,
        TransferLocationInfo source,
        TransferLocationInfo destination,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (TransferLine line in transfer.Lines)
        {
            Product product = productById[line.ProductId];

            legs.Add(new MovementLegSpec(
                line.ProductId,
                BatchId: null,
                transfer.SourceLocationId,
                source.Kind,
                InventoryState.Available,
                -line.RequestedQuantity,
                product.DefaultPurchaseCost,
                product.TracksBatches));

            legs.Add(new MovementLegSpec(
                line.ProductId,
                BatchId: null,
                transfer.DestinationLocationId,
                destination.Kind,
                InventoryState.Available,
                +line.RequestedQuantity,
                product.DefaultPurchaseCost,
                product.TracksBatches));
        }

        return legs;
    }

    /// <summary>Gets the UTC instant the current calendar month began.</summary>
    private static DateTimeOffset MonthStartUtc(DateTimeOffset now)
        => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Handles <see cref="ReviewCentralTransferCommand"/>. Ratifying accepts the
/// posted emergency as received; rejecting cancels the transfer and posts the
/// reversal group, moving every unit back to the source store under the
/// reviewer's authority.
/// </summary>
public sealed class ReviewCentralTransferCommandHandler(
    ITransferRepository transfers,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ReviewCentralTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ReviewCentralTransferCommand command,
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

        if (transfer.Mode != TransferMode.EmergencyOffline)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.InvalidTransferState(
                TransferStatus.PendingCentralReview, transfer.Status));
        }

        UserId reviewer = currentUser.UserId ?? UserId.Empty;

        bool hasApproval = await permissions
            .HasPermissionAsync(
                reviewer,
                Permissions.Transfer.Approve,
                transfer.SourceLocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!hasApproval)
        {
            return Result<TransferOrderId>.Failure(Error.Forbidden(
                "auth.permission_denied",
                "You do not have permission to perform this action."));
        }

        if (command.Approve)
        {
            Result ratified = transfer.Ratify(reviewer, clock.UtcNow, command.Note);

            return ratified.IsSuccess
                ? Result<TransferOrderId>.Success(transfer.Id)
                : Result<TransferOrderId>.Failure(ratified.Errors);
        }

        TransferLocationInfo? source = await transfers
            .GetLocationInfoAsync(transfer.SourceLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceLocationUnknown(transfer.SourceLocationId));
        }

        if (source.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceTimeZoneMissing(transfer.SourceLocationId));
        }

        TransferLocationInfo? destination = await transfers
            .GetLocationInfoAsync(transfer.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (destination is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationLocationUnknown(transfer.DestinationLocationId));
        }

        Result rejected = transfer.RejectReview(reviewer, clock.UtcNow, command.Note);

        if (rejected.IsFailure)
        {
            return Result<TransferOrderId>.Failure(rejected.Errors);
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        MovementGroupSpec reversal = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.TransferEmergencyReversal,
            ReferenceDocumentType: ReferenceDocumentType.TransferOrder,
            ReferenceDocumentId: transfer.Id.Value,
            ReferenceNumber: transfer.Number,
            Legs: BuildReversalLegs(transfer, source, destination, productById),
            Actor: new LedgerActor(
                reviewer,
                reviewer,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(source.TimeZoneId),
            ReasonCode: AdjustmentReasonCode.EmergencyTransferReversed,
            Notes: command.Note,
            ReversesMovementGroupId: transfer.EmergencyLedgerGroupId);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(reversal, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(posted.Errors);
    }

    /// <summary>
    /// Builds the reversal legs: each unit leaves the destination's available
    /// bucket and returns to the source's available bucket, mirroring the
    /// original emergency group exactly.
    /// </summary>
    private static List<MovementLegSpec> BuildReversalLegs(
        Transfer transfer,
        TransferLocationInfo source,
        TransferLocationInfo destination,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (TransferLine line in transfer.Lines)
        {
            Product product = productById[line.ProductId];

            legs.Add(new MovementLegSpec(
                line.ProductId,
                BatchId: null,
                transfer.DestinationLocationId,
                destination.Kind,
                InventoryState.Available,
                -line.RequestedQuantity,
                product.DefaultPurchaseCost,
                product.TracksBatches));

            legs.Add(new MovementLegSpec(
                line.ProductId,
                BatchId: null,
                transfer.SourceLocationId,
                source.Kind,
                InventoryState.Available,
                +line.RequestedQuantity,
                product.DefaultPurchaseCost,
                product.TracksBatches));
        }

        return legs;
    }
}