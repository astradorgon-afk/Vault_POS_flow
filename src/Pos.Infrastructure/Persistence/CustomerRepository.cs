using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class CustomerRepository(PosDbContext context) : ICustomerRepository
{
    /// <inheritdoc />
    public async Task<Result<CustomerId>> AddAsync(
        Customer customer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);

        context.Customers.Add(customer);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Result<CustomerId>.Failure(CustomerErrors.Unknown(customer.Id));
        }

        return Result<CustomerId>.Success(customer.Id);
    }

    /// <inheritdoc />
    public async Task<Customer?> GetByIdAsync(
        CustomerId id,
        CancellationToken cancellationToken)
    {
        return await context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<CustomerId>> UpdateAsync(
        Customer customer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);

        context.Customers.Update(customer);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<CustomerId>.Failure(CustomerErrors.Unknown(customer.Id));
        }

        return Result<CustomerId>.Success(customer.Id);
    }

    /// <inheritdoc />
    public async Task<CustomerSearchResult> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IQueryable<Customer> query = context.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{EscapeLikePattern(search.Trim())}%";

            query = context.IsSqlite
                ? query.Where(c =>
                    EF.Functions.Like(c.DisplayName, pattern, "\\") ||
                    (c.Phone != null && EF.Functions.Like(c.Phone, pattern, "\\")) ||
                    (c.Email != null && EF.Functions.Like(c.Email, pattern, "\\")))
                : query.Where(c =>
                    EF.Functions.ILike(c.DisplayName, pattern, "\\") ||
                    (c.Phone != null && EF.Functions.ILike(c.Phone, pattern, "\\")) ||
                    (c.Email != null && EF.Functions.ILike(c.Email, pattern, "\\")));
        }

        int total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Customer> customers = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CustomerSearchResult(customers, total);
    }

    private static string EscapeLikePattern(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
