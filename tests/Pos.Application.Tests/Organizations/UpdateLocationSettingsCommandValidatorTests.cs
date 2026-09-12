using FluentAssertions;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Application.Tests.Organizations;

/// <summary>Validates <see cref="UpdateLocationSettingsCommandValidator"/>.</summary>
public sealed class UpdateLocationSettingsCommandValidatorTests
{
    private readonly UpdateLocationSettingsCommandValidator _validator = new();

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = new UpdateLocationSettingsCommand(LocationId.Empty, null);
        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.id_required");
    }

    [Fact]
    public void NullSettings_AreValid()
    {
        var command = new UpdateLocationSettingsCommand(LocationId.New(), null);

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ReceiptHeaderOverMaxLength_IsRejected()
    {
        LocationSettings settings = LocationSettings.Default with
        {
            ReceiptHeader = new string('x', LocationSettings.ReceiptTextMaxLength + 1),
        };

        var command = new UpdateLocationSettingsCommand(LocationId.New(), settings);
        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.receipt_header_too_long");
    }

    [Fact]
    public void ReceiptFooterOverMaxLength_IsRejected()
    {
        LocationSettings settings = LocationSettings.Default with
        {
            ReceiptFooter = new string('y', LocationSettings.ReceiptTextMaxLength + 1),
        };

        var command = new UpdateLocationSettingsCommand(LocationId.New(), settings);
        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.receipt_footer_too_long");
    }

    [Fact]
    public void ReceiptTextAtExactlyMaxLength_IsValid()
    {
        LocationSettings settings = LocationSettings.Default with
        {
            ReceiptHeader = new string('x', LocationSettings.ReceiptTextMaxLength),
            ReceiptFooter = new string('y', LocationSettings.ReceiptTextMaxLength),
        };

        var command = new UpdateLocationSettingsCommand(LocationId.New(), settings);

        _validator.Validate(command).IsValid.Should().BeTrue();
    }
}