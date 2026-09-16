using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Sales;

/// <summary>Customer persistence and provider-translated search behavior.</summary>
public sealed class CustomerRepositoryTests : IAsyncLifetime
{
    private static readonly UserId Actor = UserId.New();
    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private CustomerRepository _repository = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _repository = new CustomerRepository(_context);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AddUpdateAndGet_RoundTripsLifecycleFields()
    {
        Customer customer = NewCustomer("Ana Santos", "09170001111", "ana@example.test");
        (await _repository.AddAsync(customer, CancellationToken.None)).IsSuccess.Should().BeTrue();
        customer.Deactivate("Requested closure", Actor, DateTimeOffset.UtcNow.AddMinutes(1));
        (await _repository.UpdateAsync(customer, CancellationToken.None)).IsSuccess.Should().BeTrue();

        Customer stored = (await _repository.GetByIdAsync(customer.Id, CancellationToken.None))!;

        stored.DisplayName.Should().Be("Ana Santos");
        stored.IsActive.Should().BeFalse();
        stored.DeactivationReason.Should().Be("Requested closure");
    }

    [Theory]
    [InlineData("ana", 1)]
    [InlineData("EXAMPLE.TEST", 2)]
    [InlineData("09170002222", 1)]
    [InlineData("%", 0)]
    [InlineData("_", 0)]
    public async Task SearchAsync_MatchesCaseInsensitively_AndTreatsWildcardsLiterally(string search, int expected)
    {
        await _repository.AddAsync(NewCustomer("Ana Santos", "09170001111", "ana@example.test"), CancellationToken.None);
        await _repository.AddAsync(NewCustomer("Ben Cruz", "09170002222", "ben@example.test"), CancellationToken.None);

        CustomerSearchResult result = await _repository.SearchAsync(search, 1, 20, CancellationToken.None);

        result.Total.Should().Be(expected);
        result.Customers.Should().HaveCount(expected);
    }

    [Fact]
    public async Task SearchAsync_PaginatesInNewestFirstOrder()
    {
        await _repository.AddAsync(NewCustomer("Older", null, null, minutes: 0), CancellationToken.None);
        await _repository.AddAsync(NewCustomer("Newer", null, null, minutes: 1), CancellationToken.None);

        CustomerSearchResult result = await _repository.SearchAsync(null, page: 2, pageSize: 1, CancellationToken.None);

        result.Total.Should().Be(2);
        result.Customers.Should().ContainSingle().Which.DisplayName.Should().Be("Older");
    }

    private static Customer NewCustomer(
        string name,
        string? phone,
        string? email,
        int minutes = 0)
        => Customer.Create(CustomerId.New(), name, phone, email, null, null, Actor,
            new DateTimeOffset(2026, 9, 16, 1, minutes, 0, TimeSpan.Zero)).Value;
}
