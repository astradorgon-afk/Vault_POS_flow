using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Tests <see cref="OpenShiftCommandHandler"/>.</summary>
public sealed class OpenShiftCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);

    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();

    private readonly OpenShiftCommandHandler _handler;

    public OpenShiftCommandHandlerTests()
    {
        _currentUser.DeviceId.Returns(DeviceId.New());
        _currentUser.UserId.Returns(UserId.New());
        _clock.UtcNow.Returns(Now);

        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D01"));
        _shifts.GetOpenShiftForDeviceAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);
        _shifts.AddAsync(Arg.Any<CashierShift>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(CashierShiftId.New()));

        _handler = new OpenShiftCommandHandler(_shifts, _audit, _currentUser, _clock);
    }

    private DeviceId TestDevice => _currentUser.DeviceId!.Value;

    [Fact]
    public async Task HappyPath_ReturnsSuccessAndPersistsShift()
    {
        LocationId locationId = LocationId.New();
        DeviceId deviceId = TestDevice;
        UserId cashierId = _currentUser.UserId!.Value;
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.Store, LocationSettings.Default));

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, 100m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _shifts.Received(1).AddAsync(
            Arg.Is<CashierShift>(s =>
                s.Number == TestShiftNumber.Value
                && s.LocationId == locationId
                && s.DeviceId == deviceId
                && s.CashierUserId == cashierId
                && s.OpeningFloat == 100m
                && s.BusinessDate == BusinessDate
                && s.OpenedAtUtc == Now
                && s.Status == ShiftStatus.Open),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesShiftOpenedAudit()
    {
        LocationId locationId = LocationId.New();
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.Store, LocationSettings.Default));

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, 0m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ShiftOpened
                && e.EntityType == "cashier_shift"
                && e.LocationId == locationId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingDevice_ReturnsForbidden()
    {
        _currentUser.DeviceId.Returns((DeviceId?)null);

        var command = new OpenShiftCommand(TestShiftNumber, LocationId.New(), BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.DeviceRequired);
    }

    [Fact]
    public async Task UnknownDevice_ReturnsNotFound()
    {
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns((ShiftDeviceFacts?)null);

        var command = new OpenShiftCommand(TestShiftNumber, LocationId.New(), BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.DeviceUnknown(TestDevice));
    }

    [Fact]
    public async Task NumberFromAnotherDevice_ReturnsConflict()
    {
        _shifts.GetDeviceFactsAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(new ShiftDeviceFacts("D99"));

        var command = new OpenShiftCommand(TestShiftNumber, LocationId.New(), BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.NumberDeviceMismatch);
    }

    [Fact]
    public async Task UnknownLocation_ReturnsNotFound()
    {
        LocationId locationId = LocationId.New();
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns((ShiftLocationFacts?)null);

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationUnknown(locationId));
    }

    [Fact]
    public async Task ExternalLocation_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.External, LocationSettings.Default));

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationExternal);
    }

    [Fact]
    public async Task OpenShiftAlreadyExists_ReturnsConflict()
    {
        LocationId locationId = LocationId.New();
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.Store, LocationSettings.Default));
        _shifts.GetOpenShiftForDeviceAsync(TestDevice, Arg.Any<CancellationToken>())
            .Returns(NewOpenShift());

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, 0m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.ShiftAlreadyOpen(TestDevice));
    }

    [Fact]
    public async Task NegativeOpeningFloat_ReturnsDomainError()
    {
        LocationId locationId = LocationId.New();
        _shifts.GetLocationFactsAsync(locationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.Store, LocationSettings.Default));

        var command = new OpenShiftCommand(TestShiftNumber, locationId, BusinessDate, -10m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.OpeningFloatInvalid.Code);
    }

    private static readonly DocumentNumber TestShiftNumber =
        DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 9);

    private static CashierShift NewOpenShift() => CashierShift.Open(
        TestShiftNumber,
        LocationId.New(),
        DeviceId.New(),
        UserId.New(),
        0m,
        BusinessDate,
        Now).Value;
}

/// <summary>Tests <see cref="CloseShiftCommandHandler"/>.</summary>
public sealed class CloseShiftCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);

    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IPermissionEvaluator _permissions = Substitute.For<IPermissionEvaluator>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();

    private readonly CloseShiftCommandHandler _handler;

    private readonly UserId _owner = UserId.New();
    private readonly LocationId _location = LocationId.New();
    private readonly CashierShift _openShift;

    public CloseShiftCommandHandlerTests()
    {
        _openShift = NewOpenShift(_owner, _location);

        _currentUser.UserId.Returns(_owner);
        _clock.UtcNow.Returns(Now);

        _shifts.GetShiftAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns(_openShift);
        _shifts.GetShiftCashTotalsAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns(new ShiftCashTotals(450m, 0m, 0m));
        _shifts.UpdateAsync(Arg.Any<CashierShift>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(_openShift.Id));

        _handler = new CloseShiftCommandHandler(_shifts, _permissions, _audit, _currentUser, _clock);
    }

    [Fact]
    public async Task HappyPath_ClosesShiftWithComputedVariance()
    {
        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _openShift.Status.Should().Be(ShiftStatus.Closed);
        _openShift.DeclaredCash.Should().Be(500m);
        _openShift.CountedCash.Should().Be(550m);
        _openShift.ClosedAtUtc.Should().Be(Now);

        // Expected = float 100 + cash sales 450 = 550; counted 550 => variance 0.
        _openShift.CashVariance.Should().Be(0m);

        await _shifts.Received(1).UpdateAsync(_openShift, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_RecordsVarianceAboveExpected()
    {
        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 530m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _openShift.CashVariance.Should().Be(-20m);
    }

    [Fact]
    public async Task HappyPath_WritesShiftClosedAudit()
    {
        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ShiftClosed
                && e.EntityType == "cashier_shift"
                && e.EntityId == _openShift.Id.Value
                && e.LocationId == _location),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerCloses_NoPermissionLookup()
    {
        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _permissions.DidNotReceive().HasPermissionAsync(
            Arg.Any<UserId>(), Arg.Any<string>(), Arg.Any<LocationId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherCashierWithoutPermission_ReturnsForbidden()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.CloseOtherShiftDenied);
    }

    [Fact]
    public async Task OtherCashierWithPermission_Succeeds()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);

        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftErrors.ShiftUnknown(_openShift.Id));
    }

    [Fact]
    public async Task LocationMismatch_ReturnsConflict()
    {
        var command = new CloseShiftCommand(_openShift.Id, LocationId.New(), 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationMismatch);
    }

    [Fact]
    public async Task ShiftNotInDeclareableState_ReturnsStateError()
    {
        CashierShift closed = NewClosedShift(_owner, _location);
        _shifts.GetShiftAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns(closed);

        var command = new CloseShiftCommand(_openShift.Id, _location, 500m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ShiftInvalidState(ShiftStatus.Open, ShiftStatus.Closed).Code);
    }

    [Fact]
    public async Task NegativeDeclaredCash_ReturnsDomainError()
    {
        var command = new CloseShiftCommand(_openShift.Id, _location, -10m, 550m);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.CashAmountInvalid(-10m).Code);
    }

    private static CashierShift NewOpenShift(UserId owner, LocationId location) => CashierShift.Open(
        DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 9),
        location,
        DeviceId.New(),
        owner,
        100m,
        BusinessDate,
        new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Value;

    private static CashierShift NewClosedShift(UserId owner, LocationId location)
    {
        CashierShift shift = NewOpenShift(owner, location);
        shift.DeclareCash(500m);
        shift.Close(550m, 450m, 0m, 0m, new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero));
        return shift;
    }
}

/// <summary>Tests <see cref="SuspendShiftCommandHandler"/>.</summary>
public sealed class SuspendShiftCommandHandlerTests
{
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IPermissionEvaluator _permissions = Substitute.For<IPermissionEvaluator>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly SuspendShiftCommandHandler _handler;

    private readonly UserId _owner = UserId.New();
    private readonly LocationId _location = LocationId.New();
    private readonly CashierShift _openShift;

    public SuspendShiftCommandHandlerTests()
    {
        _openShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 10),
            _location,
            DeviceId.New(),
            _owner,
            200m,
            new DateOnly(2026, 9, 14),
            new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Value;

        _currentUser.UserId.Returns(_owner);

        _shifts.GetShiftAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns(_openShift);
        _shifts.UpdateAsync(Arg.Any<CashierShift>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(_openShift.Id));

        _handler = new SuspendShiftCommandHandler(_shifts, _permissions, _audit, _currentUser);
    }

    [Fact]
    public async Task HappyPath_SuspendsShift()
    {
        var command = new SuspendShiftCommand(_openShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _openShift.Status.Should().Be(ShiftStatus.Suspended);
        await _shifts.Received(1).UpdateAsync(_openShift, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesShiftSuspendedAudit()
    {
        var command = new SuspendShiftCommand(_openShift.Id, _location);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ShiftSuspended
                && e.EntityType == "cashier_shift"
                && e.EntityId == _openShift.Id.Value
                && e.LocationId == _location),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerSuspends_NoPermissionLookup()
    {
        var command = new SuspendShiftCommand(_openShift.Id, _location);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _permissions.DidNotReceive().HasPermissionAsync(
            Arg.Any<UserId>(), Arg.Any<string>(), Arg.Any<LocationId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherCashierWithoutPermission_ReturnsForbidden()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new SuspendShiftCommand(_openShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.CloseOtherShiftDenied);
        _openShift.Status.Should().Be(ShiftStatus.Open);
    }

    [Fact]
    public async Task OtherCashierWithPermission_Succeeds()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new SuspendShiftCommand(_openShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _openShift.Status.Should().Be(ShiftStatus.Suspended);
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(_openShift.Id, Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);

        var command = new SuspendShiftCommand(_openShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftErrors.ShiftUnknown(_openShift.Id));
    }

    [Fact]
    public async Task LocationMismatch_ReturnsConflict()
    {
        var command = new SuspendShiftCommand(_openShift.Id, LocationId.New());

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationMismatch);
    }

    [Fact]
    public async Task ClosedShift_ReturnsStateError()
    {
        _openShift.DeclareCash(200m);
        _openShift.Close(200m, 0m, 0m, 0m, new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero));

        var command = new SuspendShiftCommand(_openShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ShiftInvalidState(ShiftStatus.Open, ShiftStatus.Closed).Code);
    }
}

/// <summary>Tests <see cref="ResumeShiftCommandHandler"/>.</summary>
public sealed class ResumeShiftCommandHandlerTests
{
    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IPermissionEvaluator _permissions = Substitute.For<IPermissionEvaluator>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly ResumeShiftCommandHandler _handler;

    private readonly UserId _owner = UserId.New();
    private readonly LocationId _location = LocationId.New();
    private readonly CashierShift _suspendedShift;

    public ResumeShiftCommandHandlerTests()
    {
        _suspendedShift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 11),
            _location,
            DeviceId.New(),
            _owner,
            200m,
            new DateOnly(2026, 9, 14),
            new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Value;
        _suspendedShift.Suspend();

        _currentUser.UserId.Returns(_owner);

        _shifts.GetShiftAsync(_suspendedShift.Id, Arg.Any<CancellationToken>())
            .Returns(_suspendedShift);
        _shifts.UpdateAsync(Arg.Any<CashierShift>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(_suspendedShift.Id));

        _handler = new ResumeShiftCommandHandler(_shifts, _permissions, _audit, _currentUser);
    }

    [Fact]
    public async Task HappyPath_ResumesShift()
    {
        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _suspendedShift.Status.Should().Be(ShiftStatus.Open);
        await _shifts.Received(1).UpdateAsync(_suspendedShift, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesShiftResumedAudit()
    {
        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ShiftResumed
                && e.EntityType == "cashier_shift"
                && e.EntityId == _suspendedShift.Id.Value
                && e.LocationId == _location),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherCashierWithoutPermission_ReturnsForbidden()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.CloseOtherShiftDenied);
        _suspendedShift.Status.Should().Be(ShiftStatus.Suspended);
    }

    [Fact]
    public async Task OtherCashierWithPermission_Succeeds()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _suspendedShift.Status.Should().Be(ShiftStatus.Open);
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(_suspendedShift.Id, Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);

        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftErrors.ShiftUnknown(_suspendedShift.Id));
    }

    [Fact]
    public async Task LocationMismatch_ReturnsConflict()
    {
        var command = new ResumeShiftCommand(_suspendedShift.Id, LocationId.New());

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationMismatch);
    }

    [Fact]
    public async Task OpenShift_ReturnsStateError()
    {
        _suspendedShift.Resume();

        var command = new ResumeShiftCommand(_suspendedShift.Id, _location);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ShiftInvalidState(ShiftStatus.Suspended, ShiftStatus.Open).Code);
    }
}

/// <summary>Tests <see cref="ReconcileShiftCommandHandler"/>.</summary>
public sealed class ReconcileShiftCommandHandlerTests
{
    private static readonly DateTimeOffset CloseAt = new(2026, 9, 14, 22, 0, 0, TimeSpan.Zero);

    private readonly IShiftRepository _shifts = Substitute.For<IShiftRepository>();
    private readonly IPermissionEvaluator _permissions = Substitute.For<IPermissionEvaluator>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private readonly ReconcileShiftCommandHandler _handler;

    private readonly UserId _owner = UserId.New();
    private readonly LocationId _location = LocationId.New();
    private readonly CashierShift _shift;

    public ReconcileShiftCommandHandlerTests()
    {
        _shift = NewClosedShift(_owner, _location, 650m);

        _currentUser.UserId.Returns(_owner);

        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns(_shift);
        _shifts.GetLocationFactsAsync(_location, Arg.Any<CancellationToken>())
            .Returns(new ShiftLocationFacts(LocationKind.Store, LocationSettings.Default));
        _shifts.UpdateAsync(Arg.Any<CashierShift>(), Arg.Any<CancellationToken>())
            .Returns(Result<CashierShiftId>.Success(_shift.Id));

        _handler = new ReconcileShiftCommandHandler(_shifts, _permissions, _audit, _currentUser);
    }

    [Fact]
    public async Task HappyPath_WithinThreshold_ReconcilesWithoutReason()
    {
        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _shift.Status.Should().Be(ShiftStatus.Reconciled);
        await _shifts.Received(1).UpdateAsync(_shift, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HappyPath_WritesShiftReconciledAudit()
    {
        var command = new ReconcileShiftCommand(_shift.Id, _location, "Shortfall walked to safe");

        await _handler.HandleAsync(command, CancellationToken.None);

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditActions.Sales.ShiftReconciled
                && e.EntityType == "cashier_shift"
                && e.EntityId == _shift.Id.Value
                && e.Reason == "Shortfall walked to safe"
                && e.LocationId == _location),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VarianceBeyondThreshold_WithoutReason_Fails()
    {
        CashierShift shortfall = NewClosedShift(_owner, _location, 500m);
        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns(shortfall);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ReconcileReasonRequired.Code);
        shortfall.Status.Should().Be(ShiftStatus.Closed);
    }

    [Fact]
    public async Task VarianceBeyondThreshold_WithReason_Succeeds()
    {
        CashierShift shortfall = NewClosedShift(_owner, _location, 500m);
        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns(shortfall);

        var command = new ReconcileShiftCommand(_shift.Id, _location, "Shortage in drawer");

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        shortfall.Status.Should().Be(ShiftStatus.Reconciled);
    }

    [Fact]
    public async Task ForceClosedShift_WithoutReason_Fails()
    {
        CashierShift forced = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 12),
            _location,
            DeviceId.New(),
            _owner,
            200m,
            new DateOnly(2026, 9, 14),
            new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Value;
        forced.ForceClose(CloseAt);
        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns(forced);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ReconcileReasonRequired.Code);
    }

    [Fact]
    public async Task MissingLocationFacts_FailsClosedToStrictestSettings()
    {
        CashierShift shortfall = NewClosedShift(_owner, _location, 500m);
        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns(shortfall);
        _shifts.GetLocationFactsAsync(_location, Arg.Any<CancellationToken>())
            .Returns((ShiftLocationFacts?)null);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        // Default settings: zero tolerance, so a variance still demands a reason.
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be(ShiftErrors.ReconcileReasonRequired.Code);
    }

    [Fact]
    public async Task OtherCashierWithoutPermission_ReturnsForbidden()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.CloseOtherShiftDenied);
        _shift.Status.Should().Be(ShiftStatus.Closed);
    }

    [Fact]
    public async Task OtherCashierWithPermission_Succeeds()
    {
        UserId other = UserId.New();
        _currentUser.UserId.Returns(other);
        _permissions.HasPermissionAsync(
                other, Permissions.Sales.CloseOtherShift, _location, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownShift_ReturnsNotFound()
    {
        _shifts.GetShiftAsync(_shift.Id, Arg.Any<CancellationToken>())
            .Returns((CashierShift?)null);

        var command = new ReconcileShiftCommand(_shift.Id, _location, null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftErrors.ShiftUnknown(_shift.Id));
    }

    [Fact]
    public async Task LocationMismatch_ReturnsConflict()
    {
        var command = new ReconcileShiftCommand(_shift.Id, LocationId.New(), null);

        Result<CashierShiftId> result = await _handler.HandleAsync(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ShiftCommandErrors.LocationMismatch);
    }

    private static CashierShift NewClosedShift(UserId owner, LocationId location, decimal countedCash)
    {
        CashierShift shift = CashierShift.Open(
            DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 12),
            location,
            DeviceId.New(),
            owner,
            200m,
            new DateOnly(2026, 9, 14),
            new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Value;
        shift.DeclareCash(500m);

        // Expected = float 200 + cash sales 450 = 650; counted varies per test.
        shift.Close(countedCash, 450m, 0m, 0m, CloseAt);
        return shift;
    }
}
