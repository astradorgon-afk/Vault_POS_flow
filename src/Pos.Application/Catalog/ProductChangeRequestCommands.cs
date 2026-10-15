using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>Reads and writes held catalogue changes.</summary>
public interface IProductChangeRequestRepository
{
    /// <summary>Stores a newly raised request, committing it immediately.</summary>
    /// <remarks>
    /// The gate raises a request and then reports a refusal, which rolls the
    /// command's transaction back. The request has to outlive that rollback, so
    /// it is saved in its own right rather than left to the unit of work.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddAsync(ProductChangeRequest request, CancellationToken cancellationToken);

    /// <summary>Loads a request for a decision.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request, or <see langword="null"/>.</returns>
    Task<ProductChangeRequest?> GetForUpdateAsync(
        ProductChangeRequestId requestId,
        CancellationToken cancellationToken);

    /// <summary>Lists requests, newest first.</summary>
    /// <param name="pendingOnly">True to list only those awaiting a decision.</param>
    /// <param name="productId">A product to filter by, or null for all.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The requests.</returns>
    Task<IReadOnlyList<ProductChangeRequestView>> ListAsync(
        bool pendingOnly,
        ProductId? productId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>A held catalogue change as the approval queue shows it.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="ProductId">The product being changed.</param>
/// <param name="ProductName">The product's name.</param>
/// <param name="ProductSku">The product's SKU.</param>
/// <param name="ChangeType">The registry key of the held command.</param>
/// <param name="Summary">A one-line description of the change.</param>
/// <param name="Reason">Why the requester says it is needed.</param>
/// <param name="Status">Where the request has got to.</param>
/// <param name="RequestedBy">Who raised it.</param>
/// <param name="RequestedByName">Their display name.</param>
/// <param name="RequestedAtUtc">When it was raised.</param>
/// <param name="DecidedByName">Who decided, when decided.</param>
/// <param name="DecidedAtUtc">When it was decided.</param>
/// <param name="DecisionNote">The approver's note.</param>
/// <param name="FailureReason">Why applying it failed, when it did.</param>
public sealed record ProductChangeRequestView(
    Guid RequestId,
    Guid ProductId,
    string ProductName,
    string ProductSku,
    string ChangeType,
    string Summary,
    string Reason,
    string Status,
    Guid RequestedBy,
    string? RequestedByName,
    DateTimeOffset RequestedAtUtc,
    string? DecidedByName,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    string? FailureReason);

/// <summary>Approves a held change and applies it.</summary>
/// <param name="RequestId">The request to approve.</param>
/// <param name="Note">An optional note.</param>
public sealed record ApproveProductChangeCommand(ProductChangeRequestId RequestId, string? Note)
    : ICommand<ProductChangeRequestId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ApproveChanges;
}

/// <summary>Refuses a held change.</summary>
/// <param name="RequestId">The request to refuse.</param>
/// <param name="Note">Why; required.</param>
public sealed record RejectProductChangeCommand(ProductChangeRequestId RequestId, string? Note)
    : ICommand<ProductChangeRequestId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ApproveChanges;
}

/// <summary>Takes back a change the caller raised.</summary>
/// <param name="RequestId">The request to withdraw.</param>
public sealed record WithdrawProductChangeCommand(ProductChangeRequestId RequestId)
    : ICommand<ProductChangeRequestId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Lists held catalogue changes.</summary>
/// <param name="PendingOnly">True for only those awaiting a decision.</param>
/// <param name="ProductId">A product to filter by, or null for all.</param>
/// <param name="Limit">The most rows to return.</param>
public sealed record ListProductChangeRequestsQuery(bool PendingOnly, ProductId? ProductId, int Limit)
    : IQuery<IReadOnlyList<ProductChangeRequestView>>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.View;
}

/// <summary>Handles <see cref="ApproveProductChangeCommand"/>.</summary>
/// <remarks>
/// Approval and the change it releases commit together: the replay runs inside
/// the same unit of work, so a request can never read as approved while the
/// product it was meant to change is untouched.
/// </remarks>
public sealed class ApproveProductChangeCommandHandler(
    IProductChangeRequestRepository requests,
    IDispatcher dispatcher,
    ProductChangeReplayScope replay,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApproveProductChangeCommand, ProductChangeRequestId>
{
    /// <inheritdoc />
    public async Task<Result<ProductChangeRequestId>> HandleAsync(
        ApproveProductChangeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ProductChangeRequest? request = await requests
            .GetForUpdateAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<ProductChangeRequestId>.Failure(CatalogErrors.ChangeRequestUnknown(command.RequestId));
        }

        Result<object> restored = ProductChangeRegistry.Deserialize(request.ChangeType, request.Payload);
        if (restored.IsFailure)
        {
            return Result<ProductChangeRequestId>.Failure(restored.Errors);
        }

        Result approved = request.Approve(currentUser.UserId ?? UserId.Empty, command.Note, clock.UtcNow);
        if (approved.IsFailure)
        {
            return Result<ProductChangeRequestId>.Failure(approved.Errors);
        }

        Result applied = await replay
            .RunAsync(() => SendAsync(restored.Value, cancellationToken))
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            // The held command no longer fits the product — the barcode was
            // taken in the meantime, the price window closed. Say so against
            // the request rather than reporting an approval that did nothing.
            request.MarkFailed(applied.Errors.Count > 0 ? applied.Errors[0].Message : null);
            return Result<ProductChangeRequestId>.Failure(applied.Errors);
        }

        return Result<ProductChangeRequestId>.Success(request.Id);
    }

    /// <summary>
    /// Dispatches a command restored as <see cref="object"/>, whose result type
    /// is only known at run time.
    /// </summary>
    private async Task<Result> SendAsync(object command, CancellationToken cancellationToken)
    {
        Type? contract = Array.Find(
            command.GetType().GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));

        if (contract is null)
        {
            return Result.Failure(CatalogErrors.ChangeRequestNotReplayable(command.GetType().Name));
        }

        object? pending = typeof(IDispatcher)
            .GetMethod(nameof(IDispatcher.SendAsync))!
            .MakeGenericMethod(contract.GetGenericArguments()[0])
            .Invoke(dispatcher, [command, cancellationToken]);

        await ((Task)pending!).ConfigureAwait(false);

        object outcome = pending.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(pending)!;
        bool success = (bool)outcome.GetType().GetProperty(nameof(Result.IsSuccess))!.GetValue(outcome)!;
        if (success)
        {
            return Result.Success();
        }

        IReadOnlyList<Error> errors =
            (IReadOnlyList<Error>)outcome.GetType().GetProperty(nameof(Result.Errors))!.GetValue(outcome)!;
        return Result.Failure(errors);
    }
}

/// <summary>Handles <see cref="RejectProductChangeCommand"/>.</summary>
public sealed class RejectProductChangeCommandHandler(
    IProductChangeRequestRepository requests,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectProductChangeCommand, ProductChangeRequestId>
{
    /// <inheritdoc />
    public async Task<Result<ProductChangeRequestId>> HandleAsync(
        RejectProductChangeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ProductChangeRequest? request = await requests
            .GetForUpdateAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<ProductChangeRequestId>.Failure(CatalogErrors.ChangeRequestUnknown(command.RequestId));
        }

        Result rejected = request.Reject(currentUser.UserId ?? UserId.Empty, command.Note, clock.UtcNow);
        return rejected.IsFailure
            ? Result<ProductChangeRequestId>.Failure(rejected.Errors)
            : Result<ProductChangeRequestId>.Success(request.Id);
    }
}

/// <summary>Handles <see cref="WithdrawProductChangeCommand"/>.</summary>
public sealed class WithdrawProductChangeCommandHandler(
    IProductChangeRequestRepository requests,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<WithdrawProductChangeCommand, ProductChangeRequestId>
{
    /// <inheritdoc />
    public async Task<Result<ProductChangeRequestId>> HandleAsync(
        WithdrawProductChangeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ProductChangeRequest? request = await requests
            .GetForUpdateAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<ProductChangeRequestId>.Failure(CatalogErrors.ChangeRequestUnknown(command.RequestId));
        }

        Result withdrawn = request.Withdraw(currentUser.UserId ?? UserId.Empty, clock.UtcNow);
        return withdrawn.IsFailure
            ? Result<ProductChangeRequestId>.Failure(withdrawn.Errors)
            : Result<ProductChangeRequestId>.Success(request.Id);
    }
}

/// <summary>Handles <see cref="ListProductChangeRequestsQuery"/>.</summary>
public sealed class ListProductChangeRequestsQueryHandler(IProductChangeRequestRepository requests)
    : IQueryHandler<ListProductChangeRequestsQuery, IReadOnlyList<ProductChangeRequestView>>
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ProductChangeRequestView>>> HandleAsync(
        ListProductChangeRequestsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<ProductChangeRequestView> rows = await requests
            .ListAsync(query.PendingOnly, query.ProductId, Math.Clamp(query.Limit, 1, 200), cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<ProductChangeRequestView>>.Success(rows);
    }
}
