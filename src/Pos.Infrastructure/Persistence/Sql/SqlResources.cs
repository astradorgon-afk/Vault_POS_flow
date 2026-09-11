using System.Reflection;

namespace Pos.Infrastructure.Persistence.Sql;

/// <summary>
/// Reads the hand-written SQL that migrations execute.
/// </summary>
/// <remarks>
/// Triggers, partition scaffolding and exclusion constraints cannot be expressed
/// in the EF model builder, so they live as .sql files embedded in this
/// assembly. Keeping them as files rather than inline strings means they can be
/// reviewed, diffed and linted like the rest of the schema.
/// </remarks>
public static class SqlResources
{
    private const string Prefix = "Pos.Infrastructure.Persistence.Sql.";

    /// <summary>Reads an embedded SQL script.</summary>
    /// <param name="relativePath">
    /// Path under <c>Persistence/Sql</c>, for example <c>Postgres/01_ledger_immutability.sql</c>.
    /// </param>
    /// <returns>The script text.</returns>
    /// <exception cref="InvalidOperationException">The resource is not embedded.</exception>
    public static string Read(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string resourceName = Prefix + relativePath.Replace('/', '.').Replace('\\', '.');
        Assembly assembly = typeof(SqlResources).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            string available = string.Join(
                ", ",
                assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)));

            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Embedded SQL resource {resourceName} was not found. Available: {available}"));
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
