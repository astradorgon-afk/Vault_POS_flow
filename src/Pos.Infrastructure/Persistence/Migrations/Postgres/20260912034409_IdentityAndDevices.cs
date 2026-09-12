using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class IdentityAndDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "app_role",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_system_role = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "app_user",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    employee_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    pin_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    pin_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_tier = table.Column<short>(type: "smallint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_login_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SecurityStamp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_role_snapshot = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    previous_value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    new_value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    reference_document_type = table.Column<short>(type: "smallint", nullable: true),
                    reference_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    short_code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<short>(type: "smallint", nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    os_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clock_skew_seconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    public_key_thumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    status_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_attempt",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    identifier_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    method = table.Column<short>(type: "smallint", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    failure_reason = table.Column<short>(type: "smallint", nullable: false),
                    attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempt", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permission",
                schema: "core",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_offline_capable = table.Column<bool>(type: "boolean", nullable: false),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "policy_version",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_change_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_version", x => x.id);
                    table.CheckConstraint("ck_policy_version_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "refresh_token",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_to_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<short>(type: "smallint", nullable: false),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reuse_detected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_token", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_location",
                schema: "core",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_location", x => new { x.user_id, x.location_id });
                });

            migrationBuilder.CreateTable(
                name: "app_role_claim",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClaimValue = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_role_claim", x => x.Id);
                    table.ForeignKey(
                        name: "FK_app_role_claim_app_role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "core",
                        principalTable: "app_role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_user_claim",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClaimValue = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user_claim", x => x.Id);
                    table.ForeignKey(
                        name: "FK_app_user_claim_app_user_UserId",
                        column: x => x.UserId,
                        principalSchema: "core",
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_user_login",
                schema: "core",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user_login", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_app_user_login_app_user_UserId",
                        column: x => x.UserId,
                        principalSchema: "core",
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_user_role",
                schema: "core",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user_role", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_app_user_role_app_role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "core",
                        principalTable: "app_role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_user_role_app_user_UserId",
                        column: x => x.UserId,
                        principalSchema: "core",
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_user_token",
                schema: "core",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user_token", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_app_user_token_app_user_UserId",
                        column: x => x.UserId,
                        principalSchema: "core",
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_enrolment_code",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    redeemed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    is_burned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_enrolment_code", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_enrolment_code_device_device_id",
                        column: x => x.device_id,
                        principalSchema: "core",
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_session",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refresh_token_family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_session", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_session_device_device_id",
                        column: x => x.device_id,
                        principalSchema: "core",
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permission",
                schema: "core",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    granted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permission", x => new { x.role_id, x.permission_code });
                    table.ForeignKey(
                        name: "FK_role_permission_app_role_role_id",
                        column: x => x.role_id,
                        principalSchema: "core",
                        principalTable: "app_role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_permission_permission_permission_code",
                        column: x => x.permission_code,
                        principalSchema: "core",
                        principalTable: "permission",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_permission_override",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effect = table.Column<short>(type: "smallint", nullable: false),
                    granted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_permission_override", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_permission_override_permission_permission_code",
                        column: x => x.permission_code,
                        principalSchema: "core",
                        principalTable: "permission",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "core",
                table: "app_role",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_role_claim_RoleId",
                schema: "core",
                table: "app_role_claim",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "core",
                table: "app_user",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "core",
                table: "app_user",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_app_user_employee_code",
                schema: "core",
                table: "app_user",
                column: "employee_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_user_claim_UserId",
                schema: "core",
                table: "app_user_claim",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_app_user_login_UserId",
                schema: "core",
                table: "app_user_login",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_app_user_role_RoleId",
                schema: "core",
                table: "app_user_role",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_action",
                schema: "audit",
                table: "audit_log",
                columns: new[] { "action", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_correlation",
                schema: "audit",
                table: "audit_log",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity",
                schema: "audit",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_user",
                schema: "audit",
                table: "audit_log",
                columns: new[] { "user_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_device_location_status",
                schema: "core",
                table: "device",
                columns: new[] { "location_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_device_short_code",
                schema: "core",
                table: "device",
                column: "short_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_enrolment_code_device",
                schema: "core",
                table: "device_enrolment_code",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_device_enrolment_code_hash",
                schema: "core",
                table: "device_enrolment_code",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_session_device",
                schema: "core",
                table: "device_session",
                columns: new[] { "device_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_device_session_family",
                schema: "core",
                table: "device_session",
                column: "refresh_token_family_id");

            migrationBuilder.CreateIndex(
                name: "ix_login_attempt_identifier",
                schema: "core",
                table: "login_attempt",
                columns: new[] { "identifier_hash", "attempted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_login_attempt_ip",
                schema: "core",
                table: "login_attempt",
                columns: new[] { "ip_address", "attempted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_permission_module",
                schema: "core",
                table: "permission",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_family",
                schema: "core",
                table: "refresh_token",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_user",
                schema: "core",
                table: "refresh_token",
                columns: new[] { "user_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_refresh_token_hash",
                schema: "core",
                table: "refresh_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permission_permission_code",
                schema: "core",
                table: "role_permission",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "ix_user_location_user",
                schema: "core",
                table: "user_location",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_permission_override_permission_code",
                schema: "core",
                table: "user_permission_override",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "ix_user_permission_override_user",
                schema: "core",
                table: "user_permission_override",
                columns: new[] { "user_id", "permission_code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_role_claim",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_user_claim",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_user_login",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_user_role",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_user_token",
                schema: "core");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "device_enrolment_code",
                schema: "core");

            migrationBuilder.DropTable(
                name: "device_session",
                schema: "core");

            migrationBuilder.DropTable(
                name: "login_attempt",
                schema: "core");

            migrationBuilder.DropTable(
                name: "policy_version",
                schema: "core");

            migrationBuilder.DropTable(
                name: "refresh_token",
                schema: "core");

            migrationBuilder.DropTable(
                name: "role_permission",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_location",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_permission_override",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_user",
                schema: "core");

            migrationBuilder.DropTable(
                name: "device",
                schema: "core");

            migrationBuilder.DropTable(
                name: "app_role",
                schema: "core");

            migrationBuilder.DropTable(
                name: "permission",
                schema: "core");
        }
    }
}
