using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Domain.Sales;
using Pos.Infrastructure.Sync;

namespace Pos.Client.Services;

/// <summary>What head office returns when a register enrols.</summary>
/// <param name="DeviceId">The register's identity.</param>
/// <param name="ShortCode">The code its offline documents are numbered under.</param>
/// <param name="LocationId">The store it trades at.</param>
public sealed record EnrolledDevice(Guid DeviceId, string ShortCode, Guid LocationId);

/// <summary>A signed-in session with head office.</summary>
/// <param name="AccessToken">The short-lived bearer token.</param>
/// <param name="RefreshToken">The token that ends the session at sign-out.</param>
/// <param name="User">Who signed in.</param>
public sealed record HeadOfficeSession(string AccessToken, string RefreshToken, HeadOfficeUser User);

/// <summary>The signed-in user, as head office describes them.</summary>
/// <param name="UserId">Their identifier.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Permissions">Their effective permissions for this session.</param>
public sealed record HeadOfficeUser(Guid UserId, string DisplayName, IReadOnlyList<string> Permissions);

/// <summary>A store a register can be set up for.</summary>
/// <param name="Id">The location identifier.</param>
/// <param name="Code">Its short code.</param>
/// <param name="Name">Its name.</param>
public sealed record StoreChoice(Guid Id, string Code, string Name);

/// <summary>The product identity fields a register needs when it submits a sale.</summary>
/// <param name="Id">The product identifier.</param>
/// <param name="Sku">The stock-keeping unit.</param>
/// <param name="BaseUnitOfMeasureId">The unit the inventory ledger counts.</param>
public sealed record ProductSaleReference(Guid Id, string Sku, Guid BaseUnitOfMeasureId);

/// <summary>The shift currently open on this physical register.</summary>
/// <param name="ShiftId">The shift identifier.</param>
/// <param name="Number">The human-readable shift number.</param>
/// <param name="CashierUserId">Who opened the shift; sales must be made by this user.</param>
/// <param name="CashierName">The cashier's display name.</param>
/// <param name="BusinessDate">The store business date.</param>
/// <param name="OpeningFloat">The cash placed in the drawer.</param>
public sealed record RegisterOpenShift(
    Guid ShiftId,
    string Number,
    Guid CashierUserId,
    string CashierName,
    DateOnly BusinessDate,
    decimal OpeningFloat);

/// <summary>The server-authoritative checkout context for this register.</summary>
/// <param name="BusinessDate">The store business date.</param>
/// <param name="CashRoundingIncrement">The increment used for cash change.</param>
/// <param name="OpenShift">The open shift, if there is one.</param>
public sealed record RegisterCheckoutContext(
    DateOnly BusinessDate,
    decimal CashRoundingIncrement,
    RegisterOpenShift? OpenShift);

/// <summary>One line sent from the physical register to sale completion.</summary>
/// <param name="ProductId">The product sold.</param>
/// <param name="UnitOfMeasureId">The unit in which it was sold.</param>
/// <param name="Barcode">The scanned barcode, if any.</param>
/// <param name="Quantity">The quantity sold.</param>
public sealed record RegisterSaleLine(
    Guid ProductId,
    Guid UnitOfMeasureId,
    string? Barcode,
    decimal Quantity);

/// <summary>One payment allocated to a physical-register sale.</summary>
/// <param name="Method">The payment rail.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">Cash handed over, required for cash.</param>
/// <param name="ProviderReference">The card or wallet reference, when supplied.</param>
public sealed record RegisterSalePayment(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>A sale accepted by head office, or queued on the register while it was offline.</summary>
/// <param name="Id">The sale identifier, or the queued event's identifier for an offline sale.</param>
/// <param name="Number">The printed sale number.</param>
/// <param name="QueuedOffline">Whether the sale is waiting on the register to be sent to head office.</param>
public sealed record CompletedRegisterSale(Guid Id, string Number, bool QueuedOffline = false);

/// <summary>The X-REPORT facts head office keeps for a cashier shift.</summary>
/// <param name="ShiftId">The shift identifier.</param>
/// <param name="Number">The SHF document number.</param>
/// <param name="Status">The shift status, for example Open or Closed.</param>
/// <param name="BusinessDate">The business date the shift belongs to.</param>
/// <param name="OpenedAtUtc">When the shift opened.</param>
/// <param name="OpeningFloat">The cash in the drawer at open.</param>
/// <param name="CashSales">Cash settled on sales.</param>
/// <param name="CashRefunds">Cash refunded on returns.</param>
/// <param name="Payouts">Cash paid out on expenses and withdrawals.</param>
/// <param name="ExpectedCash">Opening float plus cash sales minus refunds and payouts.</param>
public sealed record RegisterShiftSummary(
    Guid ShiftId,
    string Number,
    string Status,
    DateOnly BusinessDate,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningFloat,
    decimal CashSales,
    decimal CashRefunds,
    decimal Payouts,
    decimal ExpectedCash);

/// <summary>A completed sale as returned by the sales search.</summary>
/// <param name="Id">The sale identifier.</param>
/// <param name="Number">The SAL document number.</param>
/// <param name="BusinessDate">The business date the sale belongs to.</param>
/// <param name="CompletedAtUtc">When the sale was completed.</param>
/// <param name="NetTotal">The amount settled.</param>
public sealed record RegisterSaleSummary(
    Guid Id,
    string Number,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    decimal NetTotal);

/// <summary>A completed sale with its lines, as loaded for a return.</summary>
/// <param name="Id">The sale identifier.</param>
/// <param name="Number">The SAL document number.</param>
/// <param name="Status">The sale status, for example Completed or Voided.</param>
/// <param name="LocationId">The store the sale was completed at.</param>
/// <param name="BusinessDate">The business date the sale belongs to.</param>
/// <param name="CompletedAtUtc">When the sale was completed.</param>
/// <param name="Lines">The sale lines, ordered by line number.</param>
public sealed record RegisterSaleDetail(
    Guid Id,
    string Number,
    string Status,
    Guid LocationId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<RegisterSaleDetailLine> Lines);

/// <summary>One line of a completed sale, as loaded for a return.</summary>
/// <param name="LineNumber">The line number on the sale.</param>
/// <param name="ProductId">The product sold.</param>
/// <param name="ProductName">The product display name.</param>
/// <param name="Barcode">The scanned barcode, if any.</param>
/// <param name="Quantity">The quantity sold.</param>
/// <param name="UnitPrice">The selling unit price.</param>
/// <param name="NetAmount">The net line amount.</param>
public sealed record RegisterSaleDetailLine(
    int LineNumber,
    Guid ProductId,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    decimal UnitPrice,
    decimal NetAmount);

/// <summary>One line accepted on a return against a sale.</summary>
/// <param name="ProductId">The product returned.</param>
/// <param name="Quantity">The quantity returned.</param>
public sealed record RegisterReturnLine(Guid ProductId, decimal Quantity);

/// <summary>A return accepted by head office.</summary>
/// <param name="Id">The return identifier.</param>
/// <param name="Number">The printed RET number.</param>
public sealed record AcceptedRegisterReturn(Guid Id, string Number);

/// <summary>A customer as returned by the customer search.</summary>
/// <param name="Id">The customer identifier.</param>
/// <param name="DisplayName">The customer's display name.</param>
/// <param name="Phone">The customer's phone number, if any.</param>
/// <param name="Email">The customer's email address, if any.</param>
public sealed record RegisterCustomer(
    Guid Id,
    string DisplayName,
    string? Phone,
    string? Email);

/// <summary>A refusal or failure reported by head office, in words a manager can act on.</summary>
public sealed class HeadOfficeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HeadOfficeException"/> class.</summary>
    public HeadOfficeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HeadOfficeException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    public HeadOfficeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HeadOfficeException"/> class.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public HeadOfficeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets a value indicating whether head office could not be reached or could
    /// not answer at all (no route, timeout, or a 5xx from it or its proxy), as
    /// opposed to answering with a refusal. Only this kind of failure lets the
    /// register fall back to working offline.
    /// </summary>
    public bool IsUnreachable { get; init; }

    /// <summary>Gets the HTTP status head office answered with, when it answered.</summary>
    public int? StatusCode { get; init; }
}

/// <summary>
/// The register's calls to head office. The address is passed to every call
/// because a manager chooses it at set-up; nothing here remembers it.
/// </summary>
/// <param name="http">The shared HTTP client.</param>
/// <param name="reachability">Where each call's reachability is reported for the status banner.</param>
public sealed class HeadOfficeClient(HttpClient http, HeadOfficeReachability reachability)
{
    private const string DeviceHeader = "X-Device-Id";

    // Pos.Domain.Devices.DevicePlatform; the API reads enums as numbers.
    private const int WindowsPlatform = 1;
    private const int AndroidPlatform = 2;
    private const int StoreKind = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Redeems a one-time enrolment code for this register.</summary>
    public Task<EnrolledDevice> EnrolAsync(
        Uri server, string enrolmentCode, string keyThumbprint, CancellationToken cancellationToken)
        => SendAsync<EnrolledDevice>(
            HttpMethod.Post,
            server,
            "api/v1/devices/enrol",
            new
            {
                enrolmentCode,
                publicKeyThumbprint = keyThumbprint,
                platform = OperatingSystem.IsAndroid() ? AndroidPlatform : WindowsPlatform,
                appVersion = AppInfo.Current.VersionString,
                osVersion = DeviceInfo.Current.VersionString,
            },
            accessToken: null,
            deviceId: null,
            cancellationToken);

    /// <summary>Signs in with a username and password, bound to this register when it has an identity.</summary>
    public async Task<HeadOfficeSession> SignInAsync(
        Uri server, string userName, string password, Guid? deviceId, CancellationToken cancellationToken)
    {
        SignInResponse response = await SendAsync<SignInResponse>(
            HttpMethod.Post,
            server,
            "api/v1/auth/login",
            new { userName, password },
            accessToken: null,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return new HeadOfficeSession(response.AccessToken, response.RefreshToken, response.User);
    }

    /// <summary>Exchanges a session's single-use refresh token for a new pair.</summary>
    public async Task<HeadOfficeSession> RefreshAsync(
        Uri server, string refreshToken, Guid deviceId, CancellationToken cancellationToken)
    {
        SignInResponse response = await SendAsync<SignInResponse>(
            HttpMethod.Post,
            server,
            "api/v1/auth/refresh",
            new { refreshToken },
            accessToken: null,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return new HeadOfficeSession(response.AccessToken, response.RefreshToken, response.User);
    }

    /// <summary>Ends a session. Best effort: a register that cannot reach head office still signs out locally.</summary>
    public async Task SignOutAsync(Uri server, string refreshToken, Guid? deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync<JsonElement?>(
                HttpMethod.Post, server, "api/v1/auth/logout", new { refreshToken }, null, deviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadOfficeException)
        {
            // The refresh token expires on its own; there is nothing more to do here.
        }
    }

    /// <summary>Lists the active stores a manager can set a register up for.</summary>
    public async Task<IReadOnlyList<StoreChoice>> GetStoresAsync(
        Uri server, string accessToken, CancellationToken cancellationToken)
    {
        List<LocationRow> locations = await SendAsync<List<LocationRow>>(
            HttpMethod.Get, server, "api/v1/locations", null, accessToken, null, cancellationToken)
            .ConfigureAwait(false);

        return [.. locations
            .Where(l => l.Kind == StoreKind && l.IsActive)
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .Select(l => new StoreChoice(l.Id, l.Code, l.Name))];
    }

    /// <summary>Registers this machine as a new register and returns its one-time enrolment code.</summary>
    public async Task<string> RegisterDeviceAsync(
        Uri server,
        string accessToken,
        string shortCode,
        string name,
        Guid storeId,
        CancellationToken cancellationToken)
    {
        RegistrationResponse registration = await SendAsync<RegistrationResponse>(
            HttpMethod.Post,
            server,
            "api/v1/devices",
            new
            {
                shortCode,
                name,
                locationId = storeId,
                platform = OperatingSystem.IsAndroid() ? AndroidPlatform : WindowsPlatform,
            },
            accessToken,
            null,
            cancellationToken).ConfigureAwait(false);

        return registration.EnrolmentCode
            ?? throw new HeadOfficeException("Head office registered the register but issued no enrolment code.");
    }

    /// <summary>Downloads this register's store data and the signed-in user's offline authority.</summary>
    public Task<SyncBaselineResponse> GetBaselineAsync(
        Uri server, string accessToken, Guid deviceId, CancellationToken cancellationToken)
        => SendAsync<SyncBaselineResponse>(
            HttpMethod.Get, server, "api/v1/sync/baseline", null, accessToken, deviceId, cancellationToken);

    /// <summary>Uploads a batch of business events this register queued while it was offline.</summary>
    public Task<SyncPushResponse> PushEventsAsync(
        Uri server, string accessToken, Guid deviceId, SyncPushRequest batch, CancellationToken cancellationToken)
        => SendAsync<SyncPushResponse>(
            HttpMethod.Post, server, "api/v1/sync/push", batch, accessToken, deviceId, cancellationToken);

    /// <summary>Loads the product identifiers needed to turn cached catalogue rows into sale lines.</summary>
    public async Task<IReadOnlyDictionary<Guid, ProductSaleReference>> GetProductSaleReferencesAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, ProductSaleReference> products = [];
        const int pageSize = 200;

        for (int offset = 0; ; offset += pageSize)
        {
            List<ProductRow> page = await SendAsync<List<ProductRow>>(
                HttpMethod.Get,
                server,
                FormattableString.Invariant($"api/v1/catalog/products?offset={offset}&limit={pageSize}"),
                null,
                accessToken,
                deviceId,
                cancellationToken).ConfigureAwait(false);

            foreach (ProductRow product in page)
            {
                products[product.Id] = new ProductSaleReference(product.Id, product.Sku, product.BaseUnitOfMeasureId);
            }

            if (page.Count < pageSize)
            {
                return products;
            }
        }
    }

    /// <summary>Loads the current business date and shift for a physical register.</summary>
    public async Task<RegisterCheckoutContext> GetCheckoutContextAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        TerminalRow terminal = await SendAsync<TerminalRow>(
            HttpMethod.Get,
            server,
            FormattableString.Invariant($"api/v1/terminal/session?locationId={locationId:D}"),
            null,
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        RegisterOpenShift? shift = terminal.OpenShift is null
            ? null
            : new RegisterOpenShift(
                terminal.OpenShift.ShiftId,
                terminal.OpenShift.Number,
                terminal.OpenShift.CashierId,
                terminal.OpenShift.CashierName,
                terminal.OpenShift.BusinessDate,
                terminal.OpenShift.OpeningFloat);

        return new RegisterCheckoutContext(terminal.BusinessDate, terminal.CashRoundingIncrement, shift);
    }

    /// <summary>Opens a server shift under a number allocated by this physical register.</summary>
    public async Task<Guid> OpenShiftAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        string number,
        Guid locationId,
        DateOnly businessDate,
        decimal openingFloat,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            "api/v1/shifts/open",
            new { number, locationId, businessDate, openingFloat },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Completes a sale through the production sale command pipeline.</summary>
    public async Task<Guid> CompleteSaleAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        string number,
        Guid eventId,
        Guid locationId,
        Guid shiftId,
        Guid? customerId,
        DateOnly businessDate,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<RegisterSaleLine> lines,
        IReadOnlyList<RegisterSalePayment> payments,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            "api/v1/sales/",
            new
            {
                number,
                eventId,
                locationId,
                cashierShiftId = shiftId,
                deviceId,
                customerId,
                businessDate,
                completedAtUtc,
                lines = lines.Select(line => new
                {
                    line.ProductId,
                    line.Quantity,
                    line.UnitOfMeasureId,
                    line.Barcode,
                    unitPriceOverride = (decimal?)null,
                    priceOverrideAuthorizedByUserId = (Guid?)null,
                    discount = 0m,
                    discountAuthorizedByUserId = (Guid?)null,
                    allowExpiredOverride = false,
                    expiredOverrideReason = (string?)null,
                }),
                payments,
            },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        Uri server,
        string path,
        object? body,
        string? accessToken,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        using HttpRequestMessage request = new(method, new Uri(server, path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        ApplyAuth(request, accessToken, deviceId);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            reachability.Report(reachable: false);
            throw new HeadOfficeException(
                FormattableString.Invariant($"Head office could not be reached at {server}. Check the address and that it is running."),
                ex)
            { IsUnreachable = true };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            reachability.Report(reachable: false);
            throw new HeadOfficeException("Head office took too long to answer. Try again.", ex) { IsUnreachable = true };
        }

        using (response)
        {
            // A 5xx is head office (or the proxy in front of it) failing to
            // answer, not a decision about this request: treat it like no route.
            bool serverDown = (int)response.StatusCode >= 500;
            reachability.Report(reachable: !serverDown);

            if (!response.IsSuccessStatusCode)
            {
                throw new HeadOfficeException(await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false))
                {
                    IsUnreachable = serverDown,
                    StatusCode = (int)response.StatusCode,
                };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent
                || response.Content.Headers.ContentLength == 0)
            {
                return default!;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new HeadOfficeException("Head office sent an empty answer.");
        }
    }

    /// <summary>Gets the X-REPORT facts for a shift at this register's store.</summary>
    public Task<RegisterShiftSummary> GetShiftSummaryAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid shiftId,
        CancellationToken cancellationToken)
        => SendAsync<RegisterShiftSummary>(
            HttpMethod.Get,
            server,
            FormattableString.Invariant($"api/v1/shifts/{shiftId:D}/summary"),
            null,
            accessToken,
            deviceId,
            cancellationToken);

    /// <summary>Declares and counts the drawer, then closes the shift.</summary>
    public async Task<Guid> CloseShiftAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid shiftId,
        Guid locationId,
        decimal declaredCash,
        decimal countedCash,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            FormattableString.Invariant($"api/v1/shifts/{shiftId:D}/close"),
            new { locationId, declaredCash, countedCash },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Finds a store's newest sales in a business-date window, optionally
    /// only those whose receipt number contains <paramref name="number"/>.</summary>
    public async Task<IReadOnlyList<RegisterSaleSummary>> SearchSalesAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid locationId,
        DateOnly? from,
        DateOnly? to,
        string? number,
        int limit,
        CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"api/v1/sales?locationId={locationId:D}&limit={limit}");
        if (!string.IsNullOrWhiteSpace(number))
        {
            path += "&number=" + Uri.EscapeDataString(number.Trim());
        }

        if (from is { } fromDate)
        {
            path += FormattableString.Invariant($"&from={fromDate:yyyy-MM-dd}");
        }

        if (to is { } toDate)
        {
            path += FormattableString.Invariant($"&to={toDate:yyyy-MM-dd}");
        }

        List<SaleSearchRow> rows = await SendAsync<List<SaleSearchRow>>(
            HttpMethod.Get, server, path, null, accessToken, deviceId, cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new RegisterSaleSummary(
            row.Id, row.Number, row.BusinessDate, row.CompletedAtUtc, row.NetTotal))];
    }

    /// <summary>Loads a completed sale with its lines, ready for a return.</summary>
    public async Task<RegisterSaleDetail> GetSaleDetailAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        SaleDetailRow row = await SendAsync<SaleDetailRow>(
            HttpMethod.Get,
            server,
            FormattableString.Invariant($"api/v1/sales/{saleId:D}"),
            null,
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return new RegisterSaleDetail(
            row.Id,
            row.Number,
            row.Status,
            row.LocationId,
            row.BusinessDate,
            row.CompletedAtUtc,
            [.. row.Lines.Select(line => new RegisterSaleDetailLine(
                line.LineNumber, line.ProductId, line.ProductName, line.Barcode,
                line.Quantity, line.UnitPrice, line.NetAmount))]);
    }

    /// <summary>Accepts a return against a completed sale.</summary>
    public async Task<Guid> CreateReturnAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        string number,
        Guid eventId,
        Guid saleId,
        Guid locationId,
        Guid shiftId,
        Guid? customerId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        IReadOnlyList<RegisterReturnLine> lines,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            "api/v1/returns/",
            new
            {
                number,
                eventId,
                saleId,
                locationId,
                shiftId,
                deviceId,
                customerId,
                businessDate,
                returnedAtUtc,
                lines = lines.Select(line => new { line.ProductId, line.Quantity }),
            },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Issues a refund against a return through the open shift.</summary>
    public async Task<Guid> RefundReturnAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid returnId,
        Guid? saleId,
        Guid eventId,
        Guid locationId,
        Guid shiftId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        DateTimeOffset refundedAtUtc,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            FormattableString.Invariant($"api/v1/returns/{returnId:D}/refund"),
            new
            {
                saleId,
                eventId,
                locationId,
                shiftId,
                deviceId,
                method,
                amount,
                tendered,
                providerReference,
                refundedAtUtc,
            },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Searches customer records by name, phone or email.</summary>
    public async Task<IReadOnlyList<RegisterCustomer>> SearchCustomersAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        string? search,
        CancellationToken cancellationToken)
    {
        string path = "api/v1/customers?pageSize=20";
        if (!string.IsNullOrWhiteSpace(search))
        {
            path += $"&search={Uri.EscapeDataString(search)}";
        }

        CustomerSearchRow row = await SendAsync<CustomerSearchRow>(
            HttpMethod.Get, server, path, null, accessToken, deviceId, cancellationToken).ConfigureAwait(false);

        return [.. row.Customers.Select(customer => new RegisterCustomer(
            customer.Id, customer.DisplayName, customer.Phone, customer.Email))];
    }

    /// <summary>Creates a new customer record.</summary>
    public async Task<Guid> CreateCustomerAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        string displayName,
        string? phone,
        string? email,
        string? tin,
        string? note,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            "api/v1/customers/",
            new { displayName, phone, email, tin, note },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Logs a permissioned reprint of a sale receipt.</summary>
    public async Task<Guid> LogReprintAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid saleId,
        Guid locationId,
        string reason,
        DateTimeOffset reprintedAtUtc,
        CancellationToken cancellationToken)
    {
        IdResponse response = await SendAsync<IdResponse>(
            HttpMethod.Post,
            server,
            FormattableString.Invariant($"api/v1/sales/{saleId:D}/reprint"),
            new { locationId, deviceId, reason, reprintedAtUtc },
            accessToken,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        return response.Id;
    }

    /// <summary>Renders a completed sale receipt as printable plain text.</summary>
    public async Task<string> GetSaleReceiptAsync(
        Uri server,
        string accessToken,
        Guid deviceId,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(server, FormattableString.Invariant($"api/v1/sales/{saleId:D}/receipt")));
        ApplyAuth(request, accessToken, deviceId);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HeadOfficeException(await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false));
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyAuth(HttpRequestMessage request, string? accessToken, Guid? deviceId)
    {
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (deviceId is { } device)
        {
            request.Headers.Add(DeviceHeader, device.ToString());
        }
    }

        private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            Problem? problem = await response.Content
                .ReadFromJsonAsync<Problem>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (problem?.Detail is { Length: > 0 } detail)
            {
                return detail;
            }

            if (problem?.Title is { Length: > 0 } title)
            {
                return title;
            }
        }
        catch (JsonException)
        {
            // Not a problem document; fall through to the status code.
        }

        return FormattableString.Invariant($"Head office refused the request ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private sealed record SignInResponse(string AccessToken, string RefreshToken, HeadOfficeUser User);

    private sealed record LocationRow(Guid Id, string Code, string Name, int Kind, bool IsActive);

    private sealed record RegistrationResponse(Guid DeviceId, string ShortCode, string? EnrolmentCode);

    private sealed record ProductRow(Guid Id, string Sku, Guid BaseUnitOfMeasureId);

    private sealed record OpenShiftRow(
        Guid ShiftId,
        string Number,
        Guid CashierId,
        string CashierName,
        DateOnly BusinessDate,
        decimal OpeningFloat);

    private sealed record TerminalRow(
        DateOnly BusinessDate,
        decimal CashRoundingIncrement,
        OpenShiftRow? OpenShift);

    private sealed record SaleSearchRow(
        Guid Id,
        string Number,
        DateOnly BusinessDate,
        DateTimeOffset CompletedAtUtc,
        decimal NetTotal);

    private sealed record SaleDetailRow(
        Guid Id,
        string Number,
        string Status,
        Guid LocationId,
        DateOnly BusinessDate,
        DateTimeOffset CompletedAtUtc,
        IReadOnlyList<SaleDetailLineRow> Lines);

    private sealed record SaleDetailLineRow(
        int LineNumber,
        Guid ProductId,
        string ProductName,
        string? Barcode,
        decimal Quantity,
        decimal UnitPrice,
        decimal NetAmount);

    private sealed record CustomerSearchRow(
        IReadOnlyList<CustomerRow> Customers,
        int Total,
        int Page,
        int PageSize);

    private sealed record CustomerRow(
        Guid Id,
        string DisplayName,
        string? Phone,
        string? Email);

    private sealed record IdResponse(Guid Id);

    private sealed record Problem(string? Title, string? Detail);
}
