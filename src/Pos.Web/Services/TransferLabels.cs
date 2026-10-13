namespace Pos.Web.Services;

/// <summary>Labels for transfers that do not carry a document number yet.</summary>
public static class TransferLabels
{
    /// <summary>A readable short id shown while a transfer has no number.
    /// Twelve hex characters cover the 48-bit timestamp of a time-ordered
    /// GUID, so requests created seconds apart never share a label.</summary>
    public static string ShortId(Guid id)
    {
        string value = id.ToString("N").ToUpperInvariant();
        return $"{value[..8]}-{value[8..12]}";
    }
}
