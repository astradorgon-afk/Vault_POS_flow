using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Security.Tests;

/// <summary>
/// Sign-in, token lifetime, rotation and revocation, exercised through the real
/// HTTP pipeline.
/// </summary>
[Collection("api")]
public sealed class AuthenticationTests(PosApiFactory factory)
{
    private static readonly LocationId MainWarehouse = LocationId.New();

    [Fact]
    public async Task ValidCredentials_ReturnATokenPairAndTheCallersPermissions()
    {
        await factory.CreateUserAsync("auth-owner", Roles.Owner, [MainWarehouse], ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();

        TokenPair pair = await SignInAsync(client, "auth-owner");

        pair.AccessToken.Should().NotBeNullOrWhiteSpace();
        pair.RefreshToken.Should().NotBeNullOrWhiteSpace();
        pair.AccessTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);

        // The owner holds everything, so the client can render the full
        // interface without a second round trip.
        pair.Permissions.Should().Contain(Permissions.Inventory.ApproveAdjustment);
    }

    [Fact]
    public async Task WrongPassword_IsRefused()
    {
        await factory.CreateUserAsync("auth-wrongpw", Roles.Cashier, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "auth-wrongpw", password = "not-the-password" },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).Should().Be("auth.invalid_credentials");
    }

    [Fact]
    public async Task UnknownUser_AndWrongPassword_AreIndistinguishable()
    {
        await factory.CreateUserAsync("auth-known", Roles.Cashier, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage missing = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "nobody-by-that-name", password = "whatever" },
            CancellationToken.None);

        using HttpResponseMessage wrong = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "auth-known", password = "whatever" },
            CancellationToken.None);

        // Distinguishing the two would turn a password guess into a two-step
        // problem an attacker can solve half of for free.
        missing.StatusCode.Should().Be(wrong.StatusCode);
        (await ErrorCodeAsync(missing)).Should().Be(await ErrorCodeAsync(wrong));
    }

    [Fact]
    public async Task DisabledAccount_CannotSignIn()
    {
        UserId userId = await factory.CreateUserAsync("auth-disabled", Roles.Cashier, [MainWarehouse]);

        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            Guid id = userId.Value;
            Infrastructure.Identity.AppUser user = await context.Users.AsTracking().FirstAsync(u => u.Id == id);
            user.IsActive = false;
            user.DisabledAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "auth-disabled", password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).Should().Be("auth.account_disabled");
    }

    [Fact]
    public async Task RefreshRotatesTheToken_AndTheOldOneStopsWorking()
    {
        await factory.CreateUserAsync("auth-rotate", Roles.Auditor, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        TokenPair first = await SignInAsync(client, "auth-rotate");
        TokenPair second = await RefreshAsync(client, first.RefreshToken);

        second.RefreshToken.Should().NotBe(first.RefreshToken, "each refresh issues a fresh single-use token");
        second.AccessToken.Should().NotBeNullOrWhiteSpace();

        using HttpResponseMessage replay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = first.RefreshToken },
            CancellationToken.None);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReusingARotatedToken_BurnsTheWholeFamily()
    {
        await factory.CreateUserAsync("auth-reuse", Roles.Auditor, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        TokenPair first = await SignInAsync(client, "auth-reuse");
        TokenPair second = await RefreshAsync(client, first.RefreshToken);

        // Presenting the spent token is evidence that someone copied it. We
        // cannot tell whether the thief or the rightful holder is the one still
        // using the successor, so both go.
        using HttpResponseMessage replay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = first.RefreshToken },
            CancellationToken.None);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(replay)).Should().Be("auth.refresh_token_reuse");

        using HttpResponseMessage successor = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = second.RefreshToken },
            CancellationToken.None);

        successor.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the successor is revoked along with the rest of the family");
    }

    [Fact]
    public async Task SignOut_EndsTheSession()
    {
        await factory.CreateUserAsync("auth-signout", Roles.Auditor, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        TokenPair pair = await SignInAsync(client, "auth-signout");

        using HttpResponseMessage signOut = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = pair.RefreshToken },
            CancellationToken.None);

        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage refresh = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = pair.RefreshToken },
            CancellationToken.None);

        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignOut_WithAnUnknownToken_LooksIdenticalToSuccess()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = "not-a-real-token" },
            CancellationToken.None);

        // Reporting the difference would confirm to an attacker whether a token
        // they hold is still live.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DisablingAUser_InvalidatesTheirOutstandingAccessToken()
    {
        UserId userId = await factory.CreateUserAsync("auth-revoked", Roles.Auditor, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        TokenPair pair = await SignInAsync(client, "auth-revoked");

        using (HttpResponseMessage before = await GetAsync(client, "/api/v1/auth/me", pair.AccessToken))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await factory.WithServiceAsync<IAuthenticationService>(async auth =>
            await auth.RevokeAllSessionsAsync(userId, "disabled by a test", CancellationToken.None));

        // The access token is still inside its ten-minute window and still has a
        // valid signature. The security-stamp check is what closes that window.
        using HttpResponseMessage after = await GetAsync(client, "/api/v1/auth/me", pair.AccessToken);

        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PinSignIn_RequiresAnEnrolledDevice()
    {
        await factory.CreateUserAsync(
            "auth-pin-nodevice", Roles.Cashier, [MainWarehouse], employeeCode: "9001");

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login/pin",
            new { employeeCode = "9001", pin = PosApiFactory.TestPin },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("auth.device_header_required");
    }

    [Fact]
    public async Task PinSignIn_AtARevokedDevice_IsRefused()
    {
        await factory.CreateUserAsync(
            "auth-pin-revoked", Roles.Cashier, [MainWarehouse], employeeCode: "9002");

        DeviceId device = await factory.CreateDeviceAsync("R01", MainWarehouse, DeviceStatus.Revoked);

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", device.Value.ToString());

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login/pin",
            new { employeeCode = "9002", pin = PosApiFactory.TestPin },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("auth.device_not_operational");
    }

    [Fact]
    public async Task PinSignIn_AtAnotherStoresTerminal_IsRefused()
    {
        LocationId otherStore = LocationId.New();

        await factory.CreateUserAsync(
            "auth-pin-elsewhere", Roles.Cashier, [MainWarehouse], employeeCode: "9003");

        DeviceId device = await factory.CreateDeviceAsync("S99", otherStore);

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", device.Value.ToString());

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login/pin",
            new { employeeCode = "9003", pin = PosApiFactory.TestPin },
            CancellationToken.None);

        // Correct credentials at the wrong till. This is the difference between
        // a cashier covering their own store and one quietly working another.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("auth.location_not_permitted");
    }

    [Fact]
    public async Task PinSignIn_GrantsOnlyOfflineCapablePermissions()
    {
        await factory.CreateUserAsync(
            "auth-pin-manager", Roles.StoreManager, [MainWarehouse], ApprovalTier.Tier1, employeeCode: "9004");

        DeviceId device = await factory.CreateDeviceAsync("P01", MainWarehouse);

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", device.Value.ToString());

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login/pin",
            new { employeeCode = "9004", pin = PosApiFactory.TestPin },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TokenPair pair = await ReadPairAsync(response);

        pair.Permissions.Should().Contain(Permissions.Sales.Create);

        // A PIN is typed on a shared screen in front of customers. It buys a
        // till session, not the manager's approval authority.
        pair.Permissions.Should().NotContain(Permissions.Inventory.ApproveAdjustment);
        pair.Permissions.Should().NotContain(Permissions.Sales.ExpiredOverride);
        pair.Permissions.Should().OnlyContain(p => Permissions.Find(p)!.IsOfflineCapable);
    }

    [Fact]
    public async Task TamperedTokenSignature_IsRejected()
    {
        await factory.CreateUserAsync("auth-tamper", Roles.Auditor, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        TokenPair pair = await SignInAsync(client, "auth-tamper");

        string[] segments = pair.AccessToken.Split('.');
        segments[2] = segments[2].Length > 4
            ? segments[2][..^4] + "AAAA"
            : "AAAA";

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/auth/me", string.Join('.', segments));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        // The "alg: none" family: a token whose header claims it needs no
        // signature. Pinning the accepted algorithm is what stops it.
        const string header = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0";
        const string payload = "eyJzdWIiOiIwMTkyZjJhMS0wMDAwLTcwMDAtODAwMC0wMDAwMDAwMDAwMDAifQ";

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await GetAsync(client, "/api/v1/auth/me", $"{header}.{payload}.");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousRequest_ToAProtectedEndpoint_IsUnauthorized()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/devices", UriKind.Relative), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EveryAttempt_IsRecorded_WithoutStoringTheIdentifier()
    {
        await factory.CreateUserAsync("auth-recorded", Roles.Cashier, [MainWarehouse]);
        using HttpClient client = factory.CreateClient();

        using (HttpResponseMessage _ = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = "auth-recorded", password = "wrong" },
            CancellationToken.None))
        {
        }

        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            List<LoginAttempt> attempts = await context.LoginAttempts
                .Where(a => !a.Succeeded)
                .ToListAsync();

            attempts.Should().NotBeEmpty();

            // The identifier is hashed. Recording it raw would turn this table
            // into a list of usernames, and would capture a password typed into
            // the username box by mistake.
            attempts.Should().OnlyContain(a => a.IdentifierHash.Length == 32);
        });
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<TokenPair> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return await ReadPairAsync(response);
    }

    private static async Task<TokenPair> RefreshAsync(HttpClient client, string refreshToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return await ReadPairAsync(response);
    }

    private static async Task<TokenPair> ReadPairAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        JsonElement root = document.RootElement;

        return new TokenPair(
            root.GetProperty("accessToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("accessTokenExpiresAtUtc").GetDateTimeOffset(),
            [.. root.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)]);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private sealed record TokenPair(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset AccessTokenExpiresAtUtc,
        IReadOnlyList<string> Permissions);
}
