using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MasterDataCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "brand",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "location",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system_created = table.Column<bool>(type: "boolean", nullable: false),
                    opened_on = table.Column<DateOnly>(type: "date", nullable: false),
                    closed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settings_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organization",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    default_time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tax_settings_json = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    is_vat_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    default_purchase_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tracks_batches = table.Column<bool>(type: "boolean", nullable: false),
                    tracks_expiry = table.Column<bool>(type: "boolean", nullable: false),
                    shelf_life_days = table.Column<int>(type: "integer", nullable: true),
                    image_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    discontinued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_category",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_category", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_category_product_category_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "catalog",
                        principalTable: "product_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    payment_terms_days = table.Column<int>(type: "integer", nullable: false),
                    lead_time_days = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "unit_of_measure",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    decimal_places = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unit_of_measure", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_barcode",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    barcode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    symbology = table.Column<short>(type: "smallint", nullable: false),
                    uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_barcode", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_barcode_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_location_setting",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_stocked = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_stock = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    reorder_point = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    target_stock = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    maximum_stock = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    preferred_replenishment_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_location_setting", x => x.id);
                    table.CheckConstraint("ck_product_location_thresholds", "minimum_stock <= reorder_point AND reorder_point <= target_stock AND target_stock <= maximum_stock");
                    table.ForeignKey(
                        name: "FK_product_location_setting_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_price",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    effective_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_price", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_price_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_supplier",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    lead_time_days = table.Column<int>(type: "integer", nullable: false),
                    minimum_order_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    is_preferred = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_supplier", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_supplier_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_unit_conversion",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    factor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_unit_conversion", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_unit_conversion_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_brand_name",
                schema: "catalog",
                table: "brand",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_location_active",
                schema: "core",
                table: "location",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_location_kind",
                schema: "core",
                table: "location",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ux_location_org_code",
                schema: "core",
                table: "location",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_location_single_main",
                schema: "core",
                table: "location",
                column: "organization_id",
                filter: "\"kind\" = 0 AND \"is_active\"");

            migrationBuilder.CreateIndex(
                name: "ix_product_category",
                schema: "catalog",
                table: "product",
                column: "category_id",
                filter: "\"is_active\"");

            migrationBuilder.CreateIndex(
                name: "ix_product_name",
                schema: "catalog",
                table: "product",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ux_product_sku",
                schema: "catalog",
                table: "product",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_barcode_one_primary",
                schema: "catalog",
                table: "product_barcode",
                columns: new[] { "product_id", "is_primary" },
                unique: true,
                filter: "\"is_primary\"");

            migrationBuilder.CreateIndex(
                name: "ux_product_barcode_value",
                schema: "catalog",
                table: "product_barcode",
                column: "barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_category_parent_id",
                schema: "catalog",
                table: "product_category",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_category_code",
                schema: "catalog",
                table: "product_category",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_location",
                schema: "catalog",
                table: "product_location_setting",
                columns: new[] { "product_id", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_price_product_id",
                schema: "catalog",
                table: "product_price",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_supplier_preferred",
                schema: "catalog",
                table: "product_supplier",
                column: "is_preferred",
                filter: "\"is_preferred\"");

            migrationBuilder.CreateIndex(
                name: "ux_product_supplier",
                schema: "catalog",
                table: "product_supplier",
                columns: new[] { "product_id", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_unit_conversion",
                schema: "catalog",
                table: "product_unit_conversion",
                columns: new[] { "product_id", "from_uom_id", "to_uom_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_supplier_code",
                schema: "catalog",
                table: "supplier",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_uom_code",
                schema: "catalog",
                table: "unit_of_measure",
                column: "code",
                unique: true);

            // No two price rows for the same product and scope may overlap in time.
            // The btree_gist and pg_trgm extensions back the exclusion and trigram
            // indexes; see docs/DATABASE.md.
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS btree_gist;
                CREATE EXTENSION IF NOT EXISTS pg_trgm;

                ALTER TABLE catalog.product_price
                    ADD CONSTRAINT ex_product_price_no_overlap
                    EXCLUDE USING gist (
                        product_id WITH =,
                        coalesce(location_id, '00000000-0000-0000-0000-000000000000'::uuid) WITH =,
                        tstzrange(effective_from_utc, effective_to_utc) WITH &&);

                CREATE INDEX ix_product_name_trgm
                    ON catalog.product
                    USING gin (name gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS catalog.ix_product_name_trgm;
                ALTER TABLE catalog.product_price DROP CONSTRAINT IF EXISTS ex_product_price_no_overlap;
                """);

            migrationBuilder.DropTable(
                name: "brand",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "location",
                schema: "core");

            migrationBuilder.DropTable(
                name: "organization",
                schema: "core");

            migrationBuilder.DropTable(
                name: "product_barcode",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_category",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_location_setting",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_price",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_supplier",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_unit_conversion",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "supplier",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "unit_of_measure",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product",
                schema: "catalog");
        }
    }
}
