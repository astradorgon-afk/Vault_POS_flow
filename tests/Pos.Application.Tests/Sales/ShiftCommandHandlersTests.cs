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