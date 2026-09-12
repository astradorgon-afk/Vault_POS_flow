using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Organizations;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the location aggregate and its serialized settings.</summary>
public sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("location", PosDbContext.CoreSchema);

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(l => l.Code).HasColumnName("code").HasMaxLength(Location.CodeMaxLength).IsRequired();
        builder.Property(l => l.Name).HasColumnName("name").HasMaxLength(Location.NameMaxLength).IsRequired();
        builder.Property(l => l.Kind).HasColumnName("kind").HasConversion<short>().IsRequired();
        builder.Property(l => l.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(l => l.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(l => l.IsSystemCreated).HasColumnName("is_system_created").IsRequired();
        builder.Property(l => l.OpenedOn).HasColumnName("opened_on").IsRequired();
        builder.Property(l => l.ClosedOn).HasColumnName("closed_on");
        builder.Property(l => l.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.Property(l => l.Settings)
            .HasColumnName("settings_json")
            .HasConversion(
                to => to.ToJson(),
                from => LocationSettings.FromJson(from))
            .HasColumnType("text");

        // A code is unique within an organization.
        builder.HasIndex(l => new { l.OrganizationId, l.Code })
            .IsUnique()
            .HasDatabaseName("ux_location_org_code");

        // No two active Main Warehouses for one organization.
        builder.HasIndex(l => l.OrganizationId)
            .HasDatabaseName("ux_location_single_main")
            .HasFilter("\"kind\" = 0 AND \"is_active\"");

        builder.HasIndex(l => l.Kind).HasDatabaseName("ix_location_kind");
        builder.HasIndex(l => l.IsActive).HasDatabaseName("ix_location_active");
    }
}