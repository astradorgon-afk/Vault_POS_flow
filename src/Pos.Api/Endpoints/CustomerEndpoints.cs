using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Api.Endpoints;

/// <summary>The body of a create-customer request.</summary>
public sealed record CreateCustomerBody(
    string DisplayName,
    string? Phone,
    string? Email,
    string? Tin,
    string? Note);

/// <summary>The body of an update-customer request.</summary>
public sealed record UpdateCustomerBody(
    string DisplayName,
    string? Phone,
    string? Email,
    string? Tin,
    string? Note);

/// <summary>The body of a deactivate-customer request.</summary>
public sealed record DeactivateCustomerBody(string Reason);

/// <summary>A customer as returned by the detail and list routes.</summary>
public sealed record CustomerDetail(
    Guid Id,
    string DisplayName,
    string? Phone,
    string? Email,
    string? Tin,
    string? Note,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAtUtc,
    Guid UpdatedByUserId,
    DateTimeOffset? DeactivatedAtUtc,
    string? DeactivationReason);

/// <summary>The paged result of a customer search.</summary>
public sealed record CustomerSearchResponse(
    IReadOnlyList<CustomerDetail> Customers,
    int Total,
    int Page,
    int PageSize);

/// <summary>Customer endpoints: create, read, search, update, deactivate, reactivate.</summary>
public static class CustomerEndpoints
{
    /// <summary>Sets the maximum number of items per page.</summary>
    private const int MaxPageSize = 100;

    /// <summary>Sets the default number of items per page.</summary>
    private const int DefaultPageSize = 20;

    /// <summary>Maps the customer routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/customers").WithTags("Customers");

        group.MapPost("/", CreateCustomerAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ManageCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateCustomer")
            .WithSummary("Creates a new customer record.");

        group.MapGet("/", SearchCustomersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ViewCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("SearchCustomers")
            .WithSummary("Searches customers by name, phone, or email.");

        group.MapGet("/{id:guid}", GetCustomerAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ViewCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetCustomer")
            .WithSummary("Gets a customer by its identifier.");

        group.MapPut("/{id:guid}", UpdateCustomerAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ManageCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("UpdateCustomer")
            .WithSummary("Updates a customer's details.");

        group.MapPost("/{id:guid}/deactivate", DeactivateCustomerAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ManageCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("DeactivateCustomer")
            .WithSummary("Deactivates a customer.");

        group.MapPost("/{id:guid}/reactivate", ReactivateCustomerAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.ManageCustomers)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReactivateCustomer")
            .WithSummary("Reactivates a deactivated customer.");

        return app;
    }

    private static async Task<IResult> CreateCustomerAsync(
        [FromBody] CreateCustomerBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<CustomerId> result = await dispatcher
            .SendAsync(
                new CreateCustomerCommand(
                    body.DisplayName,
                    body.Phone,
                    body.Email,
                    body.Tin,
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/customers/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> SearchCustomersAsync(
        [FromQuery] string? search,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] ICustomerRepository repository,
        CancellationToken cancellationToken)
    {
        int p = page < 1 ? 1 : page;
        int size = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        Pos.Application.Sales.CustomerSearchResult result = await repository
            .SearchAsync(search, p, size, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CustomerDetail> details = result.Customers
            .Select(ToDetail)
            .ToList();

        return TypedResults.Ok(new CustomerSearchResponse(details, result.Total, p, size));
    }

    private static async Task<IResult> GetCustomerAsync(
        Guid id,
        [FromServices] ICustomerRepository repository,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken = default)
    {
        CustomerId customerId = new(id);

        Customer? customer = await repository
            .GetByIdAsync(customerId, cancellationToken)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<CustomerDetail>.Failure(CustomerErrors.Unknown(customerId)),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(ToDetail(customer));
    }

    private static async Task<IResult> UpdateCustomerAsync(
        Guid id,
        [FromBody] UpdateCustomerBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken = default)
    {
        Result result = await dispatcher
            .SendAsync(
                new UpdateCustomerCommand(
                    new CustomerId(id),
                    body.DisplayName,
                    body.Phone,
                    body.Email,
                    body.Tin,
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> DeactivateCustomerAsync(
        Guid id,
        [FromBody] DeactivateCustomerBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher
            .SendAsync(
                new DeactivateCustomerCommand(new CustomerId(id), body.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ReactivateCustomerAsync(
        Guid id,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher
            .SendAsync(
                new ReactivateCustomerCommand(new CustomerId(id)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static CustomerDetail ToDetail(Customer c) => new(
        c.Id.Value,
        c.DisplayName,
        c.Phone,
        c.Email,
        c.Tin,
        c.Note,
        c.IsActive,
        c.CreatedAtUtc,
        c.CreatedByUserId.Value,
        c.UpdatedAtUtc,
        c.UpdatedByUserId.Value,
        c.DeactivatedAtUtc,
        c.DeactivationReason);
}
