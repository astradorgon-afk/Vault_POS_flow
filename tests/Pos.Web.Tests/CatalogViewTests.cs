using FluentAssertions;
using Pos.Web.Components.Pages;

namespace Pos.Web.Tests;

/// <summary>
/// Reading a barcode lookup, and catching a hand-typed code that cannot be the
/// number on the label.
/// </summary>
public sealed class CatalogViewTests
{
    [Fact]
    public void AKnownCode_ReadsAsKnown()
        => CatalogView.Classify("4800016641107", succeeded: true, errorCode: null)
            .Should().Be(BarcodeLookupOutcome.Known);

    [Theory]
    [InlineData("catalog.barcode_unknown")]
    [InlineData("catalog.barcode_retired")]
    public void AnUnregisteredOrRetiredCode_IsTheCaseThisScreenResolves(string errorCode)
    {
        // Both answer the same on purpose: a retired code finds no product, so
        // the operator is offered the same two ways out as an unknown one.
        CatalogView.Classify("4800016641107", succeeded: false, errorCode)
            .Should().Be(BarcodeLookupOutcome.Unregistered);
    }

    [Fact]
    public void AFailedLookup_IsNotMistakenForAnUnregisteredCode()
    {
        // Saying "not registered" when the server was merely unreachable would
        // invite someone to register a code that already exists.
        CatalogView.Classify("4800016641107", succeeded: false, errorCode: "server.unreachable")
            .Should().Be(BarcodeLookupOutcome.Failed);

        CatalogView.Classify("4800016641107", succeeded: false, errorCode: null)
            .Should().Be(BarcodeLookupOutcome.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTyped_AsksNothing(string? code)
        => CatalogView.Classify(code, succeeded: false, errorCode: null)
            .Should().Be(BarcodeLookupOutcome.Empty);

    [Theory]
    [InlineData("4006381333931")]  // EAN-13
    [InlineData("036000291452")]   // UPC-A
    [InlineData("96385074")]       // EAN-8
    public void AValidRetailBarcode_DrawsNoWarning(string code)
        => CatalogView.CheckDigitWarning(code).Should().BeNull();

    [Fact]
    public void AMistypedRetailBarcode_IsFlaggedBeforeItIsSaved()
    {
        // Last digit changed from 1 to 2: the shape is right, the arithmetic is
        // not. Registering it would mean the code never scans and the real one
        // stays unregistered.
        string? warning = CatalogView.CheckDigitWarning("4006381333932");

        warning.Should().NotBeNull();
        warning.Should().Contain("13-digit");
        warning.Should().Contain("check digit");
    }

    [Theory]
    [InlineData("INTERNAL-42")]     // own-brand, not a GTIN
    [InlineData("12345")]           // no GTIN length
    [InlineData("123456789012345")] // longer than any GTIN
    [InlineData("")]
    [InlineData(null)]
    public void ACodeThatIsNotARetailBarcode_IsLeftAlone(string? code)
    {
        // Internal and own-brand codes carry no check digit, so there is
        // nothing to be wrong about and a warning would just be noise.
        CatalogView.CheckDigitWarning(code).Should().BeNull();
    }

    [Fact]
    public void AScannedCode_IsCleanedBeforeItIsChecked()
    {
        // Scanners append a carriage return and may send an AIM prefix; a
        // warning triggered by those would fire on every good scan.
        CatalogView.CheckDigitWarning("]E04006381333931\r\n").Should().BeNull();
    }

    [Fact]
    public void ASingleUnitCode_SaysSoPlainly()
        => CatalogView.PackSummary(1m, "EA").Should().Be("One scan counts as 1 EA.");

    [Fact]
    public void ACaseCode_SaysHowManyBaseUnitsItIs()
        => CatalogView.PackSummary(24m, "EA").Should().Be("One scan counts as 24 EA.");

    [Fact]
    public void AFractionalPack_KeepsItsDecimalsWithoutTrailingZeros()
    {
        CatalogView.PackSummary(1.5m, "KG").Should().Be("One scan counts as 1.5 KG.");
        CatalogView.Quantity(24.000m).Should().Be("24");
        CatalogView.Quantity(0.125m).Should().Be("0.125");
    }
}
