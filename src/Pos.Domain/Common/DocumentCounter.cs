namespace Pos.Domain.Common;

/// <summary>
/// One row of the central document counter. The row is created and advanced by
/// raw upsert SQL outside the change tracker, which is also why the entity
/// carries no behaviour of its own. It exists in the model so that the table is
/// created by <c>EnsureCreated</c> and represented in migrations.
/// </summary>
/// <remarks>
/// <para>
/// The counters are allocated inside the caller's transaction: a rolled-back
/// document number is a gap in the sequence but never a double-allocation. That
/// is an accepted consequence of allocation at commit time; sequences do not
/// need to be gap-free, they need to be never-repeated.
/// </para>
/// </remarks>
public sealed class DocumentCounter
{
    /// <summary>Required by the persistence provider for materialization.</summary>
    private DocumentCounter()
    {
    }

    /// <summary>The document type being counted.</summary>
    public DocumentType DocumentType { get; private set; }

    /// <summary>The period, for example <c>2026</c> for the calendar year.</summary>
    public string PeriodKey { get; private set; } = string.Empty;

    /// <summary>The scope, empty for central counters; a device short code for device-scoped ones.</summary>
    public string ScopeKey { get; private set; } = string.Empty;

    /// <summary>The next value that will be allocated.</summary>
    public long NextValue { get; private set; }
}