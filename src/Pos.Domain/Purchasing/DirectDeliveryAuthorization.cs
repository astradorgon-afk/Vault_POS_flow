using Pos.Domain.Common;

namespace Pos.Domain.Purchasing;

/// <summary>The lifecycle state of a direct-to-store delivery authorization.</summary>
public enum DirectDeliveryAuthorizationStatus
{
    /// <summary>The authorization is valid and can be relied on for receiving.</summary>
    Active = 1,

    /// <summary>The authorization has been withdrawn and can no longer be relied on.</summary>
    Revoked = 2,
}

/// <summary>
/// A standing authorization for a supplier to deliver goods straight to a store
/// rather than through the main warehouse. It is the explicit path of
/// QUARANTINE.md §3: without an approved store-destined purchase order or a
/// valid authorization, goods arriving at a store are an unauthorized delivery.
/// </summary>
/// <remarks>
/// <para>
/// The authorization is scoped to a supplier, a store, a date window and
/// optionally a single product and a value cap. A <see langword="null"/>
/// product means any product the supplier sells; a <see langword="null"/> value
/// cap means an unlimited value. Issuing or revoking requires
/// <c>purchase.direct_to_store.authorize</c>.
/// </para>
/// </remarks>
public sealed class DirectDeliveryAuthorization : AggregateRoot<DirectDeliveryAuthorizationId>
{
    private DirectDeliveryAuthorization(
        DirectDeliveryAuthorizationId id,
        SupplierId supplierId,
        LocationId storeLocationId,
        DateOnly validFrom,
        DateOnly validUntil,
        ProductId? productId,
        decimal? valueCap,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        SupplierId = supplierId;
        StoreLocationId = storeLocationId;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        ProductId = productId;
        ValueCap = valueCap;
        CreatedByUserId = createdBy;
        CreatedAtUtc = createdAtUtc;
        Status = DirectDeliveryAuthorizationStatus.Active;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private DirectDeliveryAuthorization()
    {
        SupplierId = SupplierId.Empty;
        StoreLocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
    }

    /// <summary>Creates a new active authorization.</summary>
    /// <param name="supplierId">The supplier allowed to deliver.</param>
    /// <param name="storeLocationId">The store that may receive the delivery.</param>
    /// <param name="validFrom">The first day the authorization applies.</param>
    /// <param name="validUntil">The last day the authorization applies.</param>
    /// <param name="createdBy">The user issuing the authorization.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <param name="productId">An optional product the authorization is limited to.</param>
    /// <param name="valueCap">An optional cap on the delivered value, in the
    /// store's currency. <see langword="null"/> means uncapped.</param>
    /// <returns>The authorization, or a validation error.</returns>
    public static Result<DirectDeliveryAuthorization> Create(
        SupplierId supplierId,
        LocationId storeLocationId,
        DateOnly validFrom,
        DateOnly validUntil,
        UserId createdBy,
        DateTimeOffset createdAtUtc,
        ProductId? productId = null,
        decimal? valueCap = null)
    {
        List<Error> errors = [];

        if (validFrom > validUntil)
        {
            errors.Add(PurchasingErrors.InvalidAuthorizationWindow(validFrom, validUntil));
        }

        if (valueCap is <= 0m)
        {
            errors.Add(PurchasingErrors.AuthorizationValueCapInvalid);
        }

        return errors.Count == 0
            ? Result<DirectDeliveryAuthorization>.Success(new DirectDeliveryAuthorization(
                DirectDeliveryAuthorizationId.New(),
                supplierId,
                storeLocationId,
                validFrom,
                validUntil,
                productId,
                valueCap,
                createdBy,
                createdAtUtc))
            : Result<DirectDeliveryAuthorization>.Failure(errors);
    }

    /// <summary>Gets whether the authorization is valid on a given business day.</summary>
    /// <param name="businessDate">The day in question.</param>
    /// <returns><see langword="true"/> when active and the day falls inside the window.</returns>
    public bool IsActiveOn(DateOnly businessDate)
        => Status == DirectDeliveryAuthorizationStatus.Active
            && businessDate >= ValidFrom
            && businessDate <= ValidUntil;

    /// <summary>Withdraws the authorization so it can no longer be relied on.</summary>
    /// <param name="revokedBy">The user withdrawing it.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a conflict when already revoked.</returns>
    public Result Revoke(UserId revokedBy, DateTimeOffset now)
    {
        if (Status != DirectDeliveryAuthorizationStatus.Active)
        {
            return Result.Failure(PurchasingErrors.AuthorizationAlreadyRevoked(Id));
        }

        Status = DirectDeliveryAuthorizationStatus.Revoked;
        RevokedByUserId = revokedBy;
        RevokedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Gets the lifecycle status.</summary>
    public DirectDeliveryAuthorizationStatus Status { get; private set; }

    /// <summary>Gets the supplier allowed to deliver.</summary>
    public SupplierId SupplierId { get; private set; }

    /// <summary>Gets the store that may receive the delivery.</summary>
    public LocationId StoreLocationId { get; private set; }

    /// <summary>Gets the first day the authorization applies.</summary>
    public DateOnly ValidFrom { get; private set; }

    /// <summary>Gets the last day the authorization applies.</summary>
    public DateOnly ValidUntil { get; private set; }

    /// <summary>Gets the product the authorization is limited to, if any.</summary>
    public ProductId? ProductId { get; private set; }

    /// <summary>Gets the cap on the delivered value, if any.</summary>
    public decimal? ValueCap { get; private set; }

    /// <summary>Gets the user who issued the authorization.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets the instant the authorization was issued.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the user who revoked the authorization, if it has been revoked.</summary>
    public UserId? RevokedByUserId { get; private set; }

    /// <summary>Gets the instant the authorization was revoked, if it has been.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }
}