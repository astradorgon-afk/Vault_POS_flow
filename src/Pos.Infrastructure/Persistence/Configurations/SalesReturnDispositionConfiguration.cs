using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps immutable return inspection events.</summary>
public sealed class SalesReturnDispositionConfiguration : IEntityTypeConfiguration<SalesReturnDisposition>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SalesReturnDisposition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("sales_return_disposition", PosDbContext.SalesSchema);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("event_id").ValueGeneratedNever();
        builder.Property(d => d.SalesReturnId).HasColumnName("sales_return_id");
        builder.Property(d => d.SalesReturnItemId).HasColumnName("sales_return_item_id");
        builder.Property(d => d.Quantity).HasColumnName("quantity").HasPrecision(18, Quantity.Scale);
        builder.Property(d => d.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(d => d.ReasonCode).HasColumnName("reason_code").HasConversion<short>();
        builder.Property(d => d.Note).HasColumnName("note").HasMaxLength(512).IsRequired();
        builder.Property(d => d.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(d => d.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.HasOne<SalesReturn>().WithMany().HasForeignKey(d => d.SalesReturnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesReturnItem>().WithMany().HasForeignKey(d => d.SalesReturnItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
