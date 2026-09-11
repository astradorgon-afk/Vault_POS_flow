namespace Pos.Domain.Locations;

/// <summary>
/// The nature of a location. Persisted as <see cref="short"/>; values are part
/// of the database contract and must never be renumbered.
/// </summary>
public enum LocationKind
{
    /// <summary>
    /// The Main Warehouse. Exactly one active instance exists; it is the
    /// authority for product registration, receiving and approvals.
    /// </summary>
    MainWarehouse = 0,

    /// <summary>A retail store holding sellable stock and running POS devices.</summary>
    Store = 1,

    /// <summary>
    /// A virtual counterparty outside the business (supplier, customer,
    /// write-off). Exists so every inventory event is double-entry and the
    /// ledger sums to zero. Never selectable by users.
    /// </summary>
    External = 2,
}

/// <summary>Well-known codes for the system-created external locations.</summary>
public static class SystemLocationCodes
{
    /// <summary>Counterparty for supplier receipts and supplier returns.</summary>
    public const string ExternalSupplier = "EXT-SUPPLIER";

    /// <summary>Counterparty for POS sales and customer returns.</summary>
    public const string ExternalCustomer = "EXT-CUSTOMER";

    /// <summary>
    /// Counterparty for disposal, loss, theft, spoilage and count corrections.
    /// Its balance is the business's cumulative shrinkage.
    /// </summary>
    public const string ExternalWriteOff = "EXT-WRITEOFF";

    /// <summary>Gets every system location code.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ExternalSupplier,
        ExternalCustomer,
        ExternalWriteOff,
    ];
}
