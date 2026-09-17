using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// What the server records for the registers to download. The feed is read by
/// cursor, so two things matter above everything: that a change a device caches
/// always produces a row, and that the rows are numbered in an order a cursor
/// can follow.
/// </summary>
public sealed class ChangeFeedRecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;

    public async Task InitializeAsync()
    {
        this.connection = new SqliteConnection("Data Source=:memory:");
        await this.connection.OpenAsync();

        this.context = NewContext();
        await this.context.Database.EnsureCreatedAsync();

        this.context.Organizations.Add(
            Organization.Create("VaultFlow", "VaultFlow Retail Inc.", "PHP", "Asia/Manila").Value);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task AProductChange_IsRecordedForEveryRegister()
    {
        Product product = await AddProductAsync("SKU-1", "Biscuits");

        ChangeFeedEntry entry = await SingleEntryAsync();

        entry.Kind.Should().Be("ProductChanged");
        entry.Sequence.Should().Be(1, "the first change the server ever records is number one");
        entry.LocationScopeId.Should().BeNull("a product is every register's business");
        entry.RecordedAtUtc.Should().Be(Now);

        using JsonDocument payload = JsonDocument.Parse(entry.PayloadJson);
        payload.RootElement.GetProperty("sku").GetString().Should().Be("SKU-1");
        payload.RootElement.GetProperty("name").GetString().Should().Be("Biscuits");
        payload.RootElement.GetProperty("sequence").GetInt64().Should().Be(entry.Sequence);
        payload.RootElement.GetProperty("sourceVersion").GetInt64().Should().Be(
            entry.Sequence, "the feed's own order is what says which server state a cache came from");

        _ = product;
    }

    [Fact]
    public async Task APriceIsScopedToItsLocation_AndAGlobalPriceIsNot()
    {
        Product product = await AddProductAsync("SKU-2", "Soap");
        LocationId store = await AddLocationAsync("S1");

        await ClearFeedAsync();

        product.SchedulePrice(store, 45m, Now, null, UserId.New(), "store price", Now);
        product.SchedulePrice(null, 50m, Now, null, UserId.New(), "list price", Now);
        await this.context.SaveChangesAsync();

        List<ChangeFeedEntry> entries = await EntriesAsync();
        List<ChangeFeedEntry> prices = [.. entries.Where(e => e.Kind == "ProductPriceChanged")];

        prices.Should().HaveCount(2);
        prices.Should().Contain(e => e.LocationScopeId == store.Value, "a store price is that store's business");
        prices.Should().Contain(e => e.LocationScopeId == null, "a list price is everybody's");
    }

    [Fact]
    public async Task ASupersededPrice_IsRecordedToo_SoATillStopsUsingIt()
    {
        Product product = await AddProductAsync("SKU-2b", "Flour");
        product.SchedulePrice(null, 40m, Now, null, UserId.New(), "list price", Now);
        await this.context.SaveChangesAsync();
        await ClearFeedAsync();

        // Scheduling a later price closes the open end of the one before it. A
        // register that only heard about the new row would keep both open and
        // resolve whichever it liked.
        product.SchedulePrice(null, 50m, Now.AddDays(1), null, UserId.New(), "rise", Now);
        await this.context.SaveChangesAsync();

        List<ChangeFeedEntry> prices = [.. (await EntriesAsync()).Where(e => e.Kind == "ProductPriceChanged")];

        prices.Should().HaveCount(2, "the new row and the one it closed");
        prices.Select(e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("amount").GetDecimal())
            .Should().BeEquivalentTo(new[] { 40m, 50m });
    }

    [Fact]
    public async Task ACancelledPrice_TellsRegistersToForgetIt()
    {
        Product product = await AddProductAsync("SKU-2c", "Oil");
        Result<ProductPriceId> scheduled = product.SchedulePrice(
            null, 60m, Now.AddDays(7), null, UserId.New(), "promo", Now);
        await this.context.SaveChangesAsync();
        await ClearFeedAsync();

        product.CancelScheduledPrice(scheduled.Value, Now);
        await this.context.SaveChangesAsync();

        // The product row is touched too — cancelling a price stamps the
        // aggregate — so the removal is picked out rather than assumed alone.
        ChangeFeedEntry entry = (await EntriesAsync())
            .Should().ContainSingle(e => e.Kind == "ProductPriceRemoved",
                "a register that never hears this goes on selling at a price the server no longer holds")
            .Subject;

        using JsonDocument payload = JsonDocument.Parse(entry.PayloadJson);
        payload.RootElement.GetProperty("priceId").GetGuid().Should().Be(scheduled.Value.Value);
    }

    [Fact]
    public async Task IdentifiersGoOnTheWireAsBareGuids()
    {
        Product product = await AddProductAsync("SKU-W1", "Milk");

        ChangeFeedEntry entry = await SingleEntryAsync();

        // Left to itself the serializer writes a single-property record as an
        // object, so every identifier would travel as {"value":"..."} and a
        // device would need a matching wrapper to read its own catalogue.
        entry.PayloadJson.Should().NotContain(
            "\"value\"", "the feed is JSON somebody has to be able to read at three in the morning");

        using JsonDocument payload = JsonDocument.Parse(entry.PayloadJson);
        payload.RootElement.GetProperty("productId").GetGuid().Should().Be(product.Id.Value);
    }

    [Fact]
    public async Task ChangesTheDeviceDoesNotCache_AreNotRecorded()
    {
        // A supplier is master data, but not master data a register holds. A
        // reflective rule would ship it; the recorder's list is deliberate.
        this.context.Suppliers.Add(Supplier.Create("SUP-1", "Acme", null, 30, 7).Value);
        await this.context.SaveChangesAsync();

        (await EntriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AChangeThatRollsBack_TakesItsFeedRowWithIt()
    {
        await AddProductAsync("SKU-3", "Tea");
        await ClearFeedAsync();

        await using (IDbContextTransaction transaction = await this.context.Database.BeginTransactionAsync())
        {
            await AddProductAsync("SKU-4", "Coffee", save: true);
            await transaction.RollbackAsync();
        }

        this.context.ChangeTracker.Clear();

        (await EntriesAsync()).Should().BeEmpty(
            "there is no window in which a register can be told about a product that does not exist");
    }

    [Fact]
    public async Task SequencesAreGaplessAndAscendingAcrossSaves()
    {
        for (int i = 0; i < 4; i++)
        {
            await AddProductAsync($"SKU-B{i}", $"Product {i}");
        }

        List<long> sequences = [.. (await EntriesAsync()).Select(e => e.Sequence)];

        // A cursor reads "everything after N", so a gap is a change nobody ever
        // sees again.
        sequences.Should().Equal(new long[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task SeveralChangesInOneSave_AreNumberedInOneRun()
    {
        Product first = Product.Create("SKU-M1", "One", CategoryId.New(), UnitOfMeasureId.New(), UserId.New()).Value;
        Product second = Product.Create("SKU-M2", "Two", CategoryId.New(), UnitOfMeasureId.New(), UserId.New()).Value;

        this.context.Products.AddRange(first, second);
        await this.context.SaveChangesAsync();

        List<long> sequences = [.. (await EntriesAsync()).Select(e => e.Sequence)];

        // One allocation covers the batch, and still hands out consecutive numbers.
        sequences.Should().Equal(new long[] { 1, 2 });
    }

    [Fact]
    public async Task ASaveThatChangesNothingCached_AllocatesNoSequence()
    {
        await AddProductAsync("SKU-5", "Rice");

        this.context.Suppliers.Add(Supplier.Create("SUP-2", "Beta", null, 30, 7).Value);
        await this.context.SaveChangesAsync();

        await AddProductAsync("SKU-6", "Salt");

        List<long> sequences = [.. (await EntriesAsync()).Select(e => e.Sequence)];

        // A save with nothing to feed must not burn a number.
        sequences.Should().Equal(new long[] { 1, 2 });
    }

    [Fact]
    public async Task EditingAProduct_RecordsItAgain()
    {
        Product product = await AddProductAsync("SKU-7", "Sugar");
        await ClearFeedAsync();

        product.UpdateDetails("Brown sugar", null, product.CategoryId, null, null, null, false, 0m, null);
        await this.context.SaveChangesAsync();

        ChangeFeedEntry entry = await SingleEntryAsync();

        using JsonDocument payload = JsonDocument.Parse(entry.PayloadJson);
        payload.RootElement.GetProperty("name").GetString().Should().Be(
            "Brown sugar", "a register that never hears the new name keeps printing the old one");
    }

    private async Task<Product> AddProductAsync(string sku, string name, bool save = true)
    {
        Product product = Product.Create(
            sku, name, CategoryId.New(), UnitOfMeasureId.New(), UserId.New()).Value;

        this.context.Products.Add(product);

        if (save)
        {
            await this.context.SaveChangesAsync();
        }

        return product;
    }

    private async Task<LocationId> AddLocationAsync(string code)
    {
        Location location = Location.Create(
            OrganizationId.New(), code, "Store " + code, LocationKind.Store, "Asia/Manila").Value;

        this.context.Locations.Add(location);
        await this.context.SaveChangesAsync();

        return location.Id;
    }

    /// <summary>
    /// Empties the feed without detaching anything: several tests change an
    /// entity they already saved, and a cleared tracker would quietly make those
    /// changes no-ops and the assertions meaningless.
    /// </summary>
    private Task<int> ClearFeedAsync() => this.context.ChangeFeed.ExecuteDeleteAsync();

    private async Task<List<ChangeFeedEntry>> EntriesAsync()
        => await this.context.ChangeFeed
            .AsNoTracking()
            .OrderBy(e => e.Sequence)
            .ToListAsync();

    private async Task<ChangeFeedEntry> SingleEntryAsync()
    {
        List<ChangeFeedEntry> entries = await EntriesAsync();
        entries.Should().ContainSingle();
        return entries[0];
    }

    private PosDbContext NewContext()
    {
        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(this.connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new ChangeFeedRecorder(new FixedClock(Now)))
            .Options;

        return new PosDbContext(options);
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
