using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Web.Services;

#pragma warning disable IDE0011 // Endpoint guards are intentionally concise.

namespace Pos.Web.Pwa;

/// <summary>Same-origin API gateway. API credentials never leave this server.</summary>
public static class PwaGateway
{
    private const string Cookie = "vaultflow.pwa";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPwaGateway(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/offline/api");
        group.MapPost("/enrol", EnrolAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/handoff", HandoffAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", Me);
        group.MapGet("/employees", EmployeesAsync);
        group.MapPost("/provision", ProvisionAsync);
        group.MapGet("/snapshot", SnapshotAsync);
        group.MapGet("/pull", PullAsync);
        group.MapPost("/push", PushAsync);
        return app;
    }

    private static async Task<IResult> EnrolAsync(
        HttpContext context, IHttpClientFactory clients, JsonElement body, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        using HttpResponseMessage response = await clients.CreateClient("pwa-api")
            .PostAsJsonAsync("/api/v1/devices/enrol", body, ct);
        return await ForwardAsync(response, ct);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions,
        PwaLogin body, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        if (body.DeviceId == Guid.Empty || body.LocationId == Guid.Empty)
            return Results.BadRequest(new { message = "Enroll this installation and choose its work location." });

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { body.UserName, body.Password, body.TwoFactorCode }),
        };
        request.Headers.Add("X-Device-Id", body.DeviceId.ToString("D"));
        using HttpResponseMessage response = await clients.CreateClient("pwa-api").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return await ForwardAsync(response, ct);
        SignInResponse? signIn = await response.Content.ReadFromJsonAsync<SignInResponse>(JsonOptions, ct);
        if (signIn is null) return Results.StatusCode(502);
        if (!signIn.User.HasAllLocations && !signIn.User.Locations.Contains(body.LocationId))
        {
            await RevokeAsync(clients, signIn.RefreshToken, ct);
            return Results.Forbid();
        }

        using HttpRequestMessage deviceRequest = new(HttpMethod.Get,
            $"/api/v1/devices/{body.DeviceId:D}/locations");
        deviceRequest.Headers.Add("X-Device-Id", body.DeviceId.ToString("D"));
        using HttpResponseMessage deviceResponse = await clients.CreateClient("pwa-api")
            .SendAsync(deviceRequest, ct);
        if (!deviceResponse.IsSuccessStatusCode)
        {
            await RevokeAsync(clients, signIn.RefreshToken, ct);
            return await ForwardAsync(deviceResponse, ct);
        }
        JsonElement deviceLocations = await deviceResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!deviceLocations.TryGetProperty("defaultLocationId", out JsonElement defaultLocation) ||
            defaultLocation.GetGuid() != body.LocationId)
        {
            await RevokeAsync(clients, signIn.RefreshToken, ct);
            return Results.Forbid();
        }

        SetSession(context, sessions, body.DeviceId, body.LocationId, signIn);
        return Results.Ok(new { signIn.User, body.DeviceId, body.LocationId,
            locationKind = LocationKindOf(deviceLocations) });
    }

    /// <summary>Reads the kind of the device's home location (0 warehouse, 1 store).</summary>
    private static short? LocationKindOf(JsonElement deviceLocations)
        => deviceLocations.TryGetProperty("defaultLocationKind", out JsonElement kind) &&
           kind.ValueKind == JsonValueKind.Number && kind.TryGetInt16(out short value)
            ? value
            : null;

    private static async Task<IResult> HandoffAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions,
        PwaLoginHandoffs handoffs, PwaHandoff body, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        PwaLoginHandoffs.Entry? entry = handoffs.Take(body.Token);
        if (entry is null || body.DeviceId == Guid.Empty || body.LocationId == Guid.Empty)
            return Results.Unauthorized();
        SignInResponse credentials = entry.Credentials;
        if (entry.WorkLocationId is { } chosen && chosen != body.LocationId)
            return Results.Forbid();
        if (!credentials.User.HasAllLocations && !credentials.User.Locations.Contains(body.LocationId))
            return Results.Forbid();
        using HttpRequestMessage deviceRequest = new(HttpMethod.Get,
            $"/api/v1/devices/{body.DeviceId:D}/locations");
        deviceRequest.Headers.Add("X-Device-Id", body.DeviceId.ToString("D"));
        using HttpResponseMessage deviceResponse = await clients.CreateClient("pwa-api")
            .SendAsync(deviceRequest, ct);
        if (!deviceResponse.IsSuccessStatusCode) return await ForwardAsync(deviceResponse, ct);
        JsonElement locations = await deviceResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!locations.TryGetProperty("defaultLocationId", out JsonElement defaultLocation) ||
            defaultLocation.GetGuid() != body.LocationId) return Results.Forbid();
        SetSession(context, sessions, body.DeviceId, body.LocationId, credentials);
        return Results.Ok(new { credentials.User, body.DeviceId, body.LocationId,
            locationKind = LocationKindOf(locations) });
    }

    private static void SetSession(HttpContext context, PwaSessions sessions,
        Guid deviceId, Guid locationId, SignInResponse credentials)
    {
        if (context.Request.Cookies.TryGetValue(Cookie, out string? previous)) sessions.Remove(previous);
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions.Add(key, new PwaSession(deviceId, locationId, credentials));
        context.Response.Cookies.Append(Cookie, key, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
            Path = "/offline/api", IsEssential = true, MaxAge = TimeSpan.FromDays(7),
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        if (context.Request.Cookies.TryGetValue(Cookie, out string? key) &&
            sessions.Remove(key) is { } session)
            await RevokeAsync(clients, session.Credentials.RefreshToken, ct);
        context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/offline/api" });
        return Results.NoContent();
    }

    private static IResult Me(HttpContext context, PwaSessions sessions)
        => FindSession(context, sessions) is { } session
            ? Results.Ok(new { session.Credentials.User, session.DeviceId, session.LocationId })
            : Results.Unauthorized();

    private static async Task<IResult> EmployeesAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions, CancellationToken ct)
    {
        PwaSession? session = FindSession(context, sessions);
        if (session is null) return Results.Unauthorized();
        HttpClient client = clients.CreateClient("pwa-api");
        if (!await RefreshIfNeededAsync(session, client, ct)) return Results.Unauthorized();
        using HttpResponseMessage response = await GetAsync(client, session, "/api/v1/pwa/employees", ct);
        return await ForwardAsync(response, ct);
    }

    private static async Task<IResult> ProvisionAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions,
        PwaProvisionRequest body, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        PwaSession? administrator = FindSession(context, sessions);
        if (administrator is null) return Results.Unauthorized();
        HttpClient client = clients.CreateClient("pwa-api");
        if (!await RefreshIfNeededAsync(administrator, client, ct)) return Results.Unauthorized();
        using HttpRequestMessage request = Authorized(administrator, HttpMethod.Post, "/api/v1/pwa/provision-token");
        request.Content = JsonContent.Create(new { body.UserId });
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return await ForwardAsync(response, ct);
        PwaProvisionToken? token = await response.Content.ReadFromJsonAsync<PwaProvisionToken>(JsonOptions, ct);
        if (token is null || token.UserId != body.UserId || token.DeviceId != administrator.DeviceId ||
            token.LocationId != administrator.LocationId) return Results.StatusCode(502);

        SignedInUser user = new(token.UserId, token.DisplayName, token.Roles ?? [], token.Permissions ?? [],
            [token.LocationId], false, 0);
        SignInResponse credentials = new(token.AccessToken, token.AccessTokenExpiresAtUtc,
            string.Empty, token.AccessTokenExpiresAtUtc, user);
        PwaSession employee = new(token.DeviceId, token.LocationId, credentials);
        SnapshotOutcome result = await FetchSnapshotAsync(client, employee, ct);
        return result.Error ?? Results.Json(new { employee = new { token.UserId, token.UserName,
            token.DisplayName, token.Permissions, token.Roles }, snapshot = result.Snapshot });
    }

    private static async Task<IResult> SnapshotAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions, CancellationToken ct)
    {
        PwaSession? session = FindSession(context, sessions);
        if (session is null) return Results.Unauthorized();
        HttpClient client = clients.CreateClient("pwa-api");
        if (!await RefreshIfNeededAsync(session, client, ct)) return Results.Unauthorized();
        SnapshotOutcome result = await FetchSnapshotAsync(client, session, ct);
        return result.Error ?? Results.Json(result.Snapshot);
    }

    private static async Task<SnapshotOutcome> FetchSnapshotAsync(
        HttpClient client, PwaSession session, CancellationToken ct)
    {
        using HttpResponseMessage baseline = await GetAsync(client, session, "/api/v1/sync/baseline", ct);
        if (!baseline.IsSuccessStatusCode) return new(await ForwardAsync(baseline, ct), null);

        // The baseline is device-scoped. Purchasing references and order details
        // are fetched under the same employee token and remain scoped locally.
        JsonElement baselineJson = await baseline.Content.ReadFromJsonAsync<JsonElement>(ct);
        using HttpResponseMessage categoriesResponse = await GetAsync(client, session, "/api/v1/catalog/categories", ct);
        if (!categoriesResponse.IsSuccessStatusCode && categoriesResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
            return new(await ForwardAsync(categoriesResponse, ct), null);
        using HttpResponseMessage unitsResponse = await GetAsync(client, session, "/api/v1/catalog/units", ct);
        if (!unitsResponse.IsSuccessStatusCode && unitsResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
            return new(await ForwardAsync(unitsResponse, ct), null);
        using HttpResponseMessage suppliersResponse = await GetAsync(client, session, "/api/v1/catalog/suppliers", ct);
        if (!suppliersResponse.IsSuccessStatusCode && suppliersResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
            return new(await ForwardAsync(suppliersResponse, ct), null);
        JsonElement empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
        JsonElement categories = categoriesResponse.IsSuccessStatusCode
            ? await categoriesResponse.Content.ReadFromJsonAsync<JsonElement>(ct) : empty;
        JsonElement units = unitsResponse.IsSuccessStatusCode
            ? await unitsResponse.Content.ReadFromJsonAsync<JsonElement>(ct) : empty;
        JsonElement suppliers = suppliersResponse.IsSuccessStatusCode
            ? await suppliersResponse.Content.ReadFromJsonAsync<JsonElement>(ct) : empty;
        JsonElement? stockLevels = null;
        using (HttpResponseMessage stockResponse = await GetAsync(client, session,
            $"/api/v1/inventory/stock-levels?locationId={session.LocationId:D}", ct))
        {
            if (stockResponse.IsSuccessStatusCode)
                stockLevels = await stockResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            else if (stockResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
                return new(await ForwardAsync(stockResponse, ct), null);
        }
        List<JsonElement> products = [];
        bool catalogComplete = false;
        for (int offset = 0; offset < 20_000; offset += 200)
        {
            using HttpResponseMessage productResponse = await GetAsync(client, session,
                $"/api/v1/catalog/products?offset={offset}&limit=200", ct);
            if (productResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                catalogComplete = true;
                break;
            }
            if (!productResponse.IsSuccessStatusCode) return new(await ForwardAsync(productResponse, ct), null);
            JsonElement page = await productResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (page.ValueKind != JsonValueKind.Array) return new(Results.StatusCode(502), null);
            products.AddRange(page.EnumerateArray().Select(product => product.Clone()));
            if (page.GetArrayLength() < 200)
            {
                catalogComplete = true;
                break;
            }
        }
        if (!catalogComplete)
            return new(Results.Problem("The catalog is too large for this PWA snapshot. Contact an administrator.", statusCode: 413), null);
        List<JsonElement> details = [];
        foreach (string status in new[] { "Ordered", "PartiallyReceived" })
        {
            bool ordersComplete = false;
            for (int offset = 0; offset < 20_000; offset += 200)
            {
                using HttpResponseMessage ordersResponse = await GetAsync(client, session,
                    $"/api/v1/purchasing/orders?status={status}&offset={offset}&limit=200", ct);
                if (ordersResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    ordersComplete = true;
                    break;
                }
                if (!ordersResponse.IsSuccessStatusCode) return new(await ForwardAsync(ordersResponse, ct), null);
                JsonElement page = await ordersResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (page.ValueKind != JsonValueKind.Array) return new(Results.StatusCode(502), null);
                foreach (JsonElement order in page.EnumerateArray())
                {
                    if (!order.TryGetProperty("id", out JsonElement id) ||
                        !order.TryGetProperty("destinationLocationId", out JsonElement location) ||
                        location.GetGuid() != session.LocationId) continue;
                    using HttpResponseMessage detailResponse = await GetAsync(client, session,
                        $"/api/v1/purchasing/orders/{id.GetGuid():D}", ct);
                    if (!detailResponse.IsSuccessStatusCode) return new(await ForwardAsync(detailResponse, ct), null);
                    JsonElement detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (detail.ValueKind != JsonValueKind.Object) return new(Results.StatusCode(502), null);
                    details.Add(detail);
                }
                if (page.GetArrayLength() < 200)
                {
                    ordersComplete = true;
                    break;
                }
            }
            if (!ordersComplete)
                return new(Results.Problem("There are too many open orders for one offline snapshot.", statusCode: 413), null);
        }
        return new(null, new { baseline = baselineJson, categories, units, suppliers, stockLevels, products,
            orders = details, downloadedAtUtc = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> PullAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions,
        long cursor, CancellationToken ct)
    {
        PwaSession? session = FindSession(context, sessions);
        if (session is null) return Results.Unauthorized();
        HttpClient client = clients.CreateClient("pwa-api");
        if (!await RefreshIfNeededAsync(session, client, ct)) return Results.Unauthorized();
        using HttpResponseMessage response = await GetAsync(client, session,
            $"/api/v1/sync/pull?cursor={cursor}&limit=500", ct);
        return await ForwardAsync(response, ct);
    }

    private static async Task<IResult> PushAsync(
        HttpContext context, IHttpClientFactory clients, PwaSessions sessions,
        PwaPush body, CancellationToken ct)
    {
        if (!SafeMutation(context)) return Results.StatusCode(403);
        PwaSession? session = FindSession(context, sessions);
        if (session is null) return Results.Unauthorized();
        if (body.Events is null || body.Events.Count > 100 || body.Events.Any(item =>
            item.EventId == Guid.Empty || item.DeviceSequence <= 0 ||
            item.EventType is not ("PwaPurchaseDraft" or "PwaReceivingCount" or "PwaRestockDraft") ||
            item.Payload.ValueKind != JsonValueKind.Object || item.Payload.GetRawText().Length > 1_000_000))
            return Results.BadRequest(new { message = "The PWA event batch is invalid." });
        HttpClient client = clients.CreateClient("pwa-api");
        if (!await RefreshIfNeededAsync(session, client, ct)) return Results.Unauthorized();
        Guid userId = session.Credentials.User.Id;
        var events = body.Events.Select(item => new
        {
            item.EventId, item.DeviceSequence, item.EventType,
            PayloadJson = item.Payload.GetRawText(),
            PayloadHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(item.Payload.GetRawText()))),
            UserId = userId, LocationId = session.LocationId,
            OccurredAtUtc = item.OccurredAtUtc,
            DeviceUptimeTicks = 0L, CorrelationId = item.EventId,
        }).ToArray();
        using HttpRequestMessage request = Authorized(session, HttpMethod.Post, "/api/v1/sync/push");
        request.Content = JsonContent.Create(new
        {
            session.DeviceId, ClientSentAtUtc = DateTimeOffset.UtcNow,
            DeviceUptimeTicks = 0L, Events = events,
        });
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return await ForwardAsync(response, ct);
    }

    private static PwaSession? FindSession(HttpContext context, PwaSessions sessions)
        => context.Request.Cookies.TryGetValue(Cookie, out string? key) ? sessions.Get(key) : null;

    private static bool SafeMutation(HttpContext context)
    {
        if (context.Request.Headers["X-VaultFlow-PWA"] != "1") return false;
        string? origin = context.Request.Headers.Origin;
        return string.IsNullOrEmpty(origin) || string.Equals(origin,
            $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Authorized(PwaSession session, HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Credentials.AccessToken);
        request.Headers.Add("X-Device-Id", session.DeviceId.ToString("D"));
        return request;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, PwaSession session, string path, CancellationToken ct)
    {
        using HttpRequestMessage request = Authorized(session, HttpMethod.Get, path);
        return await client.SendAsync(request, ct);
    }

    private static async Task<bool> RefreshIfNeededAsync(PwaSession session, HttpClient client, CancellationToken ct)
    {
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Credentials.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return true;
            using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/refresh",
                new { session.Credentials.RefreshToken }, ct);
            if (!response.IsSuccessStatusCode) return false;
            SignInResponse? refreshed = await response.Content.ReadFromJsonAsync<SignInResponse>(JsonOptions, ct);
            if (refreshed is null || refreshed.User.Id != session.Credentials.User.Id) return false;
            session.Credentials = refreshed;
            return true;
        }
        finally { session.Gate.Release(); }
    }

    private static async Task RevokeAsync(IHttpClientFactory clients, string refreshToken, CancellationToken ct)
    {
        try { using HttpResponseMessage _ = await clients.CreateClient("pwa-api")
            .PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken }, ct); }
        catch (HttpRequestException) { /* A lost connection cannot block local sign-out. */ }
    }

    private static async Task<IResult> ForwardAsync(HttpResponseMessage response, CancellationToken ct)
        => Results.Content(await response.Content.ReadAsStringAsync(ct),
            response.Content.Headers.ContentType?.ToString() ?? "application/json",
            statusCode: (int)response.StatusCode);
}

public sealed record PwaLogin(string UserName, string Password, string? TwoFactorCode, Guid DeviceId, Guid LocationId);
public sealed record PwaHandoff(string Token, Guid DeviceId, Guid LocationId);
public sealed record PwaProvisionRequest(Guid UserId);
public sealed record PwaProvisionToken(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc,
    Guid UserId, string UserName, string DisplayName, IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Roles, Guid DeviceId, Guid LocationId);
public sealed record SnapshotOutcome(IResult? Error, object? Snapshot);
public sealed record PwaPush(IReadOnlyList<PwaEvent> Events);
public sealed record PwaEvent(Guid EventId, long DeviceSequence, string EventType, JsonElement Payload,
    DateTimeOffset OccurredAtUtc);

public sealed class PwaSession(Guid deviceId, Guid locationId, SignInResponse credentials)
{
    public Guid DeviceId { get; } = deviceId;
    public Guid LocationId { get; } = locationId;
    public SignInResponse Credentials { get; set; } = credentials;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTimeOffset ExpiresAtUtc { get; } = DateTimeOffset.UtcNow.AddDays(7);
}

public sealed class PwaSessions
{
    private readonly ConcurrentDictionary<string, PwaSession> sessions = new(StringComparer.Ordinal);
    public void Add(string key, PwaSession session) => sessions[key] = session;
    public PwaSession? Get(string key)
    {
        if (!sessions.TryGetValue(key, out PwaSession? session)) return null;
        if (session.ExpiresAtUtc > DateTimeOffset.UtcNow) return session;
        sessions.TryRemove(key, out _);
        return null;
    }
    public PwaSession? Remove(string key) => sessions.TryRemove(key, out PwaSession? session) ? session : null;
}
