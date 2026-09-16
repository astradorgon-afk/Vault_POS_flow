using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Notifications;

namespace Pos.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notification", PosDbContext.CoreSchema);
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(n => n.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(n => n.Severity).HasColumnName("severity").HasConversion<short>();
        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(Notification.TitleMaxLength).IsRequired();
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(Notification.BodyMaxLength).IsRequired();
        builder.Property(n => n.DeduplicationKey).HasColumnName("deduplication_key").HasMaxLength(Notification.DeduplicationKeyMaxLength).IsRequired();
        builder.Property(n => n.LocationId).HasColumnName("location_id");
        builder.Property(n => n.ProductId).HasColumnName("product_id");
        builder.Property(n => n.BatchId).HasColumnName("batch_id");
        builder.Property(n => n.ReferenceDocumentType).HasColumnName("reference_document_type").HasConversion<short?>();
        builder.Property(n => n.ReferenceDocumentId).HasColumnName("reference_document_id");
        builder.Property(n => n.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasIndex(n => n.DeduplicationKey).IsUnique().HasDatabaseName("ux_notification_deduplication_key");
        builder.HasIndex(n => new { n.LocationId, n.CreatedAtUtc }).HasDatabaseName("ix_notification_location_time");
    }
}

public sealed class NotificationReceiptConfiguration : IEntityTypeConfiguration<NotificationReceipt>
{
    public void Configure(EntityTypeBuilder<NotificationReceipt> builder)
    {
        builder.ToTable("notification_receipt", PosDbContext.CoreSchema);
        builder.HasKey(r => new { r.NotificationId, r.UserId });
        builder.Property(r => r.NotificationId).HasColumnName("notification_id");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.ReadAtUtc).HasColumnName("read_at_utc");
        builder.Property(r => r.AcknowledgedAtUtc).HasColumnName("acknowledged_at_utc");
        builder.HasOne<Notification>().WithMany().HasForeignKey(r => r.NotificationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.UserId, r.ReadAtUtc }).HasDatabaseName("ix_notification_receipt_user_read");
    }
}
