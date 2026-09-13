using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Persistence;

/// <summary>
/// Guards the PostgreSQL context registration the API host actually uses.
/// </summary>
/// <remarks>
/// The unit-of-work behaviour, the ledger, the reconciler and the development
/// seeder all open their own transactions. A retrying execution strategy refuses
/// that outright, which crashed the API at start-up on PostgreSQL while every
/// SQLite-hosted test stayed green. No database connection is needed to prove it.
/// </remarks>
public sealed class PersistenceRegistrationTests
{
    [Fact]
    public void Postgres_ContextAllowsUserInitiatedTransactions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            })
            .Build();

        ServiceCollection services = new();
        services.AddPersistence(configuration, PersistenceProvider.Postgres);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse();
    }
}
