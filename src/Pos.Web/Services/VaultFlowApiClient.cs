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
            ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            string fallback = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Too many sign-in attempts. Wait a minute, then try again."
                : "Sign-in failed. Check your credentials and try again.";
            return ApiResult<SignInResponse>.Failure(
                problem?.Detail ?? fallback,
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

        return request;
    }

    /// <summary>Gets and deserializes an authenticated API resource.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
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
            ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            return ApiResult<T>.Failure(
                problem?.Detail ?? "VaultFlow could not complete that operation.",
                problem?.ErrorCode);
        }

        T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        return value is null
            ? ApiResult<T>.Failure("The API returned an unreadable response.")
            : ApiResult<T>.Success(value);
    }

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
            ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
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
            $"/api/v1/inventory/exceptions/negative-attempts/summary?from={QueryTimestamp(from)}&to={QueryTimestamp(to)}",
            cancellationToken);

    /// <summary>Gets products with repeated posted count variances in the last window.</summary>
    public Task<ApiResult<List<PosRepeatVariance>>> GetRepeatVariancesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => GetAsync<List<PosRepeatVariance>>(
            $"/api/v1/inventory/counts/repeat-variances?from={QueryTimestamp(from)}&to={QueryTimestamp(to)}",
            cancellationToken);

    /// <summary>Gets scoped inventory availability and threshold totals.</summary>
    public Task<ApiResult<PosInventoryOverview>> GetInventoryOverviewAsync(
        CancellationToken cancellationToken)
        => GetAsync<PosInventoryOverview>(
            "/api/v1/dashboard/inventory-overview", cancellationToken);

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

    /// <summary>Gets a customer return with its lines, inspections and refunds.</summary>
    public Task<ApiResult<PosReturnDetail>> GetReturnAsync(
        Guid returnId, CancellationToken cancellationToken)
        => GetAsync<PosReturnDetail>(
            FormattableString.Invariant($"/api/v1/returns/{returnId:D}"),
            cancellationToken);

    /// <summary>Routes inspected returned goods to restock, quarantine, damaged,
    /// supplier-return staging, or waste.</summary>
    public Task<ApiResult<PosReference>> DisposeReturnAsync(
        Guid returnId, PosDisposeReturnRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/returns/{returnId:D}/disposition"),
            request,
            cancellationToken);

    // ---- Inventory counts ----

    /// <summary>Sends a command whose response is empty on success (204).</summary>
    public async Task<ApiResult<string>> SendCommandAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            return ApiResult<string>.Failure(
                problem?.Detail ?? "VaultFlow could not complete that operation.",
                problem?.ErrorCode);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return ApiResult<string>.Success(string.Empty);
        }

        string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ApiResult<string>.Success(raw);
    }

    /// <summary>Lists accounts for administration.</summary>
    public Task<ApiResult<List<PosUserSummary>>> GetUsersAsync(
        string? search, bool includeInactive, CancellationToken cancellationToken)
    {
        string query = $"/api/v1/users?includeInactive={includeInactive.ToString().ToLowerInvariant()}&limit=200";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search)}";
        }

        return GetAsync<List<PosUserSummary>>(query, cancellationToken);
    }

    /// <summary>Gets one account with its assignments, overrides and effective permissions.</summary>
    public Task<ApiResult<PosUserDetail>> GetUserAsync(Guid userId, CancellationToken cancellationToken)
        => GetAsync<PosUserDetail>(
            FormattableString.Invariant($"/api/v1/users/{userId:D}"),
            cancellationToken);

    /// <summary>Creates an account with roles and locations.</summary>
    public Task<ApiResult<PosReference>> CreateUserAsync(PosCreateUserRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/users", request, cancellationToken);

    /// <summary>Changes an account's name, e-mail, employee code or approval tier.</summary>
    public Task<ApiResult<string>> UpdateUserAsync(
        Guid userId, PosUpdateUserRequest request, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/users/{userId:D}"),
            request,
            cancellationToken);

    /// <summary>Disables an account and ends its sessions.</summary>
    public Task<ApiResult<string>> DisableUserAsync(Guid userId, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/disable"),
            new PosReasonRequest(reason),
            cancellationToken);

    /// <summary>Re-enables a disabled account.</summary>
    public Task<ApiResult<string>> EnableUserAsync(Guid userId, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/enable"),
            null,
            cancellationToken);

    /// <summary>Replaces an account's roles.</summary>
    public Task<ApiResult<string>> SetUserRolesAsync(
        Guid userId, IReadOnlyList<string> roles, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/roles"),
            new PosUserRolesRequest(roles),
            cancellationToken);

    /// <summary>Replaces an account's location assignments.</summary>
    public Task<ApiResult<string>> SetUserLocationsAsync(
        Guid userId, IReadOnlyList<PosUserLocationSpec> locations, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/locations"),
            new PosUserLocationsRequest(locations),
            cancellationToken);

    /// <summary>Grants or withholds one permission for one account.</summary>
    public Task<ApiResult<PosReference>> GrantOverrideAsync(
        Guid userId, PosOverrideRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>(
            FormattableString.Invariant($"/api/v1/users/{userId:D}/overrides"),
            request,
            cancellationToken);

    /// <summary>Removes a permission override.</summary>
    public Task<ApiResult<string>> RemoveOverrideAsync(
        Guid userId, Guid overrideId, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/overrides/{overrideId:D}/remove"),
            new PosReasonRequest(reason),
            cancellationToken);

    /// <summary>Sets a new password and ends the account's sessions.</summary>
    public Task<ApiResult<string>> ResetPasswordAsync(
        Guid userId, string newPassword, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/password"),
            new PosResetPasswordRequest(newPassword),
            cancellationToken);

    /// <summary>Sets the cashier PIN used at a registered terminal.</summary>
    public Task<ApiResult<string>> SetUserPinAsync(Guid userId, string pin, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/pin"),
            new PosSetPinRequest(pin),
            cancellationToken);

    /// <summary>Clears an account's authenticator so it must enrol again.</summary>
    public Task<ApiResult<string>> ResetUserTwoFactorAsync(
        Guid userId, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/users/{userId:D}/two-factor/reset"),
            new PosReasonRequest(reason),
            cancellationToken);

    /// <summary>Lists roles with their bundled permissions and member counts.</summary>
    public Task<ApiResult<List<PosRoleView>>> GetRolesAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosRoleView>>("/api/v1/roles", cancellationToken);

    /// <summary>Replaces the permissions a role bundles.</summary>
    public Task<ApiResult<string>> SetRolePermissionsAsync(
        Guid roleId, IReadOnlyList<string> permissions, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/roles/{roleId:D}/permissions"),
            new PosRolePermissionsRequest(permissions, reason),
            cancellationToken);

    /// <summary>Lists the permission catalogue.</summary>
    public Task<ApiResult<List<PosPermissionView>>> GetPermissionsAsync(CancellationToken cancellationToken)
        => GetAsync<List<PosPermissionView>>("/api/v1/permissions", cancellationToken);

    // ---- Devices ----

    /// <summary>Lists every registered terminal, optionally for one location.</summary>
    public Task<ApiResult<List<PosDeviceSummary>>> GetDevicesAsync(
        Guid? locationId, CancellationToken cancellationToken)
        => GetAsync<List<PosDeviceSummary>>(
            locationId is { } id
                ? FormattableString.Invariant($"/api/v1/devices?locationId={id:D}")
                : "/api/v1/devices",
            cancellationToken);

    /// <summary>Registers a terminal and returns its one-time enrolment code when required.</summary>
    public Task<ApiResult<PosDeviceRegistration>> RegisterDeviceAsync(
        PosRegisterDeviceRequest request, CancellationToken cancellationToken)
        => PostAsync<PosDeviceRegistration>("/api/v1/devices", request, cancellationToken);

    /// <summary>Burns an outstanding code and issues a fresh one.</summary>
    public Task<ApiResult<PosDeviceRegistration>> ReissueDeviceCodeAsync(
        Guid deviceId, CancellationToken cancellationToken)
        => PostAsync<PosDeviceRegistration>(
            FormattableString.Invariant($"/api/v1/devices/{deviceId:D}/enrolment-code"),
            new { },
            cancellationToken);

    /// <summary>Temporarily blocks a terminal and ends its sessions.</summary>
    public Task<ApiResult<string>> SuspendDeviceAsync(
        Guid deviceId, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/devices/{deviceId:D}/suspend"),
            new PosReasonRequest(reason),
            cancellationToken);

    /// <summary>Lifts a terminal suspension.</summary>
    public Task<ApiResult<string>> ReactivateDeviceAsync(
        Guid deviceId, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/devices/{deviceId:D}/reactivate"),
            null,
            cancellationToken);

    /// <summary>Permanently revokes a terminal.</summary>
    public Task<ApiResult<string>> RevokeDeviceAsync(
        Guid deviceId, string reason, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Post,
            FormattableString.Invariant($"/api/v1/devices/{deviceId:D}/revoke"),
            new PosReasonRequest(reason),
            cancellationToken);

    // ---- Locations ----

    /// <summary>Lists physical locations and their operating policies.</summary>
    public Task<ApiResult<List<PosLocationAdmin>>> GetLocationAdministrationAsync(
        CancellationToken cancellationToken)
        => GetAsync<List<PosLocationAdmin>>("/api/v1/locations", cancellationToken);

    /// <summary>Adds a warehouse or store.</summary>
    public Task<ApiResult<PosReference>> CreateLocationAsync(
        PosCreateLocationRequest request, CancellationToken cancellationToken)
        => PostAsync<PosReference>("/api/v1/locations", request, cancellationToken);

    /// <summary>Replaces a location's operational policy.</summary>
    public Task<ApiResult<string>> UpdateLocationSettingsAsync(
        Guid locationId, PosLocationSettings settings, CancellationToken cancellationToken)
        => SendCommandAsync(
            HttpMethod.Put,
            FormattableString.Invariant($"/api/v1/locations/{locationId:D}/settings"),
            settings,
            cancellationToken);

    // ---- Reports ----

    /// <summary>Gets one store's daily close-of-trade report.</summary>
    public Task<ApiResult<PosDailySalesReport>> GetDailySalesReportAsync(
        Guid locationId, DateOnly businessDate, CancellationToken cancellationToken)
        => GetAsync<PosDailySalesReport>(
            FormattableString.Invariant(
                $"/api/v1/reports/daily-sales?locationId={locationId:D}&date={businessDate:yyyy-MM-dd}"),
            cancellationToken);

    // ---- Stock adjustments ----

    /// <summary>Lists stock draws the ledger refused, newest first.</summary>
    public Task<ApiResult<List<PosNegativeStockAttemptView>>> GetNegativeStockAttemptsAsync(
        Guid? locationId, CancellationToken cancellationToken)
    {
        string path = "/api/v1/inventory/exceptions/negative-attempts?offset=0&limit=200";
        if (locationId is { } location)
        {
            path += FormattableString.Invariant($"&locationId={location:D}");
        }

        return GetAsync<List<PosNegativeStockAttemptView>>(path, cancellationToken);
    }

    /// <summary>Ranks products and locations by refused stock draws.</summary>
    public Task<ApiResult<List<PosNegativeStockAttemptSummaryRow>>> GetNegativeStockAttemptSummaryAsync(
        Guid? locationId, CancellationToken cancellationToken)
    {
        string path = "/api/v1/inventory/exceptions/negative-attempts/summary";
        if (locationId is { } location)
        {
            path += FormattableString.Invariant($"?locationId={location:D}");
        }

        return GetAsync<List<PosNegativeStockAttemptSummaryRow>>(path, cancellationToken);
    }

    private static async Task<ApiProblem?> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<ApiProblem>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string QueryTimestamp(DateTimeOffset value)
        => Uri.EscapeDataString(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private sealed record ApiProblem(string? Detail, string? ErrorCode);
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
