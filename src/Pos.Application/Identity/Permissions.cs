namespace Pos.Application.Identity;

/// <summary>
/// A permission in the catalogue.
/// </summary>
/// <param name="Code">The stable dotted code. Renaming one requires a data migration.</param>
/// <param name="Module">The module it belongs to, for grouping in the admin UI.</param>
/// <param name="Description">What holding it allows.</param>
/// <param name="IsOfflineCapable">
/// Whether the permission may appear in a device's cached snapshot. Approval and
/// central-authority permissions never are: a network outage must not widen
/// anyone's authority.
/// </param>
/// <param name="IsReadOnly">
/// Whether the permission grants only reading. Used to assert that the Auditor
/// role cannot mutate anything.
/// </param>
public sealed record PermissionDefinition(
    string Code,
    string Module,
    string Description,
    bool IsOfflineCapable,
    bool IsReadOnly);

/// <summary>
/// The permission catalogue. Permissions are defined in code and seeded into the
/// database; users and administrators never invent new ones, so a permission
/// check can never silently pass because someone created a matching row.
/// </summary>
public static class Permissions
{
    /// <summary>Catalog permissions.</summary>
    public static class Catalog
    {
        /// <summary>Read products, barcodes and prices.</summary>
        public const string View = "product.view";

        /// <summary>Create products. Main Warehouse and administrators only.</summary>
        public const string Create = "product.create";

        /// <summary>Edit product master fields.</summary>
        public const string Edit = "product.edit";

        /// <summary>Add or retire barcodes.</summary>
        public const string ManageBarcodes = "product.barcode.manage";

        /// <summary>Create effective-dated price changes.</summary>
        public const string ManagePrices = "product.price.manage";

        /// <summary>See purchase cost and margin.</summary>
        public const string ViewCost = "product.cost.view";

        /// <summary>Deactivate or reactivate a product.</summary>
        public const string Disable = "product.disable";

        /// <summary>Manage categories.</summary>
        public const string ManageCategories = "category.manage";

        /// <summary>Manage brands.</summary>
        public const string ManageBrands = "brand.manage";

        /// <summary>Manage units of measure.</summary>
        public const string ManageUnits = "uom.manage";
    }

    /// <summary>Supplier and purchasing permissions.</summary>
    public static class Purchasing
    {
        /// <summary>View suppliers.</summary>
        public const string ViewSuppliers = "supplier.view";

        /// <summary>Manage suppliers.</summary>
        public const string ManageSuppliers = "supplier.manage";

        /// <summary>View purchase orders.</summary>
        public const string View = "purchase.view";

        /// <summary>Draft a purchase order.</summary>
        public const string Create = "purchase.create";

        /// <summary>Approve a purchase order, subject to the value tier.</summary>
        public const string Approve = "purchase.approve";

        /// <summary>Create goods receipts.</summary>
        public const string Receive = "purchase.receive";

        /// <summary>Close receiving discrepancies.</summary>
        public const string ResolveDiscrepancy = "purchase.discrepancy.resolve";

        /// <summary>Raise supplier returns.</summary>
        public const string Return = "purchase.return";

        /// <summary>Authorize a supplier to deliver directly to a store.</summary>
        public const string AuthorizeDirectToStore = "purchase.direct_to_store.authorize";
    }

    /// <summary>Inventory permissions.</summary>
    public static class Inventory
    {
        /// <summary>See balances for assigned locations.</summary>
        public const string View = "inventory.view";

        /// <summary>See balances business-wide.</summary>
        public const string ViewAll = "inventory.view.all";

        /// <summary>Post receipts into inventory.</summary>
        public const string Receive = "inventory.receive";

        /// <summary>Create an adjustment request.</summary>
        public const string Adjust = "inventory.adjust";

        /// <summary>Approve an adjustment, subject to the value tier.</summary>
        public const string ApproveAdjustment = "inventory.adjust.approve";

        /// <summary>Perform inventory counts.</summary>
        public const string Count = "inventory.count";

        /// <summary>Approve count variances.</summary>
        public const string ApproveCount = "inventory.count.approve";

        /// <summary>Create and release reservations.</summary>
        public const string Reserve = "inventory.reserve";

        /// <summary>Post a movement that drives stock negative, subject to policy.</summary>
        public const string NegativeStock = "inventory.negative_stock";

        /// <summary>Read the ledger and document timelines.</summary>
        public const string ViewMovements = "inventory.movement.view";

        /// <summary>Rebuild the balance projection from the ledger.</summary>
        public const string RebuildBalances = "inventory.rebuild_balances";

        /// <summary>Run the expiry quarantine sweep, or sell from an expired batch via override.</summary>
        public const string RunExpiry = "inventory.expiry.run";
    }

    /// <summary>Transfer permissions.</summary>
    public static class Transfer
    {
        /// <summary>See transfers touching assigned locations.</summary>
        public const string View = "transfer.view";

        /// <summary>Create and submit a transfer request.</summary>
        public const string Request = "transfer.request";

        /// <summary>Approve, modify or reject a transfer.</summary>
        public const string Approve = "transfer.approve";

        /// <summary>Pick stock at the source location.</summary>
        public const string Pick = "transfer.pick";

        /// <summary>Dispatch a transfer, which posts to the ledger.</summary>
        public const string Dispatch = "transfer.dispatch";

        /// <summary>Receive and count at the destination.</summary>
        public const string Receive = "transfer.receive";

        /// <summary>Verify a receipt, for segregation of duties.</summary>
        public const string Verify = "transfer.verify";

        /// <summary>Resolve transfer discrepancies.</summary>
        public const string Reconcile = "transfer.reconcile";

        /// <summary>Create an emergency offline transfer.</summary>
        public const string Emergency = "transfer.emergency";

        /// <summary>Issue pre-approval tokens.</summary>
        public const string IssuePreApproval = "transfer.preapproval.issue";

        /// <summary>Let replenishment recommendations create requests automatically.</summary>
        public const string AutoReplenish = "transfer.auto_replenish";
    }

    /// <summary>Quarantine permissions.</summary>
    public static class Quarantine
    {
        /// <summary>See quarantine incidents.</summary>
        public const string View = "quarantine.view";

        /// <summary>Raise a quarantine incident.</summary>
        public const string Create = "quarantine.create";

        /// <summary>Move an incident to investigation and record findings.</summary>
        public const string Investigate = "quarantine.investigate";

        /// <summary>Release quantity from quarantine to available. Head office only.</summary>
        public const string Release = "quarantine.release";

        /// <summary>Reject quarantined goods or return them to the supplier.</summary>
        public const string Reject = "quarantine.reject";
    }

    /// <summary>Payment receipt permissions. Interim by ADR-0026.</summary>
    public static class Receipts
    {
        /// <summary>Issue a payment receipt.</summary>
        public const string Create = "receipt.create";

        /// <summary>See payment receipts.</summary>
        public const string View = "receipt.view";
    }

    /// <summary>Point-of-sale permissions.</summary>
    public static class Sales
    {
        /// <summary>Ring up and complete a sale.</summary>
        public const string Create = "sale.create";

        /// <summary>View completed sales and receipts.</summary>
        public const string View = "sale.view";

        /// <summary>Apply a manual discount.</summary>
        public const string Discount = "sale.discount";

        /// <summary>Override a unit price.</summary>
        public const string PriceOverride = "sale.price_override";

        /// <summary>Void a sale.</summary>
        public const string Void = "sale.void";

        /// <summary>Accept a return against a sale.</summary>
        public const string Return = "sale.return";

        /// <summary>Accept a return with no original sale.</summary>
        public const string ReturnBlind = "sale.return_blind";

        /// <summary>Issue a refund.</summary>
        public const string Refund = "sale.refund";

        /// <summary>Reprint a receipt.</summary>
        public const string Reprint = "sale.reprint";

        /// <summary>Sell from an expired batch through the exception path.</summary>
        public const string ExpiredOverride = "sale.expired_override";

        /// <summary>Open a shift.</summary>
        public const string OpenShift = "shift.open";

        /// <summary>Close a shift.</summary>
        public const string CloseShift = "shift.close";

        /// <summary>Close another user's shift.</summary>
        public const string CloseOtherShift = "shift.close.other";

        /// <summary>Open the cash drawer without a sale.</summary>
        public const string OpenCashDrawer = "cashdrawer.open_without_sale";

        /// <summary>Manage customer records.</summary>
        public const string ManageCustomers = "customer.manage";
    }

    /// <summary>Reporting, audit and administration permissions.</summary>
    public static class Administration
    {
        /// <summary>View operational reports for assigned locations.</summary>
        public const string ViewReports = "report.view";

        /// <summary>View cost, margin, valuation and profit.</summary>
        public const string ViewFinancialReports = "report.view.financial";

        /// <summary>Export reports.</summary>
        public const string ExportReports = "report.export";

        /// <summary>Read the audit log.</summary>
        public const string ViewAudit = "audit.view";

        /// <summary>Create users and assign roles and locations.</summary>
        public const string ManageUsers = "user.manage";

        /// <summary>Edit roles and their permissions.</summary>
        public const string ManageRoles = "role.manage";

        /// <summary>Enrol, suspend and revoke devices.</summary>
        public const string ManageDevices = "device.manage";

        /// <summary>Inspect and retry synchronization failures.</summary>
        public const string ManageSync = "sync.manage";

        /// <summary>Create and edit locations and their settings.</summary>
        public const string ManageLocations = "location.manage";

        /// <summary>Act across every location, bypassing location scoping.</summary>
        public const string AllLocations = "location.all";

        /// <summary>Change organization settings, thresholds and policies.</summary>
        public const string ManageSettings = "settings.manage";
    }

    private static readonly PermissionDefinition[] Catalogue =
    [
        Def(Catalog.View, "Catalog", "Read products, barcodes and prices.", offline: true, readOnly: true),
        Def(Catalog.Create, "Catalog", "Create products.", offline: false, readOnly: false),
        Def(Catalog.Edit, "Catalog", "Edit product master fields.", offline: false, readOnly: false),
        Def(Catalog.ManageBarcodes, "Catalog", "Add or retire barcodes.", offline: false, readOnly: false),
        Def(Catalog.ManagePrices, "Catalog", "Create effective-dated price changes.", offline: false, readOnly: false),
        Def(Catalog.ViewCost, "Catalog", "See purchase cost and margin.", offline: false, readOnly: true),
        Def(Catalog.Disable, "Catalog", "Deactivate or reactivate a product.", offline: false, readOnly: false),
        Def(Catalog.ManageCategories, "Catalog", "Manage categories.", offline: false, readOnly: false),
        Def(Catalog.ManageBrands, "Catalog", "Manage brands.", offline: false, readOnly: false),
        Def(Catalog.ManageUnits, "Catalog", "Manage units of measure.", offline: false, readOnly: false),

        Def(Purchasing.ViewSuppliers, "Purchasing", "View suppliers.", offline: true, readOnly: true),
        Def(Purchasing.ManageSuppliers, "Purchasing", "Manage suppliers.", offline: false, readOnly: false),
        Def(Purchasing.View, "Purchasing", "View purchase orders.", offline: true, readOnly: true),
        Def(Purchasing.Create, "Purchasing", "Draft a purchase order.", offline: false, readOnly: false),
        Def(Purchasing.Approve, "Purchasing", "Approve a purchase order.", offline: false, readOnly: false),
        Def(Purchasing.Receive, "Purchasing", "Create goods receipts.", offline: true, readOnly: false),
        Def(Purchasing.ResolveDiscrepancy, "Purchasing", "Close receiving discrepancies.", offline: false, readOnly: false),
        Def(Purchasing.Return, "Purchasing", "Raise supplier returns.", offline: false, readOnly: false),
        Def(Purchasing.AuthorizeDirectToStore, "Purchasing", "Authorize direct supplier delivery to a store.", offline: false, readOnly: false),

        Def(Inventory.View, "Inventory", "See balances for assigned locations.", offline: true, readOnly: true),
        Def(Inventory.ViewAll, "Inventory", "See balances business-wide.", offline: false, readOnly: true),
        Def(Inventory.Receive, "Inventory", "Post receipts into inventory.", offline: true, readOnly: false),
        Def(Inventory.Adjust, "Inventory", "Create an adjustment request.", offline: true, readOnly: false),
        Def(Inventory.ApproveAdjustment, "Inventory", "Approve an adjustment.", offline: false, readOnly: false),
        Def(Inventory.Count, "Inventory", "Perform inventory counts.", offline: true, readOnly: false),
        Def(Inventory.ApproveCount, "Inventory", "Approve count variances.", offline: false, readOnly: false),
        Def(Inventory.Reserve, "Inventory", "Create and release reservations.", offline: true, readOnly: false),
        Def(Inventory.NegativeStock, "Inventory", "Post a movement that drives stock negative.", offline: false, readOnly: false),
        Def(Inventory.ViewMovements, "Inventory", "Read the ledger and document timelines.", offline: true, readOnly: true),
        Def(Inventory.RebuildBalances, "Inventory", "Rebuild the balance projection.", offline: false, readOnly: false),
        Def(Inventory.RunExpiry, "Inventory", "Run the expiry quarantine sweep.", offline: false, readOnly: false),

        Def(Transfer.View, "Transfers", "See transfers touching assigned locations.", offline: true, readOnly: true),
        Def(Transfer.Request, "Transfers", "Create and submit a transfer request.", offline: true, readOnly: false),
        Def(Transfer.Approve, "Transfers", "Approve, modify or reject a transfer.", offline: false, readOnly: false),
        Def(Transfer.Pick, "Transfers", "Pick stock at the source location.", offline: true, readOnly: false),
        Def(Transfer.Dispatch, "Transfers", "Dispatch a transfer.", offline: true, readOnly: false),
        Def(Transfer.Receive, "Transfers", "Receive and count at the destination.", offline: true, readOnly: false),
        Def(Transfer.Verify, "Transfers", "Verify a transfer receipt.", offline: true, readOnly: false),
        Def(Transfer.Reconcile, "Transfers", "Resolve transfer discrepancies.", offline: false, readOnly: false),
        Def(Transfer.Emergency, "Transfers", "Create an emergency offline transfer.", offline: true, readOnly: false),
        Def(Transfer.IssuePreApproval, "Transfers", "Issue pre-approval tokens.", offline: false, readOnly: false),
        Def(Transfer.AutoReplenish, "Transfers", "Allow recommendations to create requests.", offline: false, readOnly: false),

        Def(Quarantine.View, "Quarantine", "See quarantine incidents.", offline: true, readOnly: true),
        Def(Quarantine.Create, "Quarantine", "Raise a quarantine incident.", offline: true, readOnly: false),
        Def(Quarantine.Investigate, "Quarantine", "Investigate an incident.", offline: false, readOnly: false),
        Def(Quarantine.Release, "Quarantine", "Release quantity from quarantine.", offline: false, readOnly: false),
        Def(Quarantine.Reject, "Quarantine", "Reject quarantined goods.", offline: false, readOnly: false),

        Def(Receipts.Create, "Receipts", "Issue a payment receipt.", offline: false, readOnly: false),
        Def(Receipts.View, "Receipts", "See payment receipts.", offline: false, readOnly: true),

        Def(Sales.Create, "Sales", "Ring up and complete a sale.", offline: true, readOnly: false),
        Def(Sales.View, "Sales", "View completed sales and receipts.", offline: true, readOnly: true),
        Def(Sales.Discount, "Sales", "Apply a manual discount.", offline: true, readOnly: false),
        Def(Sales.PriceOverride, "Sales", "Override a unit price.", offline: true, readOnly: false),
        Def(Sales.Void, "Sales", "Void a sale.", offline: true, readOnly: false),
        Def(Sales.Return, "Sales", "Accept a return against a sale.", offline: true, readOnly: false),
        Def(Sales.ReturnBlind, "Sales", "Accept a return with no original sale.", offline: false, readOnly: false),
        Def(Sales.Refund, "Sales", "Issue a refund.", offline: true, readOnly: false),
        Def(Sales.Reprint, "Sales", "Reprint a receipt.", offline: true, readOnly: false),
        Def(Sales.ExpiredOverride, "Sales", "Sell from an expired batch.", offline: false, readOnly: false),
        Def(Sales.OpenShift, "Sales", "Open a shift.", offline: true, readOnly: false),
        Def(Sales.CloseShift, "Sales", "Close a shift.", offline: true, readOnly: false),
        Def(Sales.CloseOtherShift, "Sales", "Close another user's shift.", offline: true, readOnly: false),
        Def(Sales.OpenCashDrawer, "Sales", "Open the cash drawer without a sale.", offline: true, readOnly: false),
        Def(Sales.ManageCustomers, "Sales", "Manage customer records.", offline: true, readOnly: false),

        Def(Administration.ViewReports, "Administration", "View operational reports.", offline: true, readOnly: true),
        Def(Administration.ViewFinancialReports, "Administration", "View financial reports.", offline: false, readOnly: true),
        Def(Administration.ExportReports, "Administration", "Export reports.", offline: false, readOnly: true),
        Def(Administration.ViewAudit, "Administration", "Read the audit log.", offline: false, readOnly: true),
        Def(Administration.ManageUsers, "Administration", "Create users and assign roles.", offline: false, readOnly: false),
        Def(Administration.ManageRoles, "Administration", "Edit roles and permissions.", offline: false, readOnly: false),
        Def(Administration.ManageDevices, "Administration", "Manage devices.", offline: false, readOnly: false),
        Def(Administration.ManageSync, "Administration", "Inspect and retry sync failures.", offline: false, readOnly: false),
        Def(Administration.ManageLocations, "Administration", "Create and edit locations.", offline: false, readOnly: false),
        Def(Administration.AllLocations, "Administration", "Act across every location.", offline: false, readOnly: true),
        Def(Administration.ManageSettings, "Administration", "Change organization settings.", offline: false, readOnly: false),
    ];

    /// <summary>Gets every permission in the catalogue.</summary>
    public static IReadOnlyList<PermissionDefinition> All => Catalogue;

    /// <summary>Gets the permissions a device may cache for offline use.</summary>
    public static IReadOnlyList<PermissionDefinition> OfflineCapable
        => [.. Catalogue.Where(p => p.IsOfflineCapable)];

    /// <summary>Gets the permissions that grant only reading.</summary>
    public static IReadOnlyList<PermissionDefinition> ReadOnly
        => [.. Catalogue.Where(p => p.IsReadOnly)];

    /// <summary>
    /// Gets the governance permissions: authority over other people's authority,
    /// over the whole business, or over the controls themselves.
    /// </summary>
    /// <remarks>
    /// Administration refuses to hand one of these to anyone — by role, by role
    /// edit, or by override — unless the administrator holds it too (ADR-0028).
    /// Operational permissions such as selling or counting are deliberately not
    /// here, so an administrator can still set up cashiers and store managers
    /// without being able to sell.
    /// </remarks>
    public static IReadOnlySet<string> Privileged { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Administration.ManageUsers,
        Administration.ManageRoles,
        Administration.ManageSettings,
        Administration.ManageLocations,
        Administration.AllLocations,
        Administration.ViewAudit,
        Inventory.NegativeStock,
        Inventory.RebuildBalances,
        Inventory.ApproveAdjustment,
        Inventory.ApproveCount,
        Inventory.RunExpiry,
        Purchasing.Approve,
        Purchasing.AuthorizeDirectToStore,
        Transfer.Approve,
        Transfer.IssuePreApproval,
    };

    /// <summary>Looks up a permission definition by code.</summary>
    /// <param name="code">The permission code.</param>
    /// <returns>The definition, or <see langword="null"/> when the code is unknown.</returns>
    public static PermissionDefinition? Find(string code)
        => Catalogue.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

    /// <summary>Determines whether a permission code exists in the catalogue.</summary>
    /// <param name="code">The permission code.</param>
    /// <returns><see langword="true"/> when the code is defined.</returns>
    public static bool IsDefined(string code) => Find(code) is not null;

    private static PermissionDefinition Def(
        string code,
        string module,
        string description,
        bool offline,
        bool readOnly)
        => new(code, module, description, offline, readOnly);
}
