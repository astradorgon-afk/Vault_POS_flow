namespace Pos.Web.Services;

public sealed partial class VaultFlowApiClient
{
    /// <summary>Replaces a default password the API will not sign in with.</summary>
    public Task<ApiResult<bool>> ChangeRequiredPasswordAsync(
        string userName,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
        => GuardAsync(
            () => ChangeRequiredPasswordCoreAsync(userName, currentPassword, newPassword, cancellationToken),
            cancellationToken);

    private async Task<ApiResult<bool>> ChangeRequiredPasswordCoreAsync(
        string userName,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/auth/password/required-change",
            new { userName, currentPassword, newPassword },
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return ApiResult<bool>.Success(true);
        }

        ApiProblem? problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        string fallback = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "Too many attempts. Wait a minute, then try again."
            : "The password could not be changed. Try again.";
        return ApiResult<bool>.Failure(problem?.Detail ?? fallback, problem?.ErrorCode);
    }
}
