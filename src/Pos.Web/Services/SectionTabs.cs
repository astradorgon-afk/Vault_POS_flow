namespace Pos.Web.Services;

/// <summary>One page in a set of pages that work as a single module.</summary>
/// <param name="Href">The base-relative address of the page.</param>
/// <param name="Label">What the tab says.</param>
public sealed record SectionTab(string Href, string Label);

/// <summary>
/// Pages that used to stand alone in the menu but are views of one job. Each
/// set lists only the pages this person may open, in the order they work them.
/// </summary>
public static class SectionTabSets
{
    /// <summary>The owner decides store requests.</summary>
    public static bool CanApproveRestock(this UserSession session)
        => session.HasPermission("transfer.approve") && session.IsOwner();

    /// <summary>Warehouse staff pick and dispatch approved requests.</summary>
    public static bool CanFulfillRestock(this UserSession session)
        => session.WorkLocationKind == 0 &&
           session.HasPermission("transfer.pick") && session.HasPermission("transfer.dispatch") &&
           session.Current?.User.Roles.Any(role => role is "MainInventoryManager" or "InventoryStaff") == true;

    /// <summary>
    /// A store asks the warehouse for stock; the warehouse asks the owner to buy.
    /// Different documents, same page, same right.
    /// </summary>
    public static bool CanRequestRestock(this UserSession session)
        => session.WorkLocationKind is 0 or 1 &&
           session.HasPermission("transfer.request") && session.HasPermission("transfer.view") &&
           !session.CanApproveRestock();

    /// <summary>What the request page is called where this person works.</summary>
    public static string RestockRequestLabel(this UserSession session)
        => session.WorkLocationKind == 0 ? "Supply requests" : "Restock requests";

    /// <summary>Restock and transfers: request, decide, fulfil, and the full record.</summary>
    public static IReadOnlyList<SectionTab> Movement(UserSession session)
    {
        List<SectionTab> tabs = [];
        if (session.CanApproveRestock())
        {
            tabs.Add(new("restock/approvals", "Approvals"));
        }

        if (session.CanFulfillRestock())
        {
            tabs.Add(new("restock/fulfillment", "Fulfillment"));
        }

        if (session.CanRequestRestock())
        {
            tabs.Add(new("restock", session.RestockRequestLabel()));
        }

        if (session.HasPermission("transfer.view"))
        {
            tabs.Add(new("transfers", "All transfers"));
        }

        return tabs;
    }

    /// <summary>Where the single Restock menu entry opens for this person.</summary>
    public static string? RestockHome(UserSession session)
        => Movement(session).FirstOrDefault(tab => tab.Href.StartsWith("restock", StringComparison.Ordinal))?.Href;

    /// <summary>How each store is doing: what it sells and what moves through it.</summary>
    public static IReadOnlyList<SectionTab> StorePerformance(UserSession session)
    {
        List<SectionTab> tabs = [];
        if (session.HasPermission("sale.view"))
        {
            tabs.Add(new("analytics/products", "Best & worst sellers"));
        }

        if (session.HasPermission("transfer.view"))
        {
            tabs.Add(new("stores", "Store movement"));
        }

        return tabs;
    }

    private static bool IsOwner(this UserSession session)
        => session.Current?.User.Roles.Contains("Owner", StringComparer.Ordinal) == true;
}
