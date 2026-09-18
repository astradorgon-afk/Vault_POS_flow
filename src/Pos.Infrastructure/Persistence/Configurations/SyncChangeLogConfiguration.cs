using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only server change feed.</summary>
public sealed class SyncChangeLogConfiguration : IEntityTypeConfiguration<SyncChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<SyncChangeLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("change_log", PosDbContext.SyncSchema);
        builder.HasKey(x => x.Sequence);
        builder.Property(x => x.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
        builder.Property(x => x.ChangeType).HasColumnName("change_type").HasMaxLength(120).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasMaxLength(524288).IsRequired();
        builder.Property(x => x.LocationScopeId).HasColumnName("location_scope_id");
        builder.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();
        builder.HasIndex(x => new { x.LocationScopeId, x.Sequence })
            .HasDatabaseName("ix_change_log_scope_sequence");
    }
}
