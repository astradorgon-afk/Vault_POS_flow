using FluentAssertions;
using Pos.Shared.Scanning;

namespace Pos.Shared.Tests.Scanning;

/// <summary>Sending each scan to the screen that is waiting for it.</summary>
public sealed class ScanRouterTests
{
    private DateTimeOffset _now = new(2026, 9, 26, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheNewestClaim_TakesTheScan_AndHandsItBackWhenReleased()
    {
        ScanRouter router = new(() => _now);
        List<string> list = [];
        List<string> dialog = [];
        using IDisposable page = router.Claim(scan => Record(list, scan));

        IDisposable overlay = router.Claim(scan => Record(dialog, scan));
        await router.DispatchAsync(Scan("111111", ScanSource.Keyboard));
        overlay.Dispose();
        Advance();
        await router.DispatchAsync(Scan("222222", ScanSource.Keyboard));

        dialog.Should().Equal("111111");
        list.Should().Equal("222222");
    }

    [Fact]
    public async Task ReleasingAnOlderClaim_LeavesTheNewerOneInCharge()
    {
        ScanRouter router = new(() => _now);
        List<string> newer = [];
        IDisposable older = router.Claim(_ => Task.CompletedTask);
        using IDisposable current = router.Claim(scan => Record(newer, scan));

        older.Dispose();
        older.Dispose();
        await router.DispatchAsync(Scan("333333", ScanSource.Keyboard));

        newer.Should().Equal("333333");
    }

    [Fact]
    public async Task AScanNobodyWants_IsReportedAsUnclaimed()
    {
        ScanRouter router = new(() => _now);
        BarcodeScan? unclaimed = null;
        router.Unclaimed += scan => unclaimed = scan;

        bool taken = await router.DispatchAsync(Scan("444444", ScanSource.Keyboard));

        taken.Should().BeFalse();
        unclaimed!.Code.Should().Be("444444");
        router.HasClaim.Should().BeFalse();
    }

    [Fact]
    public async Task OneReadDeliveredTwice_ByTwoRoutes_CountsOnce()
    {
        ScanRouter router = new(() => _now);
        List<string> received = [];
        using IDisposable page = router.Claim(scan => Record(received, scan));

        await router.DispatchAsync(Scan("555555", ScanSource.AndroidIntent));
        _now = _now.AddMilliseconds(40);
        await router.DispatchAsync(Scan("555555", ScanSource.Keyboard));

        received.Should().Equal("555555");
    }

    [Fact]
    public async Task ScanningTheSameItemTwice_OnPurpose_CountsTwice()
    {
        ScanRouter router = new(() => _now);
        List<string> received = [];
        using IDisposable page = router.Claim(scan => Record(received, scan));

        await router.DispatchAsync(Scan("666666", ScanSource.Keyboard));
        _now = _now.AddMilliseconds(40);
        await router.DispatchAsync(Scan("666666", ScanSource.Keyboard));
        _now = _now.AddSeconds(1);
        await router.DispatchAsync(Scan("666666", ScanSource.AndroidIntent));

        received.Should().Equal("666666", "666666", "666666");
    }

    [Fact]
    public async Task Recent_KeepsTheNewestFirst()
    {
        ScanRouter router = new(() => _now);
        await router.DispatchAsync(Scan("A1111", ScanSource.Simulated));
        Advance();
        await router.DispatchAsync(Scan("B2222", ScanSource.Simulated));

        router.Recent.Select(scan => scan.Code).Should().Equal("B2222", "A1111");
    }

    private static Task Record(List<string> into, BarcodeScan scan)
    {
        into.Add(scan.Code);
        return Task.CompletedTask;
    }

    private BarcodeScan Scan(string code, ScanSource source) => new(code, source, _now);

    private void Advance() => _now = _now.AddSeconds(1);
}
