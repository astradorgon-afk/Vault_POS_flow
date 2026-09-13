using System.ComponentModel.DataAnnotations;

namespace Pos.Application.Transfers;

/// <summary>
/// Budget controls for emergency offline transfers. A store may originate at
/// most <see cref="MonthlyCapPerStore"/> emergencies per calendar (UTC) month;
/// the cap is a real-time guard, deliberately independent of business dates.
/// </summary>
public sealed class EmergencyTransfersOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "EmergencyTransfers";

    /// <summary>
    /// Gets or sets how many emergency transfers one store may originate per
    /// UTC calendar month. Zero disables the cap.
    /// </summary>
    [Range(0, 10_000)]
    public int MonthlyCapPerStore { get; set; } = 3;
}