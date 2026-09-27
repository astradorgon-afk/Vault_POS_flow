namespace Pos.Shared.Scanning;

/// <summary>Where a scan came from.</summary>
public enum ScanSource
{
    /// <summary>A USB or Bluetooth scanner that types like a keyboard ("keyboard wedge").</summary>
    Keyboard = 0,

    /// <summary>An Android handheld's built-in scanner, delivered as a broadcast intent.</summary>
    AndroidIntent = 1,

    /// <summary>A test scan from the scanner test page or a developer tool.</summary>
    Simulated = 2,
}

/// <summary>One barcode read, from any scanner.</summary>
/// <param name="Code">The cleaned barcode text.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="ReceivedAtUtc">When the app received it.</param>
/// <param name="Symbology">The barcode type when the scanner reports one (e.g. EAN-13).</param>
/// <param name="Vendor">The handheld maker whose broadcast carried it, for Android scans.</param>
public sealed record BarcodeScan(
    string Code,
    ScanSource Source,
    DateTimeOffset ReceivedAtUtc,
    string? Symbology = null,
    string? Vendor = null);
