using FluentAssertions;
using Pos.Application.Organizations;
using Pos.Domain.Common;

namespace Pos.Application.Tests.Organizations;

/// <summary>Validates <see cref="CloseLocationCommandValidator"/>.</summary>
public sealed class CloseLocationCommandValidatorTests
{
    private readonly CloseLocationCommandValidator _validator = new();

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = new CloseLocationCommand(LocationId.Empty, new DateOnly(2026, 10, 13));

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.id_required");
    }

    [Fact]
    public void MissingClosingDate_IsRejected()
    {
        var command = new CloseLocationCommand(LocationId.New(), default);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorCode == "location.closing_date_required");
    }

    [Fact]
    public void ClosingDateInTheFuture_IsAccepted()
    {
        // The domain closes now and records when the owner says it ends, so an
        // end-of-month retirement date is a legitimate request.
        var command = new CloseLocationCommand(LocationId.New(), new DateOnly(2026, 12, 31));

        _validator.Validate(command).IsValid.Should().BeTrue();
    }
}
