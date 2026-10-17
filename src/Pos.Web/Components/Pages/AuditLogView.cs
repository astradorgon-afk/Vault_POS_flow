using System.Globalization;
using System.Text;
using System.Text.Json;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// Wording, formatting and payload reading for the Activity log page. The page
/// itself keeps only screen state; everything that turns a stored row into
/// something a manager can read lives here.
/// </summary>
public static class AuditLogView
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// What each stored action code means, said the way a manager would say it.
    /// </summary>
    /// <remarks>
    /// The codes are a reporting contract and deliberately terse, so splitting
    /// one on its dots produces "Auth Login Succeeded" — accurate, and nothing
    /// anybody says out loud. Anything missing here still reads sensibly
    /// through <see cref="ActionLabel"/>'s fallback, so a new code ships
    /// readable and gets its phrase when someone gets to it.
    /// </remarks>
    private static readonly Dictionary<string, string> ActionPhrases = new(StringComparer.Ordinal)
    {
        ["auth.login.succeeded"] = "Signed in",
        ["auth.login.failed"] = "Failed to sign in",
        ["auth.lockout"] = "Was locked out after repeated failures",
        ["auth.logout"] = "Signed out",
        ["auth.token.refreshed"] = "Renewed their session",
        ["auth.token.reuse_detected"] = "Presented an already-used sign-in token",
        ["auth.token.revoked"] = "Revoked sign-in tokens",
        ["auth.password.changed"] = "Changed a password",
        ["auth.pin.changed"] = "Changed a cashier PIN",
        ["auth.permission.denied"] = "Was refused for lack of permission",
        ["auth.two_factor.enabled"] = "Turned on two-factor sign-in",
        ["auth.two_factor.reset"] = "Reset two-factor sign-in",

        ["user.created"] = "Added a user",
        ["user.updated"] = "Edited a user",
        ["user.disabled"] = "Disabled a user",
        ["user.enabled"] = "Re-enabled a user",
        ["user.roles.changed"] = "Changed a user's roles",
        ["user.locations.changed"] = "Changed a user's locations",
        ["user.override.changed"] = "Changed a user's permissions",
        ["role.permissions.changed"] = "Changed a role's permissions",
        ["settings.changed"] = "Changed a setting",

        ["device.locations.changed"] = "Changed a device's locations",
        ["device.enrolment_code.issued"] = "Issued a device enrolment code",
        ["device.enrolled"] = "Enrolled a device",
        ["device.suspended"] = "Suspended a device",
        ["device.reactivated"] = "Reactivated a device",
        ["device.revoked"] = "Revoked a device",
        ["device.clock_skew"] = "Reported a clock out of step",
        ["device.pwa_employee.provisioned"] = "Prepared an offline employee snapshot",

        ["product.created"] = "Added a product",
        ["product.updated"] = "Edited a product",
        ["product.activation.changed"] = "Changed whether a product is active",
        ["product.barcode.changed"] = "Changed a product barcode",
        ["product.price.changed"] = "Changed a selling price",
        ["product.price.cancelled"] = "Cancelled a scheduled price",
        ["product.cost.changed"] = "Changed a purchase cost",

        ["inventory.movement.posted"] = "Posted a stock movement",
        ["inventory.negative_stock.attempted"] = "Attempted a posting that would go negative",
        ["inventory.adjustment.created"] = "Requested a stock adjustment",
        ["inventory.adjustment.approved"] = "Approved a stock adjustment",
        ["inventory.adjustment.rejected"] = "Rejected a stock adjustment",
        ["inventory.adjustment.reversed"] = "Reversed a stock adjustment",
        ["inventory.count.opened"] = "Opened a stock count",
        ["inventory.count.submitted"] = "Submitted a stock count",
        ["inventory.count.approved"] = "Approved a stock count",
        ["inventory.count.rejected"] = "Sent a stock count back",
        ["inventory.count.cancelled"] = "Cancelled a stock count",
        ["inventory.count.repeat_variance"] = "Flagged a repeat count variance",
        ["inventory.balances.rebuilt"] = "Rebuilt stock balances",
        ["inventory.integrity.drift"] = "Found stock balances adrift",

        ["purchase.order.created"] = "Drafted a purchase order",
        ["purchase.order.submitted"] = "Sent a purchase order for approval",
        ["purchase.order.approved"] = "Approved a purchase order",
        ["purchase.order.rejected"] = "Rejected a purchase order",
        ["purchase.order.sent"] = "Sent a purchase order to the supplier",
        ["purchase.order.cancelled"] = "Cancelled a purchase order",
        ["purchase.order.closed"] = "Closed a purchase order",
        ["purchase.supply.requested"] = "Asked for the warehouse to be restocked",
        ["purchase.supply.ordered"] = "Ordered against a warehouse supply request",
        ["purchase.supply.closed"] = "Closed a warehouse supply request",
        ["purchase.goods.received"] = "Received goods",
        ["purchase.discrepancy.raised"] = "Raised a receiving discrepancy",
        ["purchase.direct_to_store"] = "Recorded a direct-to-store delivery",

        ["transfer.created"] = "Created a transfer",
        ["transfer.requested"] = "Requested a transfer",
        ["transfer.review.started"] = "Started reviewing a transfer",
        ["transfer.approved"] = "Approved a transfer",
        ["transfer.rejected"] = "Rejected a transfer",
        ["transfer.dispatched"] = "Dispatched a transfer",
        ["transfer.picked"] = "Picked a transfer",
        ["transfer.ready"] = "Marked a transfer ready",
        ["transfer.dispatch.cancelled"] = "Cancelled a dispatch",
        ["transfer.received"] = "Received a transfer",
        ["transfer.verified"] = "Verified and closed a transfer",
        ["transfer.discrepancy.raised"] = "Raised a transfer discrepancy",
        ["transfer.discrepancy.resolved"] = "Resolved a transfer discrepancy",
        ["transfer.emergency.created"] = "Created an emergency transfer",
        ["transfer.emergency.reviewed"] = "Reviewed an emergency transfer",

        ["quarantine.incident.raised"] = "Raised a quarantine incident",
        ["quarantine.released"] = "Released stock from quarantine",
        ["quarantine.rejected"] = "Rejected quarantined goods",

        ["expiry.run.posted"] = "Quarantined expired stock",
        ["expiry.run.clean"] = "Checked for expired stock",

        ["sale.completed"] = "Completed a sale",
        ["sale.voided"] = "Voided a sale",
        ["sale.blocked.expired"] = "Attempted to sell expired stock",
        ["sale.return.created"] = "Accepted a customer return",
        ["sale.return.dispositioned"] = "Sorted returned goods",
        ["sale.return.blind.accepted"] = "Accepted a return without its sale",
        ["sale.refund.issued"] = "Issued a refund",
        ["sale.discount.override"] = "Applied a manual discount",
        ["sale.price.override"] = "Overrode a price",
        ["sale.expired.override"] = "Sold expired stock by exception",
        ["sale.receipt.reprinted"] = "Reprinted a receipt",
        ["customer.created"] = "Added a customer",
        ["customer.updated"] = "Edited a customer",
        ["customer.deactivated"] = "Deactivated a customer",
        ["customer.reactivated"] = "Reactivated a customer",

        ["shift.opened"] = "Opened a shift",
        ["shift.closed"] = "Closed a shift",
        ["shift.suspended"] = "Suspended a shift",
        ["shift.resumed"] = "Resumed a shift",
        ["shift.reconciled"] = "Reconciled a shift",
        ["shift.force_closed"] = "Force-closed an overrunning shift",
        ["cashdrawer.opened"] = "Opened the cash drawer",

        ["sync.event.rejected"] = "Uploaded an event that was rejected",
        ["sync.event.conflict"] = "Uploaded an event that conflicted",
        ["sync.idempotency.reuse"] = "Reused an event identifier",
        ["sync.event.requires_review"] = "Uploaded an event that needs review",
    };

    /// <summary>What an entry records, in plain words: "auth.login.succeeded" → "Signed in".</summary>
    public static string ActionLabel(string action)
        => ActionPhrases.TryGetValue(action, out string? phrase) ? phrase : Humanize(action);

    /// <summary>What kind of record an entry is about: "AppUser" → "User account".</summary>
    public static string RecordLabel(string? entityType) => entityType switch
    {
        null or "" => "—",
        "AppUser" => "User account",
        "AppRole" => "Role",
        "RefreshToken" => "Sign-in token",
        "NegativeStockAttempt" => "Blocked posting",
        "TransferOrder" or "Transfer" => "Transfer",
        "PurchaseOrder" => "Purchase order",
        "GoodsReceipt" => "Goods receipt",
        "SupplyRequest" => "Supply request",
        _ => Humanize(entityType),
    };

    /// <summary>The authority someone held when they acted, or why none is shown.</summary>
    public static string RoleLabel(string? roleSnapshot)
        => string.IsNullOrWhiteSpace(roleSnapshot) ? "No role recorded" : roleSnapshot;

    /// <summary>
    /// One sentence saying who did what, for the top of the detail panel.
    /// </summary>
    /// <param name="actorName">The person who acted, or "System".</param>
    /// <param name="roleSnapshot">The authority they held, where it was recorded.</param>
    /// <param name="action">The stored action code.</param>
    /// <returns>For example, "Maria Santos (Owner) signed in."</returns>
    public static string WhoDidWhat(string actorName, string? roleSnapshot, string action)
    {
        string phrase = ActionLabel(action);
        string lowered = phrase.Length == 0 ? phrase : char.ToLowerInvariant(phrase[0]) + phrase[1..];
        string who = string.IsNullOrWhiteSpace(roleSnapshot)
            ? actorName
            : FormattableString.Invariant($"{actorName} ({roleSnapshot})");

        return FormattableString.Invariant($"{who} {lowered}.");
    }

    /// <summary>
    /// A stored code read as a sentence when no phrase is written for it:
    /// "stocktake.cycle_count" → "Stocktake cycle count".
    /// </summary>
    private static string Humanize(string code)
    {
        string spaced = SplitWords(code.Replace('.', ' ').Replace('_', ' '));
        string trimmed = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant()));

        return trimmed.Length == 0 ? code : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>The first eight characters of an identifier, for table cells.</summary>
    public static string ShortId(Guid? id)
        => id is { } value ? value.ToString("N", CultureInfo.InvariantCulture)[..8].ToUpperInvariant() : "—";

    /// <summary>The whole identifier, for the detail panel where support staff copy it.</summary>
    public static string FullId(Guid? id)
        => id is { } value ? value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() : "—";

    /// <summary>The filter chip's wording for a stored category key.</summary>
    public static string CategoryLabel(string category) => category switch
    {
        "transactions" => "Transactions",
        "inventory" => "Inventory",
        "transfers" => "Transfers",
        "purchasing" => "Purchasing",
        "people" => "People & access",
        "security" => "Security & devices",
        "products" => "Products",
        "other" => "Other activity",
        _ => "All activity",
    };

    /// <summary>The day heading above a run of entries: "Today", "Yesterday", or the weekday.</summary>
    public static string DayLabel(DateOnly day)
    {
        DateOnly today = StoreClock.Today;
        if (day == today)
        {
            return "Today";
        }

        return day == today.AddDays(-1)
            ? "Yesterday"
            : day.ToString("dddd", CultureInfo.InvariantCulture);
    }

    /// <summary>The calendar date beside a day heading.</summary>
    public static string DayDate(DateOnly day) => day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>How long ago something happened, so recency reads at a glance.</summary>
    public static string Ago(DateTimeOffset instant)
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - instant;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Plural((int)elapsed.TotalMinutes, "minute");
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return Plural((int)elapsed.TotalHours, "hour");
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            return Plural((int)elapsed.TotalDays, "day");
        }

        return elapsed < TimeSpan.FromDays(365)
            ? Plural((int)(elapsed.TotalDays / 30), "month")
            : Plural((int)(elapsed.TotalDays / 365), "year");

        static string Plural(int count, string unit)
            => FormattableString.Invariant($"{count} {unit}{(count == 1 ? string.Empty : "s")} ago");
    }

    /// <summary>Indents a stored payload so it can be read; returns it untouched when it is not JSON.</summary>
    public static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>"WarehouseToStore" → "Warehouse to store".</summary>
    public static string SplitWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        StringBuilder builder = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current) && !char.IsUpper(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>Quantities without trailing zeros: 3, not 3.000.</summary>
    public static string FormatQuantity(decimal quantity)
        => quantity % 1m == 0m
            ? quantity.ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Reads the transfer audit payload built by the API; returns null for any other or older shape.</summary>
    public static TransferDetailRead? ReadTransferDetails(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("currentStatus", out _))
            {
                return null;
            }

            return new TransferDetailRead(
                OptionalString(root, "transferNumber"),
                OptionalString(root, "shipmentNumber"),
                OptionalString(root, "receiptNumber"),
                OptionalString(root, "currentStatus") ?? "—",
                OptionalString(root, "mode") ?? "—",
                OptionalString(root, "kind") ?? "—",
                NestedName(root, "source"),
                NestedName(root, "destination"),
                ReadProductLines(root));
        }
        catch (JsonException)
        {
            return null;
        }

        static string? OptionalString(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        static string NestedName(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
                ? OptionalString(value, "name") ?? "Unknown"
                : "Unknown";

        static List<TransferDetailLine> ReadProductLines(JsonElement element)
        {
            List<TransferDetailLine> lines = [];
            if (!element.TryGetProperty("products", out JsonElement products) || products.ValueKind != JsonValueKind.Array)
            {
                return lines;
            }

            foreach (JsonElement product in products.EnumerateArray())
            {
                lines.Add(new TransferDetailLine(
                    product.TryGetProperty("lineNo", out JsonElement lineNo) && lineNo.TryGetInt32(out int number) ? number : 0,
                    OptionalString(product, "productName") ?? "Unknown product",
                    DecimalValue(product, "requestedQuantity"),
                    DecimalValue(product, "pickedQuantity"),
                    DecimalValue(product, "receivedQuantity"),
                    DecimalValue(product, "damagedQuantity")));
            }

            return lines;
        }

        static decimal DecimalValue(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) ? number : 0m;
    }
}

/// <summary>One product line of a transfer, as recorded in the audit payload.</summary>
public sealed record TransferDetailLine(int LineNo, string ProductName, decimal Requested, decimal Picked, decimal Received, decimal Damaged);

/// <summary>The transfer an audit entry belongs to, as recorded in the audit payload.</summary>
public sealed record TransferDetailRead(
    string? TransferNumber,
    string? ShipmentNumber,
    string? ReceiptNumber,
    string CurrentStatus,
    string Mode,
    string Kind,
    string Source,
    string Destination,
    IReadOnlyList<TransferDetailLine> Products);
