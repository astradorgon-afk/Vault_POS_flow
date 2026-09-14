namespace Pos.Domain.Inventory;

/// <summary>
/// Why stock moved. Every movement group carries exactly one type, and the type
/// determines which state transitions and which permissions are legitimate.
/// Persisted as <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum InventoryMovementType
{
    /// <summary>Commissioning load of starting stock. Permitted once per bucket.</summary>
    OpeningBalance = 0,

    /// <summary>Goods received from a supplier against a purchase order.</summary>
    SupplierReceipt = 1,

    /// <summary>Goods sent back to a supplier.</summary>
    SupplierReturn = 2,

    /// <summary>Transfer dispatched: available stock becomes in-transit at the source.</summary>
    TransferDispatch = 3,

    /// <summary>Transfer received: in-transit becomes available at the destination.</summary>
    TransferReceipt = 4,

    /// <summary>A dispatch reversed before the goods left.</summary>
    TransferCancelDispatch = 5,

    /// <summary>A completed point-of-sale sale.</summary>
    PosSale = 6,

    /// <summary>A sale voided in full.</summary>
    PosSaleVoid = 7,

    /// <summary>Goods returned by a customer, into the return-pending state.</summary>
    CustomerReturn = 8,

    /// <summary>A decision on where returned goods go.</summary>
    ReturnDisposition = 9,

    /// <summary>Stock committed to an open document.</summary>
    Reservation = 10,

    /// <summary>A reservation released back to available.</summary>
    ReservationRelease = 11,

    /// <summary>Unknown or unauthorized goods placed into quarantine.</summary>
    QuarantineEntry = 12,

    /// <summary>Quarantined goods approved and released to available.</summary>
    QuarantineRelease = 13,

    /// <summary>Quarantined goods rejected and returned to the supplier.</summary>
    QuarantineReject = 14,

    /// <summary>Received goods that passed inspection.</summary>
    InspectionPass = 15,

    /// <summary>Received goods that failed inspection.</summary>
    InspectionFail = 16,

    /// <summary>Written off as damaged.</summary>
    Damage = 17,

    /// <summary>Written off as spoiled.</summary>
    Spoilage = 18,

    /// <summary>Written off as lost.</summary>
    Loss = 19,

    /// <summary>Written off as stolen.</summary>
    Theft = 20,

    /// <summary>Moved from available to expired.</summary>
    ExpiryQuarantine = 21,

    /// <summary>Expired stock disposed of.</summary>
    ExpiryWriteOff = 22,

    /// <summary>A physical count found more than the system recorded.</summary>
    CountAdjustmentIncrease = 23,

    /// <summary>A physical count found less than the system recorded.</summary>
    CountAdjustmentDecrease = 24,

    /// <summary>An approved manual stock adjustment.</summary>
    ApprovedStockAdjustment = 25,

    /// <summary>A shipment shortfall resolved by locating the goods.</summary>
    TransitVarianceResolveFound = 26,

    /// <summary>A shipment shortfall written off after investigation.</summary>
    TransitVarianceWriteOff = 27,

    /// <summary>A reversal of a previously posted group, referencing the original.</summary>
    Reversal = 28,

    /// <summary>An emergency store transfer: available stock moves between two stores.</summary>
    TransferEmergency = 29,

    /// <summary>A reversal of an emergency store transfer, posted on central rejection.</summary>
    TransferEmergencyReversal = 30,
}

/// <summary>
/// The kind of business document a movement was justified by. Persisted as
/// <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum ReferenceDocumentType
{
    /// <summary>No document; only valid for commissioning opening balances.</summary>
    None = 0,

    /// <summary>A purchase order.</summary>
    PurchaseOrder = 1,

    /// <summary>A goods receipt note.</summary>
    GoodsReceipt = 2,

    /// <summary>A transfer order.</summary>
    TransferOrder = 3,

    /// <summary>A transfer shipment.</summary>
    TransferShipment = 4,

    /// <summary>A transfer receipt.</summary>
    TransferReceipt = 5,

    /// <summary>A sale.</summary>
    Sale = 6,

    /// <summary>A customer return.</summary>
    SalesReturn = 7,

    /// <summary>A stock adjustment.</summary>
    StockAdjustment = 8,

    /// <summary>An inventory count.</summary>
    InventoryCount = 9,

    /// <summary>A quarantine incident.</summary>
    QuarantineIncident = 10,

    /// <summary>A supplier return.</summary>
    SupplierReturn = 11,

    /// <summary>An inventory reservation.</summary>
    InventoryReservation = 12,

    /// <summary>An expiry quarantine run: past-expiry stock moved out of available.</summary>
    ExpiryRun = 13,
}

/// <summary>
/// Why stock was adjusted. Required on every adjustment and write-off, and
/// carried onto the ledger so shrinkage can be analysed by cause.
/// </summary>
public enum AdjustmentReasonCode
{
    /// <summary>Physically damaged.</summary>
    Damaged = 1,

    /// <summary>Past its expiry date.</summary>
    Expired = 2,

    /// <summary>Perished before expiry, typically a cold-chain failure.</summary>
    Spoilage = 3,

    /// <summary>Unaccounted for, cause unknown.</summary>
    Loss = 4,

    /// <summary>Believed stolen.</summary>
    Theft = 5,

    /// <summary>A physical count corrected the recorded quantity.</summary>
    CountCorrection = 6,

    /// <summary>Returned to the supplier.</summary>
    SupplierReturn = 7,

    /// <summary>Broken in handling.</summary>
    Broken = 8,

    /// <summary>Contaminated and unfit for sale.</summary>
    Contaminated = 9,

    /// <summary>A dispatched transfer was cancelled before arrival.</summary>
    TransferCancelled = 10,

    /// <summary>Transfer stock missing on arrival was later found.</summary>
    TransitVarianceFound = 11,

    /// <summary>Transfer stock missing on arrival is unrecoverable and written off.</summary>
    TransitVarianceWriteOff = 12,

    /// <summary>Stock moved on an emergency store transfer.</summary>
    EmergencyTransfer = 13,

    /// <summary>An emergency store transfer was reversed on central rejection.</summary>
    EmergencyTransferReversed = 14,

    /// <summary>Anything else. Requires explanatory notes.</summary>
    Other = 99,
}

/// <summary>
/// How a location handles an operation that would drive a bucket below zero.
/// The default is the strictest option.
/// </summary>
public enum NegativeStockPolicy
{
    /// <summary>Reject the operation. The default.</summary>
    Prohibit = 0,

    /// <summary>
    /// Allow only for a user holding the negative-stock permission, with a
    /// reason; raises a high-priority exception.
    /// </summary>
    AllowWithPermission = 1,

    /// <summary>
    /// Allow offline point-of-sale only, up to a configured cap, flagging the
    /// movement for central review.
    /// </summary>
    AllowOfflineWithReview = 2,
}
