using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>One requested change on a new adjustment.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, for batch-tracked products.</param>
/// <param name="State">The bucket's state.</param>
/// <param name="QuantityDelta">The signed change in base units; negative removes stock.</param>
public sealed record StockAdjustmentLineInput(ProductId ProductId, BatchId? BatchId, InventoryState State, decimal QuantityDelta);

/// <summary>Raises a draft stock adjustment at a location.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Reason">Why the stock changes.</param>
/// <param name="Notes">Notes; required for <see cref="AdjustmentReasonCode.Other"/>.</param>
/// <param name="Lines">The changes.</param>
public sealed record CreateStockAdjustmentCommand(
    LocationId LocationId,
    AdjustmentReasonCode Reason,
    string? Notes,
    IReadOnlyList<StockAdjustmentLineInput> Lines)
    : ICommand<StockAdjustmentId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Adjust;
}

/// <summary>Submits a draft adjustment for approval.</summary>
/// <param name="AdjustmentId">The adjustment.</param>
public sealed record SubmitStockAdjustmentCommand(StockAdjustmentId AdjustmentId)
    : ICommand<StockAdjustmentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Adjust;
}

/// <summary>Approves an adjustment and posts it to the ledger.</summary>
/// <param name="AdjustmentId">The adjustment.</param>
public sealed record ApproveStockAdjustmentCommand(StockAdjustmentId AdjustmentId)
    : ICommand<StockAdjustmentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.ApproveAdjustment;
}

/// <summary>Refuses a submitted adjustment.</summary>
/// <param name="AdjustmentId">The adjustment.</param>
/// <param name="Reason">Why.</param>
public sealed record RejectStockAdjustmentCommand(StockAdjustmentId AdjustmentId, string? Reason)
    : ICommand<StockAdjustmentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.ApproveAdjustment;
}

/// <summary>Reverses a posted adjustment with opposite ledger movements.</summary>
/// <param name="AdjustmentId">The adjustment.</param>
/// <param name="Reason">Why.</param>
public sealed record ReverseStockAdjustmentCommand(StockAdjustmentId AdjustmentId, string? Reason)
    : ICommand<StockAdjustmentId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.ApproveAdjustment;
}

/// <summary>Opens an inventory count and takes its sheet from the ledger.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Kind">What the count covers.</param>
/// <param name="CategoryIds">The categories, for category and cycle counts.</param>
/// <param name="ProductIds">The products, for product and cycle counts.</param>
/// <param name="Note">An optional note.</param>
public sealed record OpenInventoryCountCommand(
    LocationId LocationId,
    InventoryCountKind Kind,
    IReadOnlyList<CategoryId> CategoryIds,
    IReadOnlyList<ProductId> ProductIds,
    string? Note)
    : ICommand<InventoryCountId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Count;
}

/// <summary>One counted bucket.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, for batch-tracked products.</param>
/// <param name="PhysicalQuantity">The counted quantity in base units.</param>
public sealed record CountLineInput(ProductId ProductId, BatchId? BatchId, decimal PhysicalQuantity);

/// <summary>Records counted quantities on an open count.</summary>
/// <param name="CountId">The count.</param>
/// <param name="Lines">The counted buckets.</param>
public sealed record RecordCountLinesCommand(InventoryCountId CountId, IReadOnlyList<CountLineInput> Lines)
    : ICommand<InventoryCountId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Count;
}

/// <summary>Submits a fully counted sheet for approval.</summary>
/// <param name="CountId">The count.</param>
public sealed record SubmitInventoryCountCommand(InventoryCountId CountId)
    : ICommand<InventoryCountId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Count;
}

/// <summary>Approves a count and posts its variance.</summary>
/// <param name="CountId">The count.</param>
public sealed record ApproveInventoryCountCommand(InventoryCountId CountId)
    : ICommand<InventoryCountId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.ApproveCount;
}

/// <summary>Sends a submitted count back for recounting.</summary>
/// <param name="CountId">The count.</param>
/// <param name="Reason">Why.</param>
public sealed record RejectInventoryCountCommand(InventoryCountId CountId, string? Reason)
    : ICommand<InventoryCountId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.ApproveCount;
}

/// <summary>Abandons a count without posting.</summary>
/// <param name="CountId">The count.</param>
/// <param name="Reason">Why.</param>
public sealed record CancelInventoryCountCommand(InventoryCountId CountId, string? Reason)
    : ICommand<InventoryCountId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Inventory.Count;
}
