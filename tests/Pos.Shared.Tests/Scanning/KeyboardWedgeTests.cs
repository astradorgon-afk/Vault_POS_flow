using FluentAssertions;
using Pos.Shared.Scanning;

namespace Pos.Shared.Tests.Scanning;

/// <summary>
/// Telling a keyboard-wedge scanner from a person, by timing alone. The paces
/// below come from real devices: handheld and USB scanners send 1–15 ms per
/// character; people type 80–250 ms apart and unevenly.
/// </summary>
public sealed class KeyboardWedgeTests
{
    private static readonly WedgeOptions Options = WedgeOptions.Default;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    public void ScannerPace_IsAScan(double gapMilliseconds)
        => KeyboardWedge.IsScan(Typed("4800016123456", gapMilliseconds), Options).Should().BeTrue();

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(250)]
    public void PersonTyping_IsNotAScan(double gapMilliseconds)
        => KeyboardWedge.IsScan(Typed("4800016123456", gapMilliseconds), Options).Should().BeFalse();

    [Fact]
    public void SlowBluetoothScanner_ShiftingForCapitals_IsAScan()
    {
        // Measured: a slow HID stack spends 50–65 ms on each shifted capital.
        double[] gaps = [61.1, 65.6, 49.3, 49.7, 61.7, 66, 49, 32.7, 31.1, 33.5];
        List<KeyStroke> keys = [new('S', 0)];
        string code = "SKU-ABC-001";
        for (int i = 1; i < code.Length; i++)
        {
            keys.Add(new(code[i], keys[^1].AtMilliseconds + gaps[i - 1]));
        }

        KeyboardWedge.IsScan(keys, Options).Should().BeTrue();
    }

    [Fact]
    public void QuickTypistWithUnevenRhythm_IsNotAScan()
    {
        // 100 words a minute: about 120 ms a key, with some fast pairs.
        double[] gaps = [95, 140, 60, 180, 110, 75, 130];
        List<KeyStroke> keys = [new('4', 0)];
        string code = "48000161";
        for (int i = 1; i < code.Length; i++)
        {
            keys.Add(new(code[i], keys[^1].AtMilliseconds + gaps[i - 1]));
        }

        KeyboardWedge.IsScan(keys, Options).Should().BeFalse();
    }

    [Fact]
    public void FastTypistBurst_OfAFewKeys_IsNotAScan()
        => KeyboardWedge.IsScan(Typed("abc", 20), Options).Should().BeFalse("three characters is below the shortest barcode");

    [Fact]
    public void OneLongPause_InsideABurst_IsNotAScan()
    {
        List<KeyStroke> keys = [.. Typed("480001", 5)];
        keys.AddRange(Typed("6123456", 5, start: keys[^1].AtMilliseconds + 400));

        KeyboardWedge.IsScan(keys, Options).Should().BeFalse();
    }

    [Fact]
    public void JitteryButFastBluetoothScanner_IsAScan()
    {
        // Bluetooth scanners deliver in uneven packets: several characters at
        // once, then a short wait.
        double[] gaps = [1, 1, 1, 40, 1, 1, 1, 45, 1, 1, 1, 38];
        List<KeyStroke> keys = [new('4', 0)];
        string code = "4800016123456";
        for (int i = 1; i < code.Length; i++)
        {
            keys.Add(new(code[i], keys[^1].AtMilliseconds + gaps[i - 1]));
        }

        KeyboardWedge.IsScan(keys, Options).Should().BeTrue();
    }

    [Fact]
    public void ControlCharacters_AreNotAScan()
        => KeyboardWedge.IsScan(Typed("12\u000334", 3), Options).Should().BeFalse();

    [Fact]
    public void Gs1SeparatorInsideACode_IsAllowed()
        => KeyboardWedge.IsScan(Typed("0104800016\u001d10ABC", 3), Options).Should().BeTrue();

    [Fact]
    public void Text_SpellsTheKeys()
        => KeyboardWedge.Text(Typed("ABC-123", 3)).Should().Be("ABC-123");

    private static List<KeyStroke> Typed(string text, double gapMilliseconds, double start = 1000)
        => [.. text.Select((c, i) => new KeyStroke(c, start + (i * gapMilliseconds)))];
}
