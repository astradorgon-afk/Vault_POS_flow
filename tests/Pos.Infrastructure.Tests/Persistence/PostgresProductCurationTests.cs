using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;
using Testcontainers.PostgreSql;

namespace Pos.Infrastructure.Tests.Persistence;

/// <summary>
/// Product curation saved on a real PostgreSQL engine.
/// </summary>
/// <remarks>
/// Two database rules only PostgreSQL enforces here: the price exclusion
/// constraint, which fails a supersession whose insert runs before the
/// predecessor's end is closed, and the one-primary-barcode filtered unique
/// index, which fails a primary swap written in the wrong order. The API suite
/// runs on SQLite, which has no exclusion constraint. The suite skips itself when
/// no Docker daemon is reachable; CI always has one.
/// </remarks>
[Collection("postgres")]
public sealed class PostgresProductCurationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = new(Guid.CreateVersion7());

    private PostgreSqlContainer? _container;
    private DbContextOptions<PosDbContext>? _options;

    private bool DockerAvailable => _container is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("vaultflow_test")
                .WithUsername("vaultflow")
                .WithPassword("vaultflow-test-only")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _container = null;
            return;
        }

        _options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        await using PosDbContext context = new(_options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SupersedingAndTemporaryPrices_SaveWithinTheExclusionConstraint()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        ProductId productId = await CreateProductAsync("PG-PRICE-01", "4800000100017", product =>
            product.SchedulePrice(null, 100m, Now, null, Actor, "Launch price", Now).IsSuccess.Should().BeTrue());

        await ChangeAsync(productId, product =>
            product.SchedulePrice(null, 80m, Now.AddDays(1), Now.AddDays(2), Actor, "Weekend promo", Now)
                .IsSuccess.Should().BeTrue());

        await ChangeAsync(productId, product =>
            product.SchedulePrice(null, 120m, Now.AddDays(3), null, Actor, "Supplier increase", Now)
                .IsSuccess.Should().BeTrue());

        await using PosDbContext context = new(_options!);
        List<ProductPrice> prices = await context.Set<ProductPrice>()
            .Where(p => p.ProductId == productId)
            .OrderBy(p => p.EffectiveFromUtc)
            .ToListAsync();

        prices.Select(p => (p.Amount, p.EffectiveFromUtc, p.EffectiveToUtc)).Should().Equal(
            (100m, Now, Now.AddDays(1)),
            (80m, Now.AddDays(1), Now.AddDays(2)),
            (100m, Now.AddDays(2), Now.AddDays(3)),
            (120m, Now.AddDays(3), (DateTimeOffset?)null));
    }

    [SkippableFact]
    public async Task PrimarySwapAndRetirement_SaveWithinTheOnePrimaryIndex()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        ProductId productId = await CreateProductAsync("PG-CODES-01", "4800000100024", product =>
            product.AddBarcode("4800000100031", product.BaseUnitOfMeasureId, 12m, isPrimary: false, Actor, requireChecksum: false)
                .IsSuccess.Should().BeTrue());

        await ChangeAsync(productId, product =>
            product.SetPrimaryBarcode("4800000100031").IsSuccess.Should().BeTrue());

        await ChangeAsync(productId, product =>
            product.RetireBarcode("4800000100031", Actor, Now).IsSuccess.Should().BeTrue());

        await using PosDbContext context = new(_options!);
        List<ProductBarcode> barcodes = await context.ProductBarcodes
            .Where(b => b.ProductId == productId)
            .OrderBy(b => b.Value)
            .ToListAsync();

        barcodes.Select(b => (b.Value, b.IsPrimary, b.IsRetired)).Should().Equal(
            ("4800000100024", true, false),
            ("4800000100031", false, true));

        bool retiredStillReserved = await new MasterDataRepository(context)
            .BarcodeInUseAsync("4800000100031", CancellationToken.None);

        retiredStillReserved.Should().BeTrue("a retired code is never handed to another product");
    }

    private async Task<ProductId> CreateProductAsync(string sku, string barcode, Action<Product> arrange)
    {
        Product product = Product.Create(sku, sku, new CategoryId(Guid.CreateVersion7()),
            new UnitOfMeasureId(Guid.CreateVersion7()), Actor).Value;

        product.AddBarcode(barcode, product.BaseUnitOfMeasureId, 1m, isPrimary: true, Actor, requireChecksum: false)
            .IsSuccess.Should().BeTrue();
        arrange(product);

        await using PosDbContext context = new(_options!);
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product.Id;
    }

    private async Task ChangeAsync(ProductId productId, Action<Product> change)
    {
        await using PosDbContext context = new(_options!);
        Product? product = await new MasterDataRepository(context)
            .GetProductForUpdateAsync(productId, CancellationToken.None);

        product.Should().NotBeNull();
        change(product!);
        await context.SaveChangesAsync();
    }
}
