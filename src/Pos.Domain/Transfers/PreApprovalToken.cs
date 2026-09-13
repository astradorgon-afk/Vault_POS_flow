using Pos.Domain.Common;

namespace Pos.Domain.Transfers;

/// <summary>
/// The lifecycle of a head office pre-approval token.
/// </summary>
public enum PreApprovalTokenStatus
{
    /// <summary>The token is issued and can back a transfer request.</summary>
    Draft = 1,

    /// <summary>The token was consumed by a submitted transfer. Single use.</summary>
    Consumed = 2,

    /// <summary>Head office revoked the token before it was used.</summary>
    Revoked = 3,
}

/// <summary>
/// A single-use authorisation issued by head office that lets a store raise a
/// transfer request without waiting for the review queue: the token issuer's
/// authority stands in for the approver. Tokens are scoped to a route, an
/// optional product subset, an optional value ceiling, and a validity window;
/// they are consumed atomically when the transfer is submitted.
/// </summary>
public sealed class PreApprovalToken
{
    private readonly List<PreApprovalTokenProduct> _productRows = [];

    private PreApprovalToken(
        PreApprovalTokenId id,
        DocumentNumber number,
        LocationId sourceLocationId,
        LocationId destinationLocationId,
        IReadOnlyCollection<ProductId> productIds,
        decimal? maxValue,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Number = number.Value;
        SourceLocationId = sourceLocationId;
        DestinationLocationId = destinationLocationId;
        _productRows.AddRange(
            productIds.Select((p, index) => new PreApprovalTokenProduct(id, p) { Ordinal = index + 1 }));
        MaxValue = maxValue;
        ValidFromUtc = validFromUtc;
        ValidUntilUtc = validUntilUtc;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = PreApprovalTokenStatus.Draft;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private PreApprovalToken()
    {
        Number = string.Empty;
        SourceLocationId = LocationId.Empty;
        DestinationLocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
    }

    /// <summary>Gets the token identifier.</summary>
    public PreApprovalTokenId Id { get; }

    /// <summary>Gets the human-readable PAT document number.</summary>
    public string Number { get; }

    /// <summary>Gets the store the covered transfers leave.</summary>
    public LocationId SourceLocationId { get; }

    /// <summary>Gets the store the covered transfers arrive at.</summary>
    public LocationId DestinationLocationId { get; }

    /// <summary>
    /// Gets the products the token covers. An empty list means the token covers
    /// every stock product, still scoped to the route.
    /// </summary>
    public IReadOnlyList<ProductId> Products => _productRows.Select(p => p.ProductId).ToList();

    /// <summary>Gets the covered product rows, for persistence and querying.</summary>
    public IReadOnlyList<PreApprovalTokenProduct> ProductRows => _productRows;

    /// <summary>Gets the value ceiling of a single covered transfer, or null for no ceiling.</summary>
    public decimal? MaxValue { get; }

    /// <summary>Gets the instant the token becomes usable.</summary>
    public DateTimeOffset ValidFromUtc { get; }

    /// <summary>Gets the instant the token expires.</summary>
    public DateTimeOffset ValidUntilUtc { get; }

    /// <summary>Gets the head office user who issued the token; their authority approves the transfer.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets when the token was issued.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the current lifecycle status.</summary>
    public PreApprovalTokenStatus Status { get; private set; }

    /// <summary>Gets the transfer the token was consumed by, when consumed.</summary>
    public TransferOrderId? ConsumedByTransferId { get; private set; }

    /// <summary>Gets when the token was consumed, when consumed.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>Gets who revoked the token, when revoked.</summary>
    public UserId? RevokedByUserId { get; private set; }

    /// <summary>Gets when the token was revoked, when revoked.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Gets the revocation reason.</summary>
    public string? RevokeReason { get; private set; }

    /// <summary>
    /// Creates a draft pre-approval token. The PAT document number is allocated
    /// by the caller so it is never exposed to races on the counter.
    /// </summary>
    /// <param name="number">The allocated PAT number.</param>
    /// <param name="sourceLocationId">The store the covered transfers leave.</param>
    /// <param name="destinationLocationId">The store the covered transfers arrive at.</param>
    /// <param name="productIds">The covered products; empty means every stock product.</param>
    /// <param name="maxValue">The value ceiling of a single covered transfer, or null.</param>
    /// <param name="validFromUtc">The instant the token becomes usable.</param>
    /// <param name="validUntilUtc">The instant the token expires.</param>
    /// <param name="createdByUserId">The head office user issuing the token.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <returns>The token, or validation errors.</returns>
    public static Result<PreApprovalToken> Create(
        DocumentNumber number,
        LocationId sourceLocationId,
        LocationId destinationLocationId,
        IReadOnlyCollection<ProductId> productIds,
        decimal? maxValue,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        if (sourceLocationId.IsEmpty)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.SourceRequired);
        }

        if (destinationLocationId.IsEmpty)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.DestinationRequired);
        }

        if (sourceLocationId == destinationLocationId)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.SameLocation);
        }

        if (maxValue is not null && maxValue.Value <= 0m)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.MaxValueInvalid);
        }

        if (validFromUtc >= validUntilUtc)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.InvalidWindow);
        }

        if (validUntilUtc <= createdAtUtc)
        {
            return Result<PreApprovalToken>.Failure(PreApprovalTokenErrors.ExpiredAtIssue);
        }

        return Result<PreApprovalToken>.Success(new PreApprovalToken(
            PreApprovalTokenId.New(),
            number,
            sourceLocationId,
            destinationLocationId,
            productIds,
            maxValue,
            validFromUtc,
            validUntilUtc,
            createdByUserId,
            createdAtUtc));
    }

    /// <summary>Gets whether the token is currently usable.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>True when the token is drafted and inside its validity window.</returns>
    public bool IsUsableAt(DateTimeOffset now) =>
        Status == PreApprovalTokenStatus.Draft
        && now >= ValidFromUtc
        && now <= ValidUntilUtc;

    /// <summary>
    /// Consumes the token against a transfer. The caller validates scope and
    /// value before this; consumption is the atomic single-use gate.
    /// </summary>
    /// <param name="transferId">The transfer the token backs.</param>
    /// <param name="consumedAtUtc">The current instant.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result Consume(TransferOrderId transferId, DateTimeOffset consumedAtUtc)
    {
        if (Status == PreApprovalTokenStatus.Consumed)
        {
            return Result.Failure(PreApprovalTokenErrors.AlreadyConsumed(Id));
        }

        if (Status == PreApprovalTokenStatus.Revoked)
        {
            return Result.Failure(PreApprovalTokenErrors.Revoked(Id));
        }

        if (consumedAtUtc < ValidFromUtc)
        {
            return Result.Failure(PreApprovalTokenErrors.NotYetValid);
        }

        if (consumedAtUtc > ValidUntilUtc)
        {
            return Result.Failure(PreApprovalTokenErrors.Expired);
        }

        Status = PreApprovalTokenStatus.Consumed;
        ConsumedByTransferId = transferId;
        ConsumedAtUtc = consumedAtUtc;
        return Result.Success();
    }

    /// <summary>Revokes the token before it is used.</summary>
    /// <param name="revokedBy">The head office user revoking the token.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="reason">Why the token is revoked; required.</param>
    /// <returns>Success, or a lifecycle error.</returns>
    public Result Revoke(UserId revokedBy, DateTimeOffset now, string? reason)
    {
        if (Status == PreApprovalTokenStatus.Consumed)
        {
            return Result.Failure(PreApprovalTokenErrors.AlreadyConsumed(Id));
        }

        if (Status == PreApprovalTokenStatus.Revoked)
        {
            return Result.Failure(PreApprovalTokenErrors.Revoked(Id));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PreApprovalTokenErrors.RevokeReasonRequired);
        }

        Status = PreApprovalTokenStatus.Revoked;
        RevokedByUserId = revokedBy;
        RevokedAtUtc = now;
        RevokeReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Gets whether a transfer value exceeds the token's ceiling.</summary>
    /// <param name="estimatedValue">The estimated value of the transfer.</param>
    /// <returns>True when the ceiling is set and the value exceeds it.</returns>
    public bool ValueExceeds(decimal estimatedValue) =>
        MaxValue is not null && estimatedValue > MaxValue.Value;
}

/// <summary>
/// One product covered by a pre-approval token. Persisted as a child row so the
/// product scope can be queried and listed without deserialising a collection.
/// </summary>
public sealed class PreApprovalTokenProduct
{
    internal PreApprovalTokenProduct(PreApprovalTokenId tokenId, ProductId productId)
    {
        TokenId = tokenId;
        ProductId = productId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private PreApprovalTokenProduct()
    {
    }

    /// <summary>Gets the owning token.</summary>
    public PreApprovalTokenId TokenId { get; }

    /// <summary>Gets the covered product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the ordering key within the token's scope.</summary>
    public int Ordinal { get; internal set; }
}