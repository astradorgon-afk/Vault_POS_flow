using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pos.Application.Common.Abstractions;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Tracks the authorization policy version, which is bumped whenever anything
/// that affects who can do what changes.
/// </summary>
public interface IPolicyVersionProvider
{
    /// <summary>Gets the current version.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version number.</returns>
    Task<long> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Increments the version, invalidating every cached permission set.
    /// </summary>
    /// <param name="reason">What changed, recorded for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new version number.</returns>
    Task<long> BumpAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The database-backed policy version, cached briefly in process.
/// </summary>
/// <remarks>
/// <para>
/// Permission sets are cached under a key that includes this version, so a
/// single increment invalidates all of them at once. That is what makes taking
/// authority away take effect now rather than at the next token expiry.
/// </para>
/// <para>
/// The version itself is cached for a few seconds to keep it off the hot path.
/// A change made through this instance clears that cache immediately, so a
/// single-instance deployment sees revocation instantly. When the API is scaled
/// out, other instances converge within the cache window; a Redis backplane
/// closes that gap and is the documented next step.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="cache">The in-process cache.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class PolicyVersionProvider(
    PosDbContext context,
    IMemoryCache cache,
    ISystemClock clock) : IPolicyVersionProvider
{
    private const string CacheKey = "authz:policy-version";

    /// <summary>How long the version itself is held in process.</summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<long> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out long cached))
        {
            return cached;
        }

        AuthorizationPolicyVersion? row = await context.PolicyVersion
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == AuthorizationPolicyVersion.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        long version = row?.Version ?? 0L;
        cache.Set(CacheKey, version, CacheWindow);

        return version;
    }

    /// <inheritdoc />
    public async Task<long> BumpAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        AuthorizationPolicyVersion? row = await context.PolicyVersion
            .AsTracking()
            .FirstOrDefaultAsync(v => v.Id == AuthorizationPolicyVersion.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new AuthorizationPolicyVersion
            {
                Id = AuthorizationPolicyVersion.SingletonId,
                Version = 1,
                UpdatedAtUtc = clock.UtcNow,
                LastChangeReason = reason,
            };

            context.PolicyVersion.Add(row);
        }
        else
        {
            row.Version++;
            row.UpdatedAtUtc = clock.UtcNow;
            row.LastChangeReason = reason;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        cache.Remove(CacheKey);

        return row.Version;
    }
}
