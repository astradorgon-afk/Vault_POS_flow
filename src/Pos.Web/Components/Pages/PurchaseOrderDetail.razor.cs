using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Pos.Application.Identity;
using Pos.Web.Components.Shared;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>One purchase order: approval line by line, sending, and receiving each delivery.</summary>
public partial class PurchaseOrderDetail
{
    private readonly HashSet<Guid> reviewed = [];
    private readonly List<PosGoodsReceipt> receipts = [];
    private readonly List<ReceiveLine> receiving = [];
    private readonly List<QuarantineLine> quarantineLines = [];
    private readonly Dictionary<Guid, PosLocation> locations = [];
    private readonly Dictionary<Guid, PosUnitOfMeasure> units = [];
    private readonly Dictionary<Guid, string> people = [];

    private PosPurchaseOrder? order;
    private PosSupplier? supplier;
    private bool loading = true;
    private bool busy;
    private bool confirmDiscard;
    private bool documentsMissing;
    private bool receivingOpen;
    private bool arrivalConfirmed;
    private bool receiptsLoaded;
    private bool raisingQuarantine;
    private string? quarantineError;
    private Pos.Web.Services.ScanLine? scanQueue;
    private string? notice;
    private bool noticeOk;
    private string decisionNotes = string.Empty;
    private string endReason = string.Empty;

    private enum ReceiveMode
    {
        None,
        All,
        Counted,
        Missing,
    }

    /// <summary>Gets or sets the order shown.</summary>
    [Parameter]
    public Guid OrderId { get; set; }

    /// <summary>Gets or sets what the previous page just did, to confirm it here.</summary>
    [SupplyParameterFromQuery(Name = "done")]
    public string? Done { get; set; }

    private Guid MyId => Session.Current?.User.Id ?? Guid.Empty;

    private bool IsOwner => Session.Current?.User.Roles.Contains(Roles.Owner, StringComparer.Ordinal) == true;

    private string SupplierName => supplier?.Name ?? "the supplier";

    private WorkflowReceipt BuildPrintReceipt(PosPurchaseOrder value)
    {
        WorkflowReceiptLine[] lines = [.. value.Lines.Select(line => new WorkflowReceiptLine(
            line.ProductName ?? "Product",
            line.ProductSku,
            [
                $"{PurchasingText.Quantity(line.OrderedQuantity)} {UnitName(line.UnitOfMeasureId)}",
                receiptsLoaded ? $"{PurchasingText.Quantity(ReceivedFor(line.Id))} {UnitName(line.UnitOfMeasureId)}" : "Not loaded",
                receiptsLoaded ? $"{PurchasingText.Quantity(Due(line))} {UnitName(line.UnitOfMeasureId)}" : "Not loaded",
            ]))];

        return new WorkflowReceipt(
            "Purchase order",
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
            ["Ordered", "Received good", "Still due"],
            lines);
    }

    private DateOnly ReceivingToday => DateOnly.FromDateTime(ReceivingTime(DateTimeOffset.UtcNow).DateTime);

    private bool ReceivingDateAllowed => order?.OrderedAtUtc is { } sent && DateTimeOffset.UtcNow >= sent;

    private bool IsSameDayDelivery => order?.OrderedAtUtc is { } sent
        && DateOnly.FromDateTime(ReceivingTime(sent).DateTime) == ReceivingToday;

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

    private int AccountedCount => receiving.Count(line => line.Mode != ReceiveMode.None);

    private bool CanRecord => receiptsLoaded && ReceivingDateAllowed && arrivalConfirmed && quarantineLines.Count == 0 && receiving.Count > 0
        && AccountedCount == receiving.Count
        && receiving.All(line => line.Problem is null)
        && receiving.Any(line => line.ArrivingQuantity > 0);

    private string ReceiveSummary
    {
        get
        {
            decimal arriving = receiving.Sum(line => line.ArrivingQuantity);
            int missing = receiving.Count(line => line.Mode == ReceiveMode.Missing);
            string text = $"{PurchasingText.Quantity(arriving)} units arriving";
            return missing > 0 ? $"{text} · {missing} line{(missing == 1 ? "" : "s")} not in this delivery" : text;
        }
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        switch (Done)
        {
            case "submitted":
                Tell(true, "Order saved and sent for approval.");
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
        receivingOpen = false;
        arrivalConfirmed = false;
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

        // Approval ticks and a half-entered delivery belong to the order as it
        // was; a reload starts both again.
        reviewed.RemoveWhere(id => fresh.Lines.All(line => line.Id != id));
        receiving.Clear();
        receiving.AddRange(fresh.Lines
            .Where(line => Due(line) > 0)
            .Select(line => new ReceiveLine(line, Due(line))));
        documentsMissing = false;
        receivingOpen = false;
        arrivalConfirmed = false;
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

    private string? scanNote;

    // A scan switches that line to counting and adds one received unit.
    private Task OnScannedAsync(Pos.Shared.Scanning.BarcodeScan scan)
    {
        scanQueue ??= new Pos.Web.Services.ScanLine(ProcessScanAsync);
        return scanQueue.EnqueueAsync(scan);
    }

    private async Task ProcessScanAsync(Pos.Shared.Scanning.BarcodeScan scan)
    {
        ApiResult<PosScannedProduct> found = await Api.FindProductByScanAsync(scan.Code, CancellationToken.None);
        if (found is not { IsSuccess: true, Value: { } product })
        {
            if (found.ErrorCode is "catalog.barcode_unknown" or "catalog.barcode_retired")
            {
                AddQuarantineLine(scan.Code, null);
                scanNote = $"{scan.Code} is not catalogued. Record it as a quarantine incident.";
            }
            else
            {
                scanNote = $"{scan.Code}: {found.Error ?? "The product could not be found."}";
            }
            await InvokeAsync(StateHasChanged);
            return;
        }

        ReceiveLine? line = receiving.FirstOrDefault(l => l.Line.ProductId == product.Id);
        if (line is null)
        {
            AddQuarantineLine(scan.Code, product.Name);
            scanNote = $"{product.Name} is not on this order. Record it as a quarantine incident.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (line.Mode != ReceiveMode.Counted)
        {
            line.Mode = ReceiveMode.Counted;
            line.Received = 0m;
        }

        line.Received += 1m;
        scanNote = $"{product.Name}: {PurchasingText.Quantity(line.Received)} of {PurchasingText.Quantity(line.Due)} counted.";
        await InvokeAsync(StateHasChanged);
    }

    private sealed record QuarantineLine(string Barcode, string? ProductName, decimal Quantity);

    private void AddQuarantineLine(string barcode, string? productName)
    {
        int index = quarantineLines.FindIndex(line => string.Equals(line.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            quarantineLines[index] = quarantineLines[index] with { Quantity = quarantineLines[index].Quantity + 1m };
        }
        else
        {
            quarantineLines.Add(new QuarantineLine(barcode, productName, 1m));
        }
    }

    private void SetQuarantineQuantity(QuarantineLine line, ChangeEventArgs change)
    {
        int index = quarantineLines.IndexOf(line);
        if (index >= 0 && decimal.TryParse(change.Value?.ToString(), out decimal quantity) && quantity >= 0m)
        {
            quarantineLines[index] = line with { Quantity = quantity };
        }
    }

    private void RemoveQuarantineLine(QuarantineLine line) => quarantineLines.Remove(line);

    private async Task RaiseQuarantineAsync()
    {
        if (raisingQuarantine || order is null || quarantineLines.Count == 0)
        {
            return;
        }

        if (!Can("quarantine.create"))
        {
            quarantineError = "Your account needs the quarantine.create permission to record these goods.";
            return;
        }

        List<PosCreateQuarantineLine> lines = [.. quarantineLines
            .Where(line => line.Quantity > 0m)
            .Select(line => new PosCreateQuarantineLine(line.Barcode, line.Quantity, ClaimedProductName: line.ProductName))];
        if (lines.Count == 0)
        {
            quarantineError = "Enter a quantity for at least one item.";
            return;
        }

        raisingQuarantine = true;
        quarantineError = null;
        try
        {
            ApiResult<PosReference> result = await Api.CreateQuarantineIncidentAsync(
                order.DestinationLocationId, lines,
                $"Found while receiving {PurchasingText.OrderName(order.Number)}.", CancellationToken.None);
            if (!result.IsSuccess)
            {
                quarantineError = result.Error ?? "The quarantine incident could not be recorded.";
                return;
            }

            quarantineLines.Clear();
            Tell(true, "Quarantine incident recorded for the unordered goods.");
        }
        catch (HttpRequestException)
        {
            quarantineError = "The quarantine service is unavailable. Try again shortly.";
        }
        finally
        {
            raisingQuarantine = false;
        }
    }

    private static void SetMode(ReceiveLine line, ReceiveMode mode)
    {
        line.Mode = line.Mode == mode ? ReceiveMode.None : mode;
        if (line.Mode == ReceiveMode.Counted && line.Received == 0)
        {
            line.Received = line.Due;
        }
    }

    private Task SubmitAsync() => RunAsync(
        () => Api.SubmitPurchaseOrderAsync(OrderId, CancellationToken.None), "Sent for approval.");

    private Task ApproveAsync() => RunAsync(
        () => Api.ApprovePurchaseOrderAsync(OrderId, NullIfBlank(decisionNotes), CancellationToken.None),
        "Order approved. Send it to the supplier next.");

    private Task RejectAsync() => RunAsync(
        () => Api.RejectPurchaseOrderAsync(OrderId, decisionNotes.Trim(), CancellationToken.None), "Order rejected.");

    private Task SendAsync() => RunAsync(
        () => Api.SendPurchaseOrderAsync(OrderId, CancellationToken.None),
        "Marked as sent. Record the delivery here when it arrives.");

    private Task CloseAsync() => RunAsync(
        () => Api.ClosePurchaseOrderAsync(OrderId, NullIfBlank(endReason), CancellationToken.None), "Order closed.");

    private Task CancelAsync() => RunAsync(
        () => Api.CancelPurchaseOrderAsync(OrderId, endReason.Trim(), CancellationToken.None), "Order cancelled.");

    private async Task RecordDeliveryAsync()
    {
        if (busy || !CanRecord)
        {
            return;
        }

        await RecordReceiptAsync();
        if (noticeOk && order is { Status: "PartiallyReceived" } recorded)
        {
            int shortLines = recorded.Lines.Count(line => Due(line) > 0);
            Tell(true, $"Delivery recorded. The order is marked incomplete: {shortLines} line{(shortLines == 1 ? "" : "s")} still short ({PurchasingText.Quantity(Outstanding)} units).");
        }
    }

    private Task RecordReceiptAsync() => RunAsync(
        () => Api.CreateGoodsReceiptAsync(
            OrderId,
            new PosCreateGoodsReceipt(
                [.. receiving.Where(line => line.ArrivingQuantity > 0 || line.Mode == ReceiveMode.Counted && line.IssueQuantity > 0)
                    .Select(line => line.ToRequest())],
                documentsMissing),
            CancellationToken.None),
        "Delivery recorded. Accepted goods are awaiting inventory inspection.");

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

    /// <summary>What was found for one order line in the delivery being recorded.</summary>
    private sealed class ReceiveLine(PosPurchaseOrderLine line, decimal due)
    {
        public PosPurchaseOrderLine Line { get; } = line;

        public decimal Due { get; } = due;

        public ReceiveMode Mode { get; set; }

        public decimal Received { get; set; }

        public decimal Damaged { get; set; }

        public decimal WrongItem { get; set; }

        public decimal Expired { get; set; }

        public decimal UnitCost { get; set; } = line.UnitCost;

        public string? Lot { get; set; }

        public DateOnly? ExpiresOn { get; set; }

        public decimal IssueQuantity => Damaged + WrongItem + Expired;

        public decimal ArrivingQuantity => Mode switch
        {
            ReceiveMode.All => Due,
            ReceiveMode.Counted => Math.Max(0m, Received),
            _ => 0m,
        };

        public string? Problem => Mode != ReceiveMode.Counted
            ? null
            : Received < 0 || Damaged < 0 || WrongItem < 0 || Expired < 0 ? "Counts cannot be negative."
            : IssueQuantity > Received ? "Damaged, wrong and expired are part of what was received, so together they cannot exceed it."
            : UnitCost < 0 ? "The unit cost cannot be negative."
            : Received == 0 ? "Enter how many arrived, or mark the line as not in this delivery."
            : null;

        public PosCreateGoodsReceiptLine ToRequest() => Mode == ReceiveMode.All
            ? new PosCreateGoodsReceiptLine(Line.Id, Due, 0m, 0m, 0m, Line.UnitCost)
            : new PosCreateGoodsReceiptLine(
                Line.Id,
                Received,
                Damaged,
                WrongItem,
                Expired,
                UnitCost,
                string.IsNullOrWhiteSpace(Lot) ? null : Lot.Trim(),
                ExpiresOn: ExpiresOn);
    }
}
