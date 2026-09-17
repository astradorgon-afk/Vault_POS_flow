using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Domain.Purchasing;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Notifications;

namespace Pos.Infrastructure.Tests.Notifications;

public sealed class DiscrepancyAlertWorkerTests
{
    [Fact]
    public async Task SweepOnceAsync_ReadsTheLookbackWindow_AndWritesReceiptAndTransferAlerts()
    {
        DateTimeOffset now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        ReceivingDiscrepancyAlert receipt = new(
            GoodsReceiptId.New(), "GRN-2026-000001", LocationId.New(), now.AddHours(-2),
            [new(ReceivingDiscrepancyKind.Shortage, 3m, 30m)]);
        TransferShortageAlert transfer = new(
            TransferOrderId.New(), "TRF-2026-000001", LocationId.New(), LocationId.New(), now.AddHours(-1), 1, 1m);

        IDiscrepancyAlertRepository repository = Substitute.For<IDiscrepancyAlertRepository>();
        repository.GetOpenReceivingDiscrepanciesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([receipt]);
        repository.GetOpenTransferShortagesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([transfer]);

        INotificationWriter writer = Substitute.For<INotificationWriter>();
        writer.WriteOnceAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(true, false, true);

        ISystemClock clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(now);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(writer)
            .BuildServiceProvider();
        await using (provider)
        {
            DiscrepancyAlertWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new DiscrepancyAlertOptions { LookbackDays = 3 }),
                clock,
                NullLogger<DiscrepancyAlertWorker>.Instance);

            int written = await worker.SweepOnceAsync(CancellationToken.None);

            written.Should().Be(2);
            await repository.Received(1).GetOpenReceivingDiscrepanciesAsync(now.AddDays(-3), Arg.Any<CancellationToken>());
            await repository.Received(1).GetOpenTransferShortagesAsync(now.AddDays(-3), Arg.Any<CancellationToken>());
            await writer.Received(1).WriteOnceAsync(
                Arg.Is<Notification>(n => n.Kind == NotificationKind.ReceivingDiscrepancy),
                Arg.Any<CancellationToken>());
            await writer.Received(2).WriteOnceAsync(
                Arg.Is<Notification>(n => n.Kind == NotificationKind.TransferShortage),
                Arg.Any<CancellationToken>());
        }
    }
}
