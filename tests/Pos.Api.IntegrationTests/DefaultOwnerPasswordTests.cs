using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Identity;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The public demo's owner starts on a published password, and the first
/// sign-in must replace it before any session is issued.
/// </summary>
[Collection("api")]
public sealed class DefaultOwnerPasswordTests
{
    private const string DefaultPassword = "cash1234";
    private const string ChosenPassword = "a-brand-new-owner-phrase";

    [Fact]
    public async Task DefaultPassword_GetsNoSession_UntilReplaced_AndIsNeverRestored()
    {
        await using PosApiFactory host = new(new Dictionary<string, string?>
        {
            ["DefaultOwnerPassword__Enabled"] = "true",
            ["DefaultOwnerPassword__UserName"] = "dp-owner",
            ["DefaultOwnerPassword__Password"] = DefaultPassword,
        });
        await host.InitializeAsync();

        await host.CreateUserAsync("dp-owner", Roles.Owner, tier: ApprovalTier.Unlimited);
        using HttpClient client = host.CreateClient();
        await SeedAsync();

        using (HttpResponseMessage old = await LoginAsync(PosApiFactory.TestPassword))
        {
            old.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the default replaces whatever the account had");
        }

        using (HttpResponseMessage refused = await LoginAsync(DefaultPassword))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(refused)).Should().Be("auth.password_change_required");
        }

        using (HttpResponseMessage wrong = await ChangeAsync("not-the-default", ChosenPassword))
        {
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (HttpResponseMessage tooShort = await ChangeAsync(DefaultPassword, "short1"))
        {
            tooShort.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(tooShort)).Should().Be("identity.password_rejected");
        }

        using (HttpResponseMessage changed = await ChangeAsync(DefaultPassword, ChosenPassword))
        {
            changed.StatusCode.Should().Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        }

        // A restart runs the seeder again; it must leave the chosen password alone.
        await SeedAsync();

        using (HttpResponseMessage stale = await LoginAsync(DefaultPassword))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (HttpResponseMessage signedIn = await LoginAsync(ChosenPassword))
        {
            signedIn.StatusCode.Should().Be(HttpStatusCode.OK, await signedIn.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage again = await ChangeAsync(ChosenPassword, "yet-another-owner-phrase"))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorCodeAsync(again)).Should().Be("auth.password_change_not_required");
        }

        async Task SeedAsync()
        {
            using IServiceScope scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DefaultOwnerPasswordSeeder>().SeedAsync(CancellationToken.None);
        }

        Task<HttpResponseMessage> LoginAsync(string password)
            => client.PostAsJsonAsync("/api/v1/auth/login", new { userName = "dp-owner", password });

        Task<HttpResponseMessage> ChangeAsync(string currentPassword, string newPassword)
            => client.PostAsJsonAsync(
                "/api/v1/auth/password/required-change",
                new { userName = "dp-owner", currentPassword, newPassword });
    }
}
