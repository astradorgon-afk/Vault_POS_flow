using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Concurrency;

/// <summary>
/// A throwaway SQLite file that several writers can genuinely contend on.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory SQLite used by most suites here supports a single connection,
/// so every "concurrent" writer is really the same one and a lost update can
/// never appear. A file in WAL mode with a generous busy timeout gives each
/// writer its own connection and its own transaction, which is what these
/// suites need: the question they ask is what happens when two operators, or
/// two nodes of the sync processor, act on the same row at the same moment.
/// </para>
/// <para>
/// PostgreSQL is the production engine and its own guarantees are proven by the
/// Testcontainers suites. What is proven here is the behaviour of the code
/// above the engine, which must hold on any relational provider.
/// </para>
/// </remarks>
internal sealed class ConcurrentDatabase : IAsyncDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        FormattableString.Invariant($"vaultflow-race-{Guid.NewGuid():N}.db"));

    // Cache=Shared is deliberately omitted: with a file-based database the
    // shared-cache mode imposes table-level locking that produces spurious
    // SQLITE_LOCKED errors across connections. WAL without shared cache gives
    // the correct per-writer semantics.
    private string ConnectionString => FormattableString.Invariant($"Data Source={this.databasePath}");

    /// <summary>Creates the schema. Call once, before any writer starts.</summary>
    /// <returns>A task that completes when the schema exists.</returns>
    public async Task CreateSchemaAsync()
    {
        await using Session session = await this.OpenAsync();
        await session.Context.Database.EnsureCreatedAsync();
    }

    /// <summary>Opens one writer's own connection and context.</summary>
    /// <returns>The session; disposing it closes both.</returns>
    public async Task<Session> OpenAsync()
    {
        SqliteConnection connection = new(this.ConnectionString);
        await connection.OpenAsync();

        await using (SqliteCommand pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 30000;";
            await pragmas.ExecuteNonQueryAsync();
        }

        PosDbContext context = new(new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options);

        return new Session(connection, context);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // WAL spills into sidecar files; remove all three, best effort.
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = this.databasePath + suffix;

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Still held open by a failed test; leave it to the OS.
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>One writer's connection and the context bound to it.</summary>
    /// <param name="Connection">The open connection.</param>
    /// <param name="Context">The context bound to that connection.</param>
    internal sealed record Session(SqliteConnection Connection, PosDbContext Context) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await this.Context.DisposeAsync();
            await this.Connection.DisposeAsync();
        }
    }
}
