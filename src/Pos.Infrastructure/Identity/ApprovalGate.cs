using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Decides whether a named approver may authorise an action of a given value.
/// </summary>
/// <remarks>
/// <para>
/// Permission answers "may this person approve adjustments at all"; the tier
/// answers "up to how much". Both are needed: a store manager correcting a
/// four-hundred-peso miscount and one writing off a quarter of a million are
/// the same operation with very different consequences.
/// </para>
/// <para>
/// Self-approval is refused above a configured limit, which defaults to zero —
/// that is, never. Letting the person who raised a write-off sign it off is the
/// single easiest way to take stock out of a business.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="permissions">The permission evaluator.</param>
/// <param name="options">Organization settings carrying the tier ceilings.</param>
public sealed class ApprovalGate(
    PosDbContext context,
    DatabasePermissionEvaluator permissions,
    IOptions<OrganizationOptions> options) : IApprovalGate
{
    private readonly OrganizationOptions _organization = options.Value;

    /// <inheritdoc />
    public async Task<Result> RequireAsync(
        string action,
        decimal absoluteValue,
        UserId approver,
        UserId documentCreator,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (absoluteValue < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteValue), absoluteValue, "Approval value must be an absolute amount.");
        }

        UserAuthorization authorization = await permissions
            .GetAuthorizationAsync(approver, cancellationToken)
            .ConfigureAwait(false);

        if (!authorization.IsActive)
        {
            return Result.Failure(Error.Forbidden(
                "approval.approver_inactive",
                "The named approver is not an active user."));
        }

        if (!authorization.HasAllLocations && !authorization.Locations.Contains(locationId))
        {
            return Result.Failure(Error.Forbidden(
                "approval.approver_out_of_scope",
                "The approver is not assigned to this location."));
        }

        if (approver == documentCreator && absoluteValue > _organization.ApprovalLimits.SelfApprovalLimit)
        {
            return Result.Failure(Error.ApprovalRequired(
                "approval.self_approval_refused",
                "This document must be approved by someone other than the person who raised it.",
                new Dictionary<string, object?>
                {
                    ["value"] = absoluteValue,
                    ["selfApprovalLimit"] = _organization.ApprovalLimits.SelfApprovalLimit,
                }));
        }

        decimal ceiling = CeilingFor(authorization.ApprovalTier);

        if (authorization.ApprovalTier != ApprovalTier.Unlimited && absoluteValue > ceiling)
        {
            return Result.Failure(Error.ApprovalRequired(
                "approval.tier_exceeded",
                FormattableString.Invariant(
                    $"This action is worth {absoluteValue} and needs a higher approval tier than {authorization.ApprovalTier}."),
                new Dictionary<string, object?>
                {
                    ["action"] = action,
                    ["value"] = absoluteValue,
                    ["approverTier"] = authorization.ApprovalTier.ToString(),
                    ["approverCeiling"] = ceiling,
                    ["requiredTier"] = RequiredTierFor(absoluteValue).ToString(),
                }));
        }

        return Result.Success();
    }

    /// <summary>Gets the value ceiling attached to a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The ceiling, or <see cref="decimal.MaxValue"/> for the owner.</returns>
    public decimal CeilingFor(ApprovalTier tier) => tier switch
    {
        ApprovalTier.Tier1 => _organization.ApprovalLimits.Tier1,
        ApprovalTier.Tier2 => _organization.ApprovalLimits.Tier2,
        ApprovalTier.Tier3 => _organization.ApprovalLimits.Tier3,
        ApprovalTier.Unlimited => decimal.MaxValue,
        _ => 0m,
    };

    /// <summary>Gets the lowest tier that could approve a value.</summary>
    /// <param name="absoluteValue">The value at stake.</param>
    /// <returns>The required tier.</returns>
    public ApprovalTier RequiredTierFor(decimal absoluteValue)
    {
        if (absoluteValue <= _organization.ApprovalLimits.Tier1)
        {
            return ApprovalTier.Tier1;
        }

        if (absoluteValue <= _organization.ApprovalLimits.Tier2)
        {
            return ApprovalTier.Tier2;
        }

        return absoluteValue <= _organization.ApprovalLimits.Tier3
            ? ApprovalTier.Tier3
            : ApprovalTier.Unlimited;
    }

    /// <summary>Finds the users who could approve a value at a location.</summary>
    /// <param name="permissionCode">The approval permission required.</param>
    /// <param name="absoluteValue">The value at stake.</param>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidate approvers.</returns>
    /// <remarks>
    /// Used to route an approval request to someone who can actually grant it,
    /// rather than leaving a document sitting in a queue nobody is entitled to
    /// clear.
    /// </remarks>
    public async Task<IReadOnlyList<UserId>> FindEligibleApproversAsync(
        string permissionCode,
        decimal absoluteValue,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        ApprovalTier required = RequiredTierFor(absoluteValue);

        List<Guid> candidates = await context.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.DisabledAtUtc == null && u.ApprovalTier >= required)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<UserId> eligible = [];

        foreach (Guid candidate in candidates)
        {
            UserId userId = new(candidate);

            if (await permissions.HasPermissionAsync(userId, permissionCode, locationId, cancellationToken)
                    .ConfigureAwait(false))
            {
                eligible.Add(userId);
            }
        }

        return eligible;
    }
}
