using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Receipts;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The RCT-numbered payment receipt (ADR-0026): issued against a real branch,
/// read and printed by the roles entitled to receipts at that branch, and
/// refused to everyone else — through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class ReceiptEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Issue_ThenView_AndPrint_AtOwnStore()
    {
        Seed seed = await SeedAsync("r1");
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, seed.StoreManagerUserName);

        Guid receiptId = await IssueAsync(
            client,
            storeManager,
            new
            {
                locationId = seed.Store.Value,
                kind = ReceiptKind.WalkInSale,
                amount = 150.25m,
                counterparty = "  Maria Santos ",
                note = "Payment for the register open.",
                referenceNumber = "po-2026-000017",
            });

        string number;
        using (JsonDocument detail = await GetDetailAsync(client, storeManager, receiptId))
        {
            JsonElement root = detail.RootElement;
            number = root.GetProperty("number").GetString()!;
            number.Should().MatchRegex(@"^RCT-\d{4}-\d{6}$");
            root.GetProperty("kind").GetString().Should().Be("WalkInSale");
            root.GetProperty("locationId").GetGuid().Should().Be(seed.Store.Value);
            root.GetProperty("amount").GetDecimal().Should().Be(150.25m);
            root.GetProperty("counterparty").GetString().Should().Be("Maria Santos");
            root.GetProperty("note").GetString().Should().Be("Payment for the register open.");
            root.GetProperty("referenceNumber").GetString().Should().Be("PO-2026-000017");
            root.GetProperty("issuedByUserId").GetGuid().Should().Be(seed.StoreManagerId.Value);
            root.GetProperty("issuedAtUtc").GetDateTimeOffset().Should().BeCloseTo(
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }

        using (HttpResponseMessage printed = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}/print"), storeManager))
        {
            printed.StatusCode.Should().Be(HttpStatusCode.OK, await printed.Content.ReadAsStringAsync());
            printed.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");

            string text = await printed.Content.ReadAsStringAsync();
            text.Should().StartWith("PAYMENT RECEIPT");
            text.Should().Contain(number);
            text.Should().Contain("Location: Receipt Store r1");
            text.Should().Contain("Type: Walk-in sale");
            text.Should().Contain("Amount: 150.25");
            text.Should().Contain("Counterparty: Maria Santos");
            text.Should().Contain("Reference: PO-2026-000017");
            text.Should().Contain("Issued by: " + seed.StoreManagerUserName);
        }
    }

    [Fact]
    public async Task Issue_AllocatesAFreshNumberPerReceipt()
    {
        Seed seed = await SeedAsync("r2");
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, seed.StoreManagerUserName);

        Guid first = await IssueAsync(
            client, storeManager, new { locationId = seed.Store.Value, kind = ReceiptKind.BranchExpense, amount = 10m });
        Guid second = await IssueAsync(
            client, storeManager, new { locationId = seed.Store.Value, kind = ReceiptKind.OwnerWithdrawal, amount = 20m });

        string firstNumber = await NumberOfAsync(client, storeManager, first);
        string secondNumber = await NumberOfAsync(client, storeManager, second);

        // The api collection runs its classes serially, but the counter is shared
        // with every other RCT issued in the run, so only ordering is asserted.
        SequenceOf(secondNumber).Should().BeGreaterThan(SequenceOf(firstNumber));
    }

    [Fact]
    public async Task Issue_RefusesCashier_AndReadsStayWithinScope()
    {
        Seed seed = await SeedAsync("r3");
        Seed other = await SeedAsync("r3b");
        await factory.CreateUserAsync("r3-cashier", Roles.Cashier, locations: [seed.Store]);
        await factory.CreateUserAsync("r3-audit", Roles.Auditor);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, seed.StoreManagerUserName);
        string otherManager = await SignInAsync(client, other.StoreManagerUserName);
        string cashier = await SignInAsync(client, "r3-cashier");
        string auditor = await SignInAsync(client, "r3-audit");

        // Cashiers take sale payments through the POS, not standalone receipts. The
        // route policy refuses them before the handler, so there is no error body.
        using (HttpResponseMessage denied = await PostAsJsonAsync(
            client,
            "/api/v1/receipts",
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m },
            cashier))
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        }

        // A store manager may not issue at a store they are not assigned to.
        using (HttpResponseMessage elsewhere = await PostAsJsonAsync(
            client,
            "/api/v1/receipts",
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m },
            otherManager))
        {
            elsewhere.StatusCode.Should().Be(HttpStatusCode.Forbidden, await elsewhere.Content.ReadAsStringAsync());
        }

        Guid receiptId = await IssueAsync(
            client, storeManager, new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 25m });

        using (HttpResponseMessage cashierRead = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}"), cashier))
        {
            cashierRead.StatusCode.Should().Be(HttpStatusCode.Forbidden, await cashierRead.Content.ReadAsStringAsync());
        }

        foreach (string path in new[] { "", "/print" })
        {
            using HttpResponseMessage outside = await GetAsync(
                client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}{path}"), otherManager);
            outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(outside)).Should().Be("receipt.outside_scope");
        }

        // The auditor reads business-wide, and changes nothing.
        using (JsonDocument detail = await GetDetailAsync(client, auditor, receiptId))
        {
            detail.RootElement.GetProperty("amount").GetDecimal().Should().Be(25m);
        }

        using (HttpResponseMessage auditorIssue = await PostAsJsonAsync(
            client,
            "/api/v1/receipts",
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m },
            auditor))
        {
            auditorIssue.StatusCode.Should().Be(HttpStatusCode.Forbidden, await auditorIssue.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Issue_RefusesExternalLocationAndInvalidInput()
    {
        Seed seed = await SeedAsync("r4");
        LocationId external = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);

        // Only a business-wide user can even attempt an external location: the
        // pipeline's location-scoped check denies a store manager first.
        await factory.CreateUserAsync("r4-ho", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, seed.StoreManagerUserName);
        string headOffice = await SignInAsync(client, "r4-ho");

        await ExpectBadRequestAsync(
            headOffice,
            new { locationId = external.Value, kind = ReceiptKind.BranchExpense, amount = 10m },
            "receipt.location_external");

        await ExpectBadRequestAsync(
            storeManager,
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 0m },
            "receipt.amount_invalid");

        await ExpectBadRequestAsync(
            storeManager,
            new { locationId = seed.Store.Value, kind = 99, amount = 10m },
            "receipt.kind_unknown");

        await ExpectBadRequestAsync(
            storeManager,
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m, referenceNumber = "sale 17" },
            "receipt.reference_number_invalid");

        await ExpectBadRequestAsync(
            storeManager,
            new { locationId = seed.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m, note = new string('x', Receipt.NoteMaxLength + 1) },
            "receipt.note_too_long");

        using (HttpResponseMessage missing = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{Guid.NewGuid()}"), storeManager))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound, await missing.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(missing)).Should().Be("receipt.unknown");
        }

        async Task ExpectBadRequestAsync(string accessToken, object body, string errorCode)
        {
            using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/receipts", body, accessToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be(errorCode);
        }
    }

    // ---- Seeds ----------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        LocationId store = await factory.CreateLocationAsync($"RC-{suffix}", $"Receipt Store {suffix}", LocationKind.Store);
        string storeManager = $"rct-{suffix}-sm";
        UserId storeManagerId = await factory.CreateUserAsync(storeManager, Roles.StoreManager, locations: [store]);
        return new Seed(store, storeManager, storeManagerId);
    }

    private static long SequenceOf(string number)
        => long.Parse(number[(number.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);

    // ---- Request helpers ------------------------------------------------------

    private static async Task<Guid> IssueAsync(HttpClient client, string accessToken, object body)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(client, "/api/v1/receipts", body, accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> GetDetailAsync(HttpClient client, string accessToken, Guid receiptId)
    {
        using HttpResponseMessage response = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}"), accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> NumberOfAsync(HttpClient client, string accessToken, Guid receiptId)
    {
        using JsonDocument detail = await GetDetailAsync(client, accessToken, receiptId);
        return detail.RootElement.GetProperty("number").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string path, object body, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private sealed record Seed(LocationId Store, string StoreManagerUserName, UserId StoreManagerId);
}
