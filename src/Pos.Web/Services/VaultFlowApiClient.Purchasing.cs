namespace Pos.Web.Services;

/// <summary>Purchase orders and goods receiving.</summary>
public sealed partial class VaultFlowApiClient
{
    private const string Orders = "/api/v1/purchasing/orders";

    /// <summary>Lists purchase orders, newest first; head office returns at most 200.</summary>
    public Task<ApiResult<List<PosPurchaseOrderSummary>>> GetPurchaseOrdersAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosPurchaseOrderSummary>>($"{Orders}?limit=200", cancellationToken);

    /// <summary>Gets one purchase order with its lines and decisions.</summary>
    public Task<ApiResult<PosPurchaseOrder>> GetPurchaseOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => GetAsync<PosPurchaseOrder>(OrderPath(orderId), cancellationToken);

    /// <summary>Creates a purchase order as a draft.</summary>
    public Task<ApiResult<PosReference>> CreatePurchaseOrderAsync(PosCreatePurchaseOrder body, CancellationToken cancellationToken)
        => PostAsync<PosReference>(Orders, body, cancellationToken);

    /// <summary>Sends a draft for approval.</summary>
    public Task<ApiResult<PosReference>> SubmitPurchaseOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "submit"), new { }, cancellationToken);

    /// <summary>Approves an order awaiting approval.</summary>
    public Task<ApiResult<PosReference>> ApprovePurchaseOrderAsync(Guid orderId, string? notes, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "approve"), new { notes }, cancellationToken);

    /// <summary>Rejects an order awaiting approval.</summary>
    public Task<ApiResult<PosReference>> RejectPurchaseOrderAsync(Guid orderId, string? notes, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "reject"), new { notes }, cancellationToken);

    /// <summary>Records that an approved order has gone to the supplier.</summary>
    public Task<ApiResult<PosReference>> SendPurchaseOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "send"), new { }, cancellationToken);

    /// <summary>Cancels an order that has not been fully received.</summary>
    public Task<ApiResult<PosReference>> CancelPurchaseOrderAsync(Guid orderId, string reason, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "cancel"), new { reason }, cancellationToken);

    /// <summary>Closes an order, with a reason when it was not fully received.</summary>
    public Task<ApiResult<PosReference>> ClosePurchaseOrderAsync(Guid orderId, string? reason, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "close"), new { reason }, cancellationToken);

    /// <summary>Discards a draft that was never submitted.</summary>
    public async Task<ApiResult<bool>> WithdrawPurchaseOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, OrderPath(orderId));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return ApiResult<bool>.Success(true);
        }

        ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        return ApiResult<bool>.Failure(problem?.Detail ?? "VaultFlow could not discard that draft.", problem?.ErrorCode);
    }

    /// <summary>Records one delivery against an ordered purchase order.</summary>
    public Task<ApiResult<PosReference>> CreateGoodsReceiptAsync(Guid orderId, PosCreateGoodsReceipt body, CancellationToken cancellationToken)
        => PostAsync<PosReference>(OrderPath(orderId, "receipts"), body, cancellationToken);

    /// <summary>Lists the deliveries recorded against an order.</summary>
    public Task<ApiResult<List<PosGoodsReceiptSummary>>> GetGoodsReceiptsAsync(Guid orderId, CancellationToken cancellationToken)
        => GetAsync<List<PosGoodsReceiptSummary>>(OrderPath(orderId, "receipts"), cancellationToken);

    /// <summary>Gets one delivery with its lines and discrepancies.</summary>
    public Task<ApiResult<PosGoodsReceipt>> GetGoodsReceiptAsync(Guid orderId, Guid receiptId, CancellationToken cancellationToken)
        => GetAsync<PosGoodsReceipt>(OrderPath(orderId, FormattableString.Invariant($"receipts/{receiptId:D}")), cancellationToken);

    /// <summary>Lists the suppliers the signed-in user may buy from.</summary>
    public Task<ApiResult<List<PosSupplier>>> GetSuppliersAsync(CancellationToken cancellationToken, bool includeInactive = false)
        => GetAsync<List<PosSupplier>>(
            includeInactive ? "/api/v1/catalog/suppliers?includeInactive=true" : "/api/v1/catalog/suppliers",
            cancellationToken);

    /// <summary>Searches the catalogue for products to order.</summary>
    public Task<ApiResult<List<PosOrderableProduct>>> GetOrderableProductsAsync(
        string? query, int offset, int limit, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"/api/v1/catalog/products?offset={offset}&limit={limit}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Uri.EscapeDataString(query.Trim())}";
        }

        return GetAsync<List<PosOrderableProduct>>(path, cancellationToken);
    }

    /// <summary>Lists the units of measure order lines are counted in.</summary>
    public Task<ApiResult<List<PosUnitOfMeasure>>> GetUnitsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosUnitOfMeasure>>("/api/v1/catalog/units", cancellationToken);

    /// <summary>
    /// Finds the product a scanned code belongs to at head office, trying its
    /// equivalent retail forms (UPC-A and EAN-13).
    /// </summary>
    /// <param name="raw">What was scanned.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The product's id, SKU and name, or the reason none was found.</returns>
    public async Task<ApiResult<PosScannedProduct>> FindProductByScanAsync(string raw, CancellationToken cancellationToken)
    {
        string code = Pos.Shared.Scanning.BarcodeText.Clean(raw);
        ApiResult<PosScannedProduct> last = ApiResult<PosScannedProduct>.Failure("Nothing was scanned.");
        foreach (string variant in Pos.Shared.Scanning.BarcodeText.LookupVariants(code))
        {
            last = await GetAsync<PosScannedProduct>(
                $"/api/v1/catalog/products/by-barcode/{Uri.EscapeDataString(variant)}", cancellationToken).ConfigureAwait(false);
            if (last.IsSuccess || last.ErrorCode is not ("catalog.barcode_unknown" or "catalog.barcode_retired"))
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>Gets each store's best- and worst-selling products over a period.</summary>
    /// <param name="from">The first business date.</param>
    /// <param name="to">The last business date.</param>
    /// <param name="byUnits">True to rank by units sold, false by revenue.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The report.</returns>
    public Task<ApiResult<PosProductPerformanceReport>> GetProductPerformanceAsync(
        DateOnly from, DateOnly to, bool byUnits, CancellationToken cancellationToken)
        => GetAsync<PosProductPerformanceReport>(
            FormattableString.Invariant(
                $"/api/v1/dashboard/product-performance?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&rankBy={(byUnits ? "units" : "revenue")}&limit=10"),
            cancellationToken);

    private static string OrderPath(Guid orderId, string? action = null)
        => action is null
            ? FormattableString.Invariant($"{Orders}/{orderId:D}")
            : FormattableString.Invariant($"{Orders}/{orderId:D}/{action}");
}
