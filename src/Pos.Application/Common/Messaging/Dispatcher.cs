using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;

namespace Pos.Application.Common.Messaging;

/// <summary>A use case that changes state.</summary>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public interface ICommand<TResult>;

/// <summary>A use case that reads state.</summary>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public interface IQuery<TResult>;

/// <summary>
/// A command that originates from a device and must be processed exactly once.
/// The event identifier is generated where the business event happens and never
/// regenerated, which is what makes a retry after a network timeout safe.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>Gets the globally unique identifier of the business event.</summary>
    EventId EventId { get; }
}

/// <summary>
/// A message that names the location it acts on, so authorization can check
/// location scope without every handler repeating the check.
/// </summary>
public interface ILocationScoped
{
    /// <summary>Gets the location the message acts on.</summary>
    LocationId LocationId { get; }
}

/// <summary>
/// A message that declares the permission required to execute it. Declaring it
/// on the message rather than in the handler means the authorization behaviour
/// enforces it uniformly, including for messages dispatched by background
/// workers, by the sync processor and by the offline client. It is an instance
/// property so a message can choose its permission from its own content, such
/// as an adjustment that needs approval authority only above a threshold.
/// </summary>
public interface IAuthorizedMessage
{
    /// <summary>Gets the permission code the caller must hold.</summary>
    string RequiredPermission { get; }
}

/// <summary>Handles a command.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Executes the use case.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles a query.</summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>Executes the query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// A cross-cutting step in the command pipeline. Behaviours run in registration
/// order, outermost first.
/// </summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public interface IPipelineBehaviour<in TMessage, TResult>
{
    /// <summary>Runs this step and, if appropriate, the rest of the pipeline.</summary>
    /// <param name="message">The message being handled.</param>
    /// <param name="next">The remainder of the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result<TResult>> HandleAsync(
        TMessage message,
        Func<Task<Result<TResult>>> next,
        CancellationToken cancellationToken);
}

/// <summary>Routes messages to their handler through the behaviour pipeline.</summary>
public interface IDispatcher
{
    /// <summary>Dispatches a command.</summary>
    /// <typeparam name="TResult">The value produced on success.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    /// <summary>Dispatches a query.</summary>
    /// <typeparam name="TResult">The value produced on success.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result<TResult>> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default dispatcher. Resolves the handler for the concrete message type
/// and wraps it in the registered behaviours.
/// </summary>
/// <param name="services">The scoped service provider.</param>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    /// <inheritdoc />
    public Task<Result<TResult>> SendAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Type executorType = typeof(CommandExecutor<,>).MakeGenericType(command.GetType(), typeof(TResult));
        IMessageExecutor<TResult> executor = CreateExecutor<TResult>(executorType);

        return executor.ExecuteAsync(command, services, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        Type executorType = typeof(QueryExecutor<,>).MakeGenericType(query.GetType(), typeof(TResult));
        IMessageExecutor<TResult> executor = CreateExecutor<TResult>(executorType);

        return executor.ExecuteAsync(query, services, cancellationToken);
    }

    private static IMessageExecutor<TResult> CreateExecutor<TResult>(Type executorType)
        => (IMessageExecutor<TResult>)(Activator.CreateInstance(executorType)
            ?? throw new InvalidOperationException($"Could not create dispatcher executor {executorType}."));

    private interface IMessageExecutor<TResult>
    {
        Task<Result<TResult>> ExecuteAsync(
            object message,
            IServiceProvider services,
            CancellationToken cancellationToken);
    }

    private sealed class CommandExecutor<TCommand, TResult> : IMessageExecutor<TResult>
        where TCommand : ICommand<TResult>
    {
        public Task<Result<TResult>> ExecuteAsync(
            object message,
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            TCommand command = (TCommand)message;

            ICommandHandler<TCommand, TResult>? handler =
                services.GetService<ICommandHandler<TCommand, TResult>>();

            if (handler is null)
            {
                // A command with no registered handler in this container is not a
                // defect: it is how the offline client refuses use cases that must
                // not run without the server. Fail closed, explicitly.
                return Task.FromResult(Result<TResult>.Failure(new Error(
                    "application.handler_unavailable",
                    FormattableString.Invariant(
                        $"{typeof(TCommand).Name} is not available in this environment."),
                    ErrorType.Unavailable)));
            }

            Func<Task<Result<TResult>>> pipeline = () => handler.HandleAsync(command, cancellationToken);

            IPipelineBehaviour<TCommand, TResult>[] behaviours =
                [.. services.GetServices<IPipelineBehaviour<TCommand, TResult>>()];

            for (int i = behaviours.Length - 1; i >= 0; i--)
            {
                IPipelineBehaviour<TCommand, TResult> behaviour = behaviours[i];
                Func<Task<Result<TResult>>> next = pipeline;
                pipeline = () => behaviour.HandleAsync(command, next, cancellationToken);
            }

            return pipeline();
        }
    }

    private sealed class QueryExecutor<TQuery, TResult> : IMessageExecutor<TResult>
        where TQuery : IQuery<TResult>
    {
        public Task<Result<TResult>> ExecuteAsync(
            object message,
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            TQuery query = (TQuery)message;

            IQueryHandler<TQuery, TResult>? handler = services.GetService<IQueryHandler<TQuery, TResult>>();

            if (handler is null)
            {
                return Task.FromResult(Result<TResult>.Failure(new Error(
                    "application.handler_unavailable",
                    FormattableString.Invariant(
                        $"{typeof(TQuery).Name} is not available in this environment."),
                    ErrorType.Unavailable)));
            }

            Func<Task<Result<TResult>>> pipeline = () => handler.HandleAsync(query, cancellationToken);

            IPipelineBehaviour<TQuery, TResult>[] behaviours =
                [.. services.GetServices<IPipelineBehaviour<TQuery, TResult>>()];

            for (int i = behaviours.Length - 1; i >= 0; i--)
            {
                IPipelineBehaviour<TQuery, TResult> behaviour = behaviours[i];
                Func<Task<Result<TResult>>> next = pipeline;
                pipeline = () => behaviour.HandleAsync(query, next, cancellationToken);
            }

            return pipeline();
        }
    }
}
