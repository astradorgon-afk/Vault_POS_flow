using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// The current quantity and value of one inventory bucket.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>projection</b>, not a source of truth. It exists so the POS can
/// answer "how many are on the shelf" without summing the ledger, and it can be
/// dropped and rebuilt from <see cref="InventoryMovement"/> at any time.
/// </para>
/// <para>
/// It can only be changed by applying a movement: <see cref="Apply"/> is the
/// single mutator, it takes a movement rather than a quantity, and there is no
/// setter anywhere that accepts a target quantity. A database trigger
/// independently verifies that every change to this row is matched by movements
/// inserted in the same transaction.
/// </para>
/// </remarks>
public sealed class InventoryBalance
{
    private InventoryBalance(
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state)
    {
        LocationId = locationId;
        ProductId = productId;
        BatchKey = batchKey;
        State = state;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private InventoryBalance()
    {
    }

    /// <summary>Gets the location this bucket belongs to.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets the product this bucket belongs to.</summary>
    public ProductId ProductId { get; private init; }

    /// <summary>
    /// Gets the batch this bucket belongs to, or the empty identifier when the
    /// product is not batch-tracked, so the primary key stays non-nullable.
    /// </summary>
    public BatchId BatchKey { get; private init; }

    /// <summary>Gets the inventory state this bucket represents.</summary>
    public InventoryState State { get; private init; }

    /// <summary>Gets the current quantity.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the weighted average acquisition cost per unit.</summary>
    public decimal AverageUnitCost { get; private set; }

    /// <summary>Gets the current value of the bucket.</summary>
    public decimal TotalValue { get; private set; }

    /// <summary>Gets the last movement applied to this bucket.</summary>
    public InventoryMovementId LastMovementId { get; private set; }

    /// <summary>Gets when the last movement was applied.</summary>
    public DateTimeOffset LastMovementAtUtc { get; private set; }

    /// <summary>
    /// Creates an empty bucket. Only the ledger calls this, immediately before
    /// applying the movement that brings the bucket into existence.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productId">The product.</param>
    /// <param name="batchKey">The batch key, or the empty identifier.</param>
    /// <param name="state">The inventory state.</param>
    /// <returns>A zero-quantity bucket.</returns>
    public static InventoryBalance CreateEmpty(
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state)
        => new(locationId, productId, batchKey, state);

    /// <summary>
    /// Applies one movement leg to this bucket. This is the only way the
    /// quantity ever changes.
    /// </summary>
    /// <param name="movement">The leg to apply. Must target this exact bucket.</param>
    /// <exception cref="ArgumentException">The movement targets a different bucket.</exception>
    /// <remarks>
    /// Weighted average cost is recalculated only on an increase, and only when
    /// the bucket is not going from a negative position, so a corrective entry
    /// cannot distort the average. Decreases consume at the current average,
    /// which is what keeps transfers value-neutral across locations.
    /// </remarks>
    public void Apply(InventoryMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        if (movement.LocationId != LocationId
            || movement.ProductId != ProductId
            || movement.BatchKey != BatchKey
            || movement.State != State)
        {
            throw new ArgumentException(
                "The movement does not belong to this inventory bucket.", nameof(movement));
        }

        decimal newQuantity = decimal.Round(
            Quantity + movement.QuantityDelta,
            Common.Quantity.Scale,
            MidpointRounding.ToEven);

        if (movement.IsIncrease && Quantity >= 0m && newQuantity > 0m)
        {
            decimal weightedTotal = (Quantity * AverageUnitCost) + (movement.QuantityDelta * movement.UnitCost);
            AverageUnitCost = decimal.Round(
                weightedTotal / newQuantity,
                Money.StorageScale,
                Money.IntermediateRounding);
        }
        else if (newQuantity == 0m)
        {
            AverageUnitCost = 0m;
        }

        Quantity = newQuantity;
        TotalValue = decimal.Round(
            Quantity * AverageUnitCost,
            Money.StorageScale,
            Money.IntermediateRounding);

        LastMovementId = movement.Id;
        LastMovementAtUtc = movement.RecordedAtUtc;
    }

    /// <summary>
    /// Determines whether applying a quantity change would leave the bucket
    /// below zero.
    /// </summary>
    /// <param name="quantityDelta">The signed change.</param>
    /// <returns><see langword="true"/> when the result would be negative.</returns>
    public bool WouldGoNegative(decimal quantityDelta) => Quantity + quantityDelta < 0m;
}
