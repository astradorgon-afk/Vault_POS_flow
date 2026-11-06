using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Application.Tests.Organizations;

/// <summary>Tests <see cref="UpdateLocationCommandHandler"/>.</summary>
public sealed class UpdateLocationCommandHandlerTests
{
    [Fact]
    public async Task Details_ArePassedToTheRepositoryUnchanged()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();

        repository.UpdateLocationAsync(
                Arg.Any<LocationId>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<LocationKind>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Success(locationId));

        var handler = new UpdateLocationCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new UpdateLocationCommand(locationId, "store07", "Store Seven", LocationKind.MainWarehouse, "Asia/Tokyo"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await repository.Received(1).UpdateLocationAsync(
            locationId, "store07", "Store Seven", LocationKind.MainWarehouse, "Asia/Tokyo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryFailure_IsReturnedUnchanged()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();
        Error error = LocationErrors.DuplicateCode("STORE07");

        repository.UpdateLocationAsync(
                Arg.Any<LocationId>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<LocationKind>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Failure(error));

        var handler = new UpdateLocationCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new UpdateLocationCommand(locationId, "STORE07", "Store Seven", LocationKind.Store, "Asia/Manila"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }
}
