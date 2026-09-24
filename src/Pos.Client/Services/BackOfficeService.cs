using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Infrastructure.Offline;

namespace Pos.Client.Services;

/// <summary>
/// The office console's calls to head office. It mirrors the interactive web
/// console's method surface so the ported pages read the same, but it drives
/// the register's own HTTP client and session: the bearer token and device
/// header come from <see cref="RegisterService"/> and <see cref="DeviceSession"/>.
/// </summary>
/// <param name="http">The shared HTTP client.</param>
/// <param name="register">The signed-in register session.</param>
/// <param name="session">The device execution session.</param>
public sealed class BackOfficeService(HttpClient http, RegisterService register, DeviceSession session)
{
    private const string DeviceHeader = "X-Device-Id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the signed-in session, or <see langword="null"/> when no one is signed in.</summary>
    public RegisterUser? Current => register.Current;

    /// <summary>Gets whether the signed-in user holds the given permission.</summary>
    public bool HasPermission(string code)
        => register.Current?.Session.User.Permissions.Contains(code, StringComparer.Ordinal) == true;

    /// <summary>Gets the signed-in user's identifier.</summary>
    public Guid UserId => register.Current is { } user ? user.UserId.Value : Guid.Empty;

    /// <summary>Gets the sign-in display name.</summary>
    public string DisplayName => register.Current?.DisplayName ?? string.Empty;

    /// <summary>Gets this register's store, when it has been set up.</summary>
    public Guid? DeviceLocationId => session.LocationId is { } location ? location.Value : null;

    /// <summary>Gets the locations available to the office, after the first load.</summary>
    public IReadOnlyList<PosLocation> Locations { get; private set; } = [];

    /// <summary>Gets the active stores the signed-in user can work against, after the first load.</summary>
    public IReadOnlyList<PosLocation> Stores => Locations
        .Where(location => location.Kind == StoreKind && location.IsActive)
        .OrderBy(location => location.Code, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Gets the default location: the register's store, then the first active store.</summary>
    public Guid? DefaultLocationId
        => DeviceLocationId is { } deviceStore && Stores.Any(store => store.Id == deviceStore)
            ? deviceStore
            : Stores.Count > 0 ? Stores[0].Id : null;

    private const int StoreKind = 1;

    // ---- Locations & catalogue ----

    /// <summary>Loads the organization's physical locations once; later calls reuse the cached list.</summary>
    public async Task<ApiResult<List<PosLocation>>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        if (Locations.Count > 0)
        {
            return ApiResult<List<PosLocation>>.Success([.. Locations]);
        }

        ApiResult<List<PosLocation>> result =
            await GetAsync<List<PosLocation>>("/api/v1/locations", cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Locations = result.Value ?? [];
        }

        return result;
    }

    /// <summary>Lists product categories for scoping category and cycle counts.</summary>
    public Task<ApiResult<List<PosCategory>>> GetCategoriesAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosCategory>>("/api/v1/catalog/categories", cancellationToken);

    /// <summary>Lists product master rows by name, SKU or barcode, paged.</summary>
    public Task<ApiResult<List<PosProductSummary>>> GetProductsAsync(
        string? query, int offset, int limit, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"/api/v1/catalog/products?offset={offset}&limit={limit}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Uri.EscapeDataString(query)}";
        }

        return GetAsync<List<PosProductSummary>>(path, cancellationToken);
    }

    /// <summary>Lists the suppliers the signed-in user may purchase from.</summary>
    public Task<ApiResult<List<SupplierSummary>>> GetSuppliersAsync(CancellationToken cancellationToken)
        => GetAsync<List<SupplierSummary>>("/api/v1/catalog/suppliers", cancellationToken);

    // ---- Inventory counts ----

    /// <summary>Lists counts at the caller's locations, newest first.</summary>
    public Task<ApiResult<List<PosInventoryCountSummary>>> GetInventoryCountsAsync(
        Guid? locationId, string? statusName, CancellationToken cancellationToken)
    {
        string path = "/api/v1/inventory/counts?offset=0&limit=200";
        if (locationId is { } location)
        {
            path += FormattableString.Invariant($"&locationId={location:D}");
        }

        if (!string.IsNullOrWhiteSpace(statusName))
        {
            path += $"&status={Uri.EscapeDataString(statusName)}";
        }

        return GetAsync<List<PosInventoryCountSummary>>(path, cancellationToken);
    }

    /// <summary>Gets one count with its recorded lines.</summary>
    public Task<ApiResult<PosInventoryCountDetail>> GetInventoryCountAsync(
        Guid countId, CancellationToken cancellationToken)
        => GetAsync<PosInventoryCountDetail>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}"),
            cancellationToken);

    /// <summary>Opens a count and takes its sheet from the ledger.</summary>
    public Task<ApiResult<PosReference>> OpenInventoryCountAsync(
        Guid locationId,
        int kind,
        IReadOnlyList<Guid>? categoryIds,
        string? note,
        CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            "/api/v1/inventory/counts",
            new PosOpenInventoryCountRequest(locationId, kind, categoryIds, note),
            cancellationToken);

    /// <summary>Records counted quantities on an open count sheet.</summary>
    public Task<ApiResult<PosReference>> RecordInventoryCountLinesAsync(
        Guid countId, IReadOnlyList<PosCountLineRequest> lines, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}/lines"),
            new PosRecordCountLinesRequest(lines),
            cancellationToken);

    /// <summary>Submits a fully counted sheet for approval.</summary>
    public Task<ApiResult<PosReference>> SubmitInventoryCountAsync(
        Guid countId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}/submit"),
            new { },
            cancellationToken);

    /// <summary>Approves a count and posts its variance to the ledger.</summary>
    public Task<ApiResult<PosReference>> ApproveInventoryCountAsync(
        Guid countId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}/approve"),
            new { },
            cancellationToken);

    /// <summary>Sends a count back for recounting with a reason.</summary>
    public Task<ApiResult<PosReference>> RejectInventoryCountAsync(
        Guid countId, string? reason, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}/reject"),
            new PosInventoryControlReasonRequest(reason),
            cancellationToken);

    /// <summary>Abandons a count without posting, with a reason.</summary>
    public Task<ApiResult<PosReference>> CancelInventoryCountAsync(
        Guid countId, string? reason, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/inventory/counts/{countId:D}/cancel"),
            new PosInventoryControlReasonRequest(reason),
            cancellationToken);

    // ---- Transfers ----

    /// <summary>Lists transfers touching the caller's locations, newest first.</summary>
    public Task<ApiResult<List<PosTransferSummary>>> GetTransfersAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosTransferSummary>>("/api/v1/transfers", cancellationToken);

    /// <summary>Gets one transfer with its lines, allocations and arrival state.</summary>
    public Task<ApiResult<PosTransferDetail>> GetTransferAsync(
        Guid transferId, CancellationToken cancellationToken)
        => GetAsync<PosTransferDetail>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}"),
            cancellationToken);

    /// <summary>Gets a transfer's custody timeline.</summary>
    public Task<ApiResult<List<PosTransferCustodyEventSummary>>> GetTransferCustodyAsync(
        Guid transferId, CancellationToken cancellationToken)
        => GetAsync<List<PosTransferCustodyEventSummary>>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/custody"),
            cancellationToken);

    /// <summary>Raises a draft transfer request.</summary>
    public Task<ApiResult<PosReference>> CreateTransferAsync(
        PosCreateTransferRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/transfers", request, cancellationToken);

    /// <summary>Submits a draft transfer for review.</summary>
    public Task<ApiResult<PosReference>> SubmitTransferAsync(
        Guid transferId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/submit"),
            new { },
            cancellationToken);

    /// <summary>Approves a reviewed transfer, optionally amending quantities.</summary>
    public Task<ApiResult<PosReference>> ApproveTransferAsync(
        Guid transferId, PosApproveTransferRequest? request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/approve"),
            request ?? new PosApproveTransferRequest(null, null),
            cancellationToken);

    /// <summary>Rejects a transfer and sends it back to draft.</summary>
    public Task<ApiResult<PosReference>> RejectTransferAsync(
        Guid transferId, string? note, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/reject"),
            new PosTransferReasonRequest(note),
            cancellationToken);

    /// <summary>Records what was picked at the source.</summary>
    public Task<ApiResult<PosReference>> PickTransferAsync(
        Guid transferId, PosPickTransferRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/pick"),
            request,
            cancellationToken);

    /// <summary>Marks picking complete; the transfer may now be dispatched.</summary>
    public Task<ApiResult<PosReference>> ReadyTransferAsync(
        Guid transferId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/ready"),
            new { },
            cancellationToken);

    /// <summary>Dispatches a ready transfer and posts the ledger.</summary>
    public Task<ApiResult<PosReference>> DispatchTransferAsync(
        Guid transferId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/dispatch"),
            new { },
            cancellationToken);

    /// <summary>Cancels a dispatched transfer and reverses the ledger.</summary>
    public Task<ApiResult<PosReference>> CancelTransferDispatchAsync(
        Guid transferId, string reason, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/cancel-dispatch"),
            new PosCancelTransferDispatchRequest(reason),
            cancellationToken);

    /// <summary>Records what arrived at the destination and posts the ledger.</summary>
    public Task<ApiResult<PosReference>> ReceiveTransferAsync(
        Guid transferId, PosReceiveTransferRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/receive"),
            request,
            cancellationToken);

    /// <summary>Verifies and closes a fully accounted transfer.</summary>
    public Task<ApiResult<PosReference>> VerifyTransferAsync(
        Guid transferId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/verify"),
            new { },
            cancellationToken);

    /// <summary>Ratifies or rejects a pending emergency transfer.</summary>
    public Task<ApiResult<PosReference>> ReviewCentralTransferAsync(
        Guid transferId, bool approve, string? note, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/transfers/{transferId:D}/central-review"),
            new { approve, note },
            cancellationToken);

    // ---- Movement chain & reporting ----

    /// <summary>Gets the authorization-scoped movement chain for a document.</summary>
    public Task<ApiResult<PosInventoryTimeline>> GetInventoryTimelineAsync(
        string documentType, Guid documentId, CancellationToken cancellationToken)
        => GetAsync<PosInventoryTimeline>(
            FormattableString.Invariant(
                $"/api/v1/inventory/timeline/{Uri.EscapeDataString(documentType)}/{documentId:D}"),
            cancellationToken);

    /// <summary>Gets scoped inventory availability and threshold totals.</summary>
    public Task<ApiResult<PosInventoryOverview>> GetInventoryOverviewAsync(
        CancellationToken cancellationToken)
        => GetAsync<PosInventoryOverview>(
            "/api/v1/dashboard/inventory-overview", cancellationToken);

    /// <summary>Gets products with repeated posted count variances in the last window.</summary>
    public Task<ApiResult<List<PosRepeatVariance>>> GetRepeatVariancesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => GetAsync<List<PosRepeatVariance>>(
            FormattableString.Invariant($"/api/v1/inventory/counts/repeat-variances?from={from:O}&to={to:O}"),
            cancellationToken);

    /// <summary>Gets products refused by the stock policy in the last window.</summary>
    public Task<ApiResult<List<PosNegativeStockSummary>>> GetNegativeStockSummaryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => GetAsync<List<PosNegativeStockSummary>>(
            FormattableString.Invariant(
                $"/api/v1/inventory/exceptions/negative-attempts/summary?from={from:O}&to={to:O}"),
            cancellationToken);

    /// <summary>Gets the daily close-of-trade report for a store.</summary>
    public Task<ApiResult<PosDailySalesReport>> GetDailySalesReportAsync(
        Guid locationId, DateOnly businessDate, CancellationToken cancellationToken)
        => GetAsync<PosDailySalesReport>(
            FormattableString.Invariant(
                $"/api/v1/reports/daily-sales?locationId={locationId:D}&date={businessDate:yyyy-MM-dd}"),
            cancellationToken);

    // ---- Purchase orders & goods receiving ----

    /// <summary>Lists purchase orders with an optional status filter, newest first.</summary>
    public Task<ApiResult<List<PurchaseOrderSummary>>> GetPurchaseOrdersAsync(
        string? status, CancellationToken cancellationToken)
    {
        string path = "/api/v1/purchasing/orders";
        if (!string.IsNullOrWhiteSpace(status))
        {
            path += $"?status={Uri.EscapeDataString(status)}";
        }

        return GetAsync<List<PurchaseOrderSummary>>(path, cancellationToken);
    }

    /// <summary>Gets one purchase order with its lines and decisions.</summary>
    public Task<ApiResult<PurchaseOrderDetail>> GetPurchaseOrderAsync(
        Guid orderId, CancellationToken cancellationToken)
        => GetAsync<PurchaseOrderDetail>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}"),
            cancellationToken);

    /// <summary>Records a goods receipt against an ordered purchase order.</summary>
    public Task<ApiResult<PosReference>> CreateGoodsReceiptAsync(
        Guid orderId, CreateGoodsReceiptBody body, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/receipts"),
            body,
            cancellationToken);

    /// <summary>Lists the goods receipts recorded against a purchase order.</summary>
    public Task<ApiResult<List<GoodsReceiptSummary>>> ListGoodsReceiptsAsync(
        Guid orderId, CancellationToken cancellationToken)
        => GetAsync<List<GoodsReceiptSummary>>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/receipts"),
            cancellationToken);

    /// <summary>Gets one goods receipt with its lines and discrepancies.</summary>
    public Task<ApiResult<GoodsReceiptDetail>> GetGoodsReceiptAsync(
        Guid orderId, Guid receiptId, CancellationToken cancellationToken)
        => GetAsync<GoodsReceiptDetail>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/receipts/{receiptId:D}"),
            cancellationToken);

    /// <summary>Resolves a scanned barcode to its product on the receiving sheet.</summary>
    public Task<ApiResult<PosScannedProduct>> GetProductByBarcodeAsync(
        string barcode, CancellationToken cancellationToken)
        => GetAsync<PosScannedProduct>(
            FormattableString.Invariant($"/api/v1/catalog/products/by-barcode/{Uri.EscapeDataString(barcode.Trim())}"),
            cancellationToken);

    /// <summary>Raises a quarantine incident for goods found while receiving.</summary>
    public Task<ApiResult<PosReference>> CreateQuarantineIncidentAsync(
        Guid locationId,
        IReadOnlyList<CreateQuarantineLineBody> lines,
        string? note,
        CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            "/api/v1/quarantine",
            new CreateQuarantineIncidentBody(locationId, lines, note),
            cancellationToken);

    /// <summary>Lists product master rows for the purchase order composer, paged.</summary>
    public Task<ApiResult<List<PosOrderableProduct>>> GetOrderableProductsAsync(
        string? query, int offset, int limit, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"/api/v1/catalog/products?offset={offset}&limit={limit}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Uri.EscapeDataString(query)}";
        }

        return GetAsync<List<PosOrderableProduct>>(path, cancellationToken);
    }

    /// <summary>Lists the units of measure available to order lines.</summary>
    public Task<ApiResult<List<PosUnitOfMeasure>>> GetUnitsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosUnitOfMeasure>>("/api/v1/catalog/units", cancellationToken);

    /// <summary>Creates a purchase order as a draft.</summary>
    public Task<ApiResult<PosReference>> CreatePurchaseOrderAsync(
        CreatePurchaseOrderBody body, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/purchasing/orders", body, cancellationToken);

    /// <summary>Submits a purchase order draft for approval.</summary>
    public Task<ApiResult<PosReference>> SubmitPurchaseOrderAsync(
        Guid orderId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/submit"),
            new { },
            cancellationToken);

    /// <summary>Approves a submitted purchase order.</summary>
    public Task<ApiResult<PosReference>> ApprovePurchaseOrderAsync(
        Guid orderId, string? notes, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/approve"),
            new PurchaseOrderDecisionBody(notes),
            cancellationToken);

    /// <summary>Rejects a submitted purchase order.</summary>
    public Task<ApiResult<PosReference>> RejectPurchaseOrderAsync(
        Guid orderId, string? notes, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/reject"),
            new PurchaseOrderDecisionBody(notes),
            cancellationToken);

    /// <summary>Sends an approved purchase order to the supplier, making it ordered.</summary>
    public Task<ApiResult<PosReference>> SendPurchaseOrderAsync(
        Guid orderId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/purchasing/orders/{orderId:D}/send"),
            new { },
            cancellationToken);

    // ---- Transport ----

    /// <summary>Gets and deserializes an authenticated API resource.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage? response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return ApiResult<T>.Failure("Head office could not be reached. Check the address and your connection.");
        }

        using (response)
        {
            return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Posts a JSON body to the API and reads the response.</summary>
    public async Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: Json);
        using HttpResponseMessage? response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return ApiResult<T>.Failure("Head office could not be reached. Check the address and your connection.");
        }

        using (response)
        {
            return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, new Uri(register.Server, path.TrimStart('/')));
        if (register.Current is { Session: { AccessToken: { } token } })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (session.DeviceId is { } device)
        {
            request.Headers.Add(DeviceHeader, device.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        }

        return request;
    }

    private async Task<HttpResponseMessage?> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<ApiResult<T>> ReadResultAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            Problem? problem = null;
            try
            {
                problem = await response.Content
                    .ReadFromJsonAsync<Problem>(Json, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Not a problem document; fall back to a generic message.
            }

            return ApiResult<T>.Failure(
                problem?.Detail ?? "Head office refused the request. Try again, and report if it persists.",
                problem?.ErrorCode);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent
            || response.Content.Headers.ContentLength == 0)
        {
            return ApiResult<T>.Success(default!);
        }

        T? value;
        try
        {
            value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure("Head office sent an unreadable answer.");
        }

        return value is null
            ? ApiResult<T>.Failure("Head office sent an empty answer.")
            : ApiResult<T>.Success(value);
    }

    private sealed record Problem(string? Detail, string? ErrorCode);
}