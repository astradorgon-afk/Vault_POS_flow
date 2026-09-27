using System.Text;

namespace Pos.Shared.Scanning;

/// <summary>
/// How one maker's Android handheld announces a scan: the broadcast action it
/// sends when the trigger is pressed, and the extras carrying the code.
/// </summary>
/// <param name="Vendor">The maker, for diagnostics.</param>
/// <param name="Action">The broadcast action.</param>
/// <param name="TextExtras">Extras that may carry the code as text, tried in order.</param>
/// <param name="SymbologyExtra">The extra naming the barcode type, when the maker sends one.</param>
/// <param name="BytesExtra">An extra carrying the code as raw bytes, for makers that send bytes.</param>
/// <param name="LengthExtra">The extra giving how many of those bytes are the code.</param>
public sealed record ScanIntentProfile(
    string Vendor,
    string Action,
    IReadOnlyList<string> TextExtras,
    string? SymbologyExtra = null,
    string? BytesExtra = null,
    string? LengthExtra = null);

/// <summary>A broadcast the app sends to make a handheld's scanner fire, like pressing its trigger.</summary>
/// <param name="Vendor">The maker.</param>
/// <param name="Action">The broadcast action.</param>
/// <param name="Extras">The string extras to attach.</param>
public sealed record SoftTriggerIntent(string Vendor, string Action, IReadOnlyDictionary<string, string> Extras);

/// <summary>
/// The Android scanner broadcasts the app listens for. Handhelds whose output
/// is configurable (Zebra DataWedge, Honeywell, Chainway and others) are set to
/// send <see cref="VaultFlowAction"/>; the built-in defaults of common makers
/// are recognised without any setup.
/// </summary>
public static class ScanIntentProfiles
{
    /// <summary>The app's own scan action, for scanners that let you choose one.</summary>
    public const string VaultFlowAction = "com.vaultflow.pos.SCAN";

    /// <summary>Gets every profile, the app's own first.</summary>
    public static IReadOnlyList<ScanIntentProfile> All { get; } =
    [
        // Configured scanners. DataWedge's own extra names are accepted so a
        // Zebra profile only needs its intent action changed.
        new("Configured", VaultFlowAction,
            ["com.symbol.datawedge.data_string", "data", "barcode", "barcode_string", "value", "scannerdata"],
            SymbologyExtra: "com.symbol.datawedge.label_type"),
        new("Sunmi", "com.sunmi.scanner.ACTION_DATA_CODE_RECEIVED", ["data"]),
        new("Urovo", "android.intent.ACTION_DECODE_DATA", ["barcode_string"],
            SymbologyExtra: "barcodeType", BytesExtra: "barocode", LengthExtra: "length"),
        new("Newland", "nlscan.action.SCANNER_RESULT", ["SCAN_BARCODE1"], SymbologyExtra: "SCAN_BARCODE_TYPE"),
        new("iData", "android.intent.action.SCANRESULT", ["value"]),
        new("Datalogic", "com.datalogic.decodewedge.decode_action",
            ["com.datalogic.decode.intentwedge.barcode_string"],
            SymbologyExtra: "com.datalogic.decode.intentwedge.barcode_type"),
        new("Seuic", "com.android.server.scannerservice.broadcast", ["scannerdata"]),
    ];

    /// <summary>
    /// Gets the broadcasts that fire a handheld's scanner from an on-screen
    /// button. Makers that do not listen for them simply ignore them; on those
    /// the physical trigger is the way to scan.
    /// </summary>
    public static IReadOnlyList<SoftTriggerIntent> SoftTriggers { get; } =
    [
        new("Zebra", "com.symbol.datawedge.api.ACTION",
            new Dictionary<string, string> { ["com.symbol.datawedge.api.SOFT_SCAN_TRIGGER"] = "TOGGLE_SCANNING" }),
        new("Newland", "nlscan.action.SCANNER_TRIG", new Dictionary<string, string>()),
    ];

    /// <summary>Gets every action to listen for.</summary>
    public static IReadOnlyList<string> Actions { get; } = [.. All.Select(profile => profile.Action).Distinct(StringComparer.Ordinal)];
}

/// <summary>Reads a barcode out of a scanner broadcast.</summary>
public static class ScanIntentParser
{
    /// <summary>
    /// Reads the code from a broadcast's extras. Extras arrive as strings from
    /// most makers and as byte arrays from some, so both are accepted.
    /// </summary>
    /// <param name="action">The broadcast action.</param>
    /// <param name="extras">The broadcast's extras by name.</param>
    /// <param name="receivedAtUtc">When it arrived.</param>
    /// <returns>The scan, or null when the broadcast carried no usable code or failed.</returns>
    public static BarcodeScan? Parse(string? action, IReadOnlyDictionary<string, object?> extras, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(extras);

        ScanIntentProfile? profile = ScanIntentProfiles.All.FirstOrDefault(p => string.Equals(p.Action, action, StringComparison.Ordinal));
        if (profile is null)
        {
            return null;
        }

        // Newland reports a failed read on the same action.
        if (extras.TryGetValue("SCAN_STATE", out object? state) && state is string s && string.Equals(s, "fail", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string code = string.Empty;
        foreach (string name in profile.TextExtras)
        {
            if (extras.TryGetValue(name, out object? value) && AsText(value) is { Length: > 0 } text)
            {
                code = BarcodeText.Clean(text);
                if (code.Length > 0)
                {
                    break;
                }
            }
        }

        if (code.Length == 0 && profile.BytesExtra is { } bytesName && extras.TryGetValue(bytesName, out object? raw) && raw is byte[] bytes)
        {
            int length = profile.LengthExtra is { } lengthName && extras.TryGetValue(lengthName, out object? l) && AsInt(l) is { } n
                ? Math.Clamp(n, 0, bytes.Length)
                : bytes.Length;
            code = BarcodeText.Clean(Encoding.UTF8.GetString(bytes, 0, length));
        }

        if (code.Length == 0)
        {
            return null;
        }

        string? symbology = profile.SymbologyExtra is { } symbologyName && extras.TryGetValue(symbologyName, out object? type)
            ? AsText(type)
            : null;
        return new BarcodeScan(code, ScanSource.AndroidIntent, receivedAtUtc, string.IsNullOrWhiteSpace(symbology) ? null : symbology, profile.Vendor);
    }

    private static string? AsText(object? value) => value switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        char[] chars => new string(chars),
        null => null,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static int? AsInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        string text when int.TryParse(text, out int parsed) => parsed,
        _ => null,
    };
}
