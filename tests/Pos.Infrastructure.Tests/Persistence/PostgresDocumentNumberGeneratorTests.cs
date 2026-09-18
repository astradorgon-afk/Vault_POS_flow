using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;
using Testcontainers.PostgreSql;

namespace Pos.Infrastructure.Tests.Persistence;

/// <summary>
/// Central document numbering on a real PostgreSQL engine.
/// </summary>
/// <remarks>
/// The counter upsert is hand-written SQL with a separate PostgreSQL statement.
/// Every API test runs on SQLite, which accepted an unqualified column that
/// PostgreSQL rejects as ambiguous (42702) — so every numbered document failed
/// on the production engine while the suite stayed green. The suite skips itself
/// when no Docker daemon is reachable; CI always has one.
/// </remarks>
[Collection("postgres")]
public sealed class PostgresDocumentNumberGeneratorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _container;
    private PosDbContext? _context;
    private DbContextOptions<PosDbContext> _options = null!;

    private bool DockerAvailable => _container is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("vaultflow_test")
                .WithUsername("vaultflow")
                .WithPassword("vaultflow-test-only")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _container = null;
            return;
        }

        _options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        _context = new PosDbContext(_options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task NextAsync_AllocatesConsecutiveNumbersPerType_InsideATransaction()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        DocumentNumberGenerator generator = new(_context!, new FixedClock(Now));

        await using IDbContextTransaction transaction = await _context!.Database.BeginTransactionAsync();

        DocumentNumber first = await generator.NextAsync(DocumentType.Receipt, CancellationToken.None);
        DocumentNumber second = await generator.NextAsync(DocumentType.Receipt, CancellationToken.None);
        DocumentNumber otherType = await generator.NextAsync(DocumentType.PurchaseOrder, CancellationToken.None);

        await transaction.CommitAsync();

        first.Value.Should().Be("RCT-2026-000001");
        second.Value.Should().Be("RCT-2026-000002");
        otherType.Value.Should().Be("PO-2026-000001");
    }

    [SkippableFact]
    public async Task ParallelAllocations_AreUniqueAndGapless_WhenContextsRace()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");
        const int allocationCount = 16;

        DocumentNumber[] numbers = await Task.WhenAll(
            Enumerable.Range(0, allocationCount)
                .Select(_ => AllocateInOwnContextAsync(DocumentType.Receipt)))
            .WaitAsync(TimeSpan.FromSeconds(120));

        numbers.Select(number => number.Value)
            .Should()
            .OnlyHaveUniqueItems()
            .And
            .BeEquivalentTo(
                Enumerable.Range(1, allocationCount)
                    .Select(sequence => $"RCT-2026-{sequence:D6}"));
    }

    private async Task<DocumentNumber> AllocateInOwnContextAsync(DocumentType type)
    {
        await using PosDbContext context = new(_options);
        DocumentNumberGenerator generator = new(context, new FixedClock(Now));
        return await generator.NextAsync(type, CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
