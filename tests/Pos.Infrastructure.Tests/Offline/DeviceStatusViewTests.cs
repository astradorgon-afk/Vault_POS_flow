using FluentAssertions;
using Pos.Shared.Devices;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The priority a status screen reads in. It lives on the contract rather than
/// in a component so it can be tested without rendering anything, and because
/// "which problem do I mention first" is a decision about the register, not
/// about markup.
/// </summary>
public sealed class DeviceStatusViewTests
{
    [Theory]
    [InlineData(DeviceStorageState.NotReady, DeviceEnrolmentState.NotEnrolled, DeviceSyncState.NeverSynchronised, DeviceAuthorityState.None, DeviceStatusConcern.StorageNotReady)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.NotEnrolled, DeviceSyncState.NeverSynchronised, DeviceAuthorityState.None, DeviceStatusConcern.NotEnrolled)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled, DeviceSyncState.NeverSynchronised, DeviceAuthorityState.Active, DeviceStatusConcern.AwaitingStoreData)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled, DeviceSyncState.Synchronised, DeviceAuthorityState.None, DeviceStatusConcern.AwaitingSignIn)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled, DeviceSyncState.Synchronised, DeviceAuthorityState.Expired, DeviceStatusConcern.AuthorityExpired)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled, DeviceSyncState.Synchronised, DeviceAuthorityState.ExpiringSoon, DeviceStatusConcern.AuthorityExpiringSoon)]
    [InlineData(DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled, DeviceSyncState.Synchronised, DeviceAuthorityState.Active, DeviceStatusConcern.WorkingOffline)]
    public void TheEarliestProblemIsTheOneReported(
        DeviceStorageState storage,
        DeviceEnrolmentState enrolment,
        DeviceSyncState sync,
        DeviceAuthorityState authority,
        DeviceStatusConcern expected)
    {
        // Offline throughout: a register with no connection and no other problem
        // is working normally, and every other state must outrank it.
        View(storage, enrolment, sync, authority, DeviceConnectivityState.Offline)
            .Concern.Should().Be(expected);
    }

    [Fact]
    public void AHealthyConnectedRegister_HasNothingToSay()
    {
        DeviceStatusView status = View(
            DeviceStorageState.Ready,
            DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised,
            DeviceAuthorityState.Active,
            DeviceConnectivityState.Online);

        status.Concern.Should().Be(DeviceStatusConcern.Ready);
        status.Severity.Should().Be(DeviceStatusSeverity.Normal);
        status.CanTrade.Should().BeTrue();
        status.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void BeingOffline_IsNotAProblem()
    {
        DeviceStatusView status = View(
            DeviceStorageState.Ready,
            DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised,
            DeviceAuthorityState.Active,
            DeviceConnectivityState.Offline);

        status.Concern.Should().Be(DeviceStatusConcern.WorkingOffline);
        status.Severity.Should().Be(DeviceStatusSeverity.Normal, "selling offline is what the device is for");
        status.CanTrade.Should().BeTrue();
        status.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void AStuckEventAsksForAttention_ButNeverStopsTheTill()
    {
        DeviceStatusView status = View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Active, DeviceConnectivityState.Online,
            escalated: 3);

        status.Concern.Should().Be(DeviceStatusConcern.SyncNeedsAttention);
        status.NeedsAttention.Should().BeTrue();
        status.CanTrade.Should().BeTrue(
            "the events are safe on the register; refusing to sell would turn a bookkeeping problem into a closed shop");
    }

    [Fact]
    public void AnythingThatStopsTheTill_IsSaidBeforeAStuckEvent()
    {
        DeviceStatusView status = View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Expired, DeviceConnectivityState.Online,
            escalated: 3);

        status.Concern.Should().Be(
            DeviceStatusConcern.AuthorityExpired,
            "one thing is said at a time, and the one that stops the queue moving comes first");
    }

    [Theory]
    [InlineData(DeviceStatusConcern.Ready, DeviceStatusSeverity.Normal)]
    [InlineData(DeviceStatusConcern.WorkingOffline, DeviceStatusSeverity.Normal)]
    [InlineData(DeviceStatusConcern.AuthorityExpiringSoon, DeviceStatusSeverity.Warning)]
    [InlineData(DeviceStatusConcern.AuthorityExpired, DeviceStatusSeverity.Blocked)]
    [InlineData(DeviceStatusConcern.AwaitingSignIn, DeviceStatusSeverity.Blocked)]
    [InlineData(DeviceStatusConcern.AwaitingStoreData, DeviceStatusSeverity.Blocked)]
    [InlineData(DeviceStatusConcern.NotEnrolled, DeviceStatusSeverity.Blocked)]
    [InlineData(DeviceStatusConcern.StorageNotReady, DeviceStatusSeverity.Blocked)]
    [InlineData(DeviceStatusConcern.SyncNeedsAttention, DeviceStatusSeverity.Warning)]
    public void EveryConcern_HasASeverity(DeviceStatusConcern concern, DeviceStatusSeverity expected)
    {
        // Walks the enum rather than a sample, so a concern added later without
        // a severity fails here instead of rendering as "ready".
        DeviceStatusView status = ViewFor(concern);

        status.Concern.Should().Be(concern);
        status.Severity.Should().Be(expected);
    }

    [Fact]
    public void ABlockedRegister_NeverClaimsItCanTrade()
    {
        foreach (DeviceStatusConcern concern in Enum.GetValues<DeviceStatusConcern>())
        {
            DeviceStatusView status = ViewFor(concern);

            if (status.Severity == DeviceStatusSeverity.Blocked)
            {
                status.CanTrade.Should().BeFalse("{0} blocks the register", concern);
            }

            status.NeedsAttention.Should().Be(
                status.Severity != DeviceStatusSeverity.Normal,
                "{0} should ask for attention exactly when it is not normal", concern);
        }
    }

    private static DeviceStatusView ViewFor(DeviceStatusConcern concern) => concern switch
    {
        DeviceStatusConcern.StorageNotReady => View(
            DeviceStorageState.NotReady, DeviceEnrolmentState.NotEnrolled,
            DeviceSyncState.NeverSynchronised, DeviceAuthorityState.None, DeviceConnectivityState.Offline),
        DeviceStatusConcern.NotEnrolled => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.NotEnrolled,
            DeviceSyncState.NeverSynchronised, DeviceAuthorityState.None, DeviceConnectivityState.Offline),
        DeviceStatusConcern.AwaitingStoreData => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.NeverSynchronised, DeviceAuthorityState.Active, DeviceConnectivityState.Offline),
        DeviceStatusConcern.AwaitingSignIn => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.None, DeviceConnectivityState.Offline),
        DeviceStatusConcern.AuthorityExpired => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Expired, DeviceConnectivityState.Offline),
        DeviceStatusConcern.AuthorityExpiringSoon => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.ExpiringSoon, DeviceConnectivityState.Offline),
        DeviceStatusConcern.WorkingOffline => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Active, DeviceConnectivityState.Offline),
        DeviceStatusConcern.Ready => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Active, DeviceConnectivityState.Online),
        DeviceStatusConcern.SyncNeedsAttention => View(
            DeviceStorageState.Ready, DeviceEnrolmentState.Enrolled,
            DeviceSyncState.Synchronised, DeviceAuthorityState.Active, DeviceConnectivityState.Online,
            escalated: 1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(concern), concern, "A new concern needs a case here and a severity."),
    };

    private static DeviceStatusView View(
        DeviceStorageState storage,
        DeviceEnrolmentState enrolment,
        DeviceSyncState sync,
        DeviceAuthorityState authority,
        DeviceConnectivityState connectivity,
        int escalated = 0)
        => new(
            storage,
            enrolment,
            enrolment == DeviceEnrolmentState.Enrolled ? "D03" : null,
            enrolment == DeviceEnrolmentState.Enrolled ? "Store 1" : null,
            connectivity,
            sync,
            sync == DeviceSyncState.Synchronised ? TemporaryDeviceDatabase.Now : null,
            authority,
            authority == DeviceAuthorityState.None ? null : TemporaryDeviceDatabase.Now.AddHours(40),
            UnsentEvents: 0,
            EscalatedEvents: escalated);
}
