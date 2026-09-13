using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;

namespace Pos.Infrastructure.Persistence.Configurations;

/// <summary>Maps the user table, including the point-of-sale additions.</summary>
public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("app_user", PosDbContext.CoreSchema);

        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
        builder.Property(u => u.EmployeeCode).HasColumnName("employee_code").HasMaxLength(16);
        builder.Property(u => u.PinHash).HasColumnName("pin_hash").HasMaxLength(256);
        builder.Property(u => u.PinChangedAtUtc).HasColumnName("pin_changed_at_utc");
        builder.Property(u => u.ApprovalTier).HasColumnName("approval_tier").HasConversion<short>().IsRequired();
        builder.Property(u => u.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(u => u.DisabledAtUtc).HasColumnName("disabled_at_utc");
        builder.Property(u => u.DisabledReason).HasColumnName("disabled_reason").HasMaxLength(512);
        builder.Property(u => u.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(u => u.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(u => u.LastLoginAtUtc).HasColumnName("last_login_at_utc");

        // A cashier types this at a terminal, so it has to be unique across the
        // business. The filter keeps the constraint off users who have none.
        builder.HasIndex(u => u.EmployeeCode)
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("ux_app_user_employee_code");

        builder.Ignore(u => u.CanAuthenticate);
    }
}

/// <summary>Maps the role table.</summary>
public sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("app_role", PosDbContext.CoreSchema);

        builder.Property(r => r.Description).HasColumnName("description").HasMaxLength(256).IsRequired();
        builder.Property(r => r.IsSystemRole).HasColumnName("is_system_role").IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
    }
}

/// <summary>Renames the framework's join and claim tables to the project convention.</summary>
public sealed class IdentitySupportingTableConfiguration :
    IEntityTypeConfiguration<IdentityUserRole<Guid>>,
    IEntityTypeConfiguration<IdentityUserClaim<Guid>>,
    IEntityTypeConfiguration<IdentityUserLogin<Guid>>,
    IEntityTypeConfiguration<IdentityUserToken<Guid>>,
    IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
        => builder?.ToTable("app_user_role", PosDbContext.CoreSchema);

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
        => builder?.ToTable("app_user_claim", PosDbContext.CoreSchema);

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
        => builder?.ToTable("app_user_login", PosDbContext.CoreSchema);

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
        => builder?.ToTable("app_user_token", PosDbContext.CoreSchema);

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
        => builder?.ToTable("app_role_claim", PosDbContext.CoreSchema);
}

/// <summary>Maps the permission catalogue.</summary>
public sealed class PermissionRecordConfiguration : IEntityTypeConfiguration<PermissionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermissionRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("permission", PosDbContext.CoreSchema);

        builder.HasKey(p => p.Code);

        builder.Property(p => p.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(p => p.Module).HasColumnName("module").HasMaxLength(64).IsRequired();
        builder.Property(p => p.Description).HasColumnName("description").HasMaxLength(256).IsRequired();
        builder.Property(p => p.IsOfflineCapable).HasColumnName("is_offline_capable").IsRequired();
        builder.Property(p => p.IsReadOnly).HasColumnName("is_read_only").IsRequired();

        builder.HasIndex(p => p.Module).HasDatabaseName("ix_permission_module");
    }
}

/// <summary>Maps the record of default grants already applied to seeded roles.</summary>
public sealed class RoleDefaultGrantAppliedConfiguration : IEntityTypeConfiguration<RoleDefaultGrantApplied>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoleDefaultGrantApplied> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_default_grant_applied", PosDbContext.CoreSchema);

        builder.HasKey(g => new { g.RoleName, g.PermissionCode });

        builder.Property(g => g.RoleName).HasColumnName("role_name").HasMaxLength(256);
        builder.Property(g => g.PermissionCode).HasColumnName("permission_code").HasMaxLength(64);
        builder.Property(g => g.AppliedAtUtc).HasColumnName("applied_at_utc").IsRequired();
    }
}

/// <summary>Maps role-to-permission grants.</summary>
public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermissionGrant>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RolePermissionGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_permission", PosDbContext.CoreSchema);

        builder.HasKey(rp => new { rp.RoleId, rp.PermissionCode });

        builder.Property(rp => rp.RoleId).HasColumnName("role_id");
        builder.Property(rp => rp.PermissionCode).HasColumnName("permission_code").HasMaxLength(64);
        builder.Property(rp => rp.GrantedAtUtc).HasColumnName("granted_at_utc").IsRequired();
        builder.Property(rp => rp.GrantedByUserId).HasColumnName("granted_by_user_id");

        builder.HasOne<AppRole>()
            .WithMany()
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // A grant naming a permission that is not in the catalogue would be a
        // check that silently never passes, so the database refuses it.
        builder.HasOne<PermissionRecord>()
            .WithMany()
            .HasForeignKey(rp => rp.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps per-user permission overrides.</summary>
public sealed class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_permission_override", PosDbContext.CoreSchema);

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(o => o.PermissionCode).HasColumnName("permission_code").HasMaxLength(64).IsRequired();
        builder.Property(o => o.Effect).HasColumnName("effect").HasConversion<short>().IsRequired();
        builder.Property(o => o.GrantedAtUtc).HasColumnName("granted_at_utc").IsRequired();
        builder.Property(o => o.GrantedByUserId).HasColumnName("granted_by_user_id").IsRequired();
        builder.Property(o => o.Reason).HasColumnName("reason").HasMaxLength(512).IsRequired();
        builder.Property(o => o.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(o => o.LocationId).HasColumnName("location_id");

        builder.HasIndex(o => new { o.UserId, o.PermissionCode })
            .HasDatabaseName("ix_user_permission_override_user");

        builder.HasOne<PermissionRecord>()
            .WithMany()
            .HasForeignKey(o => o.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps user-to-location assignments.</summary>
public sealed class UserLocationAssignmentConfiguration : IEntityTypeConfiguration<UserLocationAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserLocationAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_location", PosDbContext.CoreSchema);

        builder.HasKey(a => new { a.UserId, a.LocationId });

        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.LocationId).HasColumnName("location_id");
        builder.Property(a => a.IsPrimary).HasColumnName("is_primary").IsRequired();
        builder.Property(a => a.AssignedAtUtc).HasColumnName("assigned_at_utc").IsRequired();
        builder.Property(a => a.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired();

        builder.HasIndex(a => a.UserId).HasDatabaseName("ix_user_location_user");
    }
}

/// <summary>Maps the single-row authorization policy version.</summary>
public sealed class AuthorizationPolicyVersionConfiguration : IEntityTypeConfiguration<AuthorizationPolicyVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuthorizationPolicyVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "policy_version",
            PosDbContext.CoreSchema,
            t => t.HasCheckConstraint("ck_policy_version_singleton", "id = 1"));

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.Version).HasColumnName("version").IsRequired();
        builder.Property(v => v.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(v => v.LastChangeReason).HasColumnName("last_change_reason").HasMaxLength(256);
    }
}

/// <summary>Maps refresh tokens.</summary>
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_token", PosDbContext.CoreSchema);

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.FamilyId).HasColumnName("family_id").IsRequired();
        builder.Property(t => t.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(t => t.DeviceId).HasColumnName("device_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(t => t.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
        builder.Property(t => t.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(t => t.IssuedToIpAddress).HasColumnName("issued_to_ip").HasMaxLength(64);
        builder.Property(t => t.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(t => t.RevocationReason).HasColumnName("revocation_reason").HasConversion<short>().IsRequired();
        builder.Property(t => t.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
        builder.Property(t => t.ReuseDetected).HasColumnName("reuse_detected").IsRequired();

        // The lookup on every refresh. Unique, so a hash collision or a replayed
        // insert surfaces as an error rather than an ambiguous match.
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_token_hash");

        // Revoking a whole family on reuse detection reads by this.
        builder.HasIndex(t => t.FamilyId).HasDatabaseName("ix_refresh_token_family");

        builder.HasIndex(t => new { t.UserId, t.ExpiresAtUtc }).HasDatabaseName("ix_refresh_token_user");

        builder.Ignore(t => t.WasRotated);
    }
}

/// <summary>Maps the sign-in attempt history.</summary>
public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("login_attempt", PosDbContext.CoreSchema);

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.IdentifierHash).HasColumnName("identifier_hash").IsRequired();
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.DeviceId).HasColumnName("device_id");
        builder.Property(a => a.Method).HasColumnName("method").HasConversion<short>().IsRequired();
        builder.Property(a => a.Succeeded).HasColumnName("succeeded").IsRequired();
        builder.Property(a => a.FailureReason).HasColumnName("failure_reason").HasConversion<short>().IsRequired();
        builder.Property(a => a.AttemptedAtUtc).HasColumnName("attempted_at_utc").IsRequired();
        builder.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasColumnName("user_agent").HasMaxLength(512);

        // Throttling counts failures two ways: per account and per address, so
        // an attacker cannot lock a cashier out by guessing at their account.
        builder.HasIndex(a => new { a.IdentifierHash, a.AttemptedAtUtc })
            .HasDatabaseName("ix_login_attempt_identifier");

        builder.HasIndex(a => new { a.IpAddress, a.AttemptedAtUtc })
            .HasDatabaseName("ix_login_attempt_ip");
    }
}
