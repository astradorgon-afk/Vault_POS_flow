using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Pos.Web.Services;

namespace Pos.Web.Security;

/// <summary>Projects the API session into Blazor authorization state.</summary>
public sealed class VaultFlowAuthenticationStateProvider(UserSession session) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(CreateState());

    /// <summary>Notifies the component tree that sign-in succeeded.</summary>
    public void NotifySignedIn() => NotifyAuthenticationStateChanged(Task.FromResult(CreateState()));

    /// <summary>Ends the local session.</summary>
    public void SignOut()
    {
        session.Clear();
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    private AuthenticationState CreateState()
    {
        if (!session.IsAuthenticated || session.Current is not { } current)
        {
            return Anonymous;
        }

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, current.User.Id.ToString("D")),
            new(ClaimTypes.Name, current.User.DisplayName),
        ];
        claims.AddRange(current.User.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(current.User.Permissions.Select(permission => new Claim("permission", permission)));
        claims.AddRange(current.User.Locations.Select(location => new Claim("location", location.ToString("D"))));

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "VaultFlowApi")));
    }
}
