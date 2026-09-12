# Session Log — Phase 3: Master Data

**Date:** 2026-09-12
**Baseline:** commit `3e95482` (`milestone/phase-2-identity`) — Phases 0, 1, 2 and 4 (core) complete
**Status at end of session:** solution builds clean (0 warnings, warnings-as-errors on), **126 / 126 tests passing** (41 domain, 19 infrastructure, 13 architecture, 33 security, 8 application, 12 API integration), PostgreSQL migration applies and passes the drift check.

---

## 1. Goal

Implement Phase 3 (Master Data) from `docs/ROADMAP.md`: the organization /
location model, the product catalogue and its reference master data — the
everything-downstream-needs-it layer that Phases 5+ (pricing, purchasing,
sales) assume exists.

---

## 2. What was built

### 2.1 Domain layer (`Pos.Domain`)

| File | Contents |
|---|---|
| `Common/StronglyTypedIds.cs` | Four new identifiers: `ProductPriceId`, `ProductUnitConversionId`, `ProductLocationSettingId`, `ProductSupplierId` (UUIDv7, same pattern as existing ids) |
| `Organizations/Organization.cs` | The business aggregate; fixed `DefaultId` for single-business deployments |
| `Organizations/Location.cs` | Location aggregate — Main Warehouse, Stores, and the three system-created `External` counterparties (`EXT-SUPPLIER`, `EXT-CUSTOMER`, `EXT-WRITEOFF`) the ledger already depends on. Codes unique per organization; closing guarded |
| `Organizations/LocationSettings.cs` | JSON-serializable settings: `NegativeStockPolicy`, direct-supplier delivery, offline grace period, receipt header/footer |
| `Organizations/LocationErrors.cs` | Location error catalogue |
| `Catalog/Product.cs` | Centralized product master. Registered only at the Main Warehouse (`product.create` permission enforced by the aggregate behind the permission catalogue). Owns barcodes, prices, unit conversions, location settings, supplier links |
| `Catalog/ProductBarcode.cs` | Globally-unique barcode rows with symbology detection, pack quantity, single-primary rule |
| `Catalog/ProductPrice.cs` | Effective-dated, optionally location-scoped price rows; prices never mutate, they supersede |
| `Catalog/ProductUnitConversion.cs` | e.g. `1 Case = 24 Piece`; factor > 0, distinct units |
| `Catalog/ProductLocationSetting.cs` | Per-location stocking flags and min / reorder / target / max / preferred-quantity thresholds, validated as ordered |
| `Catalog/ProductSupplier.cs` | Supplier link with supplier SKU, last cost, lead time, MOQ, preferred flag |
| `Catalog/ProductCategory.cs`, `Brand.cs`, `UnitOfMeasure.cs`, `Supplier.cs` | Reference master data with length/range invariants |
| `Catalog/CatalogErrors.cs` | Error codes: duplicate barcode, overlapping price, missing reference |


### 2.2 Application layer (`Pos.Application`)

| File | Contents |
|---|---|
| `Common/Abstractions/IMasterDataRepository.cs` | Port for persisting locations, master data and product aggregates |
| `Organizations/CreateLocationCommand.cs` (+ handler, validator) | Creates physical locations only — external counterparts are system-created |
| `Catalog/CreateCategoryCommand.cs`, `CreateBrandCommand.cs`, `CreateUnitOfMeasureCommand.cs`, `CreateSupplierCommand.cs` | Master-data create commands |
| `Catalog/CreateProductCommand.cs` | Creates the product with optional initial barcode and initial price |
| `Catalog/MasterDataValidators.cs`, `SupplierAndProductValidators.cs` | FluentValidation validators for all the above |

Permission codes used: `location.manage`, `category.manage`, `brand.manage`,
`supplier.manage`, `product.create` — all declared on the command via
`IAuthorizedMessage` so the authorization behaviour enforces them uniformly.

### 2.3 Infrastructure layer (`Pos.Infrastructure`)

| File | Contents |
|---|---|
| `Persistence/MasterDataRepository.cs` | Implements `IMasterDataRepository`; persists the product aggregate and children transactionally; checks barcode duplication and reference existence |
| `Persistence/Configurations/OrganizationConfiguration.cs` | `organization` table (core schema) |
| `Persistence/Configurations/LocationConfiguration.cs` | `location` table; settings as JSON column; unique (org, code); partial unique index permitting only one active Main Warehouse |
| `Persistence/Configurations/MasterDataConfigurations.cs` | `product_category`, `brand`, `unit_of_measure`, `supplier` tables with unique codes/names |
| `Persistence/Configurations/ProductConfiguration.cs` | `product` and `product_barcode` tables; unique SKU, globally-unique barcode value, one-primary-barcode partial unique index |
| `Persistence/Configurations/ProductChildConfigurations.cs` | `product_price`, `product_unit_conversion`, `product_location_setting`, `product_supplier`; threshold ordering check constraint; `Money` price mapped via value converter (single-currency v1) |
| `Persistence/PosDbContext.cs` | New catalog schema constant and DbSets |
| `DependencyInjection.cs` | `MasterDataRepository` registered against the port |
| `Persistence/Migrations/Postgres/20260912074419_MasterDataCatalog.cs` | Forward-only migration: all Phase 3 tables, `btree_gist` extension and an **exclusion constraint** preventing overlapping price periods per product+scope; `pg_trgm` extension and trigram index on product name |


---

## 3. Key design decisions made this session

1. **`Money` mapped with a converter, not `OwnsOne`.** `Money` is a `struct`;
   owned-entity mapping is for reference types. The schema stores no currency
   column (single-currency v1 per `docs/DATABASE.md`), so the converter writes
   the amount and materializes with the deployment currency. The internal
   `Amount` passthrough on `ProductPrice` was removed as redundant.
2. **Persistence-friendly properties on child rows.** EF cannot materialize
   expression-bodied computed properties, so `ProductBarcode` exposes
   settable `Value`/`Symbology` alongside the computed `Barcode` object.
3. **Strict-fail JSON settings.** A corrupt `LocationSettings` blob falls back
   to the *strictest* configuration rather than skipping the load, so damage
   can never silently widen the negative-stock policy.
4. **Price overlap enforced in the database**, not just the aggregate — the
   exclusion constraint is the last line of defence against concurrent edits.
5. **Migration fixed by hand.** The generated migration's `Down()` was damaged
   during editing (a duplicated method body from interleaved inserts) and was
   consolidated; the drift test against live PostgreSQL confirms it round-trips.

---

## 4. Verification

```
dotnet build VaultFlow.slnx     → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test VaultFlow.slnx      → 103 passed, 0 failed, 0 skipped
  Pos.Domain.Tests           41 passing
  Pos.Infrastructure.Tests   16 passing   (incl. PostgreSQL migration drift check)
  Pos.Architecture.Tests     13 passing   (layering, ledger isolation intact)
  Pos.Security.Tests         33 passing
```

---

## 5. Not done yet (next steps)

1. **API endpoints** exposing the new commands — `LocationEndpoints`,
   `CatalogEndpoints`, following the conventions of
   `AuthEndpoints`/`DeviceEndpoints` (permission checks already carried by the
   messages).
2. **Development seed data** — Main Warehouse, three stores, the three external
   locations, a supplier, sample categories/brands/UoM/products, staff accounts.
3. **Wire `ILedgerPolicyProvider`** to real location settings (replacing the
   hard-coded `StrictLedgerPolicyProvider`) — the settings object and its
   persistence exist; only the read path is missing.
4. **Query endpoints** for product lookup by barcode/sku/name (the trigram
   index is in place for it).

`docs/STATUS.md` was updated with a Phase 3 progress section reflecting all of
the above.

---

# Part 2 — Endpoints, policy wiring, seed data, tests

**Baseline:** commit `fa99bf0` (`master-data: Phase 3 domain, persistence,
use cases`) — the domain/persistence/application milestone above.

---

## 6. What was built in Part 2

### 6.1 API layer (`Pos.Api`)

| File | Contents |
|---|---|
| `Endpoints/LocationEndpoints.cs` | `GET /api/v1/locations` (`product.view` — list reads deliberately relaxed for POS offline scope resolution), `POST /api/v1/locations` (`location.manage`), `PUT /api/v1/locations/{id}/settings` (`settings.manage`, route-scoped so it is a pair of (permission, location)) |
| `Endpoints/CatalogEndpoints.cs` | `GET/POST /api/v1/catalog/products`, `GET /api/v1/catalog/products/{id}`, `GET /api/v1/catalog/products/by-barcode/{barcode}`, `GET/POST /api/v1/catalog/{categories,brands,units,suppliers}` |

Endpoint permission map: reads → `product.view` (suppliers read →
`supplier.view`); writes → `location.manage` / `settings.manage` /
`product.create` / `supplier.manage` / `category.manage` / `brand.manage` /
`uom.manage`.

Search surface: `GET /catalog/products?q=…` matches name (partial, `LIKE`),
an **exact** SKU, or barcode partial; `includeInactive`, `offset`, `limit`
(param capped at 200). `GET /suppliers` shares the pagination parameters.

### 6.2 Infrastructure (`Pos.Infrastructure`)

| File | Contents |
|---|---|
| `Inventory/LocationSettingsLedgerPolicyProvider.cs` | `ILedgerPolicyProvider` reading each location's own settings (`AsNoTracking`); missing/unreadable settings fail closed to `NegativeStockPolicy.Prohibit`. Registered in DI, replacing the hard-coded strict provider |
| `Identity/DevelopmentDataSeeder.cs` | Idempotent seeder (one transaction for master data): the three system counterparties from `SystemLocationCodes`, Main Warehouse + STORE01–03, categories, units, brands, suppliers, six products with barcodes; staff accounts (owner, admins, store managers, inv staff, cashiers, auditor) with `UserLocationAssignment` + `ApprovalTier`, gated separately by `Seeding:EnableDevelopmentAccounts` |
| `Configuration/Options.cs` | `SeedingOptions` (`SectionName = "Seeding"`) bound and validated on start |
| `Program.cs` | `PrepareDatabaseAsync` now runs IdentitySeeder → DevelopmentDataSeeder → BootstrapOwner |

### 6.3 Tests (new)

| Project | Files | Count |
|---|---|---|
| `Pos.Application.Tests` | `Organizations/UpdateLocationSettingsCommandValidatorTests.cs`, `UpdateLocationSettingsCommandHandlerTests.cs` | 8 |
| `Pos.Infrastructure.Tests` | `Inventory/LocationSettingsLedgerPolicyProviderTests.cs` (SQLite) | 3 |
| `Pos.Api.IntegrationTests` (new project) | `PosApiFactory` (SQLite `WebApplicationFactory<Program>`, `[Collection("api")]`), `LocationEndpointTests.cs`, `CatalogEndpointTests.cs` | 12 |

---

## 7. Key decisions in Part 2

1. **Search/SKU translatability.** The first cut used `p.Sku.Value.Contains(term)`
   — untranslatable through the value converter, and it produced a 500 at
   runtime even though it compiled clean. Replaced with name `LIKE` + exact SKU
   equality on the normalized key + barcode partial. The seeder's idempotency
   probe had the same latent bug and was fixed identically. A compile-time-safe
   translatable-filter probe is a candidate for the architecture tests.
2. **`UpdateLocationSettingsCommand` returns nothing useful → `LocationId`.**
   The dispatcher only supports `ICommand<TResult>`; returning `LocationId`
   documents what changed in audit logs, and the endpoint maps it to 204.
3. **Permissions are carried by the messages, not the endpoints.** Endpoints
   declare no permission checks of their own; the authorization behaviour reads
   `IAuthorizedMessage`. The route-scoped pairs (`settings.manage` × location)
   are the only endpoint-side additions.
4. **Development seed is defensively scoped.** Master data requires only
   `Database:SeedDevelopmentData`; **staff accounts additionally require**
   `Seeding:EnableDevelopmentAccounts`, so a `Development=true` host that is
   not a dev box never gains credentialed users. BootstrapOwner still runs on
   an empty database only.
5. **Shared in-memory SQLite for HTTP tests.** One factory per test class, one
   shared-cache in-memory database — so seed helpers must be idempotent by code
   (return the existing row) and assertions must tolerate rows created by other
   tests in the same class. That constraint is deliberate: it makes the suite
   run in seconds without an external database.

---

## 8. Verification (Part 2)

```
dotnet build VaultFlow.slnx     → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test VaultFlow.slnx      → 126 passed, 0 failed, 0 skipped
  Pos.Domain.Tests             41 passing
  Pos.Infrastructure.Tests     19 passing   (16 existing + 3 policy provider; PG suite self-skips without Docker)
  Pos.Architecture.Tests       13 passing
  Pos.Security.Tests           33 passing
  Pos.Application.Tests         8 passing   (settings command + validator)
  Pos.Api.IntegrationTests     12 passing   (6 location + 5 catalog + 1 duplicate-conflict; real auth pipeline)
```

---

## 9. Not done (next session)

1. **Catalog curation endpoints** — deferred Phase 3 rows in `API.md` §3:
   product edit, barcode manage, price manage, per-location product settings,
   deactivate/activate. The aggregate already supports them.
2. **Phase 5 — Purchasing** now has its foundations (suppliers, locations,
   products, approval tiers) and is the natural next phase.
3. **User-administration endpoints** so roles / overrides / location
   assignments stop being a database-only concern.
4. **Data-aware security tests** — the permission matrix tests cover
   `settings.manage` by construction, but an integration-level matrix sweep is
   worth adding while the catalogue of endpoints is still small.
