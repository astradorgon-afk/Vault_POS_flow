using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Auditing;
using Pos.Domain.Devices;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps registered devices.</summary>
public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("device", PosDbContext.CoreSchema);

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.ShortCode).HasColumnName("short_code").HasMaxLength(6).IsRequired();
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(d => d.LocationId).HasColumnName("location_id").IsRequired();
        builder.Property(d => d.Platform).HasColumnName("platform").HasConversion<short>().IsRequired();
        builder.Property(d => d.AppVersion).HasColumnName("app_version").HasMaxLength(64);
        builder.Property(d => d.OsVersion).HasColumnName("os_version").HasMaxLength(64);
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(d => d.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(d => d.RegisteredByUserId).HasColumnName("registered_by_user_id").IsRequired();
        builder.Property(d => d.EnrolledAtUtc).HasColumnName("enrolled_at_utc");
        builder.Property(d => d.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
        builder.Property(d => d.LastSyncAtUtc).HasColumnName("last_sync_at_utc");
        builder.Property(d => d.ClockSkewSeconds).HasColumnName("clock_skew_seconds").HasPrecision(12, 3);
        builder.Property(d => d.PublicKeyThumbprint).HasColumnName("public_key_thumbprint").HasMaxLength(128);
        builder.Property(d => d.StatusReason).HasColumnName("status_reason").HasMaxLength(512);
        builder.Property(d => d.StatusChangedAtUtc).HasColumnName("status_changed_at_utc");
        builder.Property(d => d.StatusChangedByUserId).HasColumnName("status_changed_by_user_id");

        // Offline document numbers embed this code, so two devices sharing one
        // would mint colliding receipt numbers.
        builder.HasIndex(d => d.ShortCode).IsUnique().HasDatabaseName("ux_device_short_code");

        builder.HasIndex(d => new { d.LocationId, d.Status }).HasDatabaseName("ix_device_location_status");

        builder.Ignore(d => d.IsOperational);
        builder.Ignore(d => d.DomainEvents);
    }
}

/// <summary>Maps device enrolment codes.</summary>
public sealed class DeviceEnrolmentCodeConfiguration : IEntityTypeConfiguration<DeviceEnrolmentCode>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeviceEnrolmentCode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("device_enrolment_code", PosDbContext.CoreSchema);

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(c => c.CodeHash).HasColumnName("code_hash").IsRequired();
        builder.Property(c => c.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
        builder.Property(c => c.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(c => c.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(c => c.RedeemedAtUtc).HasColumnName("redeemed_at_utc");
        builder.Property(c => c.FailedAttempts).HasColumnName("failed_attempts").IsRequired();
        builder.Property(c => c.IsBurned).HasColumnName("is_burned").IsRequired();

        builder.HasIndex(c => c.CodeHash).IsUnique().HasDatabaseName("ux_device_enrolment_code_hash");
        builder.HasIndex(c => c.DeviceId).HasDatabaseName("ix_device_enrolment_code_device");

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(c => c.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps device sign-in sessions.</summary>
public sealed class DeviceSessionConfiguration : IEntityTypeConfiguration<DeviceSession>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeviceSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("device_session", PosDbContext.CoreSchema);

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(s => s.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(s => s.RefreshTokenFamilyId).HasColumnName("refresh_token_family_id").IsRequired();
        builder.Property(s => s.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(s => s.EndedAtUtc).HasColumnName("ended_at_utc");
        builder.Property(s => s.EndedReason).HasColumnName("ended_reason").HasMaxLength(128);
        builder.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(s => s.AppVersion).HasColumnName("app_version").HasMaxLength(64);

        builder.HasIndex(s => new { s.DeviceId, s.StartedAtUtc }).HasDatabaseName("ix_device_session_device");
        builder.HasIndex(s => s.RefreshTokenFamilyId).HasDatabaseName("ix_device_session_family");

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps the append-only audit log.</summary>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_log", PosDbContext.AuditSchema);

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder.Property(a => a.EntityType).HasColumnName("entity_type").HasMaxLength(64).IsRequired();
        builder.Property(a => a.EntityId).HasColumnName("entity_id");
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.UserRoleSnapshot).HasColumnName("user_role_snapshot").HasMaxLength(256);
        builder.Property(a => a.DeviceId).HasColumnName("device_id");
        builder.Property(a => a.LocationId).HasColumnName("location_id");
        builder.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(a => a.PreviousValueJson).HasColumnName("previous_value").HasMaxLength(8192);
        builder.Property(a => a.NewValueJson).HasColumnName("new_value").HasMaxLength(8192);
        builder.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(1024);
        builder.Property(a => a.ReferenceDocumentType)
            .HasColumnName("reference_document_type")
            .HasConversion<short?>();
        builder.Property(a => a.ReferenceDocumentId).HasColumnName("reference_document_id");
        builder.Property(a => a.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(a => a.CorrelationId).HasColumnName("correlation_id").IsRequired();

        // "What happened to this record" is the question an investigation starts
        // with, so it gets a covering index rather than a scan.
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAtUtc })
            .HasDatabaseName("ix_audit_log_entity");

        builder.HasIndex(a => new { a.UserId, a.OccurredAtUtc }).HasDatabaseName("ix_audit_log_user");
        builder.HasIndex(a => a.CorrelationId).HasDatabaseName("ix_audit_log_correlation");
        builder.HasIndex(a => new { a.Action, a.OccurredAtUtc }).HasDatabaseName("ix_audit_log_action");
    }
}
