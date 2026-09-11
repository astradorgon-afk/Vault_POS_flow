using Pos.Domain.Locations;

namespace Pos.Domain.Inventory;

/// <summary>
/// Describes what a movement type is permitted to do: which states stock may
/// leave, which states it may enter, and what justification it needs.
/// </summary>
/// <param name="AllowedSourceStates">States a negative leg may be posted against.</param>
/// <param name="AllowedDestinationStates">States a positive leg may be posted against.</param>
/// <param name="RequiredReferenceDocument">
/// The document type that must justify the movement, or <see langword="null"/>
/// when several are acceptable.
/// </param>
/// <param name="RequiresApprover">Whether an approving user must be recorded.</param>
/// <param name="RequiresReasonCode">Whether an adjustment reason must be recorded.</param>
/// <param name="PermissionCode">The permission a user must hold to post this type.</param>
public sealed record MovementTypeRule(
    IReadOnlySet<InventoryState> AllowedSourceStates,
    IReadOnlySet<InventoryState> AllowedDestinationStates,
    ReferenceDocumentType? RequiredReferenceDocument,
    bool RequiresApprover,
    bool RequiresReasonCode,
    string PermissionCode);

/// <summary>
/// The movement-type rule table. This is the single place that says which state
/// transitions are legitimate; the ledger consults it on every post, and the
/// domain tests walk it exhaustively.
/// </summary>
public static class MovementTypeRules
{
    private const string PermInventoryReceive = "inventory.receive";
    private const string PermInventoryAdjust = "inventory.adjust";
    private const string PermInventoryAdjustApprove = "inventory.adjust.approve";
    private const string PermInventoryCount = "inventory.count";
    private const string PermInventoryReserve = "inventory.reserve";
    private const string PermTransferDispatch = "transfer.dispatch";
    private const string PermTransferReceive = "transfer.receive";
    private const string PermTransferReconcile = "transfer.reconcile";
    private const string PermQuarantineCreate = "quarantine.create";
    private const string PermQuarantineRelease = "quarantine.release";
    private const string PermQuarantineReject = "quarantine.reject";
    private const string PermSaleCreate = "sale.create";
    private const string PermSaleVoid = "sale.void";
    private const string PermSaleReturn = "sale.return";
    private const string PermPurchaseReturn = "purchase.return";

    private static readonly IReadOnlySet<InventoryState> External = Set(InventoryState.External);
    private static readonly IReadOnlySet<InventoryState> Available = Set(InventoryState.Available);

    private static readonly Dictionary<InventoryMovementType, MovementTypeRule> Rules = new()
    {
        [InventoryMovementType.OpeningBalance] = new(
            External,
            Set(InventoryState.Available, InventoryState.Quarantine),
            ReferenceDocumentType.None,
            RequiresApprover: true,
            RequiresReasonCode: false,
            PermInventoryAdjustApprove),

        [InventoryMovementType.SupplierReceipt] = new(
            External,
            Set(InventoryState.Available, InventoryState.PendingInspection, InventoryState.Quarantine),
            ReferenceDocumentType.GoodsReceipt,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermInventoryReceive),

        [InventoryMovementType.SupplierReturn] = new(
            Set(InventoryState.Damaged, InventoryState.Expired, InventoryState.Quarantine),
            External,
            ReferenceDocumentType.SupplierReturn,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermPurchaseReturn),

        [InventoryMovementType.TransferDispatch] = new(
            Available,
            Set(InventoryState.InTransit),
            ReferenceDocumentType.TransferShipment,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermTransferDispatch),

        [InventoryMovementType.TransferReceipt] = new(
            Set(InventoryState.InTransit),
            Set(InventoryState.Available, InventoryState.Damaged, InventoryState.PendingInspection, InventoryState.TransitVariance),
            ReferenceDocumentType.TransferReceipt,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermTransferReceive),

        [InventoryMovementType.TransferCancelDispatch] = new(
            Set(InventoryState.InTransit),
            Available,
            ReferenceDocumentType.TransferShipment,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermTransferDispatch),

        [InventoryMovementType.PosSale] = new(
            Available,
            External,
            ReferenceDocumentType.Sale,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermSaleCreate),

        [InventoryMovementType.PosSaleVoid] = new(
            External,
            Available,
            ReferenceDocumentType.Sale,
            RequiresApprover: true,
            RequiresReasonCode: false,
            PermSaleVoid),

        [InventoryMovementType.CustomerReturn] = new(
            External,
            Set(InventoryState.ReturnPending),
            ReferenceDocumentType.SalesReturn,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermSaleReturn),

        [InventoryMovementType.ReturnDisposition] = new(
            Set(InventoryState.ReturnPending),
            Set(InventoryState.Available, InventoryState.Quarantine, InventoryState.Damaged, InventoryState.External),
            ReferenceDocumentType.SalesReturn,
            RequiresApprover: false,
            RequiresReasonCode: true,
            PermInventoryAdjust),

        [InventoryMovementType.Reservation] = new(
            Available,
            Set(InventoryState.Reserved),
            ReferenceDocumentType.InventoryReservation,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermInventoryReserve),

        [InventoryMovementType.ReservationRelease] = new(
            Set(InventoryState.Reserved),
            Available,
            ReferenceDocumentType.InventoryReservation,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermInventoryReserve),

        [InventoryMovementType.QuarantineEntry] = new(
            Set(InventoryState.External, InventoryState.Available, InventoryState.PendingInspection, InventoryState.ReturnPending),
            Set(InventoryState.Quarantine),
            ReferenceDocumentType.QuarantineIncident,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermQuarantineCreate),

        [InventoryMovementType.QuarantineRelease] = new(
            Set(InventoryState.Quarantine),
            Available,
            ReferenceDocumentType.QuarantineIncident,
            RequiresApprover: true,
            RequiresReasonCode: false,
            PermQuarantineRelease),

        [InventoryMovementType.QuarantineReject] = new(
            Set(InventoryState.Quarantine),
            External,
            ReferenceDocumentType.QuarantineIncident,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermQuarantineReject),

        [InventoryMovementType.InspectionPass] = new(
            Set(InventoryState.PendingInspection),
            Available,
            ReferenceDocumentType.GoodsReceipt,
            RequiresApprover: false,
            RequiresReasonCode: false,
            PermInventoryReceive),

        [InventoryMovementType.InspectionFail] = new(
            Set(InventoryState.PendingInspection),
            Set(InventoryState.Damaged, InventoryState.Quarantine),
            ReferenceDocumentType.GoodsReceipt,
            RequiresApprover: false,
            RequiresReasonCode: true,
            PermInventoryReceive),

        [InventoryMovementType.Damage] = WriteOff(PermInventoryAdjust),
        [InventoryMovementType.Spoilage] = WriteOff(PermInventoryAdjust),
        [InventoryMovementType.Loss] = WriteOff(PermInventoryAdjust),
        [InventoryMovementType.Theft] = WriteOff(PermInventoryAdjust),

        [InventoryMovementType.ExpiryQuarantine] = new(
            Available,
            Set(InventoryState.Expired),
            ReferenceDocumentType.StockAdjustment,
            RequiresApprover: false,
            RequiresReasonCode: true,
            PermInventoryAdjust),

        [InventoryMovementType.ExpiryWriteOff] = new(
            Set(InventoryState.Expired),
            External,
            ReferenceDocumentType.StockAdjustment,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermInventoryAdjustApprove),

        [InventoryMovementType.CountAdjustmentIncrease] = new(
            External,
            Set(InventoryState.Available, InventoryState.Damaged, InventoryState.Quarantine),
            ReferenceDocumentType.InventoryCount,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermInventoryCount),

        [InventoryMovementType.CountAdjustmentDecrease] = new(
            Set(InventoryState.Available, InventoryState.Damaged, InventoryState.Quarantine, InventoryState.Expired),
            External,
            ReferenceDocumentType.InventoryCount,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermInventoryCount),

        [InventoryMovementType.ApprovedStockAdjustment] = new(
            Set(InventoryState.Available, InventoryState.Damaged, InventoryState.Expired, InventoryState.Quarantine, InventoryState.External),
            Set(InventoryState.Available, InventoryState.Damaged, InventoryState.Expired, InventoryState.Quarantine, InventoryState.External),
            ReferenceDocumentType.StockAdjustment,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermInventoryAdjustApprove),

        [InventoryMovementType.TransitVarianceResolveFound] = new(
            Set(InventoryState.TransitVariance),
            Available,
            ReferenceDocumentType.TransferOrder,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermTransferReconcile),

        [InventoryMovementType.TransitVarianceWriteOff] = new(
            Set(InventoryState.TransitVariance),
            External,
            ReferenceDocumentType.TransferOrder,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermTransferReconcile),

        [InventoryMovementType.Reversal] = new(
            AllStates(),
            AllStates(),
            null,
            RequiresApprover: true,
            RequiresReasonCode: true,
            PermInventoryAdjustApprove),
    };

    /// <summary>Gets every movement type that has a rule defined.</summary>
    public static IReadOnlyCollection<InventoryMovementType> DefinedTypes => Rules.Keys;

    /// <summary>Looks up the rule for a movement type.</summary>
    /// <param name="type">The movement type.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No rule is defined for the type.</exception>
    public static MovementTypeRule For(InventoryMovementType type)
        => Rules.TryGetValue(type, out MovementTypeRule? rule)
            ? rule
            : throw new ArgumentOutOfRangeException(
                nameof(type), type, "No ledger rule is defined for this movement type.");

    /// <summary>Determines whether a rule exists for a movement type.</summary>
    /// <param name="type">The movement type.</param>
    /// <returns><see langword="true"/> when a rule exists.</returns>
    public static bool IsDefined(InventoryMovementType type) => Rules.ContainsKey(type);

    /// <summary>
    /// Validates that a state is legal for a location of the given kind. The
    /// external state exists only on external counterparty locations, and
    /// external locations hold nothing else.
    /// </summary>
    /// <param name="kind">The location kind.</param>
    /// <param name="state">The inventory state.</param>
    /// <returns><see langword="true"/> when the combination is valid.</returns>
    public static bool IsStateValidForLocation(LocationKind kind, InventoryState state)
        => kind == LocationKind.External
            ? state == InventoryState.External
            : state != InventoryState.External;

    private static MovementTypeRule WriteOff(string permission) => new(
        Set(InventoryState.Available, InventoryState.Damaged, InventoryState.Quarantine,
            InventoryState.PendingInspection, InventoryState.ReturnPending, InventoryState.Expired),
        External,
        ReferenceDocumentType.StockAdjustment,
        RequiresApprover: true,
        RequiresReasonCode: true,
        permission);

    private static HashSet<InventoryState> Set(params InventoryState[] states) => [.. states];

    private static HashSet<InventoryState> AllStates() => [.. Enum.GetValues<InventoryState>()];
}
