using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Domain.Tests.Organizations;

/// <summary>Tests the location aggregate's editing and closing rules.</summary>
public sealed class LocationTests
{
    [Fact]
    public void UpdateDetails_ReplacesCodeNameKindAndTimezone()
    {
        Location location = NewLocation();

        Result updated = location.UpdateDetails(
            "store01b", "Store One North", LocationKind.MainWarehouse, "Asia/Tokyo");

        updated.IsSuccess.Should().BeTrue();
        location.Code.Should().Be("STORE01B");
        location.Name.Should().Be("Store One North");
        location.Kind.Should().Be(LocationKind.MainWarehouse);
        location.TimeZoneId.Should().Be("Asia/Tokyo");
    }

    [Fact]
    public void UpdateDetails_LeavesIdentityDatesAndSettingsAlone()
    {
        Location location = NewLocation();
        DateOnly openedOn = location.OpenedOn;
        DateTimeOffset createdAtUtc = location.CreatedAtUtc;

        Result updated = location.UpdateDetails("STORE01", "Renamed", LocationKind.Store, "Asia/Manila");

        updated.IsSuccess.Should().BeTrue();
        location.Id.Value.Should().NotBeEmpty();
        location.OpenedOn.Should().Be(openedOn);
        location.CreatedAtUtc.Should().Be(createdAtUtc);
        location.IsActive.Should().BeTrue();
        location.IsSystemCreated.Should().BeFalse();
    }

    [Fact]
    public void UpdateDetails_WithoutACode_IsRefused()
    {
        Location location = NewLocation();

        Result updated = location.UpdateDetails("   ", "Store One", LocationKind.Store, "Asia/Manila");

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be("location.code_invalid");
        location.Name.Should().Be("Store One");
    }

    [Fact]
    public void UpdateDetails_WithoutATimezone_IsRefused()
    {
        Location location = NewLocation();

        Result updated = location.UpdateDetails("STORE01", "Store One", LocationKind.Store, null);

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be("location.timezone_required");
    }

    [Fact]
    public void UpdateDetails_TowardsExternal_IsRefused()
    {
        Location location = NewLocation();

        Result updated = location.UpdateDetails(
            SystemLocationCodes.ExternalSupplier, "Supplier", LocationKind.External, "Etc/UTC");

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be("location.external_not_user_creatable");
        location.Kind.Should().Be(LocationKind.Store);
    }

    [Fact]
    public void UpdateDetails_OnAClosedLocation_IsRefused()
    {
        Location location = NewLocation();
        location.Close(DateOnly.FromDateTime(DateTime.UtcNow)).IsSuccess.Should().BeTrue();

        Result updated = location.UpdateDetails("STORE01", "Renamed", LocationKind.Store, "Asia/Manila");

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be("location.already_closed");
    }

    [Fact]
    public void UpdateDetails_OnASystemCounterparty_IsRefused()
    {
        Location counterparty = Location
            .CreateSystemExternal(Organization.DefaultId, SystemLocationCodes.ExternalSupplier, "Suppliers")
            .Value;

        Result updated = counterparty.UpdateDetails("EXT-NEW", "Renamed", LocationKind.Store, "Asia/Manila");

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be("location.cannot_edit_system_location");
        counterparty.Code.Should().Be(SystemLocationCodes.ExternalSupplier);
    }

    [Fact]
    public void Close_MarksTheLocationClosedOnThatDate()
    {
        Location location = NewLocation();
        DateOnly closingDate = new(2026, 10, 13);

        Result closed = location.Close(closingDate);

        closed.IsSuccess.Should().BeTrue();
        location.IsActive.Should().BeFalse();
        location.ClosedOn.Should().Be(closingDate);
        location.IsPhysical.Should().BeTrue();
    }

    [Fact]
    public void Close_Twice_IsRefused()
    {
        Location location = NewLocation();
        location.Close(new DateOnly(2026, 10, 13)).IsSuccess.Should().BeTrue();

        Result closed = location.Close(new DateOnly(2026, 11, 1));

        closed.IsFailure.Should().BeTrue();
        closed.Error.Code.Should().Be("location.already_closed");
        location.ClosedOn.Should().Be(new DateOnly(2026, 10, 13));
    }

    [Fact]
    public void Close_OnASystemCounterparty_IsRefused()
    {
        Location counterparty = Location
            .CreateSystemExternal(Organization.DefaultId, SystemLocationCodes.ExternalCustomer, "Customers")
            .Value;

        Result closed = counterparty.Close(new DateOnly(2026, 10, 13));

        closed.IsFailure.Should().BeTrue();
        closed.Error.Code.Should().Be("location.cannot_close_system_location");
        counterparty.IsActive.Should().BeTrue();
    }

    private static Location NewLocation()
    {
        Result<Location> created = Location.Create(
            Organization.DefaultId, "store01", "Store One", LocationKind.Store, "Asia/Manila");

        created.IsSuccess.Should().BeTrue();

        return created.Value;
    }
}
