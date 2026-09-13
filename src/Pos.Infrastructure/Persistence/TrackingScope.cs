using Microsoft.EntityFrameworkCore;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Switches a context to tracking queries for the lifetime of the scope.
/// </summary>
/// <remarks>
/// <para>
/// The context defaults to no-tracking because reads dominate. ASP.NET Core
/// Identity's stores were not written for that: they load a row with a plain
/// query or <c>Find</c>, change it, and rely on <c>SaveChanges</c> to write it.
/// Under the no-tracking default the change is made to a detached object and
/// silently discarded — a redeemed recovery code stayed usable, and a reset
/// authenticator key was never rotated.
/// </para>
/// <para>
/// Any operation that lets Identity modify existing rows runs inside this scope.
/// Restoring the previous behaviour on dispose keeps the rest of the request on
/// the no-tracking default.
/// </para>
/// </remarks>
public sealed class TrackingScope : IDisposable
{
    private readonly DbContext _context;
    private readonly QueryTrackingBehavior _previous;

    private TrackingScope(DbContext context)
    {
        _context = context;
        _previous = context.ChangeTracker.QueryTrackingBehavior;
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
    }

    /// <summary>Starts tracking queries on a context until the scope is disposed.</summary>
    /// <param name="context">The context.</param>
    /// <returns>The scope.</returns>
    public static TrackingScope Begin(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TrackingScope(context);
    }

    /// <inheritdoc />
    public void Dispose() => _context.ChangeTracker.QueryTrackingBehavior = _previous;
}
