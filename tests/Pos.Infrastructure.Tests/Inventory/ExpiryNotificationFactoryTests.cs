using System.Globalization;
using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Tests.Inventory;

public sealed class ExpiryNotificationFactoryTests
{
    [Fact]
    public void CreateExpiringBatch_UsesStableTargetSeverityAndDeduplicationKey()
    {
        LocationId locationId = LocationId.New();
        ProductId productId = ProductId.New();
        BatchId batchId = BatchId.New();
        DateTimeOffset now = new(2026, 9, 17, 1, 2, 3, TimeSpan.Zero);
        ExpiringBatchSummary item = new(
            locationId,
            productId,
            "Vitamin C",
            batchId,
            "LOT-42",
            12.5m,
            new DateOnly(2026, 9, 24),
            7);

        Notification notification = ExpiryNotificationFactory.CreateExpiringBatch(item, now);

        notification.Kind.Should().Be(NotificationKind.BatchExpiringSoon);
        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("Vitamin C expires in 7 days");
        notification.Body.Should().Be("Lot LOT-42 has 12.5000 units available and expires on 2026-09-24.");
        notification.DeduplicationKey.Should().Be(
            $"expiry:soon:{locationId.Value:D}:{batchId.Value:D}:20260924:critical");
        notification.CreatedAtUtc.Should().Be(now);
        notification.LocationId.Should().Be(locationId);
        notification.ProductId.Should().Be(productId);
        notification.BatchId.Should().Be(batchId);

        Notification warning = ExpiryNotificationFactory.CreateExpiringBatch(
            item with { DaysUntilExpiry = 8 },
            now);
        warning.Severity.Should().Be(NotificationSeverity.Warning);
        warning.DeduplicationKey.Should().EndWith(":warning");
        warning.DeduplicationKey.Should().NotBe(notification.DeduplicationKey);
    }

    [Fact]
    public void CreateExpiredRun_FormatsNumbersIndependentlyOfCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            LocationId locationId = LocationId.New();
            DateTimeOffset processedAtUtc = new(2026, 9, 17, 2, 3, 4, TimeSpan.Zero);
            ExpiryRunResult run = new(
                locationId,
                DocumentNumber.Create(DocumentType.ExpiryRun, 2026, 1),
                processedAtUtc,
                2,
                12.5m,
                345.6m,
                []);

            Notification notification = ExpiryNotificationFactory.CreateExpiredRun(run);

            notification.Kind.Should().Be(NotificationKind.BatchExpired);
            notification.Severity.Should().Be(NotificationSeverity.Critical);
            notification.Title.Should().Be("2 expired batches quarantined");
            notification.Body.Should().Be(
                "Expiry run EXP-2026-000001 moved 12.5000 units valued at 345.60 out of available stock.");
            notification.DeduplicationKey.Should().Be(
                $"expiry:expired:{locationId.Value:D}:EXP-2026-000001");
            notification.CreatedAtUtc.Should().Be(processedAtUtc);
            notification.LocationId.Should().Be(locationId);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
