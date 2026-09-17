using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

public sealed class ChangeFeedWriteGuardTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    public static TheoryData<string, Func<PosDeviceDbContext, Task>> TrackedWrites() => new()
    {
        {
            "add product",
            context =>
            {
                context.Products.Add(new DeviceCachedProduct(ProductId.New(), "SKU-X", "Injected", true, false, false, 1, Now));
                return Task.CompletedTask;
            }
        },
        {
            "update product",
            async context => (await context.Products.SingleAsync()).Refresh("SKU-1", "Tampered", true, false, false, 99, Now, false)
        },
        { "delete product", async context => context.Products.Remove(await context.Products.SingleAsync()) },
        {
            "add barcode",
            context =>
            {
                context.ProductBarcodes.Add(new DeviceCachedProductBarcode("999", ProductId.New(), true, true));
                return Task.CompletedTask;
            }
        },
        {
            "add price",
            context =>
            {
                context.ProductPrices.Add(new DeviceCachedProductPrice(ProductPriceId.New(), ProductId.New(), null, 1m, "PHP", Now, null));
                return Task.CompletedTask;
            }
        },
        {
            "add location",
            context =>
            {
                context.Locations.Add(new DeviceCachedLocation(LocationId.New(), "X", "X", LocationKind.Store, "Asia/Manila", "PHP", true));
                return Task.CompletedTask;
            }
        },
        {
            "add user",
            context =>
            {
                context.Users.Add(new DeviceCachedUser(UserId.New(), "intruder", "Intruder", true, 1));
                return Task.CompletedTask;
            }
        },
        {
            "grant a permission",
            context =>
            {
                context.PermissionSnapshots.Add(new DevicePermissionSnapshot(
                    Guid.NewGuid(), UserId.New(), "sale.void", null, 1, Now, Now.AddYears(1)));
                return Task.CompletedTask;
            }
        },
        { "move the cursor", async context => (await context.SyncCursors.SingleAsync()).Advance(999, Now) },
    };

    [Theory]
    [MemberData(nameof(TrackedWrites))]
    public async Task SaveChanges_OutsideTheApplier_IsRefusedByTheInterceptor(
        string write,
        Func<PosDeviceDbContext, Task> change)
    {
        await using TemporaryDeviceDatabase database = await SeededAsync();

        await using (PosDeviceDbContext context = await database.OpenContextAsync())
        {
            await change(context);
            Func<Task> save = () => context.SaveChangesAsync();
            await save.Should().ThrowAsync<ChangeFeedWriteViolationException>(write);
        }

        await AssertSeedIntactAsync(database);
    }

    [Theory]
    [InlineData("DELETE FROM cache_product")]
    [InlineData("UPDATE cache_product SET name = 'Tampered'")]
    [InlineData("INSERT INTO cache_user (id, user_name, display_name, is_active, security_version) VALUES ('00000000-0000-0000-0000-000000000001', 'x', 'x', 1, 1)")]
    [InlineData("DELETE FROM snapshot_permission")]
    [InlineData("UPDATE sync_cursor SET position = 999")]
    public async Task RawSql_OutsideTheApplier_IsRefusedByTheTrigger(string sql)
    {
        await using TemporaryDeviceDatabase database = await SeededAsync();

        await using (PosDeviceDbContext context = await database.OpenContextAsync())
        {
            Func<Task> execute = () => context.Database.ExecuteSqlRawAsync(sql);
            (await execute.Should().ThrowAsync<SqliteException>())
                .WithMessage("*written only by the change-feed applier*");
        }

        await AssertSeedIntactAsync(database);
    }

    [Fact]
    public async Task BulkStatements_OutsideTheApplier_AreRefusedByTheTrigger()
    {
        await using TemporaryDeviceDatabase database = await SeededAsync();

        await using (PosDeviceDbContext context = await database.OpenContextAsync())
        {
            Func<Task> update = () => context.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Tampered"));
            Func<Task> delete = () => context.PermissionSnapshots.ExecuteDeleteAsync();

            await update.Should().ThrowAsync<SqliteException>();
            await delete.Should().ThrowAsync<SqliteException>();
        }

        await AssertSeedIntactAsync(database);
    }

    [Fact]
    public async Task KeyedConnectionWithoutADeviceContext_CanReadButNotWriteCaches()
    {
        await using TemporaryDeviceDatabase database = await SeededAsync();

        await using (SqliteConnection connection = new(database.EncryptedConnectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT count(*) FROM cache_product;";
            (await read.ExecuteScalarAsync()).Should().Be(1L);

            await using SqliteCommand write = connection.CreateCommand();
            write.CommandText = "DELETE FROM cache_product;";
            Func<Task> delete = () => write.ExecuteNonQueryAsync();
            (await delete.Should().ThrowAsync<SqliteException>())
                .WithMessage("*" + ChangeFeedWriterFunctionInterceptor.FunctionName + "*");
        }

        await AssertSeedIntactAsync(database);
    }

    [Fact]
    public async Task DeviceProfile_IsWrittenByEnrolment_NotGuarded()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        await using PosDeviceDbContext context = await database.OpenContextAsync();
        context.DeviceProfiles.Add(new DeviceStoreProfile(DeviceId.New(), LocationId.New(), "d03", Now));
        await context.SaveChangesAsync();

        (await context.DeviceProfiles.AsNoTracking().SingleAsync()).ShortCode.Should().Be("D03");
    }

    [Fact]
    public async Task Migration_InstallsEveryDeclaredGuardTrigger_OnEveryApplierOwnedTable()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await using PosDeviceDbContext context = await database.OpenContextAsync();

        List<IEntityType> owned = [.. context.Model.GetEntityTypes().Where(e => typeof(IChangeFeedOwned).IsAssignableFrom(e.ClrType))];
        owned.Select(e => e.GetTableName()).Should().BeEquivalentTo(
            ["cache_batch", "cache_location", "cache_product", "cache_product_barcode", "cache_product_price", "cache_user", "snapshot_permission", "sync_cursor"]);

        string[] declared = [.. owned.SelectMany(e => e.GetDeclaredTriggers()).Select(t => t.ModelName)];
        declared.Should().HaveCount(owned.Count * 3);

        List<string> installed = [];
        await using (SqliteConnection connection = new(database.EncryptedConnectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            // Scoped to the applier's own guards: the device also carries the
            // ledger's triggers, which DeviceLedgerTests owns.
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE '%feed_only%';";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                installed.Add(reader.GetString(0));
            }
        }

        installed.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void WriteScope_IsUnreachableOutsideInfrastructure()
    {
        typeof(ChangeFeedWriteScope).IsPublic.Should().BeFalse();

        PropertyInfo? scope = typeof(PosDeviceDbContext).GetProperty(
            nameof(PosDeviceDbContext.ChangeFeedWrites), BindingFlags.Instance | BindingFlags.NonPublic);
        scope.Should().NotBeNull();
        scope!.GetMethod!.IsAssembly.Should().BeTrue();
    }

    private static async Task<TemporaryDeviceDatabase> SeededAsync()
    {
        TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        Result<ChangeFeedApplyOutcome> seeded = await database.Applier.ApplyAsync(new ChangeFeedPage(0, 2,
        [
            new ProductChanged(1, ProductId.New(), "SKU-1", "Rice 5kg", true, false, false, 1, Now),
            new PermissionSnapshotIssued(2, UserId.New(), 1, Now, Now.AddHours(12), [new PermissionSnapshotGrant("sale.create", null)]),
        ]));
        seeded.IsSuccess.Should().BeTrue();
        return database;
    }

    private static async Task AssertSeedIntactAsync(TemporaryDeviceDatabase database)
    {
        await using PosDeviceDbContext context = await database.OpenContextAsync();
        (await context.Products.SingleAsync()).Name.Should().Be("Rice 5kg");
        (await context.PermissionSnapshots.SingleAsync()).Permission.Should().Be("sale.create");
        (await context.ProductBarcodes.AnyAsync()).Should().BeFalse();
        (await context.Users.AnyAsync()).Should().BeFalse();
        (await database.Applier.GetCursorAsync()).Should().Be(2);
    }
}
