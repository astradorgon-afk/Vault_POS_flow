using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// The direct-to-store delivery authorization: a standing, scoped permission
/// for a supplier to deliver straight to a store. Tests cover the window
/// validation, the value cap and the revoke lifecycle.
/// </summary>
public sealed class DirectDeliveryAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly SupplierId Supplier = SupplierId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId HQ = UserId.New();

    [Fact]
    public void Create_ValidScopedAuthorization_IsActiveWithinTheWindow()
    {
        Result<DirectDeliveryAuthorization> result = DirectDeliveryAuthorization.Create(
            Supplier,
            Store1,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            HQ,
            Now,
            Coke,
            50000m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        DirectDeliveryAuthorization authorization = result.Value;

        authorization.Id.Should().NotBe(DirectDeliveryAuthorizationId.Empty);
        authorization.Status.Should().Be(DirectDeliveryAuthorizationStatus.Active);
        authorization.SupplierId.Should().Be(Supplier);
        authorization.StoreLocationId.Should().Be(Store1);
        authorization.ProductId.Should().Be(Coke);
        authorization.ValueCap.Should().Be(50000m);
        authorization.CreatedByUserId.Should().Be(HQ);
        authorization.CreatedAtUtc.Should().Be(Now);
        authorization.RevokedAtUtc.Should().BeNull();

        authorization.IsActiveOn(new DateOnly(2026, 10, 15)).Should().BeTrue();
        authorization.IsActiveOn(new DateOnly(2026, 10, 1)).Should().BeTrue();
        authorization.IsActiveOn(new DateOnly(2026, 10, 31)).Should().BeTrue();
        authorization.IsActiveOn(new DateOnly(2026, 9, 30)).Should().BeFalse();
        authorization.IsActiveOn(new DateOnly(2026, 11, 1)).Should().BeFalse();
    }

    [Fact]
    public void Create_ReversedWindow_IsRejected()
    {
        Result<DirectDeliveryAuthorization> result = DirectDeliveryAuthorization.Create(
            Supplier,
            Store1,
            new DateOnly(2026, 10, 31),
            new DateOnly(2026, 10, 1),
            HQ,
            Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.direct_delivery_invalid_window");
    }

    [Fact]
    public void Create_NonPositiveValueCap_IsRejected()
    {
        Result<DirectDeliveryAuthorization> result = DirectDeliveryAuthorization.Create(
            Supplier,
            Store1,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            HQ,
            Now,
            valueCap: -1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.direct_delivery_value_cap_invalid");
    }

    [Fact]
    public void Revoke_Active_BecomesRevokedAndNoLongerActive()
    {
        Result<DirectDeliveryAuthorization> created = DirectDeliveryAuthorization.Create(
            Supplier,
            Store1,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            HQ,
            Now);

        DirectDeliveryAuthorization authorization = created.Value;

        Result revoked = authorization.Revoke(HQ, Now.AddDays(5));

        revoked.IsSuccess.Should().BeTrue();
        authorization.Status.Should().Be(DirectDeliveryAuthorizationStatus.Revoked);
        authorization.RevokedByUserId.Should().Be(HQ);
        authorization.RevokedAtUtc.Should().Be(Now.AddDays(5));
        authorization.IsActiveOn(new DateOnly(2026, 10, 15)).Should().BeFalse();
    }

    [Fact]
    public void Revoke_AlreadyRevoked_Fails()
    {
        Result<DirectDeliveryAuthorization> created = DirectDeliveryAuthorization.Create(
            Supplier,
            Store1,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31),
            HQ,
            Now);

        DirectDeliveryAuthorization authorization = created.Value;
        authorization.Revoke(HQ, Now);

        Result again = authorization.Revoke(HQ, Now.AddMinutes(1));

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("purchasing.direct_delivery_already_revoked");
    }
}