using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the synchronization inbox and per-device checkpoints.</summary>
public sealed class ProcessedSyncEventConfiguration : IEntityTypeConfiguration<ProcessedSyncEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedSyncEvent> builder)
    {
        builder.ToTable("processed_event", PosDbContext.SyncSchema);
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        builder.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(x => x.DeviceSequence).HasColumnName("device_sequence").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(80).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasMaxLength(524288).IsRequired();
        builder.Property(x => x.PayloadHash).HasColumnName("payload_hash").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(x => x.DeviceUptimeTicks).HasColumnName("device_uptime_ticks").IsRequired();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<short>().IsRequired();
        builder.Property(x => x.AppliedAtUtc).HasColumnName("applied_at_utc").IsRequired();
        builder.Property(x => x.ResponseJson).HasColumnName("response_json").HasMaxLength(8192);
        builder.HasIndex(x => new { x.DeviceId, x.DeviceSequence }).IsUnique().HasDatabaseName("ux_processed_event_device_sequence");
        builder.HasIndex(x => new { x.DeviceId, x.AppliedAtUtc }).HasDatabaseName("ix_processed_event_device_time");
    }
}

public sealed class SyncDeviceCheckpointConfiguration : IEntityTypeConfiguration<SyncDeviceCheckpoint>
{
    public void Configure(EntityTypeBuilder<SyncDeviceCheckpoint> builder)
    {
        builder.ToTable("device_checkpoint", PosDbContext.SyncSchema);
        builder.HasKey(x => x.DeviceId);
        builder.Property(x => x.DeviceId).HasColumnName("device_id").ValueGeneratedNever();
        builder.Property(x => x.LastAcceptedSequence).HasColumnName("last_accepted_sequence").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(x => x.GapDetectedAtUtc).HasColumnName("gap_detected_at_utc");
    }
}

public sealed class SyncFailureConfiguration : IEntityTypeConfiguration<SyncFailure>
{
    public void Configure(EntityTypeBuilder<SyncFailure> builder)
    {
        builder.ToTable("sync_failure", PosDbContext.SyncSchema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(160).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.LastAttemptAtUtc).HasColumnName("last_attempt_at_utc");
        builder.Property(x => x.NextRetryAtUtc).HasColumnName("next_retry_at_utc");
        builder.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
        builder.Property(x => x.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(1000);
        builder.HasIndex(x => new { x.Status, x.NextRetryAtUtc }).HasDatabaseName("ix_sync_failure_queue");
        builder.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("ux_sync_failure_event");
    }
}
