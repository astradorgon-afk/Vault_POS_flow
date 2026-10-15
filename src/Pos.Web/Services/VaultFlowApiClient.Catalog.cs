namespace Pos.Web.Services;

/// <summary>
/// Catalogue administration: the product master records, and the barcodes that
/// translate a scanned number into one of them.
/// </summary>
public sealed partial class VaultFlowApiClient
{
    private const string CatalogProducts = "/api/v1/catalog/products";

    /// <summary>Searches the catalogue by name, SKU or barcode.</summary>
    /// <param name="query">Free text; the server matches all three.</param>
    /// <param name="includeInactive">Whether discontinued products are listed.</param>
    /// <param name="offset">How many rows to skip.</param>
    /// <param name="limit">How many rows to take, capped at 200 by the server.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The matching products.</returns>
    public Task<ApiResult<List<PosCatalogProduct>>> GetCatalogProductsAsync(
        string? query, bool includeInactive, int offset, int limit, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant(
            $"{CatalogProducts}?offset={offset}&limit={limit}&includeInactive={(includeInactive ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Uri.EscapeDataString(query.Trim())}";
        }

        return GetAsync<List<PosCatalogProduct>>(path, cancellationToken);
    }

    /// <summary>
    /// Searches the catalogue and reports how many rows match in total, so a
    /// pager can say which page of how many is on show.
    /// </summary>
    /// <remarks>
    /// The total arrives in the <c>X-Total-Count</c> header rather than the
    /// body, which stays the plain array that the POS client and the offline
    /// snapshot already read. A response without the header reports the rows
    /// it did return, which pages correctly until the last page.
    /// </remarks>
    public async Task<ApiResult<PosCatalogPage>> GetCatalogProductsPageAsync(
        string? query, bool includeInactive, int offset, int limit, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant(
            $"{CatalogProducts}?offset={offset}&limit={limit}&includeInactive={(includeInactive ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Uri.EscapeDataString(query.Trim())}";
        }

        (ApiResult<List<PosCatalogProduct>> result, string? totalHeader) =
            await GetWithHeaderAsync<List<PosCatalogProduct>>(path, "X-Total-Count", cancellationToken)
                .ConfigureAwait(false);

        if (result is not { IsSuccess: true, Value: { } items })
        {
            return ApiResult<PosCatalogPage>.Failure(result.Error ?? "The catalogue could not be read.", result.ErrorCode);
        }

        int total = int.TryParse(totalHeader, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int reported)
            ? reported
            : offset + items.Count;

        return ApiResult<PosCatalogPage>.Success(new PosCatalogPage(items, total, offset, limit));
    }

    /// <summary>Gets one product master record.</summary>
    public Task<ApiResult<PosCatalogProduct>> GetCatalogProductAsync(Guid productId, CancellationToken cancellationToken)
        => GetAsync<PosCatalogProduct>(
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}"), cancellationToken);

    /// <summary>Creates a product master record.</summary>
    public Task<ApiResult<PosReference>> CreateProductAsync(PosCreateProduct product, CancellationToken cancellationToken)
        => PostAsync<PosReference>(CatalogProducts, product, cancellationToken);

    /// <summary>Lists a product's barcodes, retired codes included.</summary>
    public Task<ApiResult<List<PosProductBarcode>>> GetProductBarcodesAsync(
        Guid productId, CancellationToken cancellationToken)
        => GetAsync<List<PosProductBarcode>>(
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes"), cancellationToken);

    /// <summary>
    /// Attaches a barcode to a product, which is what makes a scanned number
    /// resolve to it everywhere else in the system.
    /// </summary>
    /// <param name="productId">The product.</param>
    /// <param name="barcode">The code and how it scans.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <param name="changeReason">
    /// Why the change is needed. Required of anyone who cannot approve their own
    /// catalogue changes; their request is held for the owner with this against it.
    /// </param>
    public Task<ApiResult<PosReference>> AddProductBarcodeAsync(
        Guid productId, PosAddProductBarcode barcode, CancellationToken cancellationToken, string? changeReason = null)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes"),
            barcode,
            cancellationToken,
            ChangeReasonHeader(changeReason));

    /// <summary>
    /// Retires a barcode. The row stays, so older documents that scanned it
    /// still resolve and the value is never reissued to another product.
    /// </summary>
    public Task<ApiResult<string>> RetireProductBarcodeAsync(
        Guid productId, string barcode, string? reason, CancellationToken cancellationToken, string? changeReason = null)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes/{Uri.EscapeDataString(barcode)}/retire"),
            new PosRetireProductBarcode(reason),
            cancellationToken,
            ChangeReasonHeader(changeReason ?? reason));

    /// <summary>Makes an active barcode the product's primary code.</summary>
    public Task<ApiResult<string>> SetPrimaryProductBarcodeAsync(
        Guid productId, string barcode, CancellationToken cancellationToken, string? changeReason = null)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes/{Uri.EscapeDataString(barcode)}/primary"),
            null,
            cancellationToken,
            ChangeReasonHeader(changeReason));

    private const string ChangeRequests = "/api/v1/catalog/change-requests";

    private static Dictionary<string, string>? ChangeReasonHeader(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? null : new Dictionary<string, string> { ["X-Change-Reason"] = reason };

    /// <summary>Lists catalogue changes held for approval.</summary>
    /// <param name="pendingOnly">True for only those still awaiting a decision.</param>
    /// <param name="productId">A product to filter by, or null for all.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The requests, newest first.</returns>
    public Task<ApiResult<List<PosProductChangeRequest>>> GetProductChangeRequestsAsync(
        bool pendingOnly, Guid? productId, CancellationToken cancellationToken)
        => GetAsync<List<PosProductChangeRequest>>(
            FormattableString.Invariant(
                $"{ChangeRequests}?pendingOnly={(pendingOnly ? "true" : "false")}{(productId is { } id ? $"&productId={id:D}" : string.Empty)}"),
            cancellationToken);

    /// <summary>Approves a held change, which applies it.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="note">An optional note.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The request, or why it was refused.</returns>
    public Task<ApiResult<PosReference>> ApproveProductChangeAsync(
        Guid requestId, string? note, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"{ChangeRequests}/{requestId:D}/approve"),
            new PosProductChangeDecision(note),
            cancellationToken);

    /// <summary>Refuses a held change.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="note">Why; required.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The request, or why it was refused.</returns>
    public Task<ApiResult<PosReference>> RejectProductChangeAsync(
        Guid requestId, string? note, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"{ChangeRequests}/{requestId:D}/reject"),
            new PosProductChangeDecision(note),
            cancellationToken);

    /// <summary>Takes back a change the signed-in user raised.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>The request, or why it was refused.</returns>
    public Task<ApiResult<string>> WithdrawProductChangeAsync(Guid requestId, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"{ChangeRequests}/{requestId:D}/withdraw"),
            null,
            cancellationToken);

    /// <summary>Lists product brands.</summary>
    public Task<ApiResult<List<PosBrand>>> GetBrandsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosBrand>>("/api/v1/catalog/brands", cancellationToken);
}
