using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;
using Testcontainers.PostgreSql;

namespace Pos.Infrastructure.Tests.Persistence;

/// <summary>
/// The deployment grants script (<c>build/docker/initdb/02-grants.sql</c>)
/// applied to the fully migrated schema, exactly as the compose stack runs it.
/// </summary>
/// <remarks>
/// The script once granted only four schemas, one of which did not exist, so
/// the application role would have been refused every catalogue, purchasing,
/// transfer and quarantine query. These tests read privileges from the catalog
/// for every table, so a table added by a future migration is covered without
/// being named here. They skip without a Docker daemon; CI always has one.
/// </remarks>
[Collection("postgres")]
public sealed class PostgresRoleGrantsTests : IAsyncLifetime
{
    private const string BusinessSchemas =
        "'core', 'catalog', 'inventory', 'purchasing', 'transfers', 'quarantine', 'audit', 'sales'";

    private static readonly string[] AppendOnlyTables =
    [
        "inventory.inventory_movement",
        "audit.audit_log",
        "core.login_attempt",
        "core.receipt",
        "inventory.negative_stock_attempt",
    ];

    private PostgreSqlContainer? _container;

    private bool DockerAvailable => _container is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("vaultflow_grants")
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

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__migrations_history", PosDbContext.CoreSchema))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        await using (PosDbContext context = new(options))
        {
            await context.Database.MigrateAsync();
        }

        await ExecuteAsync("CREATE ROLE pos_app LOGIN PASSWORD 'app-test-only'; CREATE ROLE pos_readonly LOGIN PASSWORD 'ro-test-only';");

        // Twice: the deployment step re-runs on every release and must be idempotent.
        string grants = await File.ReadAllTextAsync(GrantsScriptPath());
        await ExecuteAsync(grants);
        await ExecuteAsync(grants);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task AppRole_ReadsAndWritesEveryBusinessTable()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        List<string> refused = await TablesWhereAsync(
            "NOT has_table_privilege('pos_app', format('%I.%I', table_schema, table_name), 'SELECT')");
        refused.Should().BeEmpty("the application must be able to read every table it maps");

        List<string> unwritable = await TablesWhereAsync(
            "NOT has_table_privilege('pos_app', format('%I.%I', table_schema, table_name), 'INSERT')");
        unwritable.Should().BeEmpty("the application must be able to insert into every table it maps");
    }

    [SkippableFact]
    public async Task AppRole_CannotRewriteAppendOnlyRecords()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        foreach (string table in AppendOnlyTables)
        {
            foreach (string privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            {
                (await HasPrivilegeAsync("pos_app", table, privilege))
                    .Should().BeFalse($"{table} is append-only, so pos_app must not hold {privilege}");
            }

            (await HasPrivilegeAsync("pos_app", table, "INSERT")).Should().BeTrue($"{table} still accepts new rows");
        }

        // Ordinary tables stay fully writable: the balance projection is updated on every posting.
        (await HasPrivilegeAsync("pos_app", "inventory.inventory_balance", "UPDATE")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task ReadonlyRole_ReadsEverything_AndWritesNothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        (await TablesWhereAsync(
                "NOT has_table_privilege('pos_readonly', format('%I.%I', table_schema, table_name), 'SELECT')"))
            .Should().BeEmpty();

        (await TablesWhereAsync(
                "has_table_privilege('pos_readonly', format('%I.%I', table_schema, table_name), 'INSERT,UPDATE,DELETE')"))
            .Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Receipt_CannotBeEditedOrDeleted_EvenByTheOwner()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        await ExecuteAsync(
            """
            INSERT INTO core.receipt (id, number, kind, location_id, amount, issued_by_user_id, issued_at_utc)
            VALUES ('01a09c10-0000-7000-8000-000000000001', 'RCT-2026-000001', 1,
                    '01a09c10-0000-7000-8000-0000000000aa', 150.25,
                    '01a09c10-0000-7000-8000-0000000000bb', now());
            """);

        Func<Task> edit = () => ExecuteAsync("UPDATE core.receipt SET amount = 1 WHERE number = 'RCT-2026-000001';");
        Func<Task> delete = () => ExecuteAsync("DELETE FROM core.receipt WHERE number = 'RCT-2026-000001';");
        Func<Task> truncate = () => ExecuteAsync("TRUNCATE core.receipt;");

        await edit.Should().ThrowAsync<PostgresException>();
        await delete.Should().ThrowAsync<PostgresException>();
        await truncate.Should().ThrowAsync<PostgresException>();
    }

    [SkippableFact]
    public async Task NegativeStockAttempt_CannotBeEditedOrDeleted_EvenByTheOwner()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available on this machine.");

        await ExecuteAsync(
            """
            INSERT INTO inventory.negative_stock_attempt
                (id, event_id, movement_type, location_id, product_id, batch_key, state,
                 requested_quantity, available_quantity, policy, reference_document_type,
                 reference_number, user_id, correlation_id, attempted_at_utc)
            VALUES ('01a09c10-0000-7000-8000-000000000101', '01a09c10-0000-7000-8000-000000000102', 1,
                    '01a09c10-0000-7000-8000-0000000000aa', '01a09c10-0000-7000-8000-0000000000cc',
                    '00000000-0000-0000-0000-000000000000', 0, 6, 1, 0, 1, 'SHP-2026-000001',
                    '01a09c10-0000-7000-8000-0000000000bb', '01a09c10-0000-7000-8000-0000000000dd', now());
            """);

        Func<Task> edit = () => ExecuteAsync("UPDATE inventory.negative_stock_attempt SET available_quantity = 6;");
        Func<Task> delete = () => ExecuteAsync("DELETE FROM inventory.negative_stock_attempt;");
        Func<Task> truncate = () => ExecuteAsync("TRUNCATE inventory.negative_stock_attempt;");

        await edit.Should().ThrowAsync<PostgresException>();
        await delete.Should().ThrowAsync<PostgresException>();
        await truncate.Should().ThrowAsync<PostgresException>();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a literal privilege predicate; no user input reaches this helper.")]
    private async Task<List<string>> TablesWhereAsync(string condition)
    {
        await using NpgsqlConnection connection = new(_container!.GetConnectionString());
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT table_schema || '.' || table_name FROM information_schema.tables " +
            $"WHERE table_type = 'BASE TABLE' AND table_schema IN ({BusinessSchemas}) AND {condition} ORDER BY 1";

        List<string> tables = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private async Task<bool> HasPrivilegeAsync(string role, string table, string privilege)
    {
        await using NpgsqlConnection connection = new(_container!.GetConnectionString());
        await connection.OpenAsync();

        await using NpgsqlCommand command = new("SELECT has_table_privilege(@role, @table, @privilege)", connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Runs the repository's grants script and literal test statements only.")]
    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(_container!.GetConnectionString());
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string GrantsScriptPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "build", "docker", "initdb", "02-grants.sql");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate build/docker/initdb/02-grants.sql above the test output.");
    }
}
