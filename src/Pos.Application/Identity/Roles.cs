namespace Pos.Application.Identity;

/// <summary>
/// The standard roles. A role is nothing but a named bundle of permissions; no
/// code anywhere branches on a role name.
/// </summary>
public static class Roles
{
    /// <summary>The business owner. Unlimited authority.</summary>
    public const string Owner = "Owner";

    /// <summary>System administrator. Manages users, devices and settings.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Runs the Main Warehouse and business-wide inventory control.</summary>
    public const string MainInventoryManager = "MainInventoryManager";

    /// <summary>Runs one store. Authority is confined to assigned locations.</summary>
    public const string StoreManager = "StoreManager";

    /// <summary>Handles stock at a location without financial authority.</summary>
    public const string InventoryStaff = "InventoryStaff";

    /// <summary>Operates the point of sale.</summary>
    public const string Cashier = "Cashier";

    /// <summary>Reads everything, changes nothing.</summary>
    public const string Auditor = "Auditor";

    /// <summary>
    /// The default permission grants per role. Seeded on first run and editable
    /// afterwards through role management, with every change audited.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultGrants { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Owner] = [.. Permissions.All.Select(p => p.Code)],

            [Administrator] =
            [
                Permissions.Catalog.View, Permissions.Catalog.Create, Permissions.Catalog.Edit,
                Permissions.Catalog.ManageBarcodes, Permissions.Catalog.ManagePrices,
                Permissions.Catalog.ViewCost, Permissions.Catalog.Disable,
                Permissions.Catalog.ManageCategories, Permissions.Catalog.ManageBrands,
                Permissions.Catalog.ManageUnits,

                Permissions.Purchasing.ViewSuppliers, Permissions.Purchasing.ManageSuppliers,
                Permissions.Purchasing.View, Permissions.Purchasing.Create, Permissions.Purchasing.Approve,
                Permissions.Purchasing.Receive, Permissions.Purchasing.ResolveDiscrepancy,
                Permissions.Purchasing.Return, Permissions.Purchasing.AuthorizeDirectToStore,

                Permissions.Inventory.View, Permissions.Inventory.ViewAll, Permissions.Inventory.Receive,
                Permissions.Inventory.Adjust, Permissions.Inventory.ApproveAdjustment,
                Permissions.Inventory.Count, Permissions.Inventory.ApproveCount,
                Permissions.Inventory.Reserve, Permissions.Inventory.ViewMovements,
                Permissions.Inventory.RebuildBalances,

                Permissions.Transfer.View, Permissions.Transfer.Request, Permissions.Transfer.Approve,
                Permissions.Transfer.Pick, Permissions.Transfer.Dispatch, Permissions.Transfer.Receive,
                Permissions.Transfer.Verify, Permissions.Transfer.Reconcile, Permissions.Transfer.Emergency,
                Permissions.Transfer.IssuePreApproval,

                Permissions.Quarantine.View, Permissions.Quarantine.Create,
                Permissions.Quarantine.Investigate, Permissions.Quarantine.Release,
                Permissions.Quarantine.Reject,

                Permissions.Receipts.Create, Permissions.Receipts.View,

                Permissions.Administration.ViewReports, Permissions.Administration.ViewFinancialReports,
                Permissions.Administration.ExportReports, Permissions.Administration.ViewAudit,
                Permissions.Administration.ManageUsers, Permissions.Administration.ManageRoles,
                Permissions.Administration.ManageDevices, Permissions.Administration.ManageSync,
                Permissions.Administration.ManageLocations, Permissions.Administration.AllLocations,
                Permissions.Administration.ManageSettings,
            ],

            [MainInventoryManager] =
            [
                Permissions.Catalog.View, Permissions.Catalog.Create, Permissions.Catalog.Edit,
                Permissions.Catalog.ManageBarcodes, Permissions.Catalog.ViewCost, Permissions.Catalog.Disable,

                Permissions.Purchasing.ViewSuppliers, Permissions.Purchasing.ManageSuppliers,
                Permissions.Purchasing.View, Permissions.Purchasing.Create, Permissions.Purchasing.Approve,
                Permissions.Purchasing.Receive, Permissions.Purchasing.ResolveDiscrepancy,
                Permissions.Purchasing.Return, Permissions.Purchasing.AuthorizeDirectToStore,

                Permissions.Inventory.View, Permissions.Inventory.ViewAll, Permissions.Inventory.Receive,
                Permissions.Inventory.Adjust, Permissions.Inventory.ApproveAdjustment,
                Permissions.Inventory.Count, Permissions.Inventory.ApproveCount,
                Permissions.Inventory.Reserve, Permissions.Inventory.ViewMovements,

                Permissions.Transfer.View, Permissions.Transfer.Request, Permissions.Transfer.Approve,
                Permissions.Transfer.Pick, Permissions.Transfer.Dispatch, Permissions.Transfer.Receive,
                Permissions.Transfer.Verify, Permissions.Transfer.Reconcile, Permissions.Transfer.Emergency,
                Permissions.Transfer.IssuePreApproval,

                Permissions.Quarantine.View, Permissions.Quarantine.Create,
                Permissions.Quarantine.Investigate, Permissions.Quarantine.Release,
                Permissions.Quarantine.Reject,

                Permissions.Receipts.Create, Permissions.Receipts.View,

                Permissions.Administration.ViewReports, Permissions.Administration.ViewFinancialReports,
                Permissions.Administration.ExportReports, Permissions.Administration.ManageDevices,
                Permissions.Administration.ManageSync, Permissions.Administration.AllLocations,
            ],

            // Scoped to assigned locations by UserLocationAssignment: this role
            // never holds location.all, which is what confines a store manager
            // to their own store.
            [StoreManager] =
            [
                Permissions.Catalog.View, Permissions.Catalog.ViewCost,
                Permissions.Purchasing.ViewSuppliers, Permissions.Purchasing.View,
                Permissions.Purchasing.Receive,

                Permissions.Inventory.View, Permissions.Inventory.Receive, Permissions.Inventory.Adjust,
                Permissions.Inventory.ApproveAdjustment, Permissions.Inventory.Count,
                Permissions.Inventory.ApproveCount, Permissions.Inventory.Reserve,
                Permissions.Inventory.ViewMovements,

                Permissions.Transfer.View, Permissions.Transfer.Request, Permissions.Transfer.Pick,
                Permissions.Transfer.Dispatch, Permissions.Transfer.Receive, Permissions.Transfer.Verify,
                Permissions.Transfer.Emergency,

                Permissions.Quarantine.View, Permissions.Quarantine.Create, Permissions.Quarantine.Investigate,

                Permissions.Receipts.Create, Permissions.Receipts.View,

                Permissions.Sales.Create, Permissions.Sales.View, Permissions.Sales.Discount, Permissions.Sales.PriceOverride,
                Permissions.Sales.Void, Permissions.Sales.Return, Permissions.Sales.ReturnBlind,
                Permissions.Sales.Refund, Permissions.Sales.Reprint, Permissions.Sales.ExpiredOverride,
                Permissions.Sales.OpenShift, Permissions.Sales.CloseShift, Permissions.Sales.CloseOtherShift,
                Permissions.Sales.OpenCashDrawer, Permissions.Sales.ManageCustomers, Permissions.Sales.ViewCustomers,

                Permissions.Administration.ViewReports, Permissions.Administration.ViewFinancialReports,
            ],

            [InventoryStaff] =
            [
                Permissions.Catalog.View, Permissions.Catalog.Create,
                Permissions.Purchasing.ViewSuppliers, Permissions.Purchasing.Create,
                Permissions.Purchasing.View, Permissions.Purchasing.Receive,
                Permissions.Inventory.View, Permissions.Inventory.Receive, Permissions.Inventory.Adjust,
                Permissions.Inventory.Count, Permissions.Inventory.ViewMovements,
                Permissions.Transfer.View, Permissions.Transfer.Request, Permissions.Transfer.Pick,
                Permissions.Transfer.Dispatch, Permissions.Transfer.Receive, Permissions.Transfer.Verify,
                Permissions.Quarantine.View, Permissions.Quarantine.Create,
                Permissions.Administration.ViewReports,
            ],

            [Cashier] =
            [
                Permissions.Catalog.View,
                Permissions.Inventory.View,
                Permissions.Quarantine.Create,
                Permissions.Sales.Create, Permissions.Sales.View, Permissions.Sales.Return,
                Permissions.Sales.OpenShift, Permissions.Sales.CloseShift,
                Permissions.Sales.ManageCustomers, Permissions.Sales.ViewCustomers,
            ],

            // Read-only by construction. PermissionCatalogueTests asserts that
            // every permission granted here is marked IsReadOnly.
            [Auditor] =
            [
                Permissions.Catalog.View, Permissions.Catalog.ViewCost,
                Permissions.Purchasing.ViewSuppliers, Permissions.Purchasing.View,
                Permissions.Inventory.View, Permissions.Inventory.ViewAll,
                Permissions.Inventory.ViewMovements,
                Permissions.Transfer.View,
                Permissions.Quarantine.View,
                Permissions.Receipts.View,
                Permissions.Sales.View, Permissions.Sales.ViewCustomers,
                Permissions.Administration.ViewReports, Permissions.Administration.ViewFinancialReports,
                Permissions.Administration.ExportReports, Permissions.Administration.ViewAudit,
                Permissions.Administration.AllLocations,
            ],
        };

    /// <summary>Gets every standard role name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Owner, Administrator, MainInventoryManager, StoreManager, InventoryStaff, Cashier, Auditor,
    ];
}
