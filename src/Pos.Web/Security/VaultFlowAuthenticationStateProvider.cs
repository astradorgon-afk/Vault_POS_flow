using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Pos.Web.Services;

namespace Pos.Web.Security;

/// <summary>Projects the API session into Blazor authorization state.</summary>
/// <remarks>
/// This is also where a signed-in browser gets its session back. A circuit
/// starts with an empty <see cref="UserSession"/>, so without this a reload
/// would read as signed out even though the server still holds the session.
/// </remarks>
/// <param name="session">The circuit's session.</param>
/// <param name="api">The API client.</param>
/// <param name="restorer">Finds the session this browser already has.</param>
public sealed class VaultFlowAuthenticationStateProvider(
    UserSession session,
    VaultFlowApiClient api,
    WebSessionRestorer restorer) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));
    private bool restored;

    /// <inheritdoc />
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!restored)
        {
            restored = true;
            restorer.Restore();
        }

        try
        {
            // A circuit that idled past its access-token window refreshes the
            // session here instead of bouncing the operator to the sign-in page.
            await api.EnsureFreshSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The API is unreachable: keep the current state; the next API call
            // reports the connectivity problem on its own.
        }
        catch (TaskCanceledException)
        {
            // The refresh timed out: handled like an unreachable API.
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable refresh response: the next API call reports it.
        }

        return CreateState();
    }

    /// <summary>Notifies the component tree that sign-in succeeded.</summary>
    public void NotifySignedIn() => NotifyAuthenticationStateChanged(Task.FromResult(CreateState()));

    /// <summary>Ends the local session, and the stored one behind it.</summary>
    /// <remarks>
    /// Clearing only the circuit's copy would leave the browser holding a
    /// cookie that signs it straight back in on the next page load.
    /// </remarks>
    public void SignOut()
    {
        restorer.Discard();
        session.Clear();
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    private AuthenticationState CreateState()
    {
        if (!session.IsAuthenticated || session.Current is not { } current)
        {
            return Anonymous;
        }

        // The same projection the authorization middleware judges a page load
        // by, so a reload and a click agree on what this account may open.
        return new AuthenticationState(WebSessionClaims.Principal(current, "VaultFlowApi"));
    }
}
