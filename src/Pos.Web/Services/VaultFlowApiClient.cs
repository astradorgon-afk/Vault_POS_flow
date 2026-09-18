namespace Pos.Web.Services;

/// <summary>Calls the VaultFlow HTTP API for the interactive web application.</summary>
public sealed class VaultFlowApiClient(HttpClient http, UserSession session)
{
    /// <summary>Signs in with a username, password and optional two-factor code.</summary>
    public async Task<ApiResult<SignInResponse>> SignInAsync(
        string userName,
        string password,
        string? twoFactorCode,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password, twoFactorCode },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await response.Content
                .ReadFromJsonAsync<ApiProblem>(cancellationToken)
                .ConfigureAwait(false);
            return ApiResult<SignInResponse>.Failure(
                problem?.Detail ?? "Sign-in failed. Check your credentials and try again.",
                problem?.ErrorCode);
        }

        SignInResponse? result = await response.Content
            .ReadFromJsonAsync<SignInResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return ApiResult<SignInResponse>.Failure("The API returned an unreadable sign-in response.");
        }

        session.Set(result);
        return ApiResult<SignInResponse>.Success(result);
    }

    /// <summary>Creates an authorized request for future API operations.</summary>
    public HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        if (session.IsAuthenticated && session.Current is { } current)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                current.AccessToken);
        }

        // Commands sent through the register carry the server-minted numbers;
        // the API binds the chosen web register from this header.
        if (session.DeviceId is { } deviceId)
        {
            request.Headers.Add("X-Device-Id", deviceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        }

        return request;
    }

    /// <summary>Gets and deserializes an authenticated API resource.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await response.Content
                .ReadFromJsonAsync<ApiProblem>(cancellationToken)
                .ConfigureAwait(false);
            return ApiResult<T>.Failure(
                problem?.Detail ?? "VaultFlow could not load the requested information.",
                problem?.ErrorCode);
        }

        T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        return value is null
            ? ApiResult<T>.Failure("The API returned an unreadable response.")
            : ApiResult<T>.Success(value);
    }

    /// <summary>Posts a JSON body through the register and reads the response.</summary>
    public async Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await response.Content
                .ReadFromJsonAsync<ApiProblem>(cancellationToken)
                .ConfigureAwait(false);
            return ApiResult<T>.Failure(
                problem?.Detail ?? "VaultFlow could not complete that operation.",
                problem?.ErrorCode);
        }

        T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        return value is null
            ? ApiResult<T>.Failure("The API returned an unreadable response.")
            : ApiResult<T>.Success(value);
    }

    /// <summary>Lists the browser registers a store's checkout can use.</summary>
    public Task<ApiResult<List<PosRegister>>> GetRegistersAsync(
        Guid locationId, CancellationToken cancellationToken)
        => GetAsync<List<PosRegister>>(
            FormattableString.Invariant($"/api/v1/terminal/registers?locationId={locationId:D}"),
            cancellationToken);

    /// <summary>Gets the checkout context for the chosen register: business
    /// date, VAT and rounding settings, and the open shift, if any.</summary>
    public Task<ApiResult<PosTerminalSession>> GetTerminalSessionAsync(
        Guid locationId, CancellationToken cancellationToken)
        => GetAsync<PosTerminalSession>(
            FormattableString.Invariant($"/api/v1/terminal/session?locationId={locationId:D}"),
            cancellationToken);

    /// <summary>Asks the server to mint the register's next device-scoped
    /// number (SAL, RET or SHF). The physical offline counters never see
    /// these, so Web registers may not mix with a physical device's sequence.</summary>
    public Task<ApiResult<PosNextNumber>> PostNextNumberAsync(
        Guid locationId, string documentType, CancellationToken cancellationToken)
        => PostAsync<PosNextNumber>(
            FormattableString.Invariant($"/api/v1/terminal/{locationId:D}/next-number"),
            new { documentType },
            cancellationToken);

    /// <summary>Opens a cashier shift on the chosen register (POS.md §1).</summary>
    public Task<ApiResult<PosShiftReference>> OpenShiftAsync(
        Guid locationId,
        string number,
        DateOnly businessDate,
        decimal openingFloat,
        CancellationToken cancellationToken)
        => PostAsync<PosShiftReference>(
            "/api/v1/shifts/open",
            new { number, locationId, businessDate, openingFloat },
            cancellationToken);

    /// <summary>Completes the sale atomically on the server (POS.md §3).</summary>
    public Task<ApiResult<PosCompletedSale>> CompleteSaleAsync(
        PosCompleteSaleRequest request, CancellationToken cancellationToken)
        => PostAsync<PosCompletedSale>("/api/v1/sales", request, cancellationToken);

    /// <summary>Schedules an effective-dated product price.</summary>
    public Task<ApiResult<PosReference>> SchedulePriceAsync(
        Guid productId, PosSchedulePriceRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/catalog/products/{productId:D}/prices"),
            request,
            cancellationToken);

    /// <summary>Cancels a future product price with its recorded reason.</summary>
    public Task<ApiResult<PosReference>> CancelScheduledPriceAsync(
        Guid productId, Guid priceId, PosCancelPriceRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/catalog/products/{productId:D}/prices/{priceId:D}/cancel"),
            request,
            cancellationToken);

    /// <summary>Loads the authenticated operator's durable notification feed.</summary>
    public Task<ApiResult<PosNotificationFeed>> GetNotificationsAsync(
        bool unreadOnly,
        CancellationToken cancellationToken)
        => GetAsync<PosNotificationFeed>(
            $"/api/v1/notifications?unreadOnly={unreadOnly.ToString().ToLowerInvariant()}&limit=100",
            cancellationToken);

    /// <summary>Marks one visible notification read.</summary>
    public Task<ApiResult<PosReference>> MarkNotificationReadAsync(
        Guid notificationId,
        CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/notifications/{notificationId:D}/read"),
            new { },
            cancellationToken);

    /// <summary>Marks every notification visible to the operator read.</summary>
    public Task<ApiResult<PosMarkedReadResult>> MarkAllNotificationsReadAsync(
        CancellationToken cancellationToken)
        => PostAsync<PosMarkedReadResult>(
            "/api/v1/notifications/read-all",
            new { },
            cancellationToken);

    /// <summary>Gets a plain-text body from an authenticated API resource.</summary>
    public async Task<ApiResult<string>> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await response.Content
                .ReadFromJsonAsync<ApiProblem>(cancellationToken)
                .ConfigureAwait(false);
            return ApiResult<string>.Failure(
                problem?.Detail ?? "VaultFlow could not load the requested information.",
                problem?.ErrorCode);
        }

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ApiResult<string>.Success(text);
    }

    /// <summary>Finds completed sales by store and a business-date window.
    /// Pass a store the operator may operate; the API re-checks <c>sale.view</c>.</summary>
    public Task<ApiResult<List<PosSaleSummary>>> SearchSalesAsync(
        Guid locationId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"/api/v1/sales?locationId={locationId:D}");
        if (from is { } fromDate)
        {
            path += FormattableString.Invariant($"&from={fromDate:yyyy-MM-dd}");
        }

        if (to is { } toDate)
        {
            path += FormattableString.Invariant($"&to={toDate:yyyy-MM-dd}");
        }

        return GetAsync<List<PosSaleSummary>>(path, cancellationToken);
    }

    /// <summary>Gets the highest repeated negative-stock attempts in the last window.</summary>
    public Task<ApiResult<List<PosNegativeStockSummary>>> GetNegativeStockSummaryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => GetAsync<List<PosNegativeStockSummary>>(
            FormattableString.Invariant($"/api/v1/inventory/exceptions/negative-attempts/summary?from={from:O}&to={to:O}"),
            cancellationToken);

    /// <summary>Gets products with repeated posted count variances in the last window.</summary>
    public Task<ApiResult<List<PosRepeatVariance>>> GetRepeatVariancesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => GetAsync<List<PosRepeatVariance>>(
            FormattableString.Invariant($"/api/v1/inventory/counts/repeat-variances?from={from:O}&to={to:O}"),
            cancellationToken);

    /// <summary>Gets scoped inventory availability and threshold totals.</summary>
    public Task<ApiResult<PosInventoryOverview>> GetInventoryOverviewAsync(
        CancellationToken cancellationToken)
        => GetAsync<PosInventoryOverview>(
            "/api/v1/dashboard/inventory-overview", cancellationToken);

    /// <summary>Gets the authorization-scoped movement chain for a document.</summary>
    public Task<ApiResult<PosInventoryTimeline>> GetInventoryTimelineAsync(
        string documentType, Guid documentId, CancellationToken cancellationToken)
        => GetAsync<PosInventoryTimeline>(
            FormattableString.Invariant($"/api/v1/inventory/timeline/{Uri.EscapeDataString(documentType)}/{documentId:D}"),
            cancellationToken);

    /// <summary>Gets open synchronization failures for authorized operators.</summary>
    public Task<ApiResult<List<PosSyncFailure>>> GetSyncFailuresAsync(
        int limit, CancellationToken cancellationToken)
        => GetAsync<List<PosSyncFailure>>(
            FormattableString.Invariant($"/api/v1/sync/failures?limit={limit}"),
            cancellationToken);

    /// <summary>Schedules an authorized synchronization failure for retry.</summary>
    public Task<ApiResult<PosSyncFailure>> RetrySyncFailureAsync(
        Guid failureId, CancellationToken cancellationToken)
        => PostAsync<PosSyncFailure>(
            FormattableString.Invariant($"/api/v1/sync/failures/{failureId:D}/retry"),
            new { },
            cancellationToken);

    /// <summary>Dismisses an authorized synchronization failure with a note.</summary>
    public Task<ApiResult<PosSyncFailure>> DismissSyncFailureAsync(
        Guid failureId, string note, CancellationToken cancellationToken)
        => PostAsync<PosSyncFailure>(
            FormattableString.Invariant($"/api/v1/sync/failures/{failureId:D}/dismiss"),
            new { note },
            cancellationToken);

    /// <summary>Gets a completed sale with its lines and payments.</summary>
    public Task<ApiResult<PosSaleDetail>> GetSaleAsync(
        Guid saleId, CancellationToken cancellationToken)
        => GetAsync<PosSaleDetail>(
            FormattableString.Invariant($"/api/v1/sales/{saleId:D}"),
            cancellationToken);

    /// <summary>Renders a completed sale as printable plain text, logging the
    /// first print against the sale.</summary>
    public Task<ApiResult<string>> GetSaleReceiptAsync(
        Guid saleId, CancellationToken cancellationToken)
        => GetTextAsync(
            FormattableString.Invariant($"/api/v1/sales/{saleId:D}/receipt"),
            cancellationToken);

    /// <summary>Renders a completed sale as a printable HTML document (browser
    /// print-to-PDF), logging the first print exactly like the plain-text form.</summary>
    public Task<ApiResult<string>> GetSaleReceiptHtmlAsync(
        Guid saleId, CancellationToken cancellationToken)
        => GetTextAsync(
            FormattableString.Invariant($"/api/v1/sales/{saleId:D}/receipt?format=Html"),
            cancellationToken);

    /// <summary>Logs a permissioned reprint of a sale receipt. The reprint flows
    /// through the register selected in the session.</summary>
    public Task<ApiResult<PosReference>> ReprintSaleAsync(
        Guid saleId, PosReprintSaleRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/sales/{saleId:D}/reprint"),
            request,
            cancellationToken);

    /// <summary>Voids a completed sale through the open shift, reversing its stock movement.</summary>
    public Task<ApiResult<PosReference>> VoidSaleAsync(
        Guid saleId, PosVoidSaleRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/sales/{saleId:D}/void"),
            request,
            cancellationToken);

    /// <summary>Gets a customer return with its lines, inspections and refunds.</summary>
    public Task<ApiResult<PosReturnDetail>> GetReturnAsync(
        Guid returnId, CancellationToken cancellationToken)
        => GetAsync<PosReturnDetail>(
            FormattableString.Invariant($"/api/v1/returns/{returnId:D}"),
            cancellationToken);

    /// <summary>Accepts a customer return against a completed sale.</summary>
    public Task<ApiResult<PosReference>> CreateReturnAsync(
        PosCreateReturnRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/returns", request, cancellationToken);

    /// <summary>Issues a refund against a return through the open shift.</summary>
    public Task<ApiResult<PosReference>> RefundReturnAsync(
        Guid returnId, PosRefundReturnRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/returns/{returnId:D}/refund"),
            request,
            cancellationToken);

    /// <summary>Routes inspected returned goods to restock, quarantine, damaged,
    /// supplier-return staging, or waste.</summary>
    public Task<ApiResult<PosReference>> DisposeReturnAsync(
        Guid returnId, PosDisposeReturnRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/returns/{returnId:D}/disposition"),
            request,
            cancellationToken);

    private sealed record ApiProblem(string? Detail, string? ErrorCode);
}

/// <summary>An allocated device-scoped document number.</summary>
public sealed class PosNextNumber
{
    /// <summary>Gets or sets the document type code (SAL, RET or SHF).</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the allocated number, e.g. <c>SAL-2026-TW1-000001</c>.</summary>
    public string Number { get; set; } = string.Empty;
}

/// <summary>A reference to a cashier shift.</summary>
public sealed class PosShiftReference
{
    /// <summary>Gets or sets the shift identifier.</summary>
    public Guid Id { get; set; }
}

/// <summary>A client API result with a user-safe failure message.</summary>
public sealed record ApiResult<T>(T? Value, string? Error, string? ErrorCode)
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess => Error is null && Value is not null;

    /// <summary>Creates a successful result.</summary>
    public static ApiResult<T> Success(T value) => new(value, null, null);

    /// <summary>Creates a failed result.</summary>
    public static ApiResult<T> Failure(string error, string? errorCode = null) => new(default, error, errorCode);
}
