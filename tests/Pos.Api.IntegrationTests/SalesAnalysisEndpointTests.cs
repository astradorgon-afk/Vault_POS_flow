using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The sales analysis of ROADMAP §Phase 15, over real HTTP. The arithmetic is
/// pinned by the repository's own tests; what is tested here is the part a
/// repository cannot enforce — that a report covers the stores the caller may
/// see and no others, and that a request cannot ask for a database's whole
/// history in one go.
/// </summary>
[Collection("api")]
public sealed class SalesAnalysisEndpointTests(PosApiFactory factory)
{
    private static readonly DateOnly Day = new(2026, 9, 18);

    [Fact]
    public async Task AManagerCannotReportOnAStoreTheyAreNotAssignedTo()
    {
        LocationId mine = await factory.CreateLocationAsync("SA-M1", "Analysis Mine", LocationKind.Store);
        LocationId theirs = await factory.CreateLocationAsync("SA-T1", "Analysis Theirs", LocationKind.Store);

        await factory.CreateUserAsync("sa-m1-sm", Roles.StoreManager, locations: [mine]);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "sa-m1-sm");

        using HttpResponseMessage refused = await GetAsync(
            client, Report(Day, Day, locationId: theirs.Value), manager);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("report.outside_scope");

        // And their own store answers, so the refusal is about the store rather
        // than about the permission.
        using HttpResponseMessage allowed = await GetAsync(
            client, Report(Day, Day, locationId: mine.Value), manager);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AManagerAssignedToNothing_IsToldSo_RatherThanShownZeroes()
    {
        await factory.CreateUserAsync("sa-none-sm", Roles.StoreManager, locations: []);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "sa-none-sm");

        using HttpResponseMessage response = await GetAsync(client, Report(Day, Day), manager);

        // Zeroes would read as "the business sold nothing", which is a worse
        // answer than being told the question does not apply to them.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("report.no_scope");
    }

    [Fact]
    public async Task APeriodThatRunsBackwards_OrRunsTooLong_IsRefused()
    {
        await factory.CreateUserAsync("sa-owner", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sa-owner");

        using HttpResponseMessage backwards = await GetAsync(
            client, Report(Day, Day.AddDays(-1)), owner);

        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(backwards)).Should().Be("report.period_invalid");

        // A report is the easiest way to ask a production database for every row
        // it has ever held.
        using HttpResponseMessage forever = await GetAsync(
            client, Report(Day.AddYears(-50), Day), owner);

        forever.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(forever)).Should().Be("report.period_invalid");
    }

    [Fact]
    public async Task AnUnknownCutIsRefused_RatherThanQuietlyBecomingTheDefault()
    {
        await factory.CreateUserAsync("sa-owner2", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sa-owner2");

        using HttpResponseMessage response = await GetAsync(
            client, Report(Day, Day) + "&groupBy=99", owner);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("report.grouping_unknown");
    }

    [Fact]
    public async Task AnOwnerSeesEveryStore_AndTheShapeIsTheOneTheClientReads()
    {
        await factory.CreateUserAsync("sa-owner3", Roles.Owner);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "sa-owner3");

        using HttpResponseMessage response = await GetAsync(client, Report(Day, Day) + "&groupBy=Category", owner);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        root.GetProperty("groupBy").GetString().Should().Be("Category");
        root.GetProperty("fromDate").GetString().Should().Be("2026-09-18");
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();
        root.GetProperty("rows").ValueKind.Should().Be(JsonValueKind.Array);

        // The totals row is always present, even over a period that sold nothing,
        // so a client never has to decide what an absent total means.
        JsonElement totals = root.GetProperty("totals");
        totals.GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Number);
        totals.GetProperty("marginPercent").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task ThePaymentBreakdownIsScopedTheSameWay()
    {
        LocationId theirs = await factory.CreateLocationAsync("SA-T2", "Analysis Theirs Two", LocationKind.Store);
        LocationId mine = await factory.CreateLocationAsync("SA-M2", "Analysis Mine Two", LocationKind.Store);
        await factory.CreateUserAsync("sa-m2-sm", Roles.StoreManager, locations: [mine]);

        using HttpClient client = factory.CreateClient();
        string manager = await SignInAsync(client, "sa-m2-sm");

        using HttpResponseMessage refused = await GetAsync(
            client,
            FormattableString.Invariant(
                $"/api/v1/reports/sales/payments?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}&locationId={theirs.Value}"),
            manager);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("report.outside_scope");
    }

    private static string Report(DateOnly from, DateOnly to, Guid? locationId = null)
    {
        string path = FormattableString.Invariant(
            $"/api/v1/reports/sales?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

        return locationId is { } id ? path + FormattableString.Invariant($"&locationId={id}") : path;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }
}
