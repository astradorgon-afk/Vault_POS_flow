using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Receipts;
using Pos.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The real API host on a real PostgreSQL engine: start-up, sign-in, a
/// numbered command and a ledger posting, end to end.
/// </summary>
/// <remarks>
/// Every other endpoint test hosts the API on SQLite, and the PostgreSQL
/// suites in Pos.Infrastructure.Tests build their own context. That left two
/// PostgreSQL-only failures invisible until the host was run by hand: a
/// retrying execution strategy that refused the pipeline's transactions (the
/// host crashed on start-up), and a counter upsert PostgreSQL rejected as
/// ambiguous (every document number failed). This test would have caught both.
/// It skips itself when no Docker daemon is reachable; CI always has one.
/// </remarks>
[Collection("api")]
public sealed class PostgresHostSmokeTests
{
    [SkippableFact]
    public async Task Host_OnPostgres_SignsIn_NumbersDocuments_AndPostsTheLedger()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("vaultflow_host")
            .WithUsername("vaultflow")
            .WithPassword("vaultflow-test-only")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await container.DisposeAsync();
            Skip.If(true, "Docker is not available on this machine.");
        }

        await using (container)
        {
            string connectionString = container.GetConnectionString();

            // The schema exists before the host starts, because start-up seeds the
            // permission catalogue into it.
            DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
                .Options;

            await using (PosDbContext migrator = new(options))
            {
                await migrator.Database.MigrateAsync();
            }

            await using PosApiFactory factory = new(new Dictionary<string, string?>
            {
                ["Database__Provider"] = "Postgres",
                ["ConnectionStrings__Postgres"] = connectionString,
            });
            await factory.InitializeAsync();

            LocationId store = await factory.CreateLocationAsync("PG-ST", "Postgres Store", LocationKind.Store);
            await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
            await factory.CreateProductAsync("PG-OIL", "Cooking Oil 1L", "4800000090048", defaultPurchaseCost: 98m);
            await factory.CreateUserAsync("pg-store", Roles.StoreManager, locations: [store]);

            using HttpClient client = factory.CreateClient();
            string token = await SignInAsync(client, "pg-store");

            // Two numbered documents through the unit-of-work transaction.
            List<string> numbers = [];
            foreach (decimal amount in new[] { 150.25m, 89.50m })
            {
                using HttpResponseMessage issued = await SendAsync(
                    client, HttpMethod.Post, "/api/v1/receipts", token,
                    new { locationId = store.Value, kind = ReceiptKind.WalkInSale, amount });
                issued.StatusCode.Should().Be(HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());

                Guid id = (await ReadJsonAsync(issued)).RootElement.GetProperty("id").GetGuid();
                using HttpResponseMessage detail = await SendAsync(
                    client, HttpMethod.Get, FormattableString.Invariant($"/api/v1/receipts/{id}"), token);
                detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
                numbers.Add((await ReadJsonAsync(detail)).RootElement.GetProperty("number").GetString()!);
            }

            numbers.Should().HaveCount(2);
            numbers[0].Should().EndWith("-000001");
            numbers[1].Should().EndWith("-000002");

            // A ledger posting nested inside the command's transaction.
            using HttpResponseMessage raised = await SendAsync(
                client, HttpMethod.Post, "/api/v1/quarantine", token,
                new { locationId = store.Value, lines = new[] { new { barcode = "4800000090048", quantity = 2m } } });
            raised.StatusCode.Should().Be(HttpStatusCode.Created, await raised.Content.ReadAsStringAsync());
            Guid incidentId = (await ReadJsonAsync(raised)).RootElement.GetProperty("id").GetGuid();

            decimal quarantined = await factory.WithServiceAsync(context => context.InventoryMovements
                .Where(m => m.ReferenceDocumentId == incidentId && m.LocationId == store)
                .SumAsync(m => m.QuantityDelta));
            quarantined.Should().Be(2m);

            decimal balance = await factory.WithServiceAsync(context => context.InventoryBalances
                .Where(b => b.LocationId == store && b.State == InventoryState.Quarantine)
                .SumAsync(b => b.Quantity));
            balance.Should().Be(2m);
        }
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string accessToken, object? body = null)
    {
        using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
