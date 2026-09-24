using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pos.DemoData;

/// <summary>A signed-in development account, optionally bound to a register.</summary>
/// <param name="UserName">The account's user name.</param>
/// <param name="UserId">The account's id.</param>
/// <param name="AccessToken">The bearer token for API calls.</param>
/// <param name="DeviceId">The register the session signed in at, or null for back-office work.</param>
internal sealed record DemoSession(string UserName, Guid UserId, string AccessToken, Guid? DeviceId = null)
{
    /// <summary>The key a renewed token is stored under.</summary>
    public string Key => DeviceId is { } device ? $"{UserName}@{device:D}" : UserName;
}

/// <summary>An API call the server refused, with the problem detail it returned.</summary>
internal sealed class DemoApiException : Exception
{
    public DemoApiException()
    {
    }

    public DemoApiException(string message)
        : base(message)
    {
    }

    public DemoApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DemoApiException(string message, string? errorCode, int statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    /// <summary>The machine-readable error code from the problem document, when present.</summary>
    public string? ErrorCode { get; }

    /// <summary>The HTTP status the API answered with, or 0 when the failure was local.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// Calls the VaultFlow HTTP API exactly as the web and desktop clients do: a
/// bearer token per account and, for register commands, the register's
/// <c>X-Device-Id</c>. Nothing here touches the database directly, so every
/// demo record passes the same validation, ledger posting and audit as a real one.
/// </summary>
internal sealed class DemoApi(HttpClient http) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Access tokens are short-lived; each session's current token and
    /// password are kept so an expired token is renewed by signing in again.</summary>
    private readonly Dictionary<string, (string Password, string Token)> _tokens = [];

    public const string DeviceHeader = "X-Device-Id";

    /// <summary>Signs in with a development account. A register signs its
    /// cashier in bound to the device, as the desktop and phone apps do.</summary>
    public async Task<DemoSession> SignInAsync(string userName, string password, Guid? deviceId = null)
    {
        JsonNode response = await SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", new { userName, password }, session: null, deviceId)
            ?? throw new DemoApiException($"Sign-in for {userName} returned no body.");

        string token = response["accessToken"]?.GetValue<string>()
            ?? throw new DemoApiException($"Sign-in for {userName} returned no access token.");
        Guid userId = response["user"]?["userId"]?.GetValue<Guid>()
            ?? throw new DemoApiException($"Sign-in for {userName} returned no user id.");

        DemoSession session = new(userName, userId, token, deviceId);
        _tokens[session.Key] = (password, token);
        return session;
    }

    /// <summary>Calls the API. A session bound to a register sends its device id
    /// on every call, as the register does.</summary>
    public Task<JsonNode?> GetAsync(string path, DemoSession session)
        => SendAsync(HttpMethod.Get, path, body: null, session, session.DeviceId);

    public Task<JsonNode?> PostAsync(string path, object? body, DemoSession session)
        => SendAsync(HttpMethod.Post, path, body ?? new { }, session, session.DeviceId);

    /// <summary>Posts without a session: device enrolment, whose one-time code is the credential.</summary>
    public Task<JsonNode?> PostAnonymousAsync(string path, object body)
        => SendAsync(HttpMethod.Post, path, body, session: null, deviceId: null);

    /// <summary>Posts a command and returns the <c>id</c> the API answered with.</summary>
    public async Task<Guid> CreateAsync(string path, object body, DemoSession session)
    {
        JsonNode? response = await PostAsync(path, body, session);
        // Most commands answer { id }; a few name the key after the resource
        // (device registration answers { deviceId, ... }).
        return (response?["id"] ?? response?["deviceId"])?.GetValue<Guid>()
            ?? throw new DemoApiException($"POST {path} returned no id.");
    }

    /// <summary>Returns the first JSON array in a response: the body itself, or
    /// the list property of a paged envelope.</summary>
    public static JsonArray Items(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                if (property.Value is JsonArray inner)
                {
                    return inner;
                }
            }
        }

        return [];
    }

    public void Dispose() => http.Dispose();

    private async Task<JsonNode?> SendAsync(
        HttpMethod method, string path, object? body, DemoSession? session, Guid? deviceId)
    {
        // The API rate-limits sign-in (ten per five minutes by default). A demo
        // run signs a dozen sessions in, so a refused attempt waits and tries
        // again for longer than one full window.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(method, path, body, session, deviceId);
            }
            catch (RateLimitedException ex) when (attempt < 15)
            {
                Console.WriteLine($"  rate limited on {path}; waiting {ex.Wait.TotalSeconds:0}s");
                await Task.Delay(ex.Wait);
            }
            catch (TokenExpiredException) when (attempt < 6 && session is not null && _tokens.ContainsKey(session.Key))
            {
                await SignInAsync(session.UserName, _tokens[session.Key].Password, session.DeviceId);
            }
        }
    }

    private async Task<JsonNode?> SendOnceAsync(
        HttpMethod method, string path, object? body, DemoSession? session, Guid? deviceId)
    {
        using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));

        if (session is not null)
        {
            string token = _tokens.TryGetValue(session.Key, out (string Password, string Token) current)
                ? current.Token
                : session.AccessToken;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (deviceId is { } device)
        {
            request.Headers.Add(DeviceHeader, device.ToString("D", CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && session is not null)
        {
            throw new TokenExpiredException();
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitedException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = text;
            string? code = null;

            try
            {
                JsonNode? problem = JsonNode.Parse(text);
                detail = problem?["detail"]?.GetValue<string>() ?? problem?["title"]?.GetValue<string>() ?? text;
                code = problem?["errorCode"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                // Not a problem document; the raw body is the best detail available.
            }

            throw new DemoApiException(
                string.Create(CultureInfo.InvariantCulture, $"{method} {path} -> {(int)response.StatusCode}: {detail}"),
                code,
                (int)response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}

/// <summary>The API answered 429; <see cref="Wait"/> is how long to back off.</summary>
internal sealed class RateLimitedException(TimeSpan wait) : Exception("The API rate limit was reached.")
{
    public TimeSpan Wait { get; } = wait;
}

/// <summary>The API answered 401 to a signed-in call: the access token expired.</summary>
internal sealed class TokenExpiredException() : Exception("The access token expired.");
