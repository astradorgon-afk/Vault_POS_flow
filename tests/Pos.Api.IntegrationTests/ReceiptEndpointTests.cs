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
            text.Should().Contain("(Asia/Manila)");
            text.Should().Contain("Type: Walk-in sale");
            text.Should().Contain("Amount: 150.25");
            text.Should().Contain("Counterparty: Maria Santos");
            text.Should().Contain("Reference: PO-2026-000017");
            text.Should().Contain("Issued by: " + seed.StoreManagerUserName);
        }

        using (HttpResponseMessage thermal = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}/print?format=Thermal"), storeManager))
        {
            thermal.StatusCode.Should().Be(HttpStatusCode.OK, await thermal.Content.ReadAsStringAsync());
            thermal.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
            (await thermal.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Should().OnlyContain(line => line.Length == 42);
        }

        using (HttpResponseMessage html = await GetAsync(
            client, FormattableString.Invariant($"/api/v1/receipts/{receiptId}/print?format=Html"), storeManager))
        {
            html.StatusCode.Should().Be(HttpStatusCode.OK, await html.Content.ReadAsStringAsync());
            html.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
            string body = await html.Content.ReadAsStringAsync();
            body.Should().StartWith("<!DOCTYPE html>");
            body.Should().Contain(number);
            body.Should().Contain("@page { size: 80mm auto;");
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

        // Bodies that cannot be bound get the same problem document as any other
        // validation failure, rather than a 500 or an empty 400.
        foreach (string raw in new[] { "{ not json", "{\"locationId\":\"not-a-guid\",\"kind\":1,\"amount\":10}" })
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/receipts", UriKind.Relative))
            {
                Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", storeManager);

            using HttpResponseMessage malformed = await client.SendAsync(request, CancellationToken.None);
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await malformed.Content.ReadAsStringAsync());
            malformed.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            (await ReadErrorCodeAsync(malformed)).Should().Be("request.malformed");
        }

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

    [Fact]
    public async Task List_IsScopedToTheCallersLocations_AndFiltersByKindAndDate()
    {
        Seed storeA = await SeedAsync("r5");
        Seed storeB = await SeedAsync("r5b");
        await factory.CreateUserAsync("r5-audit", Roles.Auditor);
        using HttpClient client = factory.CreateClient();
        string managerA = await SignInAsync(client, storeA.StoreManagerUserName);
        string managerB = await SignInAsync(client, storeB.StoreManagerUserName);
        string auditor = await SignInAsync(client, "r5-audit");

        await IssueAsync(client, managerA, new { locationId = storeA.Store.Value, kind = ReceiptKind.WalkInSale, amount = 10m });
        await IssueAsync(client, managerA, new { locationId = storeA.Store.Value, kind = ReceiptKind.BranchExpense, amount = 20m });
        await IssueAsync(client, managerB, new { locationId = storeB.Store.Value, kind = ReceiptKind.OwnerWithdrawal, amount = 30m });

        // A store manager sees only their own store, whatever they filter on.
        using (JsonDocument own = await ListAsync(client, managerA, ""))
        {
            own.RootElement.EnumerateArray().Should().NotBeEmpty()
                .And.OnlyContain(r => r.GetProperty("locationId").GetGuid() == storeA.Store.Value);
        }

        using (JsonDocument elsewhere = await ListAsync(client, managerA, FormattableString.Invariant($"?locationId={storeB.Store.Value}")))
        {
            elsewhere.RootElement.GetArrayLength().Should().Be(0);
        }

        // The auditor reads business-wide.
        using (JsonDocument storeBList = await ListAsync(client, auditor, FormattableString.Invariant($"?locationId={storeB.Store.Value}")))
        {
            storeBList.RootElement.GetArrayLength().Should().Be(1);
            storeBList.RootElement[0].GetProperty("kind").GetString().Should().Be("OwnerWithdrawal");
            storeBList.RootElement[0].GetProperty("amount").GetDecimal().Should().Be(30m);
        }

        using (JsonDocument expenses = await ListAsync(client, managerA, FormattableString.Invariant($"?locationId={storeA.Store.Value}&kind=BranchExpense")))
        {
            expenses.RootElement.GetArrayLength().Should().Be(1);
            expenses.RootElement[0].GetProperty("amount").GetDecimal().Should().Be(20m);
        }

        using (JsonDocument newest = await ListAsync(client, managerA, FormattableString.Invariant($"?locationId={storeA.Store.Value}&limit=1")))
        {
            newest.RootElement.GetArrayLength().Should().Be(1);
            newest.RootElement[0].GetProperty("kind").GetString().Should().Be("BranchExpense");
        }

        using (JsonDocument future = await ListAsync(client, managerA, "?from=2100-01-01T00:00:00Z"))
        {
            future.RootElement.GetArrayLength().Should().Be(0);
        }

        using (HttpResponseMessage badKind = await GetAsync(client, "/api/v1/receipts?kind=Refund", managerA))
        {
            badKind.StatusCode.Should().Be(HttpStatusCode.BadRequest, await badKind.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(badKind)).Should().Be("request.malformed");
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

    private static async Task<JsonDocument> ListAsync(HttpClient client, string accessToken, string query)
    {
        using HttpResponseMessage response = await GetAsync(client, "/api/v1/receipts" + query, accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
