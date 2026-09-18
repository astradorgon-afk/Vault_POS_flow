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
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Notifications;

public sealed class SyncFailureAlertWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SweepOnceAsync_AnnouncesEachFailure_AndCountsOnlyTheNewOnes()
    {
        SyncFailureAlert refused = new(
            EventId.New(), "S1-01", LocationId.New(), 42, "SaleCompleted",
            nameof(SyncOutcome.Rejected), "The cashier no longer holds sales.create.", Now.AddMinutes(-5));

        SyncFailureAlert flagged = new(
            EventId.New(), "S1-02", LocationId.New(), 7, "SaleCompleted",
            nameof(SyncOutcome.RequiresReview), "It took the shelf below zero.", Now.AddMinutes(-3));

        ISyncFailureAlertRepository repository = Substitute.For<ISyncFailureAlertRepository>();
        repository.GetRecentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([refused, flagged]);

        INotificationWriter writer = Substitute.For<INotificationWriter>();

        // The second one has been announced before — a register retries a refused
        // event for as long as it stands, and an alert per retry would bury the
        // one that mattered.
        writer.WriteOnceAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(true, false);

        ISystemClock clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(Now);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(writer)
            .BuildServiceProvider();

        try
        {
            SyncFailureAlertWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new SyncFailureAlertOptions { LookbackDays = 7 }),
                clock,
                NullLogger<SyncFailureAlertWorker>.Instance);

            int written = await worker.SweepOnceAsync(CancellationToken.None);

            written.Should().Be(1);
            await repository.Received(1).GetRecentAsync(Now.AddDays(-7), Arg.Any<CancellationToken>());
            await writer.Received(2).WriteOnceAsync(
                Arg.Is<Notification>(n => n.Kind == NotificationKind.SyncFailure),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task SweepOnceAsync_WithNothingUnresolved_WritesNothing()
    {
        ISyncFailureAlertRepository repository = Substitute.For<ISyncFailureAlertRepository>();
        repository.GetRecentAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);

        INotificationWriter writer = Substitute.For<INotificationWriter>();
        ISystemClock clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(Now);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(writer)
            .BuildServiceProvider();

        try
        {
            SyncFailureAlertWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new SyncFailureAlertOptions()),
                clock,
                NullLogger<SyncFailureAlertWorker>.Instance);

            (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);

            await writer.DidNotReceive().WriteOnceAsync(
                Arg.Any<Notification>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }
}
