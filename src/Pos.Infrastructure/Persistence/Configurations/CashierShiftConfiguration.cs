using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps a cashier shift: the physical period between drawer open and close.</summary>
public sealed class CashierShiftConfiguration : IEntityTypeConfiguration<CashierShift>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CashierShift> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("cashier_shift", PosDbContext.SalesSchema);

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        // The SHF number is allocated by the device inside the same transaction
        // as the shift, so the counter advances exactly once; the column is
        // unique because a replayed open must return the original document.
        builder.Property(s => s.Number)
            .HasColumnName("number")
            .HasMaxLength(DocumentNumber.MaxLength)
            .IsRequired();

        builder.Property(s => s.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(s => s.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(s => s.CashierUserId).HasColumnName("cashier_user_id").IsRequired();
        builder.Property(s => s.OpenedAtUtc).HasColumnName("opened_at_utc").IsRequired();
        builder.Property(s => s.BusinessDate).HasColumnName("business_date").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<short>().IsRequired();

        builder.Property(s => s.OpeningFloat)
            .HasColumnName("opening_float")
            .HasPrecision(19, Money.StorageScale)
            .IsRequired();

        builder.Property(s => s.ClosedAtUtc).HasColumnName("closed_at_utc");
        builder.Property(s => s.DeclaredCash).HasColumnName("declared_cash").HasPrecision(19, Money.StorageScale);
        builder.Property(s => s.CountedCash).HasColumnName("counted_cash").HasPrecision(19, Money.StorageScale);
        builder.Property(s => s.CashVariance).HasColumnName("cash_variance").HasPrecision(19, Money.StorageScale);
        builder.Property(s => s.IsForceClosed).HasColumnName("is_force_closed").IsRequired();

        builder.HasIndex(s => s.Number)
            .IsUnique()
            .HasDatabaseName("ux_cashier_shift_number");

        // The open-shift-for-device lookup and the daily per-location summaries
        // both scan by row; these indexes keep those reads off the full table.
        builder.HasIndex(s => s.DeviceId)
            .HasDatabaseName("ix_cashier_shift_device");

        builder.HasIndex(s => new { s.LocationId, s.BusinessDate })
            .HasDatabaseName("ix_cashier_shift_location_business_date");
    }
}