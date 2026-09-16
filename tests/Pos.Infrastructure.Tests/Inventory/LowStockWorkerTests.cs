using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Tests.Inventory;

public sealed class LowStockWorkerTests
{
    [Fact]
    public async Task SweepOnceAsync_WritesOneAlertPerLowItemAndCountsOnlyNewOnes()
    {
        DateTimeOffset now = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);
        LowStockItem low = new(LocationId.New(), ProductId.New(), "Rice", 2m, 5m, 10m);
        LowStockItem empty = new(LocationId.New(), ProductId.New(), "Salt", 0m, 5m, 10m);

        ILowStockRepository repository = Substitute.For<ILowStockRepository>();
        repository.GetLowStockAsync(Arg.Any<CancellationToken>()).Returns([low, empty]);

        INotificationWriter writer = Substitute.For<INotificationWriter>();
        writer.WriteOnceAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>())
            .Returns(true, false);

        ISystemClock clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(now);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(writer)
            .BuildServiceProvider();
        await using (provider)
        {
            LowStockWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new LowStockOptions()),
                clock,
                NullLogger<LowStockWorker>.Instance);

            int written = await worker.SweepOnceAsync(CancellationToken.None);

            written.Should().Be(1);
            await writer.Received(1).WriteOnceAsync(
                Arg.Is<Notification>(n => n.ProductId == low.ProductId && n.Kind == NotificationKind.LowStock),
                Arg.Any<CancellationToken>());
            await writer.Received(1).WriteOnceAsync(
                Arg.Is<Notification>(n => n.ProductId == empty.ProductId && n.Severity == NotificationSeverity.Critical),
                Arg.Any<CancellationToken>());
        }
    }
}
