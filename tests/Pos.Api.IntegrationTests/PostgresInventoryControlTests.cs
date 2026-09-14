using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using static Pos.Api.IntegrationTests.UserAdministrationEndpointTests;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Stock adjustments and counts with the API hosted on PostgreSQL.
/// </summary>
/// <remarks>
/// The endpoint suites run on SQLite. The inventory control queries — scoped
/// lists, the prior-variance lookup, the variance reports — and the ledger's
/// balance guard behave differently on PostgreSQL, where every earlier engine
/// difference in this project was a production failure. Stock is seeded through a
/// real opening-balance posting, because the balance guard refuses a projection
/// the ledger does not explain. Skips itself without Docker.
/// </remarks>
[Collection("api")]
public sealed class PostgresInventoryControlTests
{
    [SkippableFact]
    public async Task AdjustmentAndCounts_PostAndReport_OnPostgres()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("vaultflow_control")
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

            await using (PosDbContext migrator = new(new DbContextOptionsBuilder<PosDbContext>()
                             .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
                             .Options))
            {
                await migrator.Database.MigrateAsync();
            }

            await using PosApiFactory factory = new(new Dictionary<string, string?>
            {
                ["Database__Provider"] = "Postgres",
                ["ConnectionStrings__Postgres"] = connectionString,
            });
            await factory.InitializeAsync();

            LocationId store = await factory.CreateLocationAsync("PGC-ST", "Postgres Count Store");
            LocationId writeOff = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
            ProductId product = await factory.CreateProductAsync("PGC-01", "Evaporated Milk 370ml", "4800000700014", defaultPurchaseCost: 30m);
            UserId staffId = await factory.CreateUserAsync("pgc-staff", Roles.InventoryStaff, locations: [store]);
            UserId managerId = await factory.CreateUserAsync("pgc-manager", Roles.StoreManager, locations: [store], tier: ApprovalTier.Tier1);
            using HttpClient client = factory.CreateClient();

            string staff = await SignInAsync(client, "pgc-staff");
            string manager = await SignInAsync(client, "pgc-manager");

            await OpeningBalanceAsync(factory, store, writeOff, product, 40m, staffId, managerId);

            // An approved damage write-off, then its list and detail reads.
            Guid adjustment = await PostAsync(client, staff, "/api/v1/inventory/adjustments", new
            {
                locationId = store.Value,
                reason = (int)AdjustmentReasonCode.Damaged,
                lines = new[] { new { productId = product.Value, state = (int)InventoryState.Available, quantityDelta = -4m } },
            }, HttpStatusCode.Created);
            await PostAsync(client, staff, $"/api/v1/inventory/adjustments/{adjustment}/submit", null, HttpStatusCode.OK);
            await PostAsync(client, manager, $"/api/v1/inventory/adjustments/{adjustment}/approve", null, HttpStatusCode.OK);

            using (JsonDocument listed = await GetJsonAsync(client, $"/api/v1/inventory/adjustments?locationId={store.Value}", staff))
            {
                listed.RootElement.EnumerateArray().Should().ContainSingle()
                    .Which.GetProperty("status").GetString().Should().Be("Posted");
            }

            // Two counts that both find less than the system holds: the second is a repeat.
            foreach (decimal counted in new[] { 35m, 33m })
            {
                Guid count = await PostAsync(client, staff, "/api/v1/inventory/counts", new
                {
                    locationId = store.Value,
                    kind = (int)InventoryCountKind.ProductSpecific,
                    productIds = new[] { product.Value },
                }, HttpStatusCode.Created);

                await PostAsync(client, staff, $"/api/v1/inventory/counts/{count}/lines",
                    new { lines = new[] { new { productId = product.Value, physicalQuantity = counted } } }, HttpStatusCode.OK);
                await PostAsync(client, staff, $"/api/v1/inventory/counts/{count}/submit", null, HttpStatusCode.OK);
                await PostAsync(client, manager, $"/api/v1/inventory/counts/{count}/approve", null, HttpStatusCode.OK);
            }

            decimal available = await factory.WithServiceAsync(context => context.InventoryBalances
                .Where(b => b.LocationId == store && b.ProductId == product && b.State == InventoryState.Available)
                .SumAsync(b => b.Quantity));
            available.Should().Be(33m);

            using (JsonDocument variances = await GetJsonAsync(client, $"/api/v1/inventory/counts/variances?locationId={store.Value}", manager))
            {
                variances.RootElement.EnumerateArray().Select(v => v.GetProperty("variance").GetDecimal())
                    .Should().BeEquivalentTo([-1m, -2m]);
            }

            using JsonDocument repeats = await GetJsonAsync(client, $"/api/v1/inventory/counts/repeat-variances?locationId={store.Value}", manager);
            repeats.RootElement.EnumerateArray().Should().ContainSingle()
                .Which.GetProperty("occurrences").GetInt32().Should().Be(2);
        }
    }

    private static async Task OpeningBalanceAsync(
        PosApiFactory factory, LocationId store, LocationId writeOff, ProductId product, decimal quantity, UserId createdBy, UserId approvedBy)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IInventoryLedger ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedger>();

        Result<PostedMovementGroup> posted = await ledger.PostAsync(
            new MovementGroupSpec(
                EventId.New(),
                InventoryMovementType.OpeningBalance,
                ReferenceDocumentType.None,
                null,
                "OPENING",
                [
                    new MovementLegSpec(product, null, writeOff, LocationKind.External, InventoryState.External, -quantity, 30m, false),
                    new MovementLegSpec(product, null, store, LocationKind.Store, InventoryState.Available, quantity, 30m, false),
                ],
                new LedgerActor(createdBy, approvedBy, null, CorrelationId.New()),
                DateTimeOffset.UtcNow,
                DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        posted.IsSuccess.Should().BeTrue(string.Join("; ", posted.Errors.Select(e => e.Code)));
    }

    private static async Task<Guid> PostAsync(HttpClient client, string token, string path, object? body, HttpStatusCode status)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, path, token, body);
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }
}
