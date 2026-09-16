using System.Globalization;
using FluentAssertions;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Tests.Inventory;

public sealed class LowStockNotificationFactoryTests
{
    private static readonly LocationId Location = LocationId.New();
    private static readonly ProductId Product = ProductId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 23, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Create_AtReorderPoint_IsAWarningKeyedByLevelAndUtcDay()
    {
        Notification notification = LowStockNotificationFactory.Create(Item(available: 15m), Now);

        notification.Kind.Should().Be(NotificationKind.LowStock);
        notification.Severity.Should().Be(NotificationSeverity.Warning);
        notification.Title.Should().Be("Rice 5kg reached its reorder point");
        notification.Body.Should().Be("15.0000 units available; minimum 10.0000, reorder point 15.0000.");
        notification.DeduplicationKey.Should().Be(
            $"lowstock:{Location.Value:D}:{Product.Value:D}:reorder:20260917");
        notification.CreatedAtUtc.Should().Be(Now);
        notification.LocationId.Should().Be(Location);
        notification.ProductId.Should().Be(Product);
        notification.BatchId.Should().BeNull();
    }

    [Fact]
    public void Create_BelowMinimum_IsCriticalWithItsOwnKey()
    {
        Notification notification = LowStockNotificationFactory.Create(Item(available: 9.5m), Now);

        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("Rice 5kg is below minimum stock");
        notification.DeduplicationKey.Should().Contain(":minimum:");
    }

    [Fact]
    public void Create_WithNothingAvailable_IsOutOfStock()
    {
        Notification notification = LowStockNotificationFactory.Create(Item(available: 0m), Now);

        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("Rice 5kg is out of stock");
        notification.DeduplicationKey.Should().Contain(":out:");
    }

    [Fact]
    public void Create_TheNextUtcDay_ProducesANewKey()
    {
        Notification today = LowStockNotificationFactory.Create(Item(available: 12m), Now);
        Notification tomorrow = LowStockNotificationFactory.Create(Item(available: 12m), Now.AddDays(1));

        tomorrow.DeduplicationKey.Should().NotBe(today.DeduplicationKey);
    }

    [Fact]
    public void Create_FormatsNumbersIndependentlyOfCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Notification notification = LowStockNotificationFactory.Create(Item(available: 1234.5m, reorderPoint: 2000m), Now);

            notification.Body.Should().StartWith("1,234.5000 units available");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static LowStockItem Item(decimal available, decimal reorderPoint = 15m)
        => new(Location, Product, "Rice 5kg", available, MinimumStock: 10m, reorderPoint);
}
