using Microsoft.AspNetCore.Components;

namespace Pos.Web.Services;

/// <summary>A page the reader can go back to.</summary>
/// <param name="Label">What the link says.</param>
/// <param name="Href">Where it goes, relative to the app base.</param>
public sealed record NavCrumb(string Label, string Href);

/// <summary>Where a page sits: its section, its own name, and the page above it.</summary>
/// <param name="Section">The sidebar group it belongs to.</param>
/// <param name="Title">The page's own name.</param>
/// <param name="Parent">The page "Back" returns to, or null for a top-level page.</param>
public sealed record PagePlace(string Section, string Title, NavCrumb? Parent);

/// <summary>
/// The navigation bar's knowledge of where the reader is. The route table
/// places every page; a detail page may then name itself once it has loaded
/// ("PO-2026-000005" rather than "Purchase order") or point back somewhere
/// more specific than its section.
/// </summary>
/// <param name="navigation">The app's navigation manager.</param>
public sealed class PageTrail(NavigationManager navigation)
{
    private static readonly NavCrumb Sales = new("Sales ledger", "sales");
    private static readonly NavCrumb PurchaseOrders = new("Purchase orders", "purchasing");
    private static readonly NavCrumb Catalogue = new("Products & barcodes", "catalog");

    private string? namedPath;
    private string? namedTitle;
    private NavCrumb? namedParent;

    /// <summary>Raised when a page names itself.</summary>
    public event Action? Changed;

    /// <summary>Gets where the current page sits.</summary>
    public PagePlace Current
    {
        get
        {
            string path = CurrentPath;
            PagePlace place = Place(path);
            return namedPath == path
                ? place with { Title = namedTitle ?? place.Title, Parent = namedParent ?? place.Parent }
                : place;
        }
    }

    private string CurrentPath => navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0].Trim('/');

    /// <summary>Names the current page, and optionally points Back somewhere more specific.</summary>
    /// <param name="title">The page's own name.</param>
    /// <param name="parent">Where Back goes, when not the page's section.</param>
    public void Name(string title, NavCrumb? parent = null)
    {
        namedPath = CurrentPath;
        namedTitle = title;
        namedParent = parent;
        Changed?.Invoke();
    }

    private static PagePlace Place(string path) => path switch
    {
        "" => new("Workspace", "Overview", null),
        "notifications" => new("Workspace", "Notifications", null),
        "stores" => new("Sales & reports", "Store movement", null),
        "sales" => new("Sales & reports", "Sales ledger", null),
        "analytics/products" => new("Sales & reports", "Product analytics", null),
        _ when path.StartsWith("sales/", StringComparison.Ordinal) => new("Sales & reports", "Sale", Sales),
        _ when path.StartsWith("returns/", StringComparison.Ordinal) => new("Sales & reports", "Return", Sales),
        "inventory/stock" => new("Stock", "Stock levels", null),
        "inventory/counts" => new("Stock", "Stock counts", null),
        _ when path.StartsWith("inventory/counts/", StringComparison.Ordinal) => new("Stock", "Stock count", new NavCrumb("Stock counts", "inventory/counts")),
        "transfers" => new("Movement", "All transfers", null),
        "restock" => new("Movement", "Restock requests", null),
        "restock/approvals" => new("Movement", "Restock approvals", null),
        "restock/fulfillment" => new("Movement", "Restock fulfillment", null),
        _ when path.StartsWith("transfers/", StringComparison.Ordinal) => new("Movement", "Transfer", new NavCrumb("All transfers", "transfers")),
        _ when path.StartsWith("inventory/timeline/", StringComparison.Ordinal) => new("Stock", "Movement chain", new NavCrumb("Stock levels", "inventory/stock")),
        "reports" => new("Sales & reports", "Daily reports", null),
        "catalog" => new("Catalogue & buying", "Products & barcodes", null),
        "catalog/change-requests" => new("Catalogue & buying", "Catalogue approvals", null),
        _ when path.StartsWith("catalog/products/", StringComparison.Ordinal) => new("Catalogue & buying", "Product", Catalogue),
        "inventory/exceptions" => new("Stock", "Stock exceptions", null),
        "purchasing" => new("Catalogue & buying", "Purchase orders", null),
        "receiving" => new("Stock", "Goods receiving", null),
        _ when path.StartsWith("receiving/", StringComparison.Ordinal) => new("Stock", "Receive delivery", new("Goods receiving", "receiving")),
        "purchasing/new" => new("Catalogue & buying", "New purchase order", PurchaseOrders),
        _ when path.StartsWith("purchasing/", StringComparison.Ordinal) => new("Catalogue & buying", "Purchase order", PurchaseOrders),
        "admin/locations" => new("Administration", "Locations", null),
        "admin/users" => new("Administration", "People & roles", null),
        "admin/devices" => new("Administration", "Devices", null),
        "admin/audit" => new("Administration", "Activity log", null),
        "sync/failures" => new("Administration", "Sync failures", null),
        _ => new("Workspace", "VaultFlow", new NavCrumb("Overview", string.Empty)),
    };
}
