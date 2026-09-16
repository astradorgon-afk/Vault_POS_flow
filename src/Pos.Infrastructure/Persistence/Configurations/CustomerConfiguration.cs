using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Configures the <see cref="Customer"/> entity.</summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer", "sales");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .HasConversion(
                id => id.Value,
                value => new CustomerId(value));

        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .IsRequired()
            .HasMaxLength(Customer.DisplayNameMaxLength);

        builder.Property(c => c.Phone)
            .HasColumnName("phone")
            .HasMaxLength(Customer.PhoneMaxLength);

        builder.Property(c => c.Email)
            .HasColumnName("email")
            .HasMaxLength(Customer.EmailMaxLength);

        builder.Property(c => c.Tin)
            .HasColumnName("tin")
            .HasMaxLength(Customer.TinMaxLength);

        builder.Property(c => c.Note)
            .HasColumnName("note")
            .HasMaxLength(Customer.NoteMaxLength);

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();

        builder.Property(c => c.CreatedByUserId)
            .HasColumnName("created_by_user_id")
            .HasConversion(
                id => id.Value,
                value => new UserId(value))
            .IsRequired();

        builder.Property(c => c.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .IsRequired();

        builder.Property(c => c.UpdatedByUserId)
            .HasColumnName("updated_by_user_id")
            .HasConversion(
                id => id.Value,
                value => new UserId(value))
            .IsRequired();

        builder.Property(c => c.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        builder.Property(c => c.DeactivationReason)
            .HasColumnName("deactivation_reason")
            .HasMaxLength(Customer.DeactivationReasonMaxLength);

        builder.HasIndex(c => c.DisplayName).HasDatabaseName("ix_customer_display_name");
        builder.HasIndex(c => c.IsActive).HasDatabaseName("ix_customer_is_active");
    }
}
