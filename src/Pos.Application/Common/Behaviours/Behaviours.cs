using System.Diagnostics;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Common.Behaviours;

/// <summary>
/// Runs FluentValidation validators before the handler. Validation failures are
/// expected outcomes, so they become a failed <see cref="Result"/> rather than
/// an exception.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="validators">Validators registered for this message.</param>
public sealed class ValidationBehaviour<TMessage, TResult>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehaviour<TMessage, TResult>
{
    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        IValidator<TMessage>[] applicable = [.. validators];

        if (applicable.Length == 0)
        {
            return await next().ConfigureAwait(false);
        }

        ValidationContext<TMessage> context = new(message);
        List<Error> errors = [];

        foreach (IValidator<TMessage> validator in applicable)
        {
            ValidationResult result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);

            foreach (ValidationFailure failure in result.Errors)
            {
                errors.Add(Error.Validation(
                    failure.ErrorCode ?? "validation.failed",
                    failure.ErrorMessage,
                    new Dictionary<string, object?> { ["property"] = failure.PropertyName }));
            }
        }

        return errors.Count > 0
            ? Result<TResult>.Failure(errors)
            : await next().ConfigureAwait(false);
    }
}

/// <summary>
/// Enforces the permission a message declares, plus location scope.
/// </summary>
/// <remarks>
/// This is the second of two independent server-side checks: the HTTP endpoint
/// also carries a permission attribute. Both exist deliberately, so that a
/// message dispatched from a background worker, a Blazor server circuit or the
/// synchronization processor is checked identically to one from a controller.
/// </remarks>
/// <typeparam name="TMessage">The message type, which declares its permission.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="currentUser">The caller.</param>
/// <param name="permissions">The permission evaluator.</param>
/// <param name="logger">Logger.</param>
public sealed class AuthorizationBehaviour<TMessage, TResult>(
    ICurrentUser currentUser,
    IPermissionEvaluator permissions,
    ILogger<AuthorizationBehaviour<TMessage, TResult>> logger)
    : IPipelineBehaviour<TMessage, TResult>
    where TMessage : IAuthorizedMessage
{
    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(next);

        if (currentUser.UserId is not { } userId)
        {
            return Result<TResult>.Failure(new Error(
                "auth.unauthenticated",
                "Authentication is required.",
                ErrorType.Unauthenticated));
        }

        LocationId? scope = message is ILocationScoped scoped ? scoped.LocationId : null;

        bool authorized = await permissions
            .HasPermissionAsync(userId, message.RequiredPermission, scope, cancellationToken)
            .ConfigureAwait(false);

        if (!authorized)
        {
            // Logged at warning: repeated denials are a signal worth watching,
            // and the audit log records the attempt independently.
            BehaviourLog.PermissionDenied(
                logger, message.RequiredPermission, userId.Value, scope?.Value, typeof(TMessage).Name);

            return Result<TResult>.Failure(Error.Forbidden(
                "auth.permission_denied",
                "You do not have permission to perform this action."));
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// Logs the start, outcome and duration of every message, with the correlation
/// identifier attached. Never logs message contents, which can contain personal
/// or payment data.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="currentUser">The caller.</param>
/// <param name="logger">Logger.</param>
public sealed class LoggingBehaviour<TMessage, TResult>(
    ICurrentUser currentUser,
    ILogger<LoggingBehaviour<TMessage, TResult>> logger)
    : IPipelineBehaviour<TMessage, TResult>
{
    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        string name = typeof(TMessage).Name;
        long startedAt = Stopwatch.GetTimestamp();

        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = currentUser.CorrelationId.Value,
            ["UserId"] = currentUser.UserId?.Value,
            ["DeviceId"] = currentUser.DeviceId?.Value,
            ["Message"] = name,
        });

        try
        {
            Result<TResult> result = await next().ConfigureAwait(false);
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (result.IsSuccess)
            {
                BehaviourLog.Succeeded(logger, name, elapsedMs);
            }
            else
            {
                BehaviourLog.Failed(logger, name, elapsedMs, result.Error.Code);
            }

            return result;
        }
        catch (Exception ex)
        {
            BehaviourLog.Threw(logger, ex, name, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }
}

/// <summary>
/// Wraps a command in a single database transaction so a business operation is
/// all-or-nothing. A sale that cannot write its inventory movements does not
/// leave a sale behind.
/// </summary>
/// <remarks>
/// A command that loses a race with a concurrent writer - two branches selling
/// the same product at the same moment both move stock through the shared
/// customer counterparty - is rolled back and run again against the state the
/// winner left, a few times, before the conflict error is returned. The caller
/// sees a retryable outcome instead of a server fault either way. Contention can
/// only come from the inventory balance projection today, so the mapping is
/// unambiguous (see <see cref="Pos.Domain.Common.ConcurrencyConflictException"/>).
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="unitOfWork">The unit of work.</param>
public sealed class UnitOfWorkBehaviour<TCommand, TResult>(IUnitOfWork unitOfWork)
    : IPipelineBehaviour<TCommand, TResult>
{
    /// <summary>How many times a command runs before contention is reported.</summary>
    internal const int MaxAttempts = 5;

    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TCommand message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (unitOfWork.HasActiveTransaction)
        {
            // Already inside a transaction, for example when the synchronization
            // processor is applying a batch. Do not nest.
            return await next().ConfigureAwait(false);
        }

        for (int attempt = 1; ; attempt++)
        {
            await using (IUnitOfWorkTransaction transaction =
                await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Result<TResult> result = await next().ConfigureAwait(false);

                    if (result.IsFailure)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return result;
                    }

                    await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex) when (unitOfWork.IsConcurrencyConflict(ex))
                {
                    // A concurrent writer committed between this command's read
                    // and its save. Nothing of this attempt survives: roll back,
                    // forget what was read, and run the command again against
                    // the projection as the winner left it.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    unitOfWork.DiscardChanges();

                    if (attempt >= MaxAttempts)
                    {
                        return Result<TResult>.Failure(InventoryErrors.BalanceContention);
                    }
                }
            }

            // Stagger the retry so writers that collided do not collide again.
            await Task.Delay(TimeSpan.FromMilliseconds(attempt * Random.Shared.Next(5, 20)), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Persists the draws the ledger refused for lack of stock once the unit of work
/// has finished, so the record survives the rollback of the command that caused
/// it.
/// </summary>
/// <remarks>
/// Registered just outside <see cref="UnitOfWorkBehaviour{TCommand, TResult}"/>.
/// A message dispatched inside an existing transaction (a synchronization batch)
/// leaves its attempts for the outermost message to write. Failing to write them
/// is logged and never changes the outcome the caller sees: the command already
/// succeeded or failed on its own terms.
/// </remarks>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="attempts">The request's refused draws.</param>
/// <param name="unitOfWork">The unit of work.</param>
/// <param name="logger">Logger.</param>
public sealed class NegativeStockAttemptBehaviour<TMessage, TResult>(
    INegativeStockAttemptRecorder attempts,
    IUnitOfWork unitOfWork,
    ILogger<NegativeStockAttemptBehaviour<TMessage, TResult>> logger)
    : IPipelineBehaviour<TMessage, TResult>
{
    /// <inheritdoc />
    public async Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (unitOfWork.HasActiveTransaction)
        {
            return await next().ConfigureAwait(false);
        }

        Result<TResult> result = await next().ConfigureAwait(false);

        if (attempts.HasPending)
        {
            try
            {
                await attempts.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BehaviourLog.NegativeStockAttemptsNotRecorded(logger, ex, typeof(TMessage).Name);
            }
        }

        return result;
    }
}
