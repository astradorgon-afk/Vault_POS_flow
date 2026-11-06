using System.Text;
using FluentAssertions;
using Pos.Shared.Scanning;

namespace Pos.Shared.Tests.Scanning;

/// <summary>
/// Reading barcodes out of Android handheld broadcasts, shaped the way each
/// maker's scanner service sends them.
/// </summary>
public sealed class ScanIntentParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("com.vaultflow.pos.SCAN", "com.symbol.datawedge.data_string", "Configured")]
    [InlineData("com.vaultflow.pos.SCAN", "data", "Configured")]
    [InlineData("com.sunmi.scanner.ACTION_DATA_CODE_RECEIVED", "data", "Sunmi")]
    [InlineData("android.intent.ACTION_DECODE_DATA", "barcode_string", "Urovo")]
    [InlineData("nlscan.action.SCANNER_RESULT", "SCAN_BARCODE1", "Newland")]
    [InlineData("android.intent.action.SCANRESULT", "value", "iData")]
    [InlineData("com.datalogic.decodewedge.decode_action", "com.datalogic.decode.intentwedge.barcode_string", "Datalogic")]
    [InlineData("com.android.server.scannerservice.broadcast", "scannerdata", "Seuic")]
    public void EachMakersBroadcast_YieldsTheCode(string action, string extra, string vendor)
    {
        BarcodeScan? scan = ScanIntentParser.Parse(action, new Dictionary<string, object?> { [extra] = "4800016123456\n" }, Now);

        scan.Should().NotBeNull();
        scan!.Code.Should().Be("4800016123456", "the trailing line ending is the scanner's suffix, not part of the code");
        scan.Source.Should().Be(ScanSource.AndroidIntent);
        scan.Vendor.Should().Be(vendor);
    }

    [Fact]
    public void DataWedge_ReportsTheBarcodeType()
    {
        BarcodeScan? scan = ScanIntentParser.Parse(
            ScanIntentProfiles.VaultFlowAction,
            new Dictionary<string, object?>
            {
                ["com.symbol.datawedge.data_string"] = "4800016123456",
                ["com.symbol.datawedge.label_type"] = "LABEL-TYPE-EAN13",
            },
            Now);

        scan!.Symbology.Should().Be("LABEL-TYPE-EAN13");
    }

    [Fact]
    public void Urovo_BytesOnly_AreReadUpToTheGivenLength()
    {
        byte[] buffer = new byte[32];
        Encoding.ASCII.GetBytes("4800016123456").CopyTo(buffer, 0);

        BarcodeScan? scan = ScanIntentParser.Parse(
            "android.intent.ACTION_DECODE_DATA",
            new Dictionary<string, object?> { ["barocode"] = buffer, ["length"] = 13 },
            Now);

        scan!.Code.Should().Be("4800016123456");
    }

    [Fact]
    public void Newland_FailedRead_IsIgnored()
        => ScanIntentParser.Parse(
            "nlscan.action.SCANNER_RESULT",
            new Dictionary<string, object?> { ["SCAN_BARCODE1"] = string.Empty, ["SCAN_STATE"] = "fail" },
            Now).Should().BeNull();

    [Fact]
    public void UnknownAction_IsIgnored()
        => ScanIntentParser.Parse("com.example.OTHER", new Dictionary<string, object?> { ["data"] = "123456" }, Now)
            .Should().BeNull();

    [Fact]
    public void EmptyExtras_AreIgnored()
        => ScanIntentParser.Parse("com.sunmi.scanner.ACTION_DATA_CODE_RECEIVED", new Dictionary<string, object?>(), Now)
            .Should().BeNull();

    [Fact]
    public void EveryProfileAction_IsListenedFor()
        => ScanIntentProfiles.Actions.Should().Contain(ScanIntentProfiles.All.Select(p => p.Action));
}
