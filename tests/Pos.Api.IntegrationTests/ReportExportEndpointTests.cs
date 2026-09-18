using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// CSV export (ROADMAP §Phase 15). The point of the tests is that
/// <c>report.export</c> gets you the file format and not the report: exporting
/// must never be a way around the permission the report itself needs.
/// </summary>
[Collection("api")]
public sealed class ReportExportEndpointTests(PosApiFactory factory)
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportingAFinancialReport_NeedsTheFinancialPermission()
    {
        LocationId store = await factory.CreateLocationAsync("EX-S1", "Export Store", LocationKind.Store);

        // Stockroom staff hold report.view and not report.view.financial. Nobody
        // but an Auditor is granted report.export in the role bundles, and an
        // Auditor holds the financial permission too — so the only way to stand a
        // caller in the gap this gate exists for is an explicit override, which is
        // a real feature of the system rather than a test fiction.
        UserId staffId = await factory.CreateUserAsync(
            "ex-staff", Roles.InventoryStaff, locations: [store]);

        await GrantAsync(staffId, Permissions.Administration.ExportReports);

        using HttpClient client = factory.CreateClient();
        string staff = await SignInAsync(client, "ex-staff");

        // The operational report exports.
        using HttpResponseMessage onHand = await GetAsync(client, Export("inventory-on-hand"), staff);
        onHand.StatusCode.Should().Be(HttpStatusCode.OK, await onHand.Content.ReadAsStringAsync());

        // The valuation does not. Without this check, report.export alone would be
        // a way round report.view.financial.
        using HttpResponseMessage valuation = await GetAsync(client, Export("inventory-valuation"), staff);
        valuation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(valuation)).Should().Be("report.export_not_permitted");
    }

    [Fact]
    public async Task AnAuditorExportsAnythingTheyMayRead()
    {
        await factory.CreateUserAsync("ex-owner", Roles.Auditor);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "ex-owner");

        using HttpResponseMessage response = await GetAsync(client, Export("shrinkage"), owner);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("shrinkage");

        string csv = await response.Content.ReadAsStringAsync();

        // A header row is always written, so an empty period downloads a file that
        // says what the columns were rather than an empty one that says nothing.
        csv.Should().StartWith("MovementType,ReasonCode,LocationId,LocationCode,Entries,Quantity,Value");
    }

    [Fact]
    public async Task AnUnknownReportIsRefused()
    {
        await factory.CreateUserAsync("ex-owner2", Roles.Auditor);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "ex-owner2");

        using HttpResponseMessage response = await GetAsync(client, Export("everything"), owner);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("report.unknown");
    }

    [Fact]
    public async Task AnExportIsBoundedLikeTheReportItExports()
    {
        await factory.CreateUserAsync("ex-owner3", Roles.Auditor);

        using HttpClient client = factory.CreateClient();
        string owner = await SignInAsync(client, "ex-owner3");

        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant(
                $"/api/v1/reports/sales/export?from={Stamp(To.AddYears(-50))}&to={Stamp(To)}"),
            owner);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("report.period_invalid");
    }

    private async Task GrantAsync(UserId userId, string permission)
    {
        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            context.UserPermissionOverrides.Add(UserPermissionOverride.Create(
                userId,
                permission,
                PermissionEffect.Grant,
                DateTimeOffset.UtcNow,
                UserId.New(),
                "Export test").Value);

            await context.SaveChangesAsync();
        });
    }

    private static string Export(string report)
        => FormattableString.Invariant(
            $"/api/v1/reports/{report}/export?from={Stamp(From)}&to={Stamp(To)}");

    private static string Stamp(DateTimeOffset value)
        => Uri.EscapeDataString(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

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
