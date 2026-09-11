using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Thrown when code attempts to modify or delete a record that the system keeps
/// permanently.
/// </summary>
public sealed class AppendOnlyViolationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AppendOnlyViolationException"/> class.</summary>
    public AppendOnlyViolationException()
        : base("An append-only record cannot be modified or deleted.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AppendOnlyViolationException"/> class.</summary>
    /// <param name="message">The message.</param>
    public AppendOnlyViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AppendOnlyViolationException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AppendOnlyViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Rejects any attempt to update or delete a permanent record before it reaches
/// the database.
/// </summary>
/// <remarks>
/// This is the second of four independent guards on the ledger. The domain type
/// has no setters, this interceptor catches anything that reaches the change
/// tracker anyway, database triggers catch raw SQL, and the application's
/// database role is granted only SELECT and INSERT on these tables. One guard
/// would be a convention; four make tampering require database-owner
/// credentials that the application does not hold.
/// </remarks>
public sealed class AppendOnlyInterceptor : SaveChangesInterceptor
{
    private static readonly Type[] AppendOnlyTypes = [typeof(InventoryMovement)];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            Type clrType = entry.Metadata.ClrType;

            if (Array.Exists(AppendOnlyTypes, t => t.IsAssignableFrom(clrType)))
            {
                throw new AppendOnlyViolationException(
                    FormattableString.Invariant(
                        $"{clrType.Name} is append-only and cannot be {entry.State.ToString().ToLowerInvariant()}. Correct it with a reversing entry instead."));
            }
        }
    }
}
