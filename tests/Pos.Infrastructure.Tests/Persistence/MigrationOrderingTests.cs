using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Persistence;

/// <summary>
/// Migrations must apply in the same order on every machine.
/// </summary>
/// <remarks>
/// Entity Framework orders migrations by comparing their identifiers as
/// strings. Two hand-written migrations once had fifteen-digit timestamps
/// (<c>202609120344091_…</c>): with culture-aware comparison (a Windows host)
/// the underscore sorted first and they ran after the migration that creates the
/// audit schema; under invariant globalization (the self-contained migrations
/// bundle in its Alpine image) ordinal comparison ran them first, and a fresh
/// deployment failed with <c>schema "audit" does not exist</c>. Fourteen-digit
/// timestamps make both comparisons agree.
/// </remarks>
public sealed partial class MigrationOrderingTests
{
    [Fact]
    public void EveryMigrationId_IsAFourteenDigitTimestampAndAName()
    {
        IReadOnlyList<string> ids = Migrations();

        ids.Should().NotBeEmpty();
        ids.Should().OnlyContain(id => MigrationIdPattern().IsMatch(id));
    }

    [Fact]
    public void MigrationOrder_IsTheSameUnderOrdinalAndCultureComparison()
    {
        IReadOnlyList<string> ids = Migrations();

        ids.Order(StringComparer.Ordinal).Should().Equal(ids.Order(StringComparer.InvariantCulture));
        ids.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    private static List<string> Migrations()
    {
        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;

        using PosDbContext context = new(options);
        return [.. context.Database.GetMigrations()];
    }

    [GeneratedRegex(@"^\d{14}_[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationIdPattern();
}
