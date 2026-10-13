using FluentAssertions;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Application.Tests.Organizations;

/// <summary>Validates <see cref="UpdateLocationCommandValidator"/>.</summary>
public sealed class UpdateLocationCommandValidatorTests
{
    private readonly UpdateLocationCommandValidator _validator = new();

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = new UpdateLocationCommand(
            LocationId.Empty, "STORE01", "Store One", LocationKind.Store, "Asia/Manila");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.id_required");
    }

    [Fact]
    public void EmptyCode_IsRejected()
    {
        var command = new UpdateLocationCommand(
            LocationId.New(), string.Empty, "Store One", LocationKind.Store, "Asia/Manila");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "location.code_required");
    }

    [Fact]
    public void CodeOverMaxLength_IsRejected()
    {
        var command = new UpdateLocationCommand(
            LocationId.New(),
            new string('x', Location.CodeMaxLength + 1),
            "Store One",
            LocationKind.Store,
            "Asia/Manila");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.code_invalid");
    }

    [Fact]
    public void ExternalKind_IsRejected()
    {
        var command = new UpdateLocationCommand(
            LocationId.New(), "STORE01", "Store One", LocationKind.External, "Asia/Manila");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.external_not_user_creatable");
    }

    [Fact]
    public void CompleteDetails_AreValid()
    {
        var command = new UpdateLocationCommand(
            LocationId.New(), "STORE01", "Store One", LocationKind.Store, "Asia/Manila");

        _validator.Validate(command).IsValid.Should().BeTrue();
    }
}
