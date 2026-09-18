using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Sales;
using NSubstitute;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Sales;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

namespace Pos.Sync.Tests;

public sealed class SyncPushServiceTests
{
    [Fact]
    public async Task FirstEvent_IsParkedAndReplayReturnsTheOriginalOutcome()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushEvent item = host.Event(1, "{\"value\":1}");

        SyncPushResponse first = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 10, [item]), CancellationToken.None);
        SyncPushResponse replay = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 10, [item]), CancellationToken.None);

        first.Results.Single().Outcome.Should().Be("RequiresReview");
        first.NextCursor.Should().Be(1);
        replay.Results.Single().Outcome.Should().Be("RequiresReview");
        replay.Results.Single().AppliedAtUtc.Should().Be(first.Results.Single().AppliedAtUtc);

        await using PosDbContext context = host.CreateContext();
        (await context.ProcessedSyncEvents.CountAsync()).Should().Be(1);
        (await context.SyncDeviceCheckpoints.SingleAsync()).LastAcceptedSequence.Should().Be(1);
    }

    [Fact]
    public async Task ReusingEventIdWithDifferentPayloadHash_IsRejected()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushEvent first = host.Event(1, "{\"value\":1}");
        await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [first]), CancellationToken.None);

        SyncPushEvent tampered = first with
        {
            PayloadJson = "{\"value\":2}",
            PayloadHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("{\"value\":2}"))),
        };

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 2, [tampered]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Rejected");
        response.Results.Single().ErrorCode.Should().Be("sync.idempotency_key_reuse");
    }

    [Fact]
    public async Task SequenceGap_IsDeferredWithoutAdvancingTheCheckpoint()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [host.Event(2, "{}")]),
            CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Deferred");
        response.Results.Single().ErrorCode.Should().Be("sync.sequence_gap");
        response.NextCursor.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredSequenceGap_ParksLaterEventAndRejectsLateMissingSequence()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await using (PosDbContext context = host.CreateContext())
        {
            SyncDeviceCheckpoint checkpoint = new(host.Device.Id, 0, host.Now);
            checkpoint.MarkGapDetected(host.Now.AddMinutes(-31));
            context.SyncDeviceCheckpoints.Add(checkpoint);
            await context.SaveChangesAsync();
        }

        SyncPushResponse timedOut = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [host.Event(2, "{\"value\":2}")]),
            CancellationToken.None);

        timedOut.Results.Single().Outcome.Should().Be("RequiresReview");
        timedOut.Results.Single().ErrorCode.Should().Be("sync.sequence_gap_timeout");
        timedOut.NextCursor.Should().Be(2);

        SyncPushResponse late = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 2, [host.Event(1, "{\"value\":1}")]),
            CancellationToken.None);

        late.Results.Single().Outcome.Should().Be("Conflict");
        late.Results.Single().ErrorCode.Should().Be("sync.sequence_expired");
    }

    [Fact]
    public async Task OutOfOrderEvents_AreBufferedThenAppliedWhenTheGapCloses()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushResponse deferred = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [host.Event(2, "{\"value\":2}")]),
            CancellationToken.None);
        deferred.Results.Single().Outcome.Should().Be("Deferred");

        SyncPushResponse first = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 2, [host.Event(1, "{\"value\":1}")]),
            CancellationToken.None);
        first.Results.Single().Outcome.Should().Be("RequiresReview");

        SyncPushResponse second = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 3, [host.Event(2, "{\"value\":2}")]),
            CancellationToken.None);
        second.Results.Single().Outcome.Should().Be("RequiresReview");
        second.NextCursor.Should().Be(2);
    }

    [Fact]
    public async Task TokenDeviceMustMatchThePushEnvelope()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(Guid.NewGuid(), host.Now, 1, []), CancellationToken.None);

        response.ErrorCode.Should().Be("sync.device_binding");
    }

    [Fact]
    public async Task ShiftOpen_IsDispatchedAndReportedAccepted()
    {
        await using TestHost host = await TestHost.CreateAsync();
        CashierShiftId shiftId = CashierShiftId.New();
        ShiftSyncPayload payload = new(
            shiftId.Value,
            "SHF-2026-D03-0001",
            host.Location.Value,
            host.Device.Id.Value,
            host.User.Value,
            200m,
            new DateOnly(2026, 9, 18),
            host.Now,
            "Open");
        SyncPushEvent item = host.Event(1, System.Text.Json.JsonSerializer.Serialize(payload), "ShiftOpened");

        host.Dispatcher.SendAsync<CashierShiftId>(Arg.Any<ICommand<CashierShiftId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(shiftId));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Accepted");
        response.NextCursor.Should().Be(1);
        await host.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaleCompleted_IsDispatchedThroughTheSharedSaleCommand()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SaleId saleId = SaleId.New();
        SaleSyncPayload payload = new(
            "SAL-2026-D03-0001",
            host.Location.Value,
            CashierShiftId.New().Value,
            host.Device.Id.Value,
            host.User.Value,
            null,
            new DateOnly(2026, 9, 18),
            host.Now,
            [new SaleSyncLine(
                ProductId.New().Value,
                1m,
                UnitOfMeasureId.New().Value,
                null,
                null,
                null,
                0m,
                null,
                false,
                null)],
            [new SaleSyncPayment(PaymentMethod.Cash, 10m, 10m, null)]);
        SyncPushEvent item = host.Event(
            1,
            System.Text.Json.JsonSerializer.Serialize(payload),
            "SaleCompleted");

        host.Dispatcher.SendAsync<SaleId>(Arg.Any<ICommand<SaleId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleId>.Success(saleId));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Accepted");
        response.NextCursor.Should().Be(1);
        await host.Dispatcher.Received(1).SendAsync<SaleId>(
            Arg.Is<ICommand<SaleId>>(command => command.GetType() == typeof(CompleteSaleCommand)),
            Arg.Any<CancellationToken>());
        await host.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransferReceived_IsDispatchedThroughTheSharedTransferCommand()
    {
        await using TestHost host = await TestHost.CreateAsync();
        TransferOrderId transferId = TransferOrderId.New();
        TransferReceiveSyncPayload payload = new(
            transferId.Value,
            [new TransferReceiveSyncLine(1, null, 8m, 1m)]);
        SyncPushEvent item = host.Event(
            1,
            System.Text.Json.JsonSerializer.Serialize(payload),
            "TransferReceived");

        host.Dispatcher.SendAsync<TransferOrderId>(Arg.Any<ICommand<TransferOrderId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransferOrderId>.Success(transferId));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Accepted");
        await host.Dispatcher.Received(1).SendAsync<TransferOrderId>(
            Arg.Is<ICommand<TransferOrderId>>(command => command.GetType() == typeof(Pos.Application.Transfers.ReceiveTransferCommand)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransferReceiptAgainstClosedTransfer_RemainsAConflict()
    {
        await using TestHost host = await TestHost.CreateAsync();
        TransferOrderId transferId = TransferOrderId.New();
        TransferReceiveSyncPayload payload = new(
            transferId.Value,
            [new TransferReceiveSyncLine(1, null, 8m, 1m)]);
        SyncPushEvent item = host.Event(
            1,
            System.Text.Json.JsonSerializer.Serialize(payload),
            "TransferReceived");

        host.Dispatcher.SendAsync<TransferOrderId>(Arg.Any<ICommand<TransferOrderId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransferOrderId>.Failure(
                Error.Conflict("transfer.invalid_state", "The transfer was already received.")));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Conflict");
        response.Results.Single().ErrorCode.Should().Be("transfer.invalid_state");
    }

    [Fact]
    public async Task ReplayConflict_IsRequiresReviewAndPreservesTheEvent()
    {
        await using TestHost host = await TestHost.CreateAsync();
        CashierShiftId shiftId = CashierShiftId.New();
        ShiftSyncPayload payload = new(
            shiftId.Value,
            "SHF-2026-D03-0002",
            host.Location.Value,
            host.Device.Id.Value,
            host.User.Value,
            200m,
            new DateOnly(2026, 9, 18),
            host.Now,
            "Open");
        SyncPushEvent item = host.Event(
            1,
            System.Text.Json.JsonSerializer.Serialize(payload),
            "ShiftOpened");

        host.Dispatcher.SendAsync<CashierShiftId>(Arg.Any<ICommand<CashierShiftId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Failure(
                Error.Conflict("shift.already_open", "The shift is already open.")));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("RequiresReview");
        response.Results.Single().ErrorCode.Should().Be("shift.already_open");
        response.NextCursor.Should().Be(1);

        await using PosDbContext context = host.CreateContext();
        (await context.ProcessedSyncEvents.SingleAsync()).Outcome.Should().Be(SyncProcessingOutcome.RequiresReview);
        (await context.SyncFailures.SingleAsync()).ErrorCode.Should().Be("shift.already_open");
    }

    [Fact]
    public async Task ReplayStaleMasterData_IsRejectedWithQuarantineRemediation()
    {
        await using TestHost host = await TestHost.CreateAsync();
        CashierShiftId shiftId = CashierShiftId.New();
        ShiftSyncPayload payload = new(
            shiftId.Value,
            "SHF-2026-D03-0003",
            host.Location.Value,
            host.Device.Id.Value,
            host.User.Value,
            200m,
            new DateOnly(2026, 9, 18),
            host.Now,
            "Open");
        SyncPushEvent item = host.Event(
            1,
            System.Text.Json.JsonSerializer.Serialize(payload),
            "ShiftOpened");

        host.Dispatcher.SendAsync<CashierShiftId>(Arg.Any<ICommand<CashierShiftId>>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Failure(
                Error.NotFound("sale.product_unknown", "The product is no longer available.")));

        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [item]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Rejected");
        response.Results.Single().Remediation.Should().Be("QuarantineAndReview");

        await using PosDbContext context = host.CreateContext();
        (await context.ProcessedSyncEvents.SingleAsync()).ResponseJson.Should().Contain("QuarantineAndReview");
    }

    [Fact]
    public async Task Pull_ReturnsScopedOrderedPageAndAdvancesCursorToLastEntry()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await using (PosDbContext context = host.CreateContext())
        {
            ChangeFeedPublisher publisher = new(context, new FixedClock(host.Now));
            (await publisher.PublishAsync("ProductChanged", new { productId = "one" }, null, CancellationToken.None)).Should().Be(1);
            (await publisher.PublishAsync("LocationChanged", new { locationId = "local" }, host.Location, CancellationToken.None)).Should().Be(2);
            (await publisher.PublishAsync("LocationChanged", new { locationId = "other" }, LocationId.New(), CancellationToken.None)).Should().Be(3);
        }

        SyncPullResponse response = await host.Pull.PullAsync(0, 2, CancellationToken.None);

        response.ErrorCode.Should().BeNull();
        response.Changes.Select(change => change.Sequence).Should().Equal(1, 2);
        response.NextCursor.Should().Be(2);
        response.Changes[0].Type.Should().Be("ProductChanged");
    }

    [Fact]
    public async Task Pull_WithNoScopedChangesMovesCursorPastGlobalFeed()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await using (PosDbContext context = host.CreateContext())
        {
            ChangeFeedPublisher publisher = new(context, new FixedClock(host.Now));
            (await publisher.PublishAsync("ProductChanged", new { }, LocationId.New(), CancellationToken.None)).Should().Be(1);
        }

        SyncPullResponse response = await host.Pull.PullAsync(0, 50, CancellationToken.None);

        response.Changes.Should().BeEmpty();
        response.NextCursor.Should().Be(1);
    }

    [Fact]
    public async Task Pull_WhenCursorFallsOutsideRetainedWindow_RequiresRebaseline()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await using (PosDbContext context = host.CreateContext())
        {
            ChangeFeedPublisher publisher = new(context, new FixedClock(host.Now));
            (await publisher.PublishAsync("ProductChanged", new { version = 1 }, null, CancellationToken.None))
                .Should().Be(1);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM change_log");
            (await publisher.PublishAsync("ProductChanged", new { version = 2 }, null, CancellationToken.None))
                .Should().Be(2);
        }

        SyncPullResponse response = await host.Pull.PullAsync(0, 50, CancellationToken.None);

        response.RebaselineRequired.Should().BeTrue();
        response.ErrorCode.Should().Be("sync.cursor_expired");
        response.EarliestRetainedCursor.Should().Be(1);
    }

    [Fact]
    public async Task FailureRetry_UsesExponentialBackoffAndKeepsFailureOpen()
    {
        await using TestHost host = await TestHost.CreateAsync();
        Guid failureId;
        await using (PosDbContext context = host.CreateContext())
        {
            SyncFailure failure = new(EventId.New(), host.Device.Id, "sync.handler_unavailable", "Needs review.", host.Now);
            context.SyncFailures.Add(failure);
            await context.SaveChangesAsync();
            failureId = failure.Id;
        }

        Result<SyncFailureView> result = await host.Failures.RetryAsync(failureId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(SyncFailureStatus.RetryScheduled));
        result.Value.AttemptCount.Should().Be(1);
        result.Value.NextRetryAtUtc.Should().Be(host.Now.AddMinutes(2));
    }

    [Fact]
    public async Task FailureDismiss_RequiresAndStoresOperatorNote()
    {
        await using TestHost host = await TestHost.CreateAsync();
        Guid failureId;
        await using (PosDbContext context = host.CreateContext())
        {
            SyncFailure failure = new(EventId.New(), host.Device.Id, "sync.conflict", "Conflict.", host.Now);
            context.SyncFailures.Add(failure);
            await context.SaveChangesAsync();
            failureId = failure.Id;
        }

        Result<SyncFailureView> result = await host.Failures.DismissAsync(failureId, "Reviewed with store manager.", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(SyncFailureStatus.Dismissed));
    }

    [Fact]
    public async Task ReusingDeviceSequenceWithAnotherEventId_IsConflictAndDoesNotAdvance()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushEvent first = host.Event(1, "{\"value\":1}");
        await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [first]), CancellationToken.None);

        SyncPushEvent conflicting = host.Event(1, "{\"value\":2}");
        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 2, [conflicting]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Conflict");
        response.Results.Single().ErrorCode.Should().Be("sync.sequence_reuse");
        response.NextCursor.Should().Be(1);
        await using PosDbContext context = host.CreateContext();
        (await context.SyncFailures.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ReplayingEventIdWithDifferentSequence_IsMetadataConflict()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncPushEvent first = host.Event(1, "{\"value\":1}");
        await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 1, [first]), CancellationToken.None);

        SyncPushEvent mutated = first with { DeviceSequence = 2 };
        SyncPushResponse response = await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 2, [mutated]), CancellationToken.None);

        response.Results.Single().Outcome.Should().Be("Conflict");
        response.Results.Single().ErrorCode.Should().Be("sync.event_metadata_mismatch");
        response.NextCursor.Should().Be(1);
    }

    [Fact]
        public async Task Baseline_ReturnsTheDeviceLocationAndCurrentFeedCursor()
    {
        await using TestHost host = await TestHost.CreateAsync();
        SyncBaselineResponse response = await host.Baseline.GetAsync(CancellationToken.None);

        response.ErrorCode.Should().BeNull();
        response.Cursor.Should().Be(0);
        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Baseline_IssuesTheCallersOfflineSnapshotScopedToTheRegistersStore()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await using (PosDbContext context = host.CreateContext())
        {
            Guid roleId = Guid.CreateVersion7();
            context.Users.Add(new AppUser
            {
                Id = host.User.Value,
                UserName = "cashier1",
                DisplayName = "Cashier One",
                CreatedAtUtc = host.Now,
            });
            context.Roles.Add(new AppRole { Id = roleId, Name = "Cashier", CreatedAtUtc = host.Now });
            context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = host.User.Value, RoleId = roleId });
            foreach (string code in new[] { Permissions.Sales.OpenShift, Permissions.Catalog.Create, Permissions.Administration.AllLocations })
            {
                PermissionDefinition definition = Permissions.Find(code)!;
                context.Permissions.Add(new PermissionRecord
                {
                    Code = code,
                    Module = definition.Module,
                    Description = definition.Description,
                    IsOfflineCapable = definition.IsOfflineCapable,
                    IsReadOnly = definition.IsReadOnly,
                });
                context.RolePermissions.Add(new RolePermissionGrant { RoleId = roleId, PermissionCode = code, GrantedAtUtc = host.Now });
            }

            await context.SaveChangesAsync();
        }

        SyncBaselineResponse response = await host.Baseline.GetAsync(CancellationToken.None);

        response.ErrorCode.Should().BeNull();
        response.Items.Select(i => i.Type).Should().Equal("UserChanged", "PermissionSnapshotIssued");
        SyncBaselineItem snapshot = response.Items[1];
        snapshot.Payload.GetProperty("expiresAtUtc").GetDateTimeOffset().Should().Be(host.Now.AddHours(72));
        System.Text.Json.JsonElement grant = snapshot.Payload.GetProperty("grants").EnumerateArray().Should().ContainSingle().Subject;
        grant.GetProperty("permission").GetString().Should().Be(Permissions.Sales.OpenShift, "only offline-capable permissions travel to a device");
        grant.GetProperty("locationId").GetGuid().Should().Be(host.Location.Value, "a grant is scoped to the register's own store");
    }

    [Fact]
    public async Task Status_ReturnsDeviceCheckpointFeedCursorAndOpenFailures()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await host.Service.PushAsync(
            new SyncPushRequest(host.Device.Id.Value, host.Now, 10, [host.Event(1, "{}")] ),
            CancellationToken.None);

        SyncStatusResponse response = await host.Status.GetAsync(CancellationToken.None);

        response.ErrorCode.Should().BeNull();
        response.DeviceId.Should().Be(host.Device.Id.Value);
        response.DeviceOperational.Should().BeTrue();
        response.LastAcceptedSequence.Should().Be(1);
        response.OpenFailureCount.Should().Be(1);
        response.CurrentFeedCursor.Should().Be(0);
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<PosDbContext> options;
        private readonly PosDbContext serviceContext;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());

        private TestHost(
            SqliteConnection connection,
            DbContextOptions<PosDbContext> options,
            Device device,
            UserId user,
            LocationId location)
        {
            this.connection = connection;
            this.options = options;
            serviceContext = new PosDbContext(options);
            Device = device;
            User = user;
            Location = location;
            Now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
            Dispatcher = Substitute.For<IDispatcher>();
            Transaction = Substitute.For<IUnitOfWorkTransaction>();
            IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
            unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Service = new SyncPushService(
                serviceContext,
                new CurrentUser(user, device.Id),
                new FixedClock(Now),
                Dispatcher,
                unitOfWork,
                Substitute.For<ICurrentUserOverride>());
            Pull = new SyncPullService(
                serviceContext,
                new CurrentUser(user, device.Id),
                new FixedClock(Now));
            Failures = new SyncFailureService(serviceContext, new FixedClock(Now));
            PolicyVersionProvider policy = new(serviceContext, cache, new FixedClock(Now));
            Baseline = new SyncBaselineService(
                serviceContext,
                new CurrentUser(user, device.Id),
                new FixedClock(Now),
                new DatabasePermissionEvaluator(serviceContext, cache, policy, new FixedClock(Now)),
                policy,
                Options.Create(new SecurityOptions()));
            Status = new SyncStatusService(
                serviceContext,
                new CurrentUser(user, device.Id),
                new FixedClock(Now));
        }

        public Device Device { get; }
        public UserId User { get; }
        public LocationId Location { get; }
        public DateTimeOffset Now { get; }
        public SyncPushService Service { get; }
        public SyncPullService Pull { get; }
        public SyncFailureService Failures { get; }
        public SyncBaselineService Baseline { get; }
        public SyncStatusService Status { get; }
        public IDispatcher Dispatcher { get; }
        public IUnitOfWorkTransaction Transaction { get; }

        public static async Task<TestHost> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
                .UseSqlite(connection)
                .Options;

            UserId user = UserId.New();
            LocationId location = LocationId.New();
            Device result = Device.Register("D03", "Register", location, DevicePlatform.Web, DateTimeOffset.UtcNow, user).Value;
            result.ActivateForWeb(DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();

            await using PosDbContext context = new(options);
            await context.Database.EnsureCreatedAsync();
            context.Devices.Add(result);
            await context.SaveChangesAsync();

            return new TestHost(connection, options, result, user, location);
        }

        public PosDbContext CreateContext() => new(options);

        public SyncPushEvent Event(long sequence, string payload, string eventType = "FutureEvent")
            => new(
                Guid.CreateVersion7(),
                sequence,
                eventType,
                payload,
                Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                User.Value,
                Location.Value,
                Now,
                sequence,
                Guid.CreateVersion7());

        public async ValueTask DisposeAsync()
        {
            await serviceContext.DisposeAsync();
            cache.Dispose();
            await connection.DisposeAsync();
        }
    }

    private sealed class CurrentUser(UserId user, DeviceId device) : ICurrentUser
    {
        public UserId? UserId => user;
        public DeviceId? DeviceId => device;
        public IReadOnlyCollection<LocationId> AssignedLocations => [];
        public bool HasAllLocations => false;
        public CorrelationId CorrelationId => new(Guid.CreateVersion7());
        public string? IpAddress => null;
        public string? UserAgent => null;
        public string? RoleSnapshot => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;
        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.DateTime);
    }
}
