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

/// <summary>Maps the server change feed: what devices download, in order.</summary>
public sealed class ChangeFeedEntryConfiguration : IEntityTypeConfiguration<ChangeFeedEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChangeFeedEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("change_feed", PosDbContext.SyncSchema);

        // The sequence is the key because it is what a device reads by. It is
        // allocated by the writer, never by the database, so the order rows
        // become visible in is the order they were numbered in.
        builder.HasKey(e => e.Sequence);
        builder.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedNever();
        builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(e => e.LocationScopeId).HasColumnName("location_scope_id");
        // Unbounded: a change payload has no natural ceiling — a location's
        // settings alone can outgrow the model's default string length, and a
        // feed row silently truncated is a register told a half-truth.
        builder.Property(e => e.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(e => e.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        // The feed is served as "everything after my cursor that is global or
        // mine", which is exactly this index.
        builder.HasIndex(e => new { e.LocationScopeId, e.Sequence })
            .HasDatabaseName("ix_change_feed_scope_sequence");
    }
}

/// <summary>Maps the change feed's single counter row.</summary>
public sealed class ChangeFeedSequenceConfiguration : IEntityTypeConfiguration<ChangeFeedSequence>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChangeFeedSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("feed_sequence", PosDbContext.SyncSchema);

        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(32).ValueGeneratedNever();
        builder.Property(s => s.NextValue).HasColumnName("next_value").IsRequired();
    }
}
