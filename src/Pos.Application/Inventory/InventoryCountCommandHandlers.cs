using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Handles <see cref="OpenInventoryCountCommand"/>: resolves the products in scope,
/// takes the sheet from the available buckets, and allocates the CNT number.
/// </summary>
public sealed class OpenInventoryCountCommandHandler(
    IInventoryControlRepository repository,
    IDocumentNumberGenerator numbers,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<OpenInventoryCountCommand, InventoryCountId>
{
    private static readonly InventoryState[] Counted = [InventoryState.Available];

    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        OpenInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryControlLocation? location = await repository
            .GetLocationAsync(command.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null || location.Kind == Domain.Locations.LocationKind.External)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.LocationInvalid(command.LocationId));
        }

        // The scope is checked before the sheet is read or a number allocated.
        Result scope = InventoryCount.ValidateScope(command.Kind, command.CategoryIds.Count > 0, command.ProductIds.Count > 0);

        if (scope.IsFailure)
        {
            return Result<InventoryCountId>.Failure(scope.Errors);
        }

        Result<List<InventoryCountSheetItem>> sheet = await SheetAsync(command, cancellationToken).ConfigureAwait(false);

        if (sheet.IsFailure)
        {
            return Result<InventoryCountId>.Failure(sheet.Errors);
        }

        DocumentNumber number = await numbers.NextAsync(DocumentType.InventoryCount, cancellationToken).ConfigureAwait(false);

        Result<InventoryCount> opened = InventoryCount.Open(
            number, command.LocationId, command.Kind, command.CategoryIds.Count > 0, command.ProductIds.Count > 0,
            sheet.Value, command.Note, currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (opened.IsFailure)
        {
            return Result<InventoryCountId>.Failure(opened.Errors);
        }

        InventoryCount count = opened.Value;
        repository.AddCount(count);

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.CountOpened, nameof(InventoryCount), count.Id.Value, count.LocationId,
                new { count.Number, Kind = count.Kind.ToString(), Lines = count.Lines.Count }, count.Note,
                ReferenceDocumentType.InventoryCount, cancellationToken)
            .ConfigureAwait(false);

        return Result<InventoryCountId>.Success(count.Id);
    }

    private async Task<Result<List<InventoryCountSheetItem>>> SheetAsync(
        OpenInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        List<BucketSnapshot> held = [.. (await repository
                .GetBucketsAsync(command.LocationId, productIds: null, Counted, cancellationToken)
                .ConfigureAwait(false))
            .Where(b => b.Quantity != 0m)];

        Dictionary<ProductId, Product> scope = [];

        if (command.Kind == InventoryCountKind.FullPhysical)
        {
            foreach (Product product in await repository
                         .GetProductsAsync([.. held.Select(b => b.ProductId).Distinct()], cancellationToken)
                         .ConfigureAwait(false))
            {
                scope[product.Id] = product;
            }
        }
        else
        {
            HashSet<ProductId> holding = [.. held.Select(b => b.ProductId)];

            // Inactive products in a category are counted only where stock remains.
            foreach (Product product in await repository
                         .GetProductsInCategoriesAsync(command.CategoryIds, cancellationToken)
                         .ConfigureAwait(false))
            {
                if (product.IsActive || holding.Contains(product.Id))
                {
                    scope[product.Id] = product;
                }
            }

            Result<Dictionary<ProductId, Product>> named = await InventoryControlSupport
                .ProductsAsync(repository, command.ProductIds, cancellationToken)
                .ConfigureAwait(false);

            if (named.IsFailure)
            {
                return Result<List<InventoryCountSheetItem>>.Failure(named.Errors);
            }

            foreach ((ProductId id, Product product) in named.Value)
            {
                scope[id] = product;
            }
        }

        Dictionary<BatchId, Batch> batches = (await repository
                .GetBatchesAsync([.. held.Where(b => !b.BatchKey.IsEmpty).Select(b => b.BatchKey).Distinct()], cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(b => b.Id);

        List<InventoryCountSheetItem> sheet = [];

        foreach (Product product in scope.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            List<BucketSnapshot> buckets = [.. held.Where(b => b.ProductId == product.Id)];

            foreach (BucketSnapshot bucket in buckets)
            {
                Batch? batch = bucket.BatchKey.IsEmpty ? null : batches.GetValueOrDefault(bucket.BatchKey);
                sheet.Add(new InventoryCountSheetItem(
                    product.Id,
                    bucket.BatchKey.IsEmpty ? null : bucket.BatchKey,
                    bucket.Quantity,
                    InventoryControlSupport.UnitCost(bucket, batch, product)));
            }

            if (buckets.Count == 0)
            {
                sheet.Add(new InventoryCountSheetItem(product.Id, null, 0m, product.DefaultPurchaseCost));
            }
        }

        return Result<List<InventoryCountSheetItem>>.Success(sheet);
    }
}

/// <summary>
/// Handles <see cref="RecordCountLinesCommand"/>. Each counted bucket's system
/// quantity is read again at the moment it is recorded.
/// </summary>
public sealed class RecordCountLinesCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RecordCountLinesCommand, InventoryCountId>
{
    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        RecordCountLinesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryCount? count = await repository.GetCountAsync(command.CountId, cancellationToken).ConfigureAwait(false);

        if (count is null)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountUnknown(command.CountId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.Count, count.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.OutsideScope);
        }

        Result<Dictionary<ProductId, Product>> products = await InventoryControlSupport
            .ProductsAsync(repository, command.Lines.Select(l => l.ProductId), cancellationToken)
            .ConfigureAwait(false);

        if (products.IsFailure)
        {
            return Result<InventoryCountId>.Failure(products.Errors);
        }

        Result<Dictionary<BatchId, Batch>> batches = await InventoryControlSupport
            .BatchesAsync(repository, [.. command.Lines.Select(l => (l.ProductId, l.BatchId))], cancellationToken)
            .ConfigureAwait(false);

        if (batches.IsFailure)
        {
            return Result<InventoryCountId>.Failure(batches.Errors);
        }

        IReadOnlyList<BucketSnapshot> buckets = await repository
            .GetBucketsAsync(count.LocationId, products.Value.Keys, [InventoryState.Available], cancellationToken)
            .ConfigureAwait(false);

        UserId counter = currentUser.UserId ?? UserId.Empty;
        DateTimeOffset now = clock.UtcNow;

        foreach (CountLineInput line in command.Lines)
        {
            Product product = products.Value[line.ProductId];
            BatchId batchKey = line.BatchId ?? BatchId.Empty;
            BucketSnapshot? bucket = buckets.FirstOrDefault(b => b.ProductId == line.ProductId && b.BatchKey == batchKey);
            Batch? batch = line.BatchId is { } id ? batches.Value.GetValueOrDefault(id) : null;

            Result recorded = count.RecordCount(
                line.ProductId,
                line.BatchId,
                product.TracksBatches,
                line.PhysicalQuantity,
                bucket?.Quantity ?? 0m,
                InventoryControlSupport.UnitCost(bucket, batch, product),
                counter,
                now);

            if (recorded.IsFailure)
            {
                return Result<InventoryCountId>.Failure(recorded.Errors);
            }
        }

        return Result<InventoryCountId>.Success(count.Id);
    }
}

/// <summary>
/// Handles <see cref="SubmitInventoryCountCommand"/>, flagging every varying line
/// whose product already varied at this location on another count within the
/// look-back window.
/// </summary>
public sealed class SubmitInventoryCountCommandHandler(
    IInventoryControlRepository repository,
    IPermissionEvaluator permissions,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<SubmitInventoryCountCommand, InventoryCountId>
{
    /// <inheritdoc />
    public async Task<Result<InventoryCountId>> HandleAsync(
        SubmitInventoryCountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        InventoryCount? count = await repository.GetCountAsync(command.CountId, cancellationToken).ConfigureAwait(false);

        if (count is null)
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.CountUnknown(command.CountId));
        }

        if (!await InventoryControlSupport
                .InScopeAsync(permissions, currentUser, Permissions.Inventory.Count, count.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<InventoryCountId>.Failure(InventoryControlErrors.OutsideScope);
        }

        DateTimeOffset now = clock.UtcNow;
        ProductId[] varying = [.. count.Lines.Where(l => l.Variance is { } v && v != 0m).Select(l => l.ProductId).Distinct()];

        IReadOnlySet<ProductId> repeated = varying.Length == 0
            ? new HashSet<ProductId>()
            : await repository
                .GetProductsWithPriorVarianceAsync(
                    count.LocationId, varying, now - InventoryControlSupport.RepeatVarianceWindow, count.Id, cancellationToken)
                .ConfigureAwait(false);

        Result submitted = count.Submit(currentUser.UserId ?? UserId.Empty, repeated, now);

        if (submitted.IsFailure)
        {
            return Result<InventoryCountId>.Failure(submitted.Errors);
        }

        await InventoryControlSupport.AuditAsync(
                audit, AuditActions.Inventory.CountSubmitted, nameof(InventoryCount), count.Id.Value, count.LocationId,
                new { count.Number, VaryingLines = count.Lines.Count(l => l.Variance != 0m), count.TotalAbsoluteVarianceValue },
                reason: null, ReferenceDocumentType.InventoryCount, cancellationToken)
            .ConfigureAwait(false);

        if (repeated.Count > 0)
        {
            await InventoryControlSupport.AuditAsync(
                    audit, AuditActions.Inventory.RepeatVarianceDetected, nameof(InventoryCount), count.Id.Value,
                    count.LocationId, new { count.Number, Products = repeated.Select(p => p.Value).ToArray() },
                    reason: null, ReferenceDocumentType.InventoryCount, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<InventoryCountId>.Success(count.Id);
    }
}
