using FluentAssertions;
using Pos.Application.Catalog;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Tests.Catalog;

/// <summary>
/// The gate that holds a product change for the owner: it must let an approver
/// straight through, hold everybody else's change rather than applying it,
/// insist on a reason, and never touch a command that is not a product change.
/// </summary>
public sealed class ProductChangeApprovalBehaviourTests
{
    private static readonly ProductId Product = ProductId.New();
    private static readonly UserId Staff = new(Guid.CreateVersion7());

    [Fact]
    public async Task AnApprover_ChangesTheProductDirectly()
    {
        FakeRequests requests = new();
        var behaviour = NewBehaviour(requests, mayApprove: true, reason: null);

        Result<ProductId> result = await behaviour.HandleAsync(
            Change(), () => Task.FromResult(Result<ProductId>.Success(Product)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        requests.Added.Should().BeEmpty("the owner needs no approval and no reason");
    }

    [Fact]
    public async Task AnybodyElse_HasTheChangeHeld_AndTheProductIsNotTouched()
    {
        FakeRequests requests = new();
        bool ranHandler = false;
        var behaviour = NewBehaviour(requests, mayApprove: false, reason: "Supplier changed the pack size");

        Result<ProductId> result = await behaviour.HandleAsync(
            Change(),
            () =>
            {
                ranHandler = true;
                return Task.FromResult(Result<ProductId>.Success(Product));
            },
            CancellationToken.None);

        ranHandler.Should().BeFalse("an unapproved change must never reach the product");
        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("catalog.change_awaiting_approval");

        requests.Added.Should().ContainSingle();
        requests.Added[0].Reason.Should().Be("Supplier changed the pack size");
        requests.Added[0].ProductId.Should().Be(Product);
        requests.Added[0].Status.Should().Be(ProductChangeRequestStatus.Pending);
    }

    [Fact]
    public async Task WithoutAReason_NothingIsHeld_AndTheChangeIsRefused()
    {
        FakeRequests requests = new();
        var behaviour = NewBehaviour(requests, mayApprove: false, reason: "   ");

        Result<ProductId> result = await behaviour.HandleAsync(
            Change(), () => Task.FromResult(Result<ProductId>.Success(Product)), CancellationToken.None);

        result.Errors[0].Code.Should().Be("catalog.change_reason_required");
        requests.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task AnApprovedReplay_PassesStraightThrough()
    {
        FakeRequests requests = new();
        ProductChangeReplayScope replay = new();
        var behaviour = NewBehaviour(requests, mayApprove: false, reason: null, replay);

        Result<ProductId> result = await replay.RunAsync(() => behaviour.HandleAsync(
            Change(), () => Task.FromResult(Result<ProductId>.Success(Product)), CancellationToken.None));

        result.IsSuccess.Should().BeTrue("the owner has already approved this one");
        requests.Added.Should().BeEmpty("re-holding an approved change would loop forever");
    }

    [Fact]
    public async Task ACommandThatIsNotAProductChange_IsLeftAlone()
    {
        FakeRequests requests = new();
        var behaviour = NewStringBehaviour(requests, mayApprove: false);

        Result<int> result = await behaviour.HandleAsync(
            "CompleteSaleCommand", () => Task.FromResult(Result<int>.Success(7)), CancellationToken.None);

        result.Value.Should().Be(7);
        requests.Added.Should().BeEmpty();
    }

    private static SetProductActivationCommand Change()
        => new(Product, Active: false, Reason: "Discontinued");

    private static ProductChangeApprovalBehaviour<SetProductActivationCommand, ProductId> NewBehaviour(
        FakeRequests requests,
        bool mayApprove,
        string? reason,
        ProductChangeReplayScope? replay = null)
        => new(
            requests,
            new FakePermissions(mayApprove),
            new FakeCurrentUser(Staff),
            new ProductChangeReason { Reason = reason },
            replay ?? new ProductChangeReplayScope(),
            new FakeClock());

    private static ProductChangeApprovalBehaviour<string, int> NewStringBehaviour(
        FakeRequests requests,
        bool mayApprove)
        => new(
            requests,
            new FakePermissions(mayApprove),
            new FakeCurrentUser(Staff),
            new ProductChangeReason(),
            new ProductChangeReplayScope(),
            new FakeClock());

    private sealed class FakeRequests : IProductChangeRequestRepository
    {
        public List<ProductChangeRequest> Added { get; } = [];

        public Task AddAsync(ProductChangeRequest request, CancellationToken cancellationToken)
        {
            Added.Add(request);
            return Task.CompletedTask;
        }

        public Task<ProductChangeRequest?> GetForUpdateAsync(
            ProductChangeRequestId requestId, CancellationToken cancellationToken)
            => Task.FromResult<ProductChangeRequest?>(null);

        public Task<IReadOnlyList<ProductChangeRequestView>> ListAsync(
            bool pendingOnly, ProductId? productId, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProductChangeRequestView>>([]);
    }

    private sealed class FakePermissions(bool mayApprove) : IPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(
            UserId userId, string permissionCode, LocationId? locationId, CancellationToken cancellationToken)
            => Task.FromResult(mayApprove && permissionCode == Permissions.Catalog.ApproveChanges);

        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
            UserId userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class FakeCurrentUser(UserId user) : ICurrentUser
    {
        public UserId? UserId => user;

        public DeviceId? DeviceId => null;

        public IReadOnlyCollection<LocationId> AssignedLocations => [];

        public bool HasAllLocations => false;

        public CorrelationId CorrelationId => new(Guid.Empty);

        public string? IpAddress => null;

        public string? UserAgent => null;

        public string? RoleSnapshot => null;
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }
}
