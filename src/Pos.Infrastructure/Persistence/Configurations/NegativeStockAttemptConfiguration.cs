using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the refused stock draws. Insert-only: the append-only interceptor, the
/// PostgreSQL triggers and the application role's grants all refuse changes.
/// </summary>
public sealed class NegativeStockAttemptConfiguration : IEntityTypeConfiguration<NegativeStockAttempt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NegativeStockAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("negative_stock_attempt", PosDbContext.InventorySchema);

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(a => a.MovementType).HasColumnName("movement_type").HasConversion<short>().IsRequired();
        builder.Property(a => a.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(a => a.ProductId).HasColumnName("product_id").IsRequired();
        builder.Property(a => a.BatchKey).HasColumnName("batch_key").IsRequired();
        builder.Property(a => a.State).HasColumnName("state").HasConversion<short>().IsRequired();
        builder.Property(a => a.RequestedQuantity).HasColumnName("requested_quantity").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(a => a.AvailableQuantity).HasColumnName("available_quantity").HasPrecision(18, Quantity.Scale).IsRequired();
        builder.Property(a => a.Policy).HasColumnName("policy").HasConversion<short>().IsRequired();
        builder.Property(a => a.ReferenceDocumentType).HasColumnName("reference_document_type").HasConversion<short>().IsRequired();
        builder.Property(a => a.ReferenceDocumentId).HasColumnName("reference_document_id");
        // Same width as the ledger's own reference number, which it copies.
        builder.Property(a => a.ReferenceNumber).HasColumnName("reference_number").HasMaxLength(64).IsRequired();
        builder.Property(a => a.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(a => a.DeviceId).HasColumnName("device_id");
        builder.Property(a => a.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(a => a.AttemptedAtUtc).HasColumnName("attempted_at_utc").IsRequired();

        builder.Ignore(a => a.Shortfall);

        builder.HasIndex(a => new { a.LocationId, a.AttemptedAtUtc })
            .HasDatabaseName("ix_negative_stock_attempt_location_time");

        builder.HasIndex(a => new { a.ProductId, a.AttemptedAtUtc })
            .HasDatabaseName("ix_negative_stock_attempt_product_time");
    }
}
