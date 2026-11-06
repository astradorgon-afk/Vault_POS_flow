using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Application.Tests.Organizations;

/// <summary>Tests <see cref="CloseLocationCommandHandler"/>.</summary>
public sealed class CloseLocationCommandHandlerTests
{
    [Fact]
    public async Task LocationAndClosingDate_ArePassedToTheRepository()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();
        DateOnly closingDate = new(2026, 10, 13);

        repository.CloseLocationAsync(locationId, closingDate, Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Success(locationId));

        var handler = new CloseLocationCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new CloseLocationCommand(locationId, closingDate),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await repository.Received(1).CloseLocationAsync(locationId, closingDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryFailure_IsReturnedUnchanged()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();
        Error error = LocationErrors.AlreadyClosed;

        repository.CloseLocationAsync(
                Arg.Any<LocationId>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Failure(error));

        var handler = new CloseLocationCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new CloseLocationCommand(locationId, new DateOnly(2026, 10, 13)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }
}
