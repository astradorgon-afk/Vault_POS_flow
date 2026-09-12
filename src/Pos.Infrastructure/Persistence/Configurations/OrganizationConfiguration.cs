using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Organizations;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the organization aggregate.</summary>
public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("organization", PosDbContext.CoreSchema);

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(o => o.Name).HasColumnName("name").HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(o => o.LegalName).HasColumnName("legal_name").HasMaxLength(256).IsRequired();
        builder.Property(o => o.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(o => o.DefaultTimeZoneId).HasColumnName("default_time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(o => o.TaxSettingsJson).HasColumnName("tax_settings_json");
        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
    }
}