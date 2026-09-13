using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Infrastructure.Identity;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Changes that reach beyond one account — editing a role, enforcing two-factor
/// sign-in — each on a host of its own, so the shared roles every other suite
/// relies on are never modified.
/// </summary>
[Collection("api")]
public sealed class IdentityGovernanceTests
{
    [Fact]
    public async Task RolePermissions_AreEditable_WithinTheSafeguards_AndStayEditedAcrossRestarts()
    {
        await using PosApiFactory host = new(new Dictionary<string, string?>());
        await host.InitializeAsync();

        LocationId store = await host.CreateLocationAsync("GV-1", "Governance Store", LocationKind.Store);
        await host.CreateUserAsync("gv-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        await host.CreateUserAsync("gv-cashier", Roles.Cashier, locations: [store]);
        using HttpClient client = host.CreateClient();
        string admin = await SignInAsync(client, "gv-admin");
        string cashier = await SignInAsync(client, "gv-cashier");

        using JsonDocument rolesDocument = await GetJsonAsync(client, "/api/v1/roles", admin);
        JsonElement cashierRole = rolesDocument.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == Roles.Cashier);
        Guid cashierRoleId = cashierRole.GetProperty("id").GetGuid();
        Guid ownerRoleId = rolesDocument.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == Roles.Owner).GetProperty("id").GetGuid();
        Guid adminRoleId = rolesDocument.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == Roles.Administrator).GetProperty("id").GetGuid();
        List<string> cashierPermissions = [.. cashierRole.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];

        using (HttpResponseMessage before = await SendAsync(client, HttpMethod.Get, "/api/v1/receipts", cashier))
        {
            before.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (HttpResponseMessage widened = await SendAsync(client, HttpMethod.Put, $"/api/v1/roles/{cashierRoleId}/permissions", admin,
            new { permissions = cashierPermissions.Append(Permissions.Receipts.View), reason = "Cashiers look up receipts for customers" }))
        {
            widened.StatusCode.Should().Be(HttpStatusCode.NoContent, await widened.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage after = await SendAsync(client, HttpMethod.Get, "/api/v1/receipts", cashier))
        {
            after.StatusCode.Should().Be(HttpStatusCode.OK, await after.Content.ReadAsStringAsync());
        }

        await ExpectAsync(cashierRoleId, cashierPermissions.Append(Permissions.Inventory.NegativeStock), "identity.privilege_escalation_forbidden");
        await ExpectAsync(ownerRoleId, [Permissions.Catalog.View], "identity.target_outranks_caller");
        await ExpectAsync(adminRoleId, [Permissions.Catalog.View], "identity.self_administration_forbidden");

        // Removing a default grant must survive the seeder that runs at every start.
        using (HttpResponseMessage narrowed = await SendAsync(client, HttpMethod.Put, $"/api/v1/roles/{cashierRoleId}/permissions", admin,
            new { permissions = cashierPermissions.Where(p => p != Permissions.Sales.Return), reason = "Returns go through the manager" }))
        {
            narrowed.StatusCode.Should().Be(HttpStatusCode.NoContent, await narrowed.Content.ReadAsStringAsync());
        }

        await host.WithServiceAsync<IdentitySeeder>(async seeder => await seeder.SeedAsync(CancellationToken.None));
        (await CashierHoldsAsync(Permissions.Sales.Return)).Should().BeFalse("an administrator's removal is not undone by a restart");

        // A permission the catalogue has never applied to the role still arrives.
        await host.WithServiceAsync(async context =>
        {
            await context.RoleDefaultGrantsApplied
                .Where(g => g.RoleName == Roles.Cashier && g.PermissionCode == Permissions.Sales.Return)
                .ExecuteDeleteAsync();
            return true;
        });
        await host.WithServiceAsync<IdentitySeeder>(async seeder => await seeder.SeedAsync(CancellationToken.None));
        (await CashierHoldsAsync(Permissions.Sales.Return)).Should().BeTrue("a default not yet applied is applied once");

        using JsonDocument catalogue = await GetJsonAsync(client, "/api/v1/permissions", admin);
        JsonElement[] permissions = [.. catalogue.RootElement.EnumerateArray()];
        permissions.Single(p => p.GetProperty("code").GetString() == Permissions.Administration.ManageUsers).GetProperty("isPrivileged").GetBoolean().Should().BeTrue();
        permissions.Single(p => p.GetProperty("code").GetString() == Permissions.Sales.Create).GetProperty("isPrivileged").GetBoolean().Should().BeFalse();

        async Task ExpectAsync(Guid roleId, IEnumerable<string> codes, string errorCode)
        {
            using HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, $"/api/v1/roles/{roleId}/permissions", admin,
                new { permissions = codes, reason = "Attempted change for the test" });
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
        }

        Task<bool> CashierHoldsAsync(string code) => host.WithServiceAsync(context => (
                from grant in context.RolePermissions
                join role in context.Roles on grant.RoleId equals role.Id
                where role.Name == Roles.Cashier && grant.PermissionCode == code
                select grant)
            .AnyAsync());
    }

    [Fact]
    public async Task TwoFactor_IsRequiredForAdministrators_WhoEnrolWithTheirPassword_ThenSignInWithACode()
    {
        await using PosApiFactory host = new(new Dictionary<string, string?>
        {
            ["Security__RequireTwoFactorForAdmins"] = "true",
        });
        await host.InitializeAsync();

        LocationId store = await host.CreateLocationAsync("TF-1", "Two-Factor Store", LocationKind.Store);
        await host.CreateUserAsync("tf-admin", Roles.Administrator, tier: ApprovalTier.Tier3);
        await host.CreateUserAsync("tf-cashier", Roles.Cashier, locations: [store]);
        using HttpClient client = host.CreateClient();

        // Cashiers hold no administrative authority, so nothing changes for them.
        (await SignInAsync(client, "tf-cashier")).Should().NotBeNullOrEmpty();

        using (HttpResponseMessage refused = await LoginAsync("tf-admin", null))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(refused)).Should().Be("auth.two_factor_enrolment_required");
        }

        string sharedKey;
        using (HttpResponseMessage setup = await client.PostAsJsonAsync(
            "/api/v1/auth/two-factor/setup", new { userName = "tf-admin", password = PosApiFactory.TestPassword }))
        {
            setup.StatusCode.Should().Be(HttpStatusCode.OK, await setup.Content.ReadAsStringAsync());
            using JsonDocument body = await ReadJsonAsync(setup);
            sharedKey = body.RootElement.GetProperty("sharedKey").GetString()!;
            body.RootElement.GetProperty("authenticatorUri").GetString().Should().StartWith("otpauth://totp/VaultFlow:tf-admin?secret=");
        }

        using (HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/v1/auth/two-factor/enable",
            new { userName = "tf-admin", password = PosApiFactory.TestPassword, code = "000000" }))
        {
            wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(wrong)).Should().Be("identity.two_factor_code_invalid");
        }

        List<string> recoveryCodes;
        using (HttpResponseMessage enabled = await client.PostAsJsonAsync("/api/v1/auth/two-factor/enable",
            new { userName = "tf-admin", password = PosApiFactory.TestPassword, code = Totp(sharedKey) }))
        {
            enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
            using JsonDocument body = await ReadJsonAsync(enabled);
            recoveryCodes = [.. body.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!)];
        }

        recoveryCodes.Should().HaveCount(8);

        using (HttpResponseMessage again = await client.PostAsJsonAsync(
            "/api/v1/auth/two-factor/setup", new { userName = "tf-admin", password = PosApiFactory.TestPassword }))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadErrorCodeAsync(again)).Should().Be("identity.two_factor_already_enabled");
        }

        using (HttpResponseMessage withoutCode = await LoginAsync("tf-admin", null))
        {
            withoutCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorCodeAsync(withoutCode)).Should().Be("auth.two_factor_required");
        }

        (await SignInAsync(client, "tf-admin", Totp(sharedKey))).Should().NotBeNullOrEmpty();

        // A recovery code works once, for the day the phone is lost.
        (await SignInAsync(client, "tf-admin", recoveryCodes[0])).Should().NotBeNullOrEmpty();

        string storedCodes = await host.WithServiceAsync(async context => await context.Set<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>()
            .AsNoTracking()
            .Where(t => t.Name == "RecoveryCodes")
            .Select(t => t.Value!)
            .SingleAsync());
        storedCodes.Split(';').Should().NotContain(recoveryCodes[0], "a redeemed recovery code is consumed")
            .And.HaveCount(7);

        using (HttpResponseMessage reused = await LoginAsync("tf-admin", recoveryCodes[0]))
        {
            reused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // A lost phone: the owner, enrolled themselves, resets the administrator,
        // who must enrol again with a genuinely new key.
        UserId adminId = await host.WithServiceAsync(async context =>
            new UserId((await context.Users.SingleAsync(u => u.UserName == "tf-admin")).Id));
        await host.CreateUserAsync("tf-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        string ownerKey = await EnrolAsync("tf-owner");
        string owner = await SignInAsync(client, "tf-owner", Totp(ownerKey));

        using (HttpResponseMessage reset = await SendAsync(
            client, HttpMethod.Post, $"/api/v1/users/{adminId.Value}/two-factor/reset", owner, new { reason = "Phone lost on the way home" }))
        {
            reset.StatusCode.Should().Be(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage mustEnrol = await LoginAsync("tf-admin", Totp(sharedKey)))
        {
            mustEnrol.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorCodeAsync(mustEnrol)).Should().Be("auth.two_factor_enrolment_required");
        }

        string rotatedKey = await EnrolAsync("tf-admin");
        rotatedKey.Should().NotBe(sharedKey, "a reset must rotate the authenticator key, not just switch two-factor off");
        (await SignInAsync(client, "tf-admin", Totp(rotatedKey))).Should().NotBeNullOrEmpty();

        async Task<string> EnrolAsync(string userName)
        {
            using HttpResponseMessage setup = await client.PostAsJsonAsync(
                "/api/v1/auth/two-factor/setup", new { userName, password = PosApiFactory.TestPassword });
            setup.StatusCode.Should().Be(HttpStatusCode.OK, await setup.Content.ReadAsStringAsync());
            string key;
            using (JsonDocument body = await ReadJsonAsync(setup))
            {
                key = body.RootElement.GetProperty("sharedKey").GetString()!;
            }

            using HttpResponseMessage enable = await client.PostAsJsonAsync(
                "/api/v1/auth/two-factor/enable", new { userName, password = PosApiFactory.TestPassword, code = Totp(key) });
            enable.StatusCode.Should().Be(HttpStatusCode.OK, await enable.Content.ReadAsStringAsync());
            return key;
        }

        Task<HttpResponseMessage> LoginAsync(string userName, string? code)
            => client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password = PosApiFactory.TestPassword, twoFactorCode = code });
    }

    /// <summary>RFC 6238 time-based code for the current 30-second step, as an authenticator app computes it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 and every authenticator app use HMAC-SHA1; the test must compute the same code.")]
    private static string Totp(string groupedBase32Key)
    {
        byte[] key = Base32Decode(groupedBase32Key.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant());
        long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        byte[] hash = HMACSHA1.HashData(key, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> output = [];
        int buffer = 0;
        int bits = 0;

        foreach (char c in input.TrimEnd('='))
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
