using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Shared.Sync;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// What a register downloads. Two promises hold the whole thing up: a device is
/// served its own store's changes and nobody else's, and a cursor it stores is
/// one it can come back with and miss nothing.
/// </summary>
[Collection("api")]
public sealed class SyncPullEndpointTests(PosApiFactory factory)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task ADeviceIsServedGlobalChangesAndItsOwnStores_AndNotAnotherStores()
    {
        Seed seed = await SeedAsync("pua");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        SyncPullResponse page = await PullAsync(client, token, cursor: 0);

        page.Changes.Should().NotBeEmpty();

        IReadOnlyList<Guid> scopes = await ScopesAsync(page);

        scopes.Should().OnlyContain(
            id => id == Guid.Empty || id == seed.Store.Value,
            "a register in one shop has no business holding another shop's prices");

        // The product is everybody's, and the store's own price is this store's.
        page.Changes.Should().Contain(c => c.Kind == "ProductChanged");
        page.Changes.Should().Contain(c => c.Kind == "ProductPriceChanged");
    }

    [Fact]
    public async Task IdentifiersArriveAsBareGuids_SoADeviceCanReadItsOwnCatalogue()
    {
        Seed seed = await SeedAsync("pub");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        SyncPullResponse page = await PullAsync(client, token, cursor: 0);

        // The suite shares a database, so the feed holds every test's catalogue.
        // The same product also appears more than once — creating it and pricing
        // it both stamp the aggregate — which is exactly how the feed is meant to
        // read: a device applies them in order and ends on the latest.
        SyncPullChange product = page.Changes.Last(c => c.Kind == "ProductChanged"
            && c.Change.GetProperty("productId").GetGuid() == seed.Product.Value);

        product.Change.GetProperty("sku").GetString().Should().Be(
            seed.Sku.ToUpperInvariant(), "the feed carries the SKU as the catalogue normalised it, not as it was typed");
        product.Change.GetProperty("sequence").GetInt64().Should().BePositive();
    }

    [Fact]
    public async Task TheCursorRunsToTheEndOfTheFeed_SoSkippedChangesAreNotRescannedForEver()
    {
        Seed seed = await SeedAsync("puc");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        // Another store's price. This device is never served it, and if its
        // cursor stopped at the last change it *was* served, it would step over
        // this row on every pull from now on.
        LocationId elsewhere = await factory.CreateLocationAsync("PU-ELSE", "Somewhere else", LocationKind.Store);
        await PriceAsync(client, seed, 99m, elsewhere);

        long highest = await factory.WithServiceAsync(context => context.ChangeFeed
            .AsNoTracking()
            .Select(e => e.Sequence)
            .MaxAsync());

        SyncPullResponse page = await PullAsync(client, token, cursor: 0);

        page.NextCursor.Should().Be(highest);
        page.Changes.Should().NotContain(c => c.Kind == "ProductPriceChanged"
            && c.Change.GetProperty("locationId").GetGuid() == elsewhere.Value);
    }

    [Fact]
    public async Task ASecondPullFromTheStoredCursor_BringsOnlyWhatIsNew()
    {
        Seed seed = await SeedAsync("pud");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        SyncPullResponse first = await PullAsync(client, token, cursor: 0);

        SyncPullResponse quiet = await PullAsync(client, token, first.NextCursor);
        quiet.Changes.Should().BeEmpty("nothing has happened since");
        quiet.NextCursor.Should().Be(first.NextCursor);

        await PriceAsync(client, seed, 77m, seed.Store);

        SyncPullResponse second = await PullAsync(client, token, first.NextCursor);

        second.Changes.Should().NotBeEmpty();
        second.Changes.Should().OnlyContain(c => c.Change.GetProperty("sequence").GetInt64() > first.NextCursor);
    }

    [Fact]
    public async Task APageIsCappedAndTheCursorStopsAtItsLastChange()
    {
        Seed seed = await SeedAsync("pue");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        SyncPullResponse page = await PullAsync(client, token, cursor: 0, limit: 1);

        page.Changes.Should().ContainSingle();
        page.NextCursor.Should().Be(
            page.Changes[0].Change.GetProperty("sequence").GetInt64(),
            "a full page may have more behind it, so the cursor must not run past what was served");
    }

    [Fact]
    public async Task ADeviceAheadOfTheFeed_IsSentForAFreshBaseline()
    {
        Seed seed = await SeedAsync("puf");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        // A cursor from a server since restored from backup. Serving from zero
        // would silently replay changes this device already applied.
        using HttpResponseMessage response = await GetAsync(client, token, cursor: 999_999);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("action").GetString().Should().Be("rebaseline");
    }

    [Fact]
    public async Task ADeviceWhoseMissedChangesAreGone_IsSentForAFreshBaseline()
    {
        Seed seed = await SeedAsync("pug");
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, seed);

        long highest = await factory.WithServiceAsync(context => context.ChangeFeed
            .AsNoTracking()
            .Select(e => e.Sequence)
            .MaxAsync());

        // Everything the device missed has been pruned. A page with a hole in it
        // would leave the register quietly wrong about its own catalogue, so the
        // server says so instead.
        await factory.WithServiceAsync(context => context.ChangeFeed
            .Where(e => e.Sequence < highest)
            .ExecuteDeleteAsync());

        using HttpResponseMessage response = await GetAsync(client, token, cursor: 1);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ACallerThatIsNotADevice_IsRefused()
    {
        Seed seed = await SeedAsync("puh");
        using HttpClient client = factory.CreateClient();

        // Signed in at a desk, not at a till: there is no register to scope the
        // feed to, and guessing one would hand somebody a store's catalogue.
        string owner = await OwnerTokenAsync(client, seed);

        using HttpResponseMessage response = await GetAsync(client, owner, cursor: 0);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<IReadOnlyList<Guid>> ScopesAsync(SyncPullResponse page)
    {
        List<long> sequences = [.. page.Changes.Select(c => c.Change.GetProperty("sequence").GetInt64())];

        List<Guid?> scopes = await factory.WithServiceAsync(context => context.ChangeFeed
            .AsNoTracking()
            .Where(e => sequences.Contains(e.Sequence))
            .Select(e => e.LocationScopeId)
            .ToListAsync());

        return [.. scopes.Select(s => s ?? Guid.Empty)];
    }

    private static async Task<SyncPullResponse> PullAsync(
        HttpClient client,
        string token,
        long cursor,
        int? limit = null)
    {
        using HttpResponseMessage response = await GetAsync(client, token, cursor, limit);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonSerializer.Deserialize<SyncPullResponse>(body, ReadOptions)!;
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string token,
        long cursor,
        int? limit = null)
    {
        string path = FormattableString.Invariant($"/api/v1/sync/pull?cursor={cursor}")
            + (limit is { } take ? FormattableString.Invariant($"&limit={take}") : string.Empty);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<string> SignInAsync(HttpClient client, Seed seed)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/auth/login/pin", UriKind.Relative))
        {
            Content = JsonContent.Create(new { employeeCode = seed.EmployeeCode, pin = PosApiFactory.TestPin }),
        };
        request.Headers.Add(
            "X-Device-Id",
            seed.Device.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> OwnerTokenAsync(HttpClient client, Seed seed)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName = seed.OwnerUserName, password = PosApiFactory.TestPassword },
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task PriceAsync(HttpClient client, Seed seed, decimal amount, LocationId location)
    {
        string owner = await OwnerTokenAsync(client, seed);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"/api/v1/catalog/products/{seed.Product.Value}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { amount, reason = "Pull test price", locationId = location.Value }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<Seed> SeedAsync(string suffix)
    {
        LocationId store = await factory.CreateLocationAsync($"PU-{suffix}", $"Pull Store {suffix}", LocationKind.Store);

        string employeeCode = $"pl{suffix}";
        await factory.CreateUserAsync(
            $"pull-{suffix}-sm", Roles.StoreManager, locations: [store], employeeCode: employeeCode);

        DeviceId device = await factory.CreateDeviceAsync($"PU{suffix[^1]}", store);

        string sku = $"PU-{suffix}-P1";
        ProductId product = await factory.CreateProductAsync(sku, $"Pull Product {suffix}", $"7200{suffix}0001");

        await factory.CreateUserAsync($"pull-{suffix}-owner", Roles.Owner);

        Seed seed = new(store, employeeCode, device, product, sku, $"pull-{suffix}-owner");

        using HttpClient client = factory.CreateClient();
        await PriceAsync(client, seed, 45m, store);

        return seed;
    }

    private sealed record Seed(
        LocationId Store,
        string EmployeeCode,
        DeviceId Device,
        ProductId Product,
        string Sku,
        string OwnerUserName);
}
