using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>Where a requested catalogue change has got to.</summary>
public enum ProductChangeRequestStatus
{
    /// <summary>Raised and waiting on an approver.</summary>
    Pending = 0,

    /// <summary>Approved and applied to the product.</summary>
    Approved = 1,

    /// <summary>Refused, with a reason.</summary>
    Rejected = 2,

    /// <summary>Taken back by whoever raised it, before a decision.</summary>
    Withdrawn = 3,

    /// <summary>Approved, but applying it to the product failed.</summary>
    Failed = 4,
}

/// <summary>
/// A catalogue change somebody asked for and cannot make on their own.
/// </summary>
/// <remarks>
/// <para>
/// Product master data decides how the rest of the system behaves: the category
/// selects which cycle counts include an item, the VAT flag changes the tax on
/// every sale of it, and the batch and expiry flags drive receiving and FEFO.
/// Those are the owner's decisions, so a holder of <c>catalog.edit</c> who is
/// not also an approver raises one of these instead of writing to the product.
/// </para>
/// <para>
/// The change is held, not applied: the command that would have run is stored
/// and replayed on approval, so nothing an approver has not seen ever reaches a
/// till. The stored payload is read back only through a whitelist of known
/// command types — it is never resolved as an arbitrary type name.
/// </para>
/// </remarks>
public sealed class ProductChangeRequest : AggregateRoot<ProductChangeRequestId>
{
    /// <summary>The shortest reason accepted when raising a request.</summary>
    public const int MinimumReasonLength = 10;

    /// <summary>The longest reason or decision note accepted.</summary>
    public const int ReasonMaxLength = 512;

    /// <summary>The longest change summary stored for display.</summary>
    public const int SummaryMaxLength = 256;

    private ProductChangeRequest(
        ProductChangeRequestId id,
        ProductId productId,
        string changeType,
        string summary,
        string payload,
        string reason,
        UserId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        Id = id;
        ProductId = productId;
        ChangeType = changeType;
        Summary = summary;
        Payload = payload;
        Reason = reason;
        RequestedBy = requestedBy;
        RequestedAtUtc = requestedAtUtc;
        Status = ProductChangeRequestStatus.Pending;
    }

    private ProductChangeRequest()
    {
        ChangeType = string.Empty;
        Summary = string.Empty;
        Payload = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Gets the product the change applies to.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the registry key of the command being held.</summary>
    public string ChangeType { get; private set; }

    /// <summary>Gets a one-line description of the change, for the approval queue.</summary>
    public string Summary { get; private set; }

    /// <summary>Gets the serialized command, replayed verbatim on approval.</summary>
    public string Payload { get; private set; }

    /// <summary>Gets why the requester says the change is needed.</summary>
    public string Reason { get; private set; }

    /// <summary>Gets who raised the request.</summary>
    public UserId RequestedBy { get; private set; }

    /// <summary>Gets when the request was raised.</summary>
    public DateTimeOffset RequestedAtUtc { get; private set; }

    /// <summary>Gets where the request has got to.</summary>
    public ProductChangeRequestStatus Status { get; private set; }

    /// <summary>Gets who approved, rejected or withdrew it.</summary>
    public UserId? DecidedBy { get; private set; }

    /// <summary>Gets when the decision was made.</summary>
    public DateTimeOffset? DecidedAtUtc { get; private set; }

    /// <summary>Gets the approver's note, required on a rejection.</summary>
    public string? DecisionNote { get; private set; }

    /// <summary>Gets why applying an approved change failed, when it did.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Gets a value indicating whether a decision can still be made.</summary>
    public bool IsOpen => Status == ProductChangeRequestStatus.Pending;

    /// <summary>Raises a request to change a product.</summary>
    /// <param name="productId">The product the change applies to.</param>
    /// <param name="changeType">The registry key of the held command.</param>
    /// <param name="summary">A one-line description for the approval queue.</param>
    /// <param name="payload">The serialized command.</param>
    /// <param name="reason">Why the change is needed.</param>
    /// <param name="requestedBy">Who is asking.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The request, or why it was refused.</returns>
    public static Result<ProductChangeRequest> Create(
        ProductId productId,
        string changeType,
        string summary,
        string payload,
        string? reason,
        UserId requestedBy,
        DateTimeOffset now)
    {
        if (productId.IsEmpty)
        {
            return Result<ProductChangeRequest>.Failure(CatalogErrors.ProductUnknown(productId));
        }

        if (string.IsNullOrWhiteSpace(changeType) || string.IsNullOrWhiteSpace(payload))
        {
            return Result<ProductChangeRequest>.Failure(CatalogErrors.ChangeRequestNotReplayable(changeType));
        }

        string? trimmed = reason?.Trim();
        if (trimmed is null || trimmed.Length < MinimumReasonLength)
        {
            return Result<ProductChangeRequest>.Failure(CatalogErrors.ChangeReasonRequired(MinimumReasonLength));
        }

        if (trimmed.Length > ReasonMaxLength)
        {
            trimmed = trimmed[..ReasonMaxLength];
        }

        string shortSummary = string.IsNullOrWhiteSpace(summary) ? changeType : summary.Trim();
        if (shortSummary.Length > SummaryMaxLength)
        {
            shortSummary = shortSummary[..SummaryMaxLength];
        }

        return Result<ProductChangeRequest>.Success(new ProductChangeRequest(
            ProductChangeRequestId.New(), productId, changeType, shortSummary, payload, trimmed, requestedBy, now));
    }

    /// <summary>Approves the request. The caller has already checked approval authority.</summary>
    /// <param name="approver">Who approved it.</param>
    /// <param name="note">An optional note.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Success, or why it was refused.</returns>
    public Result Approve(UserId approver, string? note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Result.Failure(CatalogErrors.ChangeRequestClosed(Status.ToString()));
        }

        // An approver waving through their own request would make the gate
        // decorative, and the owner never needs one in the first place.
        if (approver == RequestedBy)
        {
            return Result.Failure(CatalogErrors.ChangeRequestSelfApproval());
        }

        Status = ProductChangeRequestStatus.Approved;
        DecidedBy = approver;
        DecidedAtUtc = now;
        DecisionNote = Trim(note);
        return Result.Success();
    }

    /// <summary>Refuses the request.</summary>
    /// <param name="approver">Who refused it.</param>
    /// <param name="note">Why; required.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Success, or why it was refused.</returns>
    public Result Reject(UserId approver, string? note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Result.Failure(CatalogErrors.ChangeRequestClosed(Status.ToString()));
        }

        string? trimmed = Trim(note);
        if (trimmed is null)
        {
            return Result.Failure(CatalogErrors.ChangeDecisionNoteRequired());
        }

        Status = ProductChangeRequestStatus.Rejected;
        DecidedBy = approver;
        DecidedAtUtc = now;
        DecisionNote = trimmed;
        return Result.Success();
    }

    /// <summary>Takes the request back, before a decision.</summary>
    /// <param name="requester">Who is withdrawing it; must be whoever raised it.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Success, or why it was refused.</returns>
    public Result Withdraw(UserId requester, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Result.Failure(CatalogErrors.ChangeRequestClosed(Status.ToString()));
        }

        if (requester != RequestedBy)
        {
            return Result.Failure(CatalogErrors.ChangeRequestNotYours());
        }

        Status = ProductChangeRequestStatus.Withdrawn;
        DecidedBy = requester;
        DecidedAtUtc = now;
        return Result.Success();
    }

    /// <summary>
    /// Records that an approved change could not be applied, so it shows in the
    /// queue as needing attention rather than silently reading as done.
    /// </summary>
    /// <param name="failure">What went wrong.</param>
    public void MarkFailed(string? failure)
    {
        Status = ProductChangeRequestStatus.Failed;
        FailureReason = Trim(failure) ?? "The change could not be applied.";
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length > ReasonMaxLength ? trimmed[..ReasonMaxLength] : trimmed;
    }
}
