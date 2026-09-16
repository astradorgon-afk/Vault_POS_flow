using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Reads and writes customer records.
/// </summary>
public interface ICustomerRepository
{
    /// <summary>
    /// Persists a new customer.
    /// </summary>
    /// <param name="customer">The customer to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customer identifier.</returns>
    Task<Result<CustomerId>> AddAsync(Customer customer, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a customer by its identifier.
    /// </summary>
    /// <param name="id">The customer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customer, or <see langword="null"/> when it does not exist.</returns>
    Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an existing customer.
    /// </summary>
    /// <param name="customer">The customer to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customer identifier.</returns>
    Task<Result<CustomerId>> UpdateAsync(Customer customer, CancellationToken cancellationToken);

    /// <summary>
    /// Searches customers by name, phone, or email with pagination.
    /// </summary>
    /// <param name="search">Optional search term (matches display name, phone, or email).</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching customers and the total count.</returns>
    Task<CustomerSearchResult> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

/// <summary>The result of a customer search.</summary>
/// <param name="Customers">The customers on the requested page.</param>
/// <param name="Total">The total number of matching customers.</param>
public sealed record CustomerSearchResult(
    IReadOnlyList<Customer> Customers,
    int Total);