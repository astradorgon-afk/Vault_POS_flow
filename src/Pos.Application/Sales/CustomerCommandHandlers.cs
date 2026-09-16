using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Handles <see cref="CreateCustomerCommand"/>.</summary>
public sealed class CreateCustomerCommandHandler(
    ICustomerRepository repository,
    ICurrentUser currentUser,
    ISystemClock clock,
    IAuditWriter audit) : ICommandHandler<CreateCustomerCommand, CustomerId>
{
    /// <inheritdoc />
    public async Task<Result<CustomerId>> HandleAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        CustomerId id = CustomerId.New();
        DateTimeOffset now = clock.UtcNow;
        UserId createdBy = currentUser.UserId ?? UserId.Empty;

        Result<Customer> created = Customer.Create(
            id,
            command.DisplayName,
            command.Phone,
            command.Email,
            command.Tin,
            command.Note,
            createdBy,
            now);

        if (created.IsFailure)
        {
            return Result<CustomerId>.Failure(created.Errors);
        }

        Result<CustomerId> added = await repository.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        if (added.IsFailure)
        {
            return added;
        }

        await CustomerAuditing.WriteAsync(
            audit, AuditActions.Sales.CustomerCreated, created.Value.Id, reason: null, cancellationToken)
            .ConfigureAwait(false);
        return added;
    }
}

/// <summary>Handles <see cref="UpdateCustomerCommand"/>.</summary>
public sealed class UpdateCustomerCommandHandler(
    ICustomerRepository repository,
    ICurrentUser currentUser,
    ISystemClock clock,
    IAuditWriter audit) : ICommandHandler<UpdateCustomerCommand, CustomerId>
{
    /// <inheritdoc />
    public async Task<Result<CustomerId>> HandleAsync(
        UpdateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Customer? customer = await repository.GetByIdAsync(command.CustomerId, cancellationToken).ConfigureAwait(false);

        if (customer is null)
        {
            return Result<CustomerId>.Failure(CustomerErrors.Unknown(command.CustomerId));
        }

        DateTimeOffset now = clock.UtcNow;
        UserId updatedBy = currentUser.UserId ?? UserId.Empty;

        Result updated = customer.Update(
            command.DisplayName,
            command.Phone,
            command.Email,
            command.Tin,
            command.Note,
            updatedBy,
            now);

        if (updated.IsFailure)
        {
            return Result<CustomerId>.Failure(updated.Errors);
        }

        Result<CustomerId> saved = await repository.UpdateAsync(customer, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await CustomerAuditing.WriteAsync(
            audit, AuditActions.Sales.CustomerUpdated, customer.Id, reason: null, cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }
}

/// <summary>Handles <see cref="DeactivateCustomerCommand"/>.</summary>
public sealed class DeactivateCustomerCommandHandler(
    ICustomerRepository repository,
    ICurrentUser currentUser,
    ISystemClock clock,
    IAuditWriter audit) : ICommandHandler<DeactivateCustomerCommand, CustomerId>
{
    /// <inheritdoc />
    public async Task<Result<CustomerId>> HandleAsync(
        DeactivateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Customer? customer = await repository.GetByIdAsync(command.CustomerId, cancellationToken).ConfigureAwait(false);

        if (customer is null)
        {
            return Result<CustomerId>.Failure(CustomerErrors.Unknown(command.CustomerId));
        }

        DateTimeOffset now = clock.UtcNow;
        UserId deactivatedBy = currentUser.UserId ?? UserId.Empty;

        Result deactivated = customer.Deactivate(command.Reason, deactivatedBy, now);

        if (deactivated.IsFailure)
        {
            return Result<CustomerId>.Failure(deactivated.Errors);
        }

        Result<CustomerId> saved = await repository.UpdateAsync(customer, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await CustomerAuditing.WriteAsync(
            audit, AuditActions.Sales.CustomerDeactivated, customer.Id, command.Reason, cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }
}

/// <summary>Handles <see cref="ReactivateCustomerCommand"/>.</summary>
public sealed class ReactivateCustomerCommandHandler(
    ICustomerRepository repository,
    ICurrentUser currentUser,
    ISystemClock clock,
    IAuditWriter audit) : ICommandHandler<ReactivateCustomerCommand, CustomerId>
{
    /// <inheritdoc />
    public async Task<Result<CustomerId>> HandleAsync(
        ReactivateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Customer? customer = await repository.GetByIdAsync(command.CustomerId, cancellationToken).ConfigureAwait(false);

        if (customer is null)
        {
            return Result<CustomerId>.Failure(CustomerErrors.Unknown(command.CustomerId));
        }

        DateTimeOffset now = clock.UtcNow;
        UserId reactivatedBy = currentUser.UserId ?? UserId.Empty;

        Result reactivated = customer.Reactivate(reactivatedBy, now);

        if (reactivated.IsFailure)
        {
            return Result<CustomerId>.Failure(reactivated.Errors);
        }

        Result<CustomerId> saved = await repository.UpdateAsync(customer, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await CustomerAuditing.WriteAsync(
            audit, AuditActions.Sales.CustomerReactivated, customer.Id, reason: null, cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }
}

internal static class CustomerAuditing
{
    internal static Task WriteAsync(
        IAuditWriter audit,
        string action,
        CustomerId customerId,
        string? reason,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                nameof(Customer),
                customerId.Value,
                Reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()),
            cancellationToken);
}
