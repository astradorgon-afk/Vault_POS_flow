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

        return request;
    }

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
