using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Handles <see cref="CreateDirectDeliveryAuthorizationCommand"/>.</summary>
public sealed class CreateDirectDeliveryAuthorizationCommandHandler(
    IPurchaseOrderRepository orders,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateDirectDeliveryAuthorizationCommand, DirectDeliveryAuthorizationId>
{
    /// <inheritdoc />
    public async Task<Result<DirectDeliveryAuthorizationId>> HandleAsync(
        CreateDirectDeliveryAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await orders.IsActiveSupplierAsync(command.SupplierId, cancellationToken).ConfigureAwait(false))
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(PurchasingErrors.SupplierUnknown(command.SupplierId));
        }

        if (!await orders.IsActiveStoreLocationAsync(command.StoreLocationId, cancellationToken).ConfigureAwait(false))
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(
                PurchasingErrors.AuthorizationStoreLocationRequired(command.StoreLocationId));
        }

        if (command.ProductId is { } productId
            && await orders.GetActiveProductBaseUnitAsync(productId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(PurchasingErrors.ProductUnknown(productId));
        }

        Result<DirectDeliveryAuthorization> created = DirectDeliveryAuthorization.Create(
            command.SupplierId,
            command.StoreLocationId,
            command.ValidFrom,
            command.ValidUntil,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow,
            command.ProductId,
            command.ValueCap);

        if (created.IsFailure)
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(created.Errors);
        }

        return await orders
            .AddDirectDeliveryAuthorizationAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="RevokeDirectDeliveryAuthorizationCommand"/>.</summary>
public sealed class RevokeDirectDeliveryAuthorizationCommandHandler(
    IPurchaseOrderRepository orders,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RevokeDirectDeliveryAuthorizationCommand, DirectDeliveryAuthorizationId>
{
    /// <inheritdoc />
    public async Task<Result<DirectDeliveryAuthorizationId>> HandleAsync(
        RevokeDirectDeliveryAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DirectDeliveryAuthorization? authorization = await orders
            .GetDirectDeliveryAuthorizationByIdAsync(command.AuthorizationId, cancellationToken)
            .ConfigureAwait(false);

        if (authorization is null)
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(
                PurchasingErrors.AuthorizationUnknown(command.AuthorizationId));
        }

        Result revoked = authorization.Revoke(currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (revoked.IsFailure)
        {
            return Result<DirectDeliveryAuthorizationId>.Failure(revoked.Errors);
        }

        return Result<DirectDeliveryAuthorizationId>.Success(authorization.Id);
    }
}

/// <summary>Handles <see cref="CreateSupplierReturnCommand"/>.</summary>
public sealed class CreateSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        CreateSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Lines is null || command.Lines.Count == 0)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.EmptyReturn);
        }

        if (!await orders.IsActiveSupplierAsync(command.SupplierId, cancellationToken).ConfigureAwait(false))
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.SupplierUnknown(command.SupplierId));
        }

        if (!await orders.CanReceiveGoodsAsync(command.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnLocationUnknown(command.LocationId));
        }

        ProductId[] productIds = [.. command.Lines.Select(l => l.ProductId).Distinct()];
        IReadOnlyList<Product> products = await orders
            .GetProductsForReceivingAsync(productIds, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, Product> productById = products.ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<SupplierReturnId>.Failure(PurchasingErrors.ProductUnknown(productId));
            }
        }

        BatchId[] batchIds = [.. command.Lines
            .Where(l => l.BatchId is { } batch && !batch.IsEmpty)
            .Select(l => l.BatchId!.Value)
            .Distinct()];

        Dictionary<BatchId, Batch> batchById = (await orders
            .GetBatchesAsync(batchIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(b => b.Id);

        foreach (BatchId batchId in batchIds)
        {
            if (!batchById.ContainsKey(batchId))
            {
                return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnBatchUnknown(batchId));
            }
        }

        List<SupplierReturnLineSpec> specs = [];

        for (int i = 0; i < command.Lines.Count; i++)
        {
            SupplierReturnLineSpec spec = command.Lines[i];
            int lineNo = i + 1;

            if (spec.BatchId is { } batchId && batchById[batchId].ProductId != spec.ProductId)
            {
                return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnBatchProductMismatch(lineNo));
            }

            specs.Add(spec with { TracksBatches = productById[spec.ProductId].TracksBatches });
        }

        Result<SupplierReturn> created = SupplierReturn.Create(
            command.SupplierId,
            command.LocationId,
            specs,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(created.Errors);
        }

        return await orders
            .AddSupplierReturnAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="SubmitSupplierReturnCommand"/>.</summary>
public sealed class SubmitSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    ISystemClock clock) : ICommandHandler<SubmitSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        SubmitSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplierReturn? returnDocument = await orders
            .GetSupplierReturnByIdAsync(command.ReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (returnDocument is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnUnknown(command.ReturnId));
        }

        Result submitted = returnDocument.Submit(clock.UtcNow);

        if (submitted.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(submitted.Errors);
        }

        return Result<SupplierReturnId>.Success(returnDocument.Id);
    }
}

/// <summary>Handles <see cref="ApproveSupplierReturnCommand"/>.</summary>
public sealed class ApproveSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    IApprovalGate gate,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApproveSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        ApproveSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplierReturn? returnDocument = await orders
            .GetSupplierReturnByIdAsync(command.ReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (returnDocument is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnUnknown(command.ReturnId));
        }

        UserId approver = currentUser.UserId ?? UserId.Empty;

        Result gateResult = await gate
            .RequireAsync(
                Permissions.Purchasing.Approve,
                returnDocument.TotalValue,
                approver,
                returnDocument.CreatedByUserId,
                returnDocument.LocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (gateResult.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(gateResult.Errors);
        }

        Result approved = returnDocument.Approve(approver, clock.UtcNow);

        if (approved.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(approved.Errors);
        }

        return Result<SupplierReturnId>.Success(returnDocument.Id);
    }
}

/// <summary>Handles <see cref="RejectSupplierReturnCommand"/>.</summary>
public sealed class RejectSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    ISystemClock clock) : ICommandHandler<RejectSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        RejectSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplierReturn? returnDocument = await orders
            .GetSupplierReturnByIdAsync(command.ReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (returnDocument is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnUnknown(command.ReturnId));
        }

        Result rejected = returnDocument.Reject(clock.UtcNow);

        if (rejected.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(rejected.Errors);
        }

        return Result<SupplierReturnId>.Success(returnDocument.Id);
    }
}

/// <summary>
/// Handles <see cref="DispatchSupplierReturnCommand"/>. Splits the approved
/// return into balanced movement legs — source state at the location to the
/// EXT-SUPPLIER counterparty — and posts them against the SRT number.
/// </summary>
public sealed class DispatchSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<DispatchSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        DispatchSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplierReturn? returnDocument = await orders
            .GetSupplierReturnByIdAsync(command.ReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (returnDocument is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnUnknown(command.ReturnId));
        }

        ReturnDispatchContext? context = await orders
            .GetReturnDispatchContextAsync(returnDocument.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (context is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnLocationUnknown(returnDocument.LocationId));
        }

        if (context.ExternalSupplierLocationId is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnExternalSupplierLocationMissing);
        }

        if (context.TimeZoneId is null)
        {
            return Result<SupplierReturnId>.Failure(
                PurchasingErrors.ReturnLocationTimeZoneMissing(returnDocument.LocationId));
        }

        UserId dispatcher = currentUser.UserId ?? UserId.Empty;

        // The number is allocated only after every authority and state check,
        // so a failed dispatch can never burn a sequence value. It must be
        // assigned while the return is still Approved: Dispatch advances the
        // state, after which numbering is no longer permitted.
        DocumentNumber number = await numbers
            .NextAsync(DocumentType.SupplierReturn, cancellationToken)
            .ConfigureAwait(false);

        Result numbered = returnDocument.AssignNumber(number);

        if (numbered.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(numbered.Errors);
        }

        Result dispatched = returnDocument.Dispatch(clock.UtcNow, command.SupplierAuthorizationNumber);

        if (dispatched.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(dispatched.Errors);
        }

        ProductId[] productIds = [.. returnDocument.Lines.Select(l => l.ProductId).Distinct()];
        Dictionary<ProductId, Product> productById = (await orders
            .GetProductsForReceivingAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<SupplierReturnId>.Failure(PurchasingErrors.ProductUnknown(productId));
            }
        }

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.SupplierReturn,
            ReferenceDocumentType: ReferenceDocumentType.SupplierReturn,
            ReferenceDocumentId: returnDocument.Id.Value,
            ReferenceNumber: returnDocument.Number,
            Legs: BuildLegs(returnDocument, context, productById),
            Actor: new LedgerActor(
                dispatcher,
                returnDocument.ApprovedByUserId,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(context.TimeZoneId),
            ReasonCode: AdjustmentReasonCode.SupplierReturn,
            Notes: command.SupplierAuthorizationNumber);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(posted.Errors);
        }

        return Result<SupplierReturnId>.Success(returnDocument.Id);
    }

    /// <summary>
    /// Builds the balanced legs of each return line: the negative source leg at
    /// the stock's location and state, and the positive EXT-SUPPLIER leg.
    /// </summary>
    private static List<MovementLegSpec> BuildLegs(
        SupplierReturn returnDocument,
        ReturnDispatchContext context,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (SupplierReturnLine line in returnDocument.Lines)
        {
            Product product = productById[line.ProductId];

            legs.Add(new MovementLegSpec(
                line.ProductId,
                line.BatchId,
                returnDocument.LocationId,
                context.LocationKind,
                line.SourceState,
                -line.Quantity,
                line.UnitCost,
                product.TracksBatches));

            legs.Add(new MovementLegSpec(
                line.ProductId,
                line.BatchId,
                context.ExternalSupplierLocationId!.Value,
                LocationKind.External,
                InventoryState.External,
                line.Quantity,
                line.UnitCost,
                product.TracksBatches));
        }

        return legs;
    }
}

/// <summary>Handles <see cref="ConfirmSupplierReturnCommand"/>.</summary>
public sealed class ConfirmSupplierReturnCommandHandler(
    IPurchaseOrderRepository orders,
    ISystemClock clock) : ICommandHandler<ConfirmSupplierReturnCommand, SupplierReturnId>
{
    /// <inheritdoc />
    public async Task<Result<SupplierReturnId>> HandleAsync(
        ConfirmSupplierReturnCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplierReturn? returnDocument = await orders
            .GetSupplierReturnByIdAsync(command.ReturnId, cancellationToken)
            .ConfigureAwait(false);

        if (returnDocument is null)
        {
            return Result<SupplierReturnId>.Failure(PurchasingErrors.ReturnUnknown(command.ReturnId));
        }

        Result confirmed = returnDocument.Confirm(clock.UtcNow);

        if (confirmed.IsFailure)
        {
            return Result<SupplierReturnId>.Failure(confirmed.Errors);
        }

        return Result<SupplierReturnId>.Success(returnDocument.Id);
    }
}

/// <summary>Handles <see cref="ResolveReceivingDiscrepancyCommand"/>.</summary>
public sealed class ResolveReceivingDiscrepancyCommandHandler(
    IPurchaseOrderRepository orders,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ResolveReceivingDiscrepancyCommand, GoodsReceiptId>
{
    /// <inheritdoc />
    public async Task<Result<GoodsReceiptId>> HandleAsync(
        ResolveReceivingDiscrepancyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        GoodsReceipt? receipt = await orders
            .GetGoodsReceiptByDiscrepancyAsync(command.DiscrepancyId, cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            return Result<GoodsReceiptId>.Failure(PurchasingErrors.DiscrepancyUnknown(command.DiscrepancyId));
        }

        UserId resolver = currentUser.UserId ?? UserId.Empty;

        if (!await permissions
                .HasPermissionAsync(resolver, Permissions.Purchasing.ResolveDiscrepancy, receipt.DestinationLocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<GoodsReceiptId>.Failure(
                PurchasingErrors.DiscrepancyResolveRequiresPermission(receipt.DestinationLocationId));
        }

        Result resolved = receipt.ResolveDiscrepancy(
            command.DiscrepancyId,
            command.Outcome,
            command.Note,
            resolver,
            clock.UtcNow);

        if (resolved.IsFailure)
        {
            return Result<GoodsReceiptId>.Failure(resolved.Errors);
        }

        return Result<GoodsReceiptId>.Success(receipt.Id);
    }
}