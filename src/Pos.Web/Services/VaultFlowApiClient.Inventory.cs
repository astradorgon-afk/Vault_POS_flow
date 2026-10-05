namespace Pos.Web.Services;

public sealed partial class VaultFlowApiClient
{
    public Task<ApiResult<PosReference>> CreateQuarantineIncidentAsync(
        Guid locationId, IReadOnlyList<PosCreateQuarantineLine> lines, string? note, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/quarantine", new PosCreateQuarantineIncident(locationId, lines, note), cancellationToken);

    public Task<ApiResult<List<PosLocation>>> GetLocationsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosLocation>>("/api/v1/locations", cancellationToken);

    /// <summary>Loads the minimal public directory used before sign-in.</summary>
    public Task<ApiResult<List<PosSignInLocation>>> GetSignInLocationsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosSignInLocation>>("/api/v1/locations/sign-in", cancellationToken);

    public Task<ApiResult<PosStockLevelReport>> GetStockLevelsAsync(Guid locationId, CancellationToken cancellationToken)
        => GetAsync<PosStockLevelReport>($"/api/v1/inventory/stock-levels?locationId={locationId:D}", cancellationToken);

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

    /// <summary>Raises a draft warehouse supply request for an assigned store.</summary>
    public Task<ApiResult<PosReference>> CreateRestockRequestAsync(
        PosCreateRestockRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/transfers/restock-requests", request, cancellationToken);

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


}
