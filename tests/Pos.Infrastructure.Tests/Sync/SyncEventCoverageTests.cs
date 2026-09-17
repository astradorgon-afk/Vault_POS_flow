using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// The two ends of the upload protocol, checked against each other.
/// </summary>
/// <remarks>
/// A device queues what <see cref="SyncEventType"/> lists and the server applies
/// what the appliers declare. Nothing in the type system connects those, so an
/// event type added on the device side ships happily and is refused as
/// unsupported by a server that never heard of it — with the device's queue
/// stalled behind it until somebody reads a log. This is the check that turns
/// that into a failing build.
/// </remarks>
public sealed class SyncEventCoverageTests
{
    [Fact]
    public void EveryEventADeviceCanQueue_HasAnApplierThatUnderstandsIt()
    {
        IReadOnlyList<string> queued = [.. Enum.GetNames<SyncEventType>()];

        AppliedEventTypes().Should().BeEquivalentTo(
            queued,
            "a device queues what SyncEventType lists, and the server has to answer for all of it");
    }

    /// <summary>
    /// The event types the server understands, read off the appliers themselves
    /// rather than off their class names, because the string is the contract.
    /// </summary>
    /// <remarks>
    /// Read through an uninitialized instance: every <c>EventType</c> is a
    /// constant expression that touches no state, and constructing one for real
    /// would mean standing up a database for a question about a literal.
    /// </remarks>
    private static IReadOnlyList<string> AppliedEventTypes()
        => [.. AllApplierTypes().Select(t => ((ISyncEventApplier)RuntimeHelpers.GetUninitializedObject(t)).EventType)];

    /// <summary>Every applier this assembly defines.</summary>
    private static IReadOnlyList<Type> AllApplierTypes()
        =>
        [
            .. typeof(ISyncEventApplier).Assembly
                .GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false }
                            && typeof(ISyncEventApplier).IsAssignableFrom(t)),
        ];

    [Fact]
    public void EveryApplier_IsRegistered()
    {
        // Read off the container's own descriptors, not off the source file: a
        // first draft of this test searched DependencyInjection.cs for the
        // registration line and passed happily when the line was commented out.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            })
            .Build();

        ServiceCollection services = new();
        services.AddInfrastructure(configuration);

        IReadOnlyList<Type> registered =
        [
            .. services
                .Where(d => d.ServiceType == typeof(ISyncEventApplier))
                .Select(d => d.ImplementationType)
                .OfType<Type>(),
        ];

        registered.Should().BeEquivalentTo(
            AllApplierTypes(),
            "an applier nothing resolves is an event type refused as unsupported");
    }

    [Fact]
    public void TheAssemblyScanFindsTheAppliersItIsMeantTo()
    {
        // A scan that silently matched nothing would make both tests above pass
        // for the worst possible reason.
        AppliedEventTypes().Should().HaveCountGreaterThan(1);
    }
}
