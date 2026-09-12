using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Organizations;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;

namespace Pos.Application.Tests.Organizations;

/// <summary>Tests <see cref="UpdateLocationSettingsCommandHandler"/>.</summary>
public sealed class UpdateLocationSettingsCommandHandlerTests
{
    [Fact]
    public async Task NullSettings_AreCoercedToTheConservativeDefaults()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();

        repository.UpdateLocationSettingsAsync(
                Arg.Any<LocationId>(), Arg.Any<LocationSettings>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Success(locationId));

        var handler = new UpdateLocationSettingsCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new UpdateLocationSettingsCommand(locationId, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await repository.Received(1).UpdateLocationSettingsAsync(
            locationId, LocationSettings.Default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvidedSettings_ArePassedThroughUnchanged()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();

        LocationSettings settings = LocationSettings.Default with
        {
            NegativeStockPolicy = NegativeStockPolicy.AllowWithPermission,
            AllowsDirectSupplierDelivery = true,
            ReceiptHeader = "Store One",
        };

        repository.UpdateLocationSettingsAsync(
                Arg.Any<LocationId>(), Arg.Any<LocationSettings>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Success(locationId));

        var handler = new UpdateLocationSettingsCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new UpdateLocationSettingsCommand(locationId, settings),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await repository.Received(1).UpdateLocationSettingsAsync(locationId, settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryFailure_IsReturnedUnchanged()
    {
        IMasterDataRepository repository = Substitute.For<IMasterDataRepository>();
        LocationId locationId = LocationId.New();

        Error error = LocationErrors.Unknown(locationId);

        repository.UpdateLocationSettingsAsync(
                Arg.Any<LocationId>(), Arg.Any<LocationSettings>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocationId>.Failure(error));

        var handler = new UpdateLocationSettingsCommandHandler(repository);

        Result<LocationId> result = await handler.HandleAsync(
            new UpdateLocationSettingsCommand(locationId, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }
}