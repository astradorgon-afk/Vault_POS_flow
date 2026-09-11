namespace Pos.Domain.Inventory;

/// <summary>
/// The condition a quantity of stock is held in at a location. Together with
/// location, product and batch it forms the ledger bucket. Persisted as
/// <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum InventoryState
{
    /// <summary>Free stock, sellable and transferable. The only sellable state.</summary>
    Available = 0,

    /// <summary>Physically present but committed to an open sale or transfer.</summary>
    Reserved = 1,

    /// <summary>
    /// Dispatched on a transfer and not yet received. Held at the source
    /// location, which retains custody until the destination counts it.
    /// </summary>
    InTransit = 2,

    /// <summary>
    /// Unverified or unauthorized goods awaiting Main Warehouse review. Counted
    /// and valued, but never sellable and never transferable.
    /// </summary>
    Quarantine = 3,

    /// <summary>Received goods or returns awaiting inspection.</summary>
    PendingInspection = 4,

    /// <summary>A customer return accepted but not yet dispositioned.</summary>
    ReturnPending = 5,

    /// <summary>Physically unsellable; awaiting disposal or supplier return.</summary>
    Damaged = 6,

    /// <summary>Past expiry; blocked from sale, awaiting disposal.</summary>
    Expired = 7,

    /// <summary>
    /// A shipment shortfall or overage held at the source pending investigation.
    /// Keeps the ledger balanced and the discrepancy visible instead of letting
    /// missing units silently disappear.
    /// </summary>
    TransitVariance = 8,

    /// <summary>
    /// Only valid on a location of kind External. Represents stock that has
    /// entered or left the business.
    /// </summary>
    External = 9,
}

/// <summary>Helpers for reasoning about inventory states.</summary>
public static class InventoryStates
{
    /// <summary>States that count towards stock physically held by the business.</summary>
    public static IReadOnlyList<InventoryState> OnHand { get; } =
    [
        InventoryState.Available,
        InventoryState.Reserved,
        InventoryState.Quarantine,
        InventoryState.PendingInspection,
        InventoryState.ReturnPending,
        InventoryState.Damaged,
        InventoryState.Expired,
    ];

    /// <summary>States that are not physically at the location but still ours.</summary>
    public static IReadOnlyList<InventoryState> InFlight { get; } =
    [
        InventoryState.InTransit,
        InventoryState.TransitVariance,
    ];

    /// <summary>
    /// Determines whether stock in a state may be sold. Only
    /// <see cref="InventoryState.Available"/> may; this is deliberately the
    /// single place that answer is expressed.
    /// </summary>
    /// <param name="state">The state to test.</param>
    /// <returns><see langword="true"/> when the state is sellable.</returns>
    public static bool IsSellable(this InventoryState state) => state == InventoryState.Available;

    /// <summary>Determines whether stock in a state may be picked for a transfer.</summary>
    /// <param name="state">The state to test.</param>
    /// <returns><see langword="true"/> when the state is transferable.</returns>
    public static bool IsTransferable(this InventoryState state) => state == InventoryState.Available;

    /// <summary>Determines whether a state counts towards on-hand stock.</summary>
    /// <param name="state">The state to test.</param>
    /// <returns><see langword="true"/> when the state is physically on hand.</returns>
    public static bool IsOnHand(this InventoryState state) => OnHand.Contains(state);

    /// <summary>
    /// Determines whether a state is valid only on an external counterparty location.
    /// </summary>
    /// <param name="state">The state to test.</param>
    /// <returns><see langword="true"/> for the external state.</returns>
    public static bool IsExternalOnly(this InventoryState state) => state == InventoryState.External;
}
