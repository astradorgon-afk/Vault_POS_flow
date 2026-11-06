using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Application.Identity;
using Pos.Web.Components.Shared;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>One purchase order: approval line by line, sending, and its delivery record. Deliveries are counted on the receiving page.</summary>
public partial class PurchaseOrderDetail
{
    private readonly HashSet<Guid> reviewed = [];
    private readonly List<PosGoodsReceipt> receipts = [];
    private readonly Dictionary<Guid, PosLocation> locations = [];
    private readonly Dictionary<Guid, PosUnitOfMeasure> units = [];
    private readonly Dictionary<Guid, string> people = [];

    private PosPurchaseOrder? order;
    private PosSupplier? supplier;
    private bool loading = true;
    private bool busy;
    private bool confirmDiscard;
    private bool justSent;
    private ElementReference backToOrdersButton;
    private bool focusBackToOrders;
    private bool receiptsLoaded;
    private string? notice;
    private bool noticeOk;
    private string decisionNotes = string.Empty;
    private string endReason = string.Empty;

    /// <summary>Gets or sets the order shown.</summary>
    [Parameter]
    public Guid OrderId { get; set; }

    /// <summary>Gets or sets what the previous page just did, to confirm it here.</summary>
    [SupplyParameterFromQuery(Name = "done")]
    public string? Done { get; set; }

    private Guid MyId => Session.Current?.User.Id ?? Guid.Empty;

    private bool IsOwner => Session.Current?.User.Roles.Contains(Roles.Owner, StringComparer.Ordinal) == true;

    /// <summary>Whether this buyer signs their own orders off; see <see cref="SubmitAsync"/>.</summary>
    private bool SkipsApproval => IsOwner && Can(Permissions.Purchasing.Approve);

    private string SupplierName => supplier?.Name ?? "the supplier";

    /// <summary>
    /// Whether the printed copy is the buying list rather than the delivery
    /// record. Nothing has arrived yet, so "received" and "still due" would
    /// print a column of zeroes; what the supplier and the owner need on the
    /// page at this point is what was ordered and what it costs.
    /// </summary>
    private bool PrintsOrderList => order?.Status is "Draft" or "PendingApproval" or "Approved";

    private WorkflowReceipt BuildPrintReceipt(PosPurchaseOrder value)
    {
        WorkflowReceiptLine[] lines = [.. value.Lines.Select(line => new WorkflowReceiptLine(
            line.ProductName ?? "Product",
            line.ProductSku,
            PrintsOrderList
            ?
            [
                $"{PurchasingText.Quantity(line.OrderedQuantity)} {UnitName(line.UnitOfMeasureId)}",
                PurchasingText.Money(line.UnitCost),
                PurchasingText.Money(line.LineTotal),
            ]
            :
            [
                $"{PurchasingText.Quantity(line.OrderedQuantity)} {UnitName(line.UnitOfMeasureId)}",
                receiptsLoaded ? $"{PurchasingText.Quantity(ReceivedFor(line.Id))} {UnitName(line.UnitOfMeasureId)}" : "Not loaded",
                receiptsLoaded ? $"{PurchasingText.Quantity(Due(line))} {UnitName(line.UnitOfMeasureId)}" : "Not loaded",
            ]))];

        // The supplier's paperwork arrived with a delivery, so the printed
        // order carries it — the latest one, when they presented any.
        List<WorkflowReceiptDetail> details = [];
        if (PrintsOrderList)
        {
            details.Add(new WorkflowReceiptDetail(
                "Lines",
                FormattableString.Invariant($"{value.Lines.Count} · {PurchasingText.Quantity(value.Lines.Sum(line => line.OrderedQuantity))} units")));
            details.Add(new WorkflowReceiptDetail("Order total", PurchasingText.Money(value.GrandTotal)));
        }

        if (receiptsLoaded && receipts.Count > 0 && receipts.MaxBy(r => r.ReceivedAtUtc) is { } latest)
        {
            if (latest.SupplierDeliveryReceiptNumber is { } deliveryReceipt)
            {
                details.Add(new WorkflowReceiptDetail("Supplier DR", deliveryReceipt));
            }

            if (latest.SupplierOfficialReceiptNumber is { } officialReceipt)
            {
                details.Add(new WorkflowReceiptDetail("Supplier OR", officialReceipt));
            }

            if (latest.SupplierReleasedAtUtc is { } releasedAt)
            {
                details.Add(new WorkflowReceiptDetail("Released by supplier", When(releasedAt)));
            }
        }

        return new WorkflowReceipt(
            PrintsOrderList ? "Purchase order list" : "Purchase order",
            PurchasingText.OrderName(value.Number),
            PurchasingText.Label(value.Status, Outstanding),
            "Supplier",
            SupplierName,
            "Deliver to",
            LocationName(value.DestinationLocationId),
            "Created",
            When(value.CreatedAtUtc),
            "Expected delivery",
            ExpectedDate,
            PrintsOrderList
                ? ["Quantity", "Unit cost", "Line total"]
                : ["Ordered", "Received good", "Still due"],
            lines,
            details);
    }

    private DateOnly ReceivingToday => DateOnly.FromDateTime(ReceivingTime(DateTimeOffset.UtcNow).DateTime);

    private bool ReceivingDateAllowed => order?.OrderedAtUtc is { } sent && DateTimeOffset.UtcNow >= sent;

    private bool IsDeliveryOverdue => order is { Status: "Ordered" or "PartiallyReceived", ExpectedAtUtc: { } expected }
        && DateOnly.FromDateTime(ReceivingTime(expected).DateTime) < ReceivingToday;

    private DateTimeOffset ReceivingTime(DateTimeOffset instant) => order?.ReceivingTimeZoneId is { } zone
        ? TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone)) : instant.InStoreTime();

    private string When(DateTimeOffset? instant) => instant is { } at
        ? ReceivingTime(at).ToString("d MMM yyyy · h:mm tt", System.Globalization.CultureInfo.InvariantCulture) : "Not yet";

    private DateTimeOffset? FirstReceivedAt => receipts.Count > 0 ? receipts.Min(r => r.ReceivedAtUtc) : null;

    private DateTimeOffset? LastReceivedAt => receipts.Count > 0 ? receipts.Max(r => r.ReceivedAtUtc) : null;

    private DateTimeOffset? DecisionAt(string decision) => order?.Approvals
        .Where(a => a.Decision == decision).Select(a => (DateTimeOffset?)a.DecidedAtUtc).Max();

    private string ExpectedDate => order?.ExpectedAtUtc is { } expected
        ? ReceivingTime(expected).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "Not specified";

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        switch (Done)
        {
            case "submitted":
                Tell(true, "Order saved and sent for approval.");
                break;
            case "ready-to-send":
                Tell(true, "Order saved and approved. Print the list if you need it, then send it to the supplier.");
                break;
            case "submitted-not-approved":
                Tell(false, "The order was saved but could not be approved automatically. Approve it below to carry on.");
                break;
            case "saved-not-submitted":
                Tell(false, "The order was saved as a draft, but could not be sent for approval. Try again below.");
                break;
        }

        try
        {
            Task<ApiResult<List<PosLocation>>> locationsTask = Api.GetAsync<List<PosLocation>>("/api/v1/locations", CancellationToken.None);
            Task<ApiResult<List<PosUnitOfMeasure>>> unitsTask = Api.GetUnitsAsync(CancellationToken.None);
            Task<ApiResult<List<PosUserSummary>>>? peopleTask = Session.HasPermission("user.manage")
                ? Api.GetUsersAsync(null, true, CancellationToken.None)
                : null;
            await Task.WhenAll(locationsTask, unitsTask, peopleTask ?? Task.CompletedTask);

            foreach (PosLocation location in locationsTask.Result.Value ?? [])
            {
                locations[location.Id] = location;
            }

            foreach (PosUnitOfMeasure unit in unitsTask.Result.Value ?? [])
            {
                units[unit.Id] = unit;
            }

            foreach (PosUserSummary person in peopleTask?.Result.Value ?? [])
            {
                people[person.Id] = person.DisplayName;
            }

            await LoadOrderAsync();
        }
        catch (HttpRequestException)
        {
            Tell(false, "This order is unavailable. Try again shortly.");
        }
        finally
        {
            loading = false;
        }
    }

    private async Task LoadOrderAsync()
    {
        receiptsLoaded = false;
        ApiResult<PosPurchaseOrder> loaded = await Api.GetPurchaseOrderAsync(OrderId, CancellationToken.None);
        if (loaded is not { IsSuccess: true, Value: { } fresh })
        {
            Tell(false, loaded.Error ?? "This order could not be loaded.");
            return;
        }

        order = fresh;
        Trail.Name(PurchasingText.OrderName(fresh.Number));
        if (supplier?.Id != fresh.SupplierId)
        {
            ApiResult<List<PosSupplier>> suppliers = await Api.GetSuppliersAsync(CancellationToken.None);
            supplier = suppliers.Value?.FirstOrDefault(s => s.Id == fresh.SupplierId);
        }

        receipts.Clear();
        receiptsLoaded = false;
        ApiResult<List<PosGoodsReceiptSummary>> listed = await Api.GetGoodsReceiptsAsync(OrderId, CancellationToken.None);
        if (!listed.IsSuccess)
        {
            Tell(false, listed.Error ?? "Delivery history could not be loaded. Reload before receiving stock.");
            return;
        }

        foreach (PosGoodsReceiptSummary summary in listed.Value ?? [])
        {
            ApiResult<PosGoodsReceipt> receipt = await Api.GetGoodsReceiptAsync(OrderId, summary.Id, CancellationToken.None);
            if (receipt.Value is { } detail)
            {
                receipts.Add(detail);
            }
            else
            {
                receipts.Clear();
                Tell(false, "A delivery could not be loaded. Reload before receiving stock.");
                return;
            }
        }

        receiptsLoaded = true;

        // Approval ticks belong to the order as it was; a reload starts them again.
        reviewed.RemoveWhere(id => fresh.Lines.All(line => line.Id != id));
    }

    private bool Can(string permission) => Session.HasPermission(permission);

    private void ToggleReviewed(Guid lineId)
    {
        if (!reviewed.Add(lineId))
        {
            reviewed.Remove(lineId);
        }
    }

    // Good units only, as head office counts them: refused units are still owed.
    private decimal ReceivedFor(Guid orderLineId) => receipts
        .SelectMany(receipt => receipt.Lines)
        .Where(line => line.PurchaseOrderLineId == orderLineId)
        .Sum(line => line.QuantityReceived - line.QuantityDamaged - line.QuantityWrongItem - line.QuantityExpired);

    private decimal RefusedFor(Guid orderLineId) => receipts
        .SelectMany(receipt => receipt.Lines)
        .Where(line => line.PurchaseOrderLineId == orderLineId)
        .Sum(line => line.QuantityDamaged + line.QuantityWrongItem + line.QuantityExpired);

    private decimal Outstanding => order?.Lines.Sum(Due) ?? 0m;

    private decimal Due(PosPurchaseOrderLine line) => Math.Max(0m, line.OrderedQuantity - ReceivedFor(line.Id));

    /// <summary>
    /// Sends a draft on. The owner approves their own orders, so for them this
    /// takes the signature too and leaves the order at the sending step rather
    /// than in a queue only they could clear.
    /// </summary>
    private Task SubmitAsync() => SkipsApproval
        ? RunAsync(
            async () =>
            {
                ApiResult<PosReference> submitted = await Api.SubmitPurchaseOrderAsync(OrderId, CancellationToken.None);
                return submitted.IsSuccess
                    ? await Api.ApprovePurchaseOrderAsync(OrderId, null, CancellationToken.None)
                    : submitted;
            },
            "Approved. Print the list if you need it, then send it to the supplier.")
        : RunAsync(
            () => Api.SubmitPurchaseOrderAsync(OrderId, CancellationToken.None), "Sent for approval.");

    private Task ApproveAsync() => RunAsync(
        () => Api.ApprovePurchaseOrderAsync(OrderId, NullIfBlank(decisionNotes), CancellationToken.None),
        "Order approved. Send it to the supplier next.");

    private Task RejectAsync() => RunAsync(
        () => Api.RejectPurchaseOrderAsync(OrderId, decisionNotes.Trim(), CancellationToken.None), "Order rejected.");

    /// <summary>
    /// Marks the order as sent, then asks what to do next in a dialog. The
    /// goods are normally days away, so the buyer is usually finished here —
    /// but an order handed over the counter can be received on the spot, and
    /// both ways out are one click from the confirmation.
    /// </summary>
    private async Task SendAsync()
    {
        await RunAsync(
            () => Api.SendPurchaseOrderAsync(OrderId, CancellationToken.None),
            "Marked as sent to the supplier.");

        if (order is { Status: "Ordered" })
        {
            justSent = true;
            focusBackToOrders = true;
        }
    }

    /// <summary>Takes the default way out: back to the list of orders.</summary>
    private void BackToOrders() => Navigation.NavigateTo("purchasing");

    /// <summary>
    /// Escape leaves the way the first button does. The order is already
    /// sent, so there is nothing to lose by stepping away from the dialog.
    /// </summary>
    private void OnSentDialogKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            BackToOrders();
        }
    }

    /// <summary>
    /// The receiving page every role uses: one form, one set of checks. Opened
    /// from here, its Back returns to this order.
    /// </summary>
    private string ReceiveHref => FormattableString.Invariant($"receiving/{OrderId:D}?from=order");

    /// <summary>Closes the dialog and opens the receiving page.</summary>
    private void ReceiveNow()
    {
        justSent = false;
        Navigation.NavigateTo(ReceiveHref);
    }

    /// <summary>
    /// Puts the cursor on the default choice once the dialog is on screen,
    /// so Enter takes the buyer back to the list and Escape does the same.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusBackToOrders)
        {
            return;
        }

        focusBackToOrders = false;
        try
        {
            await backToOrdersButton.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // The confirmation was replaced before the focus call landed.
        }
    }

    private Task CloseAsync() => RunAsync(
        () => Api.ClosePurchaseOrderAsync(OrderId, NullIfBlank(endReason), CancellationToken.None), "Order closed.");

    private Task CancelAsync() => RunAsync(
        () => Api.CancelPurchaseOrderAsync(OrderId, endReason.Trim(), CancellationToken.None), "Order cancelled.");

    private async Task RunAsync(Func<Task<ApiResult<PosReference>>> action, string success)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        try
        {
            ApiResult<PosReference> result = await action();
            if (!result.IsSuccess)
            {
                Tell(false, result.Error ?? "That did not work. Try again.");
                return;
            }

            decisionNotes = string.Empty;
            endReason = string.Empty;
            await LoadOrderAsync();
            if (receiptsLoaded)
            {
                Tell(true, success);
            }
        }
        catch (HttpRequestException)
        {
            Tell(false, "The result could not be confirmed. Reload the order to check its status before trying again.");
        }
        finally
        {
            busy = false;
        }
    }

    private async Task DiscardAsync()
    {
        busy = true;
        try
        {
            ApiResult<bool> result = await Api.WithdrawPurchaseOrderAsync(OrderId, CancellationToken.None);
            if (result.IsSuccess)
            {
                Navigation.NavigateTo("purchasing");
                return;
            }

            Tell(false, result.Error ?? "The draft could not be discarded.");
        }
        finally
        {
            busy = false;
            confirmDiscard = false;
        }
    }

    private void Tell(bool ok, string text)
    {
        noticeOk = ok;
        notice = text;
    }

    private string LocationName(Guid id)
        => locations.TryGetValue(id, out PosLocation? location) ? location.Name : "—";

    private string UnitName(Guid id)
        => units.TryGetValue(id, out PosUnitOfMeasure? unit) ? unit.Code.ToLowerInvariant() : string.Empty;

    private string PersonName(Guid id)
        => id == MyId ? "you" : people.TryGetValue(id, out string? name) ? name : "a colleague";

    private string LineName(Guid orderLineId)
        => order?.Lines.FirstOrDefault(line => line.Id == orderLineId) is { } line
            ? $"{line.LineNo}. {line.ProductName ?? "Product"}"
            : "Line";

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string SplitWords(string value) => Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
}
