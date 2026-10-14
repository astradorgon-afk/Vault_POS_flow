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
    public Task<ApiResult<PosReference>> AddProductBarcodeAsync(
        Guid productId, PosAddProductBarcode barcode, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes"), barcode, cancellationToken);

    /// <summary>
    /// Retires a barcode. The row stays, so older documents that scanned it
    /// still resolve and the value is never reissued to another product.
    /// </summary>
    public Task<ApiResult<string>> RetireProductBarcodeAsync(
        Guid productId, string barcode, string? reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes/{Uri.EscapeDataString(barcode)}/retire"),
            new PosRetireProductBarcode(reason),
            cancellationToken);

    /// <summary>Makes an active barcode the product's primary code.</summary>
    public Task<ApiResult<string>> SetPrimaryProductBarcodeAsync(
        Guid productId, string barcode, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"{CatalogProducts}/{productId:D}/barcodes/{Uri.EscapeDataString(barcode)}/primary"),
            null,
            cancellationToken);

    /// <summary>Lists product brands.</summary>
    public Task<ApiResult<List<PosBrand>>> GetBrandsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosBrand>>("/api/v1/catalog/brands", cancellationToken);
}
