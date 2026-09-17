using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Notifications;

namespace Pos.Infrastructure.Tests.Notifications;

public sealed class EmergencyTransferAlertWorkerTests
{
    [Fact]
    public async Task SweepOnceAsync_WritesBothLocationAlerts_AndCountsOnlyNewOnes()
    {
        DateTimeOffset now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        EmergencyTransferAlert transfer = new(
            TransferOrderId.New(), "TRF-2026-000011", LocationId.New(), LocationId.New(),
            now.AddMinutes(-2), 1, 4m);

        IEmergencyTransferAlertRepository repository = Substitute.For<IEmergencyTransferAlertRepository>();
        repository.GetRecentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([transfer]);

        INotificationWriter writer = Substitute.For<INotificationWriter>();
        writer.WriteOnceAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(true, false);

        ISystemClock clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(now);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(writer)
            .BuildServiceProvider();

        try
        {
            EmergencyTransferAlertWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new EmergencyTransferAlertOptions { LookbackDays = 30 }),
                clock,
                NullLogger<EmergencyTransferAlertWorker>.Instance);

            int written = await worker.SweepOnceAsync(CancellationToken.None);

            written.Should().Be(1);
            await repository.Received(1).GetRecentAsync(now.AddDays(-30), Arg.Any<CancellationToken>());
            await writer.Received(2).WriteOnceAsync(
                Arg.Is<Notification>(n => n.Kind == NotificationKind.EmergencyTransfer),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }
}
