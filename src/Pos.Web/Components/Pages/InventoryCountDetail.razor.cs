using System.Globalization;
using Microsoft.AspNetCore.Components;
using Pos.Shared.Scanning;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The stock count sheet: recording what is on the shelf, and deciding what to
/// do about the difference.
/// </summary>
/// <remarks>
/// The arithmetic lives in <see cref="InventoryCountSheet"/> so it can be
/// tested on its own; what is left here is loading, permissions and the
/// commands the buttons send.
/// </remarks>
public sealed partial class InventoryCountDetail : IDisposable
{
    [Inject] private VaultFlowApiClient Api { get; set; } = default!;

    [Inject] private UserSession Session { get; set; } = default!;

    /// <summary>The count on show.</summary>
    [Parameter] public Guid CountId { get; set; }

    private static readonly (CountSheetFilter Key, string Label)[] FilterChoices =
    [
        (CountSheetFilter.All, "All"),
        (CountSheetFilter.Uncounted, "To count"),
        (CountSheetFilter.Variances, "Varying"),
        (CountSheetFilter.Repeats, "Repeats"),
    ];

    private static readonly CultureInfo Peso = CultureInfo.GetCultureInfo("en-PH");

    private readonly List<PosLocation> locations = [];
    private readonly Dictionary<Guid, (string Sku, string Name)> productNames = [];
    private readonly Dictionary<Guid, string> people = [];
    private readonly InventoryCountSheet sheet = new();

    private PosInventoryCountDetail? detail;
    private CancellationTokenSource? inFlight;
    private bool loadingDetail = true;
    private bool busy;
    private bool cancelling;
    private bool rejecting;
    private bool approving;
    private string reasonText = string.Empty;
    private string search = string.Empty;
    private CountSheetFilter filter = CountSheetFilter.All;
    private string? message;

    private bool CanViewInventory => Session.HasPermission("inventory.view");
    private bool CanCount => Session.HasPermission("inventory.count");
    private bool CanApproveCount => Session.HasPermission("inventory.count.approve");

    private bool IsCounting => detail?.Count.Status == "Counting";
    private bool IsPendingApproval => detail?.Count.Status == "PendingApproval";

    private double PercentCounted => sheet.TotalLines == 0 ? 0d : sheet.CountedLines * 100d / sheet.TotalLines;

    private IEnumerable<PosInventoryCountLineView> VisibleLines
        => sheet.Visible(filter, search, Describe);

    protected override async Task OnInitializedAsync()
    {
        if (!CanViewInventory)
        {
            loadingDetail = false;
            return;
        }

        await LoadDetailAsync();
    }

    /// <summary>Stops any load still running once the page is gone.</summary>
    public void Dispose()
    {
        inFlight?.Cancel();
        inFlight?.Dispose();
        inFlight = null;
    }

    private async Task LoadDetailAsync()
    {
        inFlight?.Cancel();
        inFlight?.Dispose();
        CancellationTokenSource source = new();
        inFlight = source;

        loadingDetail = true;
        try
        {
            CancellationToken token = source.Token;

            if (locations.Count == 0)
            {
                ApiResult<List<PosLocation>> locationResponse = await Api.GetLocationsAsync(token);
                if (locationResponse.IsSuccess && locationResponse.Value is not null)
                {
                    locations.AddRange(locationResponse.Value.Where(x => x.Kind == 1 && x.IsActive && Session.CanUseInventoryLocation(x.Id)));
                }
            }

            ApiResult<PosInventoryCountDetail> response = await Api.GetInventoryCountAsync(CountId, token);
            if (!response.IsSuccess || response.Value is null)
            {
                message = response.Error ?? "This count could not be found.";
                return;
            }

            if (!Session.CanUseInventoryLocation(response.Value.Count.LocationId))
            {
                message = "This count is outside your assigned locations.";
                return;
            }

            detail = response.Value;
            sheet.Load(detail.Lines);
            await LoadProductNamesAsync(detail.Lines.Select(line => line.ProductId).Distinct().ToArray(), token);
            await LoadPeopleAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the page is gone.
        }
        finally
        {
            loadingDetail = false;
            if (ReferenceEquals(inFlight, source))
            {
                inFlight = null;
            }

            source.Dispose();
        }
    }

    private async Task LoadProductNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken token)
    {
        HashSet<Guid> wanted = [.. ids.Where(id => !productNames.ContainsKey(id))];
        foreach (Guid id in wanted)
        {
            productNames[id] = (ShortId(id), "Product");
        }

        for (int offset = 0; offset < 2000 && wanted.Count > 0; offset += 200)
        {
            ApiResult<List<PosProductSummary>> response = await Api.GetProductsAsync(null, offset, 200, token);
            if (!response.IsSuccess || response.Value is null || response.Value.Count == 0)
            {
                break;
            }

            foreach (PosProductSummary product in response.Value)
            {
                if (wanted.Remove(product.Id))
                {
                    productNames[product.Id] = (product.Sku, product.Name);
                }
            }

            if (response.Value.Count < 200)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Resolves the submitter and approver to names, where this account is
    /// allowed to read the directory.
    /// </summary>
    /// <remarks>
    /// Listing accounts needs <c>user.manage</c>, which a counter does not have,
    /// so the lookup is skipped rather than attempted and refused. Without it
    /// the panel says only that the step happened, which is what it said before.
    /// </remarks>
    private async Task LoadPeopleAsync(CancellationToken token)
    {
        Guid[] wanted = [.. new[] { detail?.SubmittedByUserId, detail?.ApprovedByUserId }
            .Where(id => id is { } value && value != Guid.Empty && !people.ContainsKey(value))
            .Select(id => id!.Value).Distinct()];

        if (wanted.Length == 0 || !Session.HasPermission("user.manage"))
        {
            return;
        }

        ApiResult<List<PosUserSummary>> response = await Api.GetUsersAsync(null, true, token);
        if (!response.IsSuccess || response.Value is null)
        {
            return;
        }

        foreach (PosUserSummary user in response.Value.Where(user => wanted.Contains(user.Id)))
        {
            people[user.Id] = user.DisplayName;
        }
    }

    private string? PersonName(Guid userId)
        => people.TryGetValue(userId, out string? name) ? name : null;

    private string? scanNote;
    private int? lastScannedLine;
    private ScanLine? scanQueue;

    private Task OnScannedAsync(BarcodeScan scan)
    {
        scanQueue ??= new ScanLine(CountScannedAsync);
        return scanQueue.EnqueueAsync(scan);
    }

    // One scan, one unit: the product's counted quantity goes up by one.
    private async Task CountScannedAsync(BarcodeScan scan)
    {
        if (detail is null)
        {
            return;
        }

        try
        {
            ApiResult<PosScannedProduct> found = await Api.FindProductByScanAsync(scan.Code, CancellationToken.None);
            if (found is not { IsSuccess: true, Value: { } product })
            {
                scanNote = $"{scan.Code}: {found.Error ?? "not a known product."}";
                lastScannedLine = null;
                return;
            }

            PosInventoryCountLineView? line = detail.Lines.FirstOrDefault(l => l.ProductId == product.Id);
            if (line is null)
            {
                scanNote = $"{product.Name} is not on this count.";
                lastScannedLine = null;
                return;
            }

            decimal counted = sheet.Increment(line.LineNo);
            lastScannedLine = line.LineNo;
            scanNote = $"{product.Name}: {counted.ToString("0.###", CultureInfo.InvariantCulture)} counted.";

            // A scan against a line the current filter hides would otherwise
            // vanish as it is counted, which reads as the scan being lost.
            if (filter == CountSheetFilter.Uncounted)
            {
                filter = CountSheetFilter.All;
            }
        }
        catch (HttpRequestException)
        {
            scanNote = "Head office could not be reached to look that barcode up.";
        }
        finally
        {
            StateHasChanged();
        }
    }

    private void OnGaugeChanged(int lineNo, Microsoft.AspNetCore.Components.ChangeEventArgs e)
        => message = sheet.SetDraft(lineNo, e.Value?.ToString());

    private string RowClass(PosInventoryCountLineView line)
    {
        List<string> classes = [];
        if (line.LineNo == lastScannedLine)
        {
            classes.Add("scan-hit");
        }

        if (sheet.IsDirty(line.LineNo))
        {
            classes.Add("row-dirty");
        }

        if (line.IsRepeatVariance && sheet.Variance(line) is { } variance && variance != 0m)
        {
            classes.Add("row-repeat");
        }

        return classes.Count == 0 ? string.Empty : string.Join(' ', classes);
    }

    private static string VarianceTone(decimal? variance)
        => variance is null || variance == 0m ? "sheet-number" : variance > 0m ? "sheet-gain" : "sheet-loss";

    private static string Quantity(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("C", Peso);

    private Task SaveCountsAsync()
        => RunCommandAsync(
            token => Api.RecordInventoryCountLinesAsync(CountId, sheet.CountedRequests(), token),
            "Counts saved.");

    private Task SubmitAsync()
        => RunCommandAsync(async token =>
        {
            if (sheet.HasUnsavedChanges)
            {
                ApiResult<PosReference> saved = await Api.RecordInventoryCountLinesAsync(
                    CountId, sheet.CountedRequests(), token);
                if (!saved.IsSuccess)
                {
                    return saved;
                }
            }

            return await Api.SubmitInventoryCountAsync(CountId, token);
        }, "Count submitted for approval.");

    private async Task ApproveAsync()
    {
        approving = false;
        await RunCommandAsync(token => Api.ApproveInventoryCountAsync(CountId, token), "Variance posted to stock.");
    }

    private async Task RejectAsync()
    {
        string? reason = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();
        rejecting = false;
        await RunCommandAsync(token => Api.RejectInventoryCountAsync(CountId, reason, token), "Count sent back for recounting.");
    }

    private async Task CancelCountAsync()
    {
        string? reason = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();
        cancelling = false;
        await RunCommandAsync(token => Api.CancelInventoryCountAsync(CountId, reason, token), "Count cancelled.");
    }

    private async Task RunCommandAsync(Func<CancellationToken, Task<ApiResult<PosReference>>> command, string successMessage)
    {
        if (detail is null)
        {
            return;
        }

        busy = true;
        message = null;
        try
        {
            ApiResult<PosReference> response = await command(CancellationToken.None);
            if (!response.IsSuccess || response.Value is null)
            {
                message = response.Error;
                return;
            }

            detail = null;
            await LoadDetailAsync();
            if (detail is not null)
            {
                message = successMessage;
            }
        }
        catch (OperationCanceledException)
        {
            // The page is gone.
        }
        finally
        {
            busy = false;
        }
    }

    private void ToggleReject()
    {
        rejecting = !rejecting;
        cancelling = false;
        approving = false;
        reasonText = string.Empty;
    }

    private void ToggleCancel()
    {
        cancelling = !cancelling;
        rejecting = false;
        approving = false;
        reasonText = string.Empty;
    }

    private void ToggleApprove()
    {
        approving = !approving;
        cancelling = false;
        rejecting = false;
    }

    private void DismissPrompt()
    {
        cancelling = false;
        rejecting = false;
        approving = false;
    }

    private string StoreName(Guid locationId)
        => locations.FirstOrDefault(location => location.Id == locationId)?.Name ?? "store";

    private (string Sku, string Name) Describe(Guid productId)
        => productNames.TryGetValue(productId, out (string Sku, string Name) product)
            ? product
            : (string.Empty, string.Empty);

    private string ProductDisplayName(Guid productId)
        => productNames.TryGetValue(productId, out (string Sku, string Name) product)
            ? product.Name
            : $"Product {ShortId(productId)}";

    private string? ProductSku(Guid productId)
        => productNames.TryGetValue(productId, out (string Sku, string Name) product) && !string.IsNullOrWhiteSpace(product.Sku)
            ? product.Sku
            : null;

    private static string ShortId(Guid value) => value.ToString("D", CultureInfo.InvariantCulture)[..8];

    private static string SplitWords(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $" {char.ToLowerInvariant(character)}"
                : character.ToString()));
    }

    private static string StatusChipClass(string status) => status switch
    {
        "Counting" => "status-chip sc-counting",
        "PendingApproval" => "status-chip sc-pending-approval",
        "Posted" => "status-chip sc-posted",
        "Cancelled" => "status-chip sc-cancelled",
        _ => "status-chip",
    };
}
