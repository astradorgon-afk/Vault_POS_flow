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

    /// <summary>Lines shown at once. A full physical runs to hundreds; a counter works a screen at a time.</summary>
    private const int PageSize = 50;

    /// <summary>
    /// Lines sent in one save. Head office refuses a request carrying more than
    /// its own cap of 500, so a sheet longer than that goes up in instalments
    /// rather than failing whole. Keep this at or under that cap — it mirrors
    /// InventoryControlRules.MaxLines, which the server owns.
    /// </summary>
    private const int SaveBatchSize = 500;

    private readonly List<PosLocation> locations = [];
    private readonly Dictionary<Guid, (string Sku, string Name)> productNames = [];
    private readonly Dictionary<Guid, Guid> productCategories = [];
    private readonly List<PosCategory> categories = [];
    private readonly Dictionary<int, string> lineErrors = [];
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
    private Guid? categoryId;
    private int page;
    private string? message;
    private bool messageIsError;

    private bool CanViewInventory => Session.HasPermission("inventory.view");
    private bool CanCount => Session.HasPermission("inventory.count");
    private bool CanApproveCount => Session.HasPermission("inventory.count.approve");

    private bool IsCounting => detail?.Count.Status == "Counting";
    private bool IsPendingApproval => detail?.Count.Status == "PendingApproval";

    private double PercentCounted => sheet.TotalLines == 0 ? 0d : sheet.CountedLines * 100d / sheet.TotalLines;

    /// <summary>Every line matching the search, the filter chips and the chosen category.</summary>
    private List<PosInventoryCountLineView> FilteredLines()
        => [.. sheet.Visible(filter, search, Describe)
            .Where(line => categoryId is not { } wanted
                || (productCategories.TryGetValue(line.ProductId, out Guid actual) && actual == wanted))];

    /// <summary>The categories actually represented on this sheet, named and sorted.</summary>
    private List<(Guid Id, string Name)> SheetCategories()
    {
        HashSet<Guid> onSheet = [.. sheet.Lines
            .Select(line => productCategories.TryGetValue(line.ProductId, out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)];

        return [.. categories
            .Where(category => onSheet.Contains(category.Id))
            .Select(category => (category.Id, category.Name))
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static int PageCountFor(int rows) => Math.Max(1, (int)Math.Ceiling(rows / (double)PageSize));

    /// <summary>Search, filter and category each reset to the first page; staying on page 9 of a list that now has two reads as an empty sheet.</summary>
    private string SearchText
    {
        get => search;
        set
        {
            search = value;
            page = 0;
        }
    }

    private void SetFilter(CountSheetFilter choice)
    {
        filter = choice;
        page = 0;
    }

    private string CategoryChoice
    {
        get => categoryId?.ToString() ?? string.Empty;
        set
        {
            categoryId = Guid.TryParse(value, out Guid parsed) ? parsed : null;
            page = 0;
        }
    }

    private void GoToPage(int target, int pageCount) => page = Math.Clamp(target, 0, pageCount - 1);

    /// <summary>Brings the page holding a line into view, so a scan is never counted off-screen.</summary>
    private void ShowPageContaining(int lineNo)
    {
        int index = FilteredLines().FindIndex(line => line.LineNo == lineNo);
        if (index >= 0)
        {
            page = index / PageSize;
        }
    }

    /// <summary>Clears the banner, so a stale failure does not sit over a sheet that has since worked.</summary>
    private void DismissMessage()
    {
        message = null;
        messageIsError = false;
    }

    private void Fail(string text)
    {
        message = text;
        messageIsError = true;
    }

    private void Succeed(string text)
    {
        message = text;
        messageIsError = false;
    }

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
                Fail(response.Error ?? "This count could not be found.");
                return;
            }

            if (!Session.CanUseInventoryLocation(response.Value.Count.LocationId))
            {
                Fail("This count is outside your assigned locations.");
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

        if (wanted.Count == 0)
        {
            return;
        }

        // The catalogue rows carry the category, which the plain product list does
        // not. Where this account may not read the catalogue the names still
        // arrive from the product list and the category filter stays hidden,
        // rather than the sheet failing to name its own lines.
        if (!await LoadNamesFromCatalogAsync(wanted, token))
        {
            await LoadNamesFromProductsAsync(wanted, token);
            return;
        }

        ApiResult<List<PosCategory>> response = await Api.GetCategoriesAsync(token);
        if (response.IsSuccess && response.Value is not null)
        {
            categories.Clear();
            categories.AddRange(response.Value);
        }
    }

    /// <summary>Names the lines from the catalogue, recording each product's category.</summary>
    /// <returns><see langword="true"/> when the catalogue answered.</returns>
    private async Task<bool> LoadNamesFromCatalogAsync(HashSet<Guid> wanted, CancellationToken token)
    {
        bool answered = false;
        for (int offset = 0; offset < 2000 && wanted.Count > 0; offset += 200)
        {
            ApiResult<List<PosCatalogProduct>> response =
                await Api.GetCatalogProductsAsync(null, true, offset, 200, token);
            if (!response.IsSuccess || response.Value is null)
            {
                return answered;
            }

            answered = true;
            foreach (PosCatalogProduct product in response.Value)
            {
                productCategories[product.Id] = product.CategoryId;
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

        return answered;
    }

    private async Task LoadNamesFromProductsAsync(HashSet<Guid> wanted, CancellationToken token)
    {
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

            // A scan against a line the current view hides would otherwise vanish
            // as it is counted, which reads as the scan being lost. The filter,
            // the category and the page all have to give way to the scanned line.
            if (filter == CountSheetFilter.Uncounted)
            {
                filter = CountSheetFilter.All;
            }

            if (categoryId is { } chosen
                && (!productCategories.TryGetValue(line.ProductId, out Guid actual) || actual != chosen))
            {
                categoryId = null;
            }

            ShowPageContaining(line.LineNo);
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
        => RecordDraft(lineNo, e.Value?.ToString());

    /// <summary>
    /// Writes a draft quantity and keeps any complaint beside the line it is about.
    /// </summary>
    /// <remarks>
    /// The banner alone was not enough: on a paged sheet the offending row can be
    /// well off-screen, so the message is pinned under the field as well.
    /// </remarks>
    private void RecordDraft(int lineNo, string? raw)
    {
        string? error = sheet.SetDraft(lineNo, raw);
        if (error is null)
        {
            lineErrors.Remove(lineNo);
            if (messageIsError && lineErrors.Count == 0)
            {
                DismissMessage();
            }

            return;
        }

        lineErrors[lineNo] = error;
        Fail(error);
    }

    private string? LineError(int lineNo)
        => lineErrors.TryGetValue(lineNo, out string? error) ? error : null;

    /// <summary>Moves a line's count by one, the way the stepper buttons do.</summary>
    /// <remarks>
    /// It goes through <see cref="InventoryCountSheet.SetDraft"/> rather than
    /// <see cref="InventoryCountSheet.Increment"/> so the floor at zero and the
    /// rounding stay in one place. Increment is the scan path and may run before
    /// a line has any draft at all.
    /// </remarks>
    private void Step(PosInventoryCountLineView line, decimal delta)
    {
        decimal current = sheet.Quantity(line) ?? 0m;
        RecordDraft(line.LineNo, Math.Max(0m, current + delta).ToString(CultureInfo.InvariantCulture));
        lastScannedLine = null;
    }

    /// <summary>Counts a line at what the ledger says it holds.</summary>
    private void MatchSystem(PosInventoryCountLineView line)
        => RecordDraft(line.LineNo, line.SystemQuantity.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Agrees with the ledger on every line still uncounted.
    /// </summary>
    /// <remarks>
    /// Lines already counted are left alone: this is a shortcut for the long
    /// tail a counter has walked past and found correct, not a way to discard
    /// counts already recorded.
    /// </remarks>
    private void MatchAllToSystem()
    {
        foreach (PosInventoryCountLineView line in sheet.Lines)
        {
            if (sheet.Quantity(line) is null)
            {
                sheet.SetDraft(line.LineNo, line.SystemQuantity.ToString(CultureInfo.InvariantCulture));
            }
        }

        lineErrors.Clear();
        DismissMessage();
    }

    private static string VarianceBand(decimal? value)
        => value is null ? "is-flat" : value > 0m ? "is-up" : value < 0m ? "is-down" : "is-flat";

    private PosInventoryCountLineView? scanLine;
    private string scanCode = string.Empty;

    private void OpenScan(PosInventoryCountLineView line)
    {
        scanLine = line;
        scanCode = string.Empty;
        scanNote = null;
    }

    private void CloseScan()
    {
        scanLine = null;
        scanCode = string.Empty;
    }

    /// <summary>
    /// Counts the code typed in the scan dialog, for a camera that will not open.
    /// </summary>
    /// <remarks>
    /// It runs the scan path rather than trusting the row the dialog was opened
    /// from, so a carton scanned against the wrong line still lands on the
    /// product it actually belongs to.
    /// </remarks>
    private async Task SubmitScanCodeAsync()
    {
        string code = BarcodeText.Clean(scanCode);
        if (code.Length == 0)
        {
            return;
        }

        scanCode = string.Empty;
        await CountScannedAsync(new BarcodeScan(code, ScanSource.Simulated, DateTimeOffset.UtcNow));
    }

    private Task OnScanCodeKeyAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
        => e.Key == "Enter" ? SubmitScanCodeAsync() : Task.CompletedTask;

    private string RowClass(PosInventoryCountLineView line)
    {
        List<string> classes = [];
        if (line.LineNo == lastScannedLine)
        {
            classes.Add("is-hit");
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
            token => SaveLinesAsync(sheet.CountedRequests(), token),
            "Counts saved.",
            "The sheet was not fully saved");

    private Task SubmitAsync()
        => RunCommandAsync(async token =>
        {
            if (sheet.HasUnsavedChanges)
            {
                ApiResult<PosReference> saved = await SaveLinesAsync(sheet.CountedRequests(), token);
                if (!saved.IsSuccess)
                {
                    return saved;
                }
            }

            return await Api.SubmitInventoryCountAsync(CountId, token);
        }, "Count submitted for approval.", "This sheet was not submitted");

    /// <summary>
    /// Saves a sheet of any size, in instalments head office will accept.
    /// </summary>
    /// <param name="lines">Every counted line on the sheet.</param>
    /// <param name="token">Cancels with the page.</param>
    /// <returns>
    /// The last reference written, or the first refusal — told along with how far
    /// the sheet got, because each instalment is recorded on its own and a half
    /// saved sheet is a different thing to recover from than an untouched one.
    /// </returns>
    private async Task<ApiResult<PosReference>> SaveLinesAsync(
        List<PosCountLineRequest> lines, CancellationToken token)
    {
        if (lines.Count == 0)
        {
            return ApiResult<PosReference>.Failure("There is nothing to save yet — count at least one line first.");
        }

        ApiResult<PosReference> result = ApiResult<PosReference>.Failure("No instalment was sent.");

        for (int sent = 0; sent < lines.Count; sent += SaveBatchSize)
        {
            result = await Api.RecordInventoryCountLinesAsync(
                CountId, lines.GetRange(sent, Math.Min(SaveBatchSize, lines.Count - sent)), token);

            if (!result.IsSuccess)
            {
                string reason = string.IsNullOrWhiteSpace(result.Error)
                    ? "Head office refused the request without saying why."
                    : result.Error;

                return ApiResult<PosReference>.Failure(
                    $"{reason} {ProgressNote(sent, lines.Count)}",
                    result.ErrorCode);
            }
        }

        return result;
    }

    /// <summary>Says how much of the sheet reached head office before a refusal.</summary>
    /// <param name="saved">Lines recorded by earlier instalments.</param>
    /// <param name="total">Lines the save set out to write.</param>
    /// <returns>A sentence the counter can act on.</returns>
    private static string ProgressNote(int saved, int total)
        => saved == 0
            ? "Nothing was recorded — the sheet is as it was."
            : $"The first {saved} of {total} lines were recorded; the rest were not. Save again to finish.";

    private async Task ApproveAsync()
    {
        approving = false;
        await RunCommandAsync(
            token => Api.ApproveInventoryCountAsync(CountId, token),
            "Variance posted to stock.",
            "The variance was not posted — stock is unchanged");
    }

    private async Task RejectAsync()
    {
        string? reason = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();
        rejecting = false;
        await RunCommandAsync(
            token => Api.RejectInventoryCountAsync(CountId, reason, token),
            "Count sent back for recounting.",
            "The sheet was not sent back");
    }

    private async Task CancelCountAsync()
    {
        string? reason = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();
        cancelling = false;
        await RunCommandAsync(
            token => Api.CancelInventoryCountAsync(CountId, reason, token),
            "Count cancelled.",
            "The count was not cancelled");
    }

    /// <summary>
    /// Runs one command against the sheet and says plainly what happened.
    /// </summary>
    /// <param name="command">The call to make.</param>
    /// <param name="successMessage">What to say when it worked.</param>
    /// <param name="failureLead">
    /// What did <em>not</em> happen, phrased so the counter knows where the sheet
    /// stands: a bare server string like "Conflict" leaves them guessing whether
    /// their counts survived.
    /// </param>
    private async Task RunCommandAsync(
        Func<CancellationToken, Task<ApiResult<PosReference>>> command,
        string successMessage,
        string failureLead)
    {
        if (detail is null)
        {
            return;
        }

        busy = true;
        DismissMessage();
        try
        {
            ApiResult<PosReference> response = await command(CancellationToken.None);
            if (!response.IsSuccess || response.Value is null)
            {
                Fail(string.IsNullOrWhiteSpace(response.Error)
                    ? $"{failureLead}. Head office refused the request without saying why — try again, and tell an administrator if it keeps happening."
                    : $"{failureLead}: {response.Error}");
                return;
            }

            detail = null;
            await LoadDetailAsync();
            if (detail is not null)
            {
                lineErrors.Clear();
                Succeed(successMessage);
            }
        }
        catch (OperationCanceledException)
        {
            // The page is gone.
        }
        catch (HttpRequestException)
        {
            Fail($"{failureLead}: head office could not be reached. Check the connection and try again — nothing on this sheet has been lost.");
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
