using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the record that makes uploading exactly-once.</summary>
public sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processed_event", PosDbContext.SyncSchema);

        builder.HasKey(e => e.EventId);
        builder.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
        builder.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(e => e.DeviceSequence).HasColumnName("device_sequence").IsRequired();
        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(64).IsRequired();
        builder.Property(e => e.PayloadHash).HasColumnName("payload_hash").IsRequired();
        builder.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(32).IsRequired();
        builder.Property(e => e.ResultJson).HasColumnName("result_json").HasMaxLength(4000);
        builder.Property(e => e.AppliedAtUtc).HasColumnName("applied_at_utc").IsRequired();

        // One device never sends the same sequence twice, so this is both a
        // lookup and a second line of defence against a replayed batch.
        builder.HasIndex(e => new { e.DeviceId, e.DeviceSequence })
            .IsUnique()
            .HasDatabaseName("ux_processed_event_device_sequence");
    }
}

/// <summary>Maps how far through a device's sequence the server has accepted.</summary>
public sealed class SyncCheckpointConfiguration : IEntityTypeConfiguration<SyncCheckpoint>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SyncCheckpoint> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sync_checkpoint", PosDbContext.SyncSchema);

        builder.HasKey(c => c.DeviceId);
        builder.Property(c => c.DeviceId).HasColumnName("device_id").ValueGeneratedNever();
        builder.Property(c => c.LastAcceptedDeviceSequence)
            .HasColumnName("last_accepted_device_sequence")
            .IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
    }
}
