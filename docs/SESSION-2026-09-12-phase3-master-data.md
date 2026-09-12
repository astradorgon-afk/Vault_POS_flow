# Session Log — Phase 3: Master Data

**Date:** 2026-09-12
**Baseline:** commit `3e95482` (`milestone/phase-2-identity`) — Phases 0, 1, 2 and 4 (core) complete
**Status at end of session:** solution builds clean (0 warnings, warnings-as-errors on), **103 / 103 tests passing**, PostgreSQL migration applies and passes the drift check.

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
