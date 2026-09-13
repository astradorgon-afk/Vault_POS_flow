using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Receipts;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps payment receipts. An RCT-numbered document is a single table with no
/// child entities; the read endpoint fetches it directly through the context.
/// </summary>
public sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("receipt", PosDbContext.CoreSchema);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.Number).HasColumnName("number").HasMaxLength(20).IsRequired();
        builder.Property(r => r.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
        builder.Property(r => r.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(r => r.Amount).HasColumnName("amount").IsRequired();
        builder.Property(r => r.Counterparty).HasColumnName("counterparty").HasMaxLength(Receipt.CounterpartyMaxLength);
        builder.Property(r => r.Note).HasColumnName("note").HasMaxLength(Receipt.NoteMaxLength);
        builder.Property(r => r.ReferenceNumber).HasColumnName("reference_number").HasMaxLength(DocumentNumber.MaxLength);
        builder.Property(r => r.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(r => r.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();

        builder.HasIndex(r => r.Number)
            .IsUnique()
            .HasDatabaseName("ux_receipt_number");

        builder.HasIndex(r => new { r.LocationId, r.IssuedAtUtc })
            .HasDatabaseName("ix_receipt_location_time");
    }
}