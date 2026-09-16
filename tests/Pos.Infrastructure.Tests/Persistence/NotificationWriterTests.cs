using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Notifications;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Persistence;

public sealed class NotificationWriterTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private NotificationWriter _writer = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _writer = new NotificationWriter(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task WriteOnceAsync_PersistsOnlyTheFirstDeduplicationKey()
    {
        Notification first = Create("expiry:soon:one");
        Notification duplicate = Create("expiry:soon:one");

        (await _writer.WriteOnceAsync(first, CancellationToken.None)).Should().BeTrue();
        (await _writer.WriteOnceAsync(duplicate, CancellationToken.None)).Should().BeFalse();

        (await _context.Notifications.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Id.Should().Be(first.Id);
    }

    private static Notification Create(string key) => Notification.Create(
        NotificationKind.BatchExpiringSoon,
        NotificationSeverity.Warning,
        "Batch expiring",
        "Check the shelf.",
        key,
        DateTimeOffset.UtcNow,
        LocationId.New(),
        ProductId.New(),
        BatchId.New());
}
