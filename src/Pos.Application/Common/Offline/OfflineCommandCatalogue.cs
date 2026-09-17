using System.Collections.Frozen;
using Pos.Application.Common.Messaging;
using Pos.Application.Inventory;
using Pos.Application.Purchasing;
using Pos.Application.Quarantine;
using Pos.Application.Sales;
using Pos.Application.Transfers;

namespace Pos.Application.Common.Offline;

/// <summary>
/// Whether a use case declared offline-capable is actually registered in the
/// device container yet.
/// </summary>
/// <remarks>
/// The declaration and the registration are deliberately separate. The
/// declaration is the contract from OFFLINE_SYNC.md §1 and changes only when
/// that table changes; the registration follows the device-side adapters, which
/// arrive over several chunks. A declared use case whose adapters do not exist
/// stays unregistered, so the dispatcher refuses it with
/// <c>application.handler_unavailable</c> instead of resolving a handler whose
/// constructor cannot be satisfied.
/// </remarks>
public enum OfflineCommandState
{
    /// <summary>
    /// Declared offline-capable, but its device-side ports (repositories,
    /// numbering, permission snapshot) are not built yet. Not registered, and
    /// therefore refused on the device.
    /// </summary>
    Pending = 0,

    /// <summary>Registered in the device container and executable offline.</summary>
    Registered = 1,
}

/// <summary>
/// One entry in the offline whitelist.
/// </summary>
/// <param name="CommandType">
/// The command type. It must be a closed <see cref="ICommand{TResult}"/>.
/// </param>
/// <param name="State">Whether the device container registers it yet.</param>
/// <param name="Permissions">
/// The permissions the pipeline checks for this command. Every one of them must
/// be offline-capable in <see cref="Identity.Permissions"/>, because a device
/// snapshot can carry no others — that is what stops an outage from widening
/// authority.
/// </param>
/// <param name="Note">Why it is on the list, in the words of OFFLINE_SYNC.md §1.</param>
public sealed record OfflineCommandDefinition(
    Type CommandType,
    OfflineCommandState State,
    IReadOnlyList<string> Permissions,
    string Note);

/// <summary>
/// The commands a POS device may execute without the server. It is an explicit
/// list of types: a command cannot opt itself in with an attribute, and a new
/// command is offline-incapable until someone adds it here and the boundary
/// tests are updated with it.
/// </summary>
/// <remarks>
/// <para>
/// The list is the executable form of the capability table in
/// OFFLINE_SYNC.md §1. Anything absent from it has no handler in the device
/// container, so an offline attempt fails closed with
/// <c>application.handler_unavailable</c> rather than silently degrading.
/// </para>
/// <para>
/// Permissions checked inside a handler rather than by the pipeline — the
/// manual discount, the price override, the expired-batch override — are not
/// listed on an entry. They need no listing: the device snapshot can only hold
/// offline-capable permissions, so <c>sale.expired_override</c> can never be
/// present offline and the override path is dead there by construction.
/// </para>
/// </remarks>
public sealed class OfflineCommandCatalogue
{
    private readonly FrozenDictionary<Type, OfflineCommandDefinition> byCommandType;

    /// <summary>
    /// Initializes a new instance of the <see cref="OfflineCommandCatalogue"/> class.
    /// </summary>
    /// <param name="entries">The declared use cases.</param>
    /// <exception cref="ArgumentException">
    /// An entry names a type that is not a command, or two entries name the same
    /// command.
    /// </exception>
    public OfflineCommandCatalogue(IEnumerable<OfflineCommandDefinition> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        OfflineCommandDefinition[] declared = [.. entries];

        foreach (OfflineCommandDefinition entry in declared)
        {
            if (!IsCommand(entry.CommandType))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"{entry.CommandType} is not an ICommand<> and cannot be declared offline-capable."),
                    nameof(entries));
            }
        }

        try
        {
            this.byCommandType = declared.ToFrozenDictionary(e => e.CommandType);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The offline catalogue declares a command twice.", nameof(entries), exception);
        }

        this.All = declared;
        this.Registered = [.. declared.Where(e => e.State == OfflineCommandState.Registered)];
    }

    /// <summary>Gets the catalogue the device client ships with.</summary>
    public static OfflineCommandCatalogue Default { get; } = new(DefaultEntries());

    /// <summary>Gets every declared use case, registered or not.</summary>
    public IReadOnlyList<OfflineCommandDefinition> All { get; }

    /// <summary>Gets the use cases the device container registers.</summary>
    public IReadOnlyList<OfflineCommandDefinition> Registered { get; }

    /// <summary>Finds the entry for a command type.</summary>
    /// <param name="commandType">The command type.</param>
    /// <returns>The entry, or <see langword="null"/> if the command is not declared.</returns>
    public OfflineCommandDefinition? Find(Type commandType)
        => this.byCommandType.TryGetValue(commandType, out OfflineCommandDefinition? entry) ? entry : null;

    /// <summary>Reports whether a command is declared offline-capable.</summary>
    /// <param name="commandType">The command type.</param>
    /// <returns><see langword="true"/> if the catalogue declares it.</returns>
    public bool Declares(Type commandType) => this.byCommandType.ContainsKey(commandType);

    /// <summary>Reports whether the device container registers a command.</summary>
    /// <param name="commandType">The command type.</param>
    /// <returns><see langword="true"/> if it is registered on the device.</returns>
    public bool IsRegistered(Type commandType)
        => this.Find(commandType)?.State == OfflineCommandState.Registered;

    /// <summary>Reports whether a type is a closed command.</summary>
    /// <param name="type">The candidate type.</param>
    /// <returns><see langword="true"/> if it implements <see cref="ICommand{TResult}"/>.</returns>
    internal static bool IsCommand(Type type)
        => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));

    private static OfflineCommandDefinition[] DefaultEntries() =>
    [
        // POS sale (cash). The card path is refused by the payment port, not here.
        Pending<CompleteSaleCommand>(
            "POS sale: the device rings up and posts the sale locally.",
            Identity.Permissions.Sales.Create),
        Pending<VoidSaleCommand>(
            "Void of a sale in the same shift and day.",
            Identity.Permissions.Sales.Void),
        Pending<ReprintSaleReceiptCommand>(
            "Receipt reprint from local sales history.",
            Identity.Permissions.Sales.Reprint),
        Pending<CreateSalesReturnCommand>(
            "Customer return referencing a sale held locally; goods land in ReturnPending.",
            Identity.Permissions.Sales.Return),
        Pending<RefundSalesReturnCommand>(
            "Cash refund against a locally held return.",
            Identity.Permissions.Sales.Refund),
        OnDevice<OpenShiftCommand>(
            "Shift open; totals are reconciled centrally after sync.",
            Identity.Permissions.Sales.OpenShift),
        OnDevice<SuspendShiftCommand>(
            "Shift suspend, part of the terminal's own shift lifecycle.",
            Identity.Permissions.Sales.OpenShift),
        OnDevice<ResumeShiftCommand>(
            "Shift resume, part of the terminal's own shift lifecycle.",
            Identity.Permissions.Sales.OpenShift),

        // Closing reconciles the drawer against the shift's cash sales, which a
        // device cannot read until it carries local sales. Balancing against a
        // zero it cannot verify would be worse than refusing.
        Pending<CloseShiftCommand>(
            "Shift close; totals are reconciled centrally after sync.",
            Identity.Permissions.Sales.CloseShift),

        // A walk-in account opened at the till. Deactivation is administrative
        // and waits for the link, so it is not on the list.
        Pending<CreateCustomerCommand>(
            "Customer record created at the till; the new PII syncs up from the encrypted device store.",
            Identity.Permissions.Sales.ManageCustomers),
        Pending<UpdateCustomerCommand>(
            "Customer record corrected at the till.",
            Identity.Permissions.Sales.ManageCustomers),

        // Inventory: create and submit only. Approval never happens offline.
        Pending<CreateStockAdjustmentCommand>(
            "Stock adjustment, create only; approval never happens offline.",
            Identity.Permissions.Inventory.Adjust),
        Pending<SubmitStockAdjustmentCommand>(
            "Stock adjustment submitted for central approval.",
            Identity.Permissions.Inventory.Adjust),
        Pending<OpenInventoryCountCommand>(
            "Stock count entry; a count never posts to the ledger offline.",
            Identity.Permissions.Inventory.Count),
        Pending<RecordCountLinesCommand>(
            "Stock count entry; a count never posts to the ledger offline.",
            Identity.Permissions.Inventory.Count),
        Pending<SubmitInventoryCountCommand>(
            "Stock count submitted for approval.",
            Identity.Permissions.Inventory.Count),
        Pending<CancelInventoryCountCommand>(
            "Abandoning the device's own count; nothing is posted.",
            Identity.Permissions.Inventory.Count),

        // Transfers: request and receive. Approval is a server or token decision.
        Pending<CreateTransferCommand>(
            "Transfer request, queued as Requested and approved centrally later.",
            Identity.Permissions.Transfer.Request),
        Pending<SubmitTransferCommand>(
            "Transfer request, queued as Requested and approved centrally later.",
            Identity.Permissions.Transfer.Request),
        Pending<ReceiveTransferCommand>(
            "Receiving against a pre-authorized transfer; the token is cached.",
            Identity.Permissions.Transfer.Receive),
        Pending<InitiateEmergencyTransferCommand>(
            "Emergency transfer, parked as PendingCentralReview under dual manager authorization.",
            Identity.Permissions.Transfer.Emergency),

        // Receiving anything unexpected raises an incident rather than stock.
        Pending<CreateGoodsReceiptCommand>(
            "Receiving against a pre-authorized purchase order.",
            Identity.Permissions.Purchasing.Receive),
        Pending<CreateQuarantineIncidentCommand>(
            "Unknown barcode or unexpected delivery; quarantine, never Available.",
            Identity.Permissions.Quarantine.Create),
        Pending<AddQuarantinePhotoCommand>(
            "Evidence attached to an incident raised on the device.",
            Identity.Permissions.Quarantine.Create),
    ];

    private static OfflineCommandDefinition Pending<TCommand>(string note, params string[] permissions)
        => new(typeof(TCommand), OfflineCommandState.Pending, permissions, note);

    private static OfflineCommandDefinition OnDevice<TCommand>(string note, params string[] permissions)
        => new(typeof(TCommand), OfflineCommandState.Registered, permissions, note);
}
