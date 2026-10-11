# VaultFlow Migration Audit — Executive Summary

> **For external review.** This summary contains verified VaultFlow-specific findings with file references. The full audit is in `VAULTFLOW_MIGRATION_AUDIT.md`.
>
> **Date:** 2026-10-12 · **Scope:** Full repository static inspection · **Verdict:** Migration is feasible with significant effort.

---

## 1. Verified Current Architecture and Project Map

VaultFlow is a **Clean Architecture** solution on **.NET 10** (`net10.0`, SDK `10.0.301` pinned in `global.json`) with **ASP.NET Core 10** and **EF Core 10**. It runs on **PostgreSQL 17** as the authoritative database, with **SQLite/SQLCipher** for the offline MAUI device store.

**9 source projects:**

| Project | Technology | Responsibility |
|---|---|---|
| `src/Pos.Domain` | C#, no dependencies | Entities, value objects, invariants, state machines (91 files) |
| `src/Pos.Shared` | C#, no dependencies | Wire contracts, DTOs, sync envelopes (8 files) |
| `src/Pos.Application` | C# | Use cases (commands/queries), ports, validators, pipeline (109 files) |
| `src/Pos.Infrastructure` | C# + EF Core 10 + Npgsql + SQLCipher | PostgreSQL/SQLite persistence, identity, sync engine, ledger (204 files) |
| `src/Pos.Api` | ASP.NET Core 10 + SignalR | HTTP endpoints, JWT auth, authorization, rate limiting (48 files) |
| `src/Pos.Web` | Blazor interactive server | Owner/admin dashboard — 31 pages (161 files) |
| `src/Pos.Web.Client` | Blazor WebAssembly | Offline PWA — 1 mega-page (32 files) |
| `src/Pos.SharedUI` | Razor Class Library | ScanTarget, ScanFeedback, LoginSurface, keyboard wedge (12 files) |
| `src/Pos.Client` | .NET MAUI (Android + Windows) | Desktop/mobile POS with encrypted SQLite (94 files) — **marked for retirement** |

**8 test projects** (~1,144+ tests): Domain (378), Application (247), Infrastructure (245, Testcontainers.PostgreSql), API Integration (176), Sync (21), Security (52), Architecture (25, NetArchTest), Shared, plus Playwright browser E2E for the PWA.

**Hosting:** Docker Compose — `postgres:17-alpine`, one-shot `migrator` + `grants`, `api` (Dockerfile.api), `web` (Dockerfile.web), `caddy:2-alpine` reverse proxy. Production overlay adds the `web` service. No Redis. No external service clients beyond PostgreSQL and SQLite.

**Key evidence:** `VaultFlow.slnx:1-26`, `Directory.Packages.props:1-94`, `compose.yaml:1-133`, `render.yaml:1-62`.

---

## 2. Exact Online Frontend Framework

**Blazor Web App with interactive server render mode** (`src/Pos.Web`):

```csharp
// src/Pos.Web/Program.cs:15-16, 105-106
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```

- **31 Razor pages** under `src/Pos.Web/Components/Pages/`, each with `@page` route directives
- **Cookie authentication** for the Blazor circuit; JWT for API calls via typed `VaultFlowApiClient` (~80+ methods)
- **Custom CSS design system** (`app.css`, `inventory-office.css`, `purchasing.css`, `restock.css`, `web-workflow.css`); Bootstrap 5 CSS ships in `wwwroot/lib/bootstrap/` but custom CSS is primary
- **Fonts:** Manrope + DM Sans (OFL, WOFF2, bundled locally in `wwwroot/fonts/`)
- **SignalR** at `/hubs/notifications` for live updates (`NotificationStore.cs`)
- **Camera scanning:** ZXing WASM 3.1.5 (`wwwroot/lib/zxing-wasm/`)
- **State management:** Blazor server circuit + `UserSession` scoped service

**Business logic in frontend:** Primarily presentation logic (sorting, filtering, chart geometry). Business rules (validation, state transitions, ledger posting) are server-side.

**Status:** CONFIRMED — `src/Pos.Web/Program.cs`, `src/Pos.Web/Services/VaultFlowApiClient.cs`, `src/Pos.Web/Components/Pages/` (31 files).

---

## 3. Exact Offline PWA Implementation

**Blazor WebAssembly 10** (`src/Pos.Web.Client`), served under `/offline/` by `Pos.Web`:

```xml
<!-- src/Pos.Web.Client/Pos.Web.Client.csproj:2 -->
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
```

```csharp
// src/Pos.Web.Client/Pos.Web.Client.csproj:8-9
<ServiceWorkerAssetsManifest>service-worker-assets.js</ServiceWorkerAssetsManifest>
<StaticWebAssetBasePath>offline</StaticWebAssetBasePath>
```

- **1 mega-page** (`Pages/Home.razor`) with routes `/`, `/login`, `/offline`, `/purchasing`, `/receiving`, `/inventory/stock`, `/restock`, `/stores`, `/sync`, `/team` — renders different sections based on a `section` variable
- **Fallback route:** `app.MapFallbackToFile("/offline/{*path:nonfile}", "offline/index.html")` in `Pos.Web/Program.cs:104`
- **Entry point:** `wwwroot/index.html` → `pwa-boot.js` registers `/service-worker.js` → Blazor WASM startup
- **Service workers:** Root worker (`Pos.Web/wwwroot/service-worker.js`) caches app shell in versioned Cache Storage (`vaultflow-root-shell-{version}`) with navigation fallback; published client worker (`Pos.Web.Client/wwwroot/service-worker.published.js`) is an upgrade path for legacy `/offline/` installations; development worker is empty (no caching)

**Status:** CONFIRMED — `src/Pos.Web.Client/Pos.Web.Client.csproj:1-25`, `src/Pos.Web.Client/wwwroot/index.html:1-64`, `src/Pos.Web/wwwroot/service-worker.js:1-92`, `src/Pos.Web.Client/wwwroot/service-worker.published.js:1-33`.

---

## 4. Differences Between Online and Offline Builds

| Aspect | Online Web (`Pos.Web`) | Offline PWA (`Pos.Web.Client`) |
|---|---|---|
| Rendering | Interactive server (Blazor Server) | WebAssembly (client-side) |
| Authentication | Cookie + JWT via API | JWT via same-origin gateway (`/offline/api/*`) |
| Data access | Live API calls | IndexedDB encrypted snapshot |
| Features | Full (31 pages) | Limited (3 workflows + read-only inventory) |
| Real-time | SignalR live updates | None; manual sync on reconnect |
| Shared components | `Pos.SharedUI` (scanning, login surface) | `Pos.SharedUI` (login surface) |
| CSS | `app.css` + page-specific | `offline.css` + `workspace.css` |
| JavaScript | `camera-scanner.js`, `scan-audio.js`, `receipt-print.js`, `root-pwa-register.js` | `offline-store.js`, `pwa-boot.js`, `pwa-install.js`, `camera-scanner.js`, `scan-audio.js` |

**Duplicated:** `camera-scanner.js`, `scan-audio.js`, `zxing-wasm/` (identical in both projects). `app.css` is shared.

**Same origin:** Both served from the same domain; the PWA accesses the API through `PwaGateway.cs` which proxies `/offline/api/*` → `/api/v1/*` with credentials held server-side.

**Status:** CONFIRMED — `src/Pos.Web/Pwa/PwaGateway.cs:18-32`, `src/Pos.Web.Client/wwwroot/js/` (5 JS files).

---

## 5. Existing Local Storage and Encryption Implementations

**IndexedDB** (`vaultflow-pwa-v1`) with 4 object stores (`src/Pos.Web.Client/wwwroot/js/offline-store.js:8-24`):

| Store | Purpose | Key |
|---|---|---|
| `meta` | Device info, sequence counter, cursor positions | String keys |
| `profiles` | Employee profiles with encrypted snapshots | `scope` = `{userId}:{locationId}` |
| `data` | Encrypted working forms, snapshots, operation payloads | `{scope}:snapshot`, `{scope}:working:{kind}`, `{scope}:operation:{eventId}` |
| `outbox` | Pending sync events | `eventId` (UUID), indexed by `scope` and `sequence` |

**Encryption** (`offline-store.js:52-75`):
- **Algorithm:** AES-GCM 256-bit
- **Key derivation:** PBKDF2-SHA256, 250,000 iterations, 16-byte random salt per profile
- **Key storage:** Derived from password/PIN at unlock; held in memory only (`active.key`)
- **Verifier:** Encrypted scope string stored in profile to verify password correctness
- **Re-wrap:** When password changes, all data decrypted and re-encrypted with new key (`offline-store.js:225-244`)
- **Profile expiry:** 7 days (`maxOfflineAge`, `offline-store.js:2`)

**Device enrolment** (`offline-store.js:149-164`): ECDSA P-256 keypair generated; public key thumbprint sent to server; private key stored in IndexedDB `meta`.

**Status:** CONFIRMED — `src/Pos.Web.Client/wwwroot/js/offline-store.js:1-541` (all functions verified).

---

## 6. Offline Sync Architecture and Protocol Contracts

### Sync Flow

```
User creates operation → offline-store.js:queue() generates EventId + seals payload
→ Event stored in IndexedDB outbox with sequence number
→ User triggers sync → offline-store.js:synchronize() iterates outbox in order
→ POST /offline/api/push → PwaGateway.cs → POST /api/v1/sync/push
→ Server: SyncPushService verifies device binding, idempotency (EventId),
  sequence continuity, payload hash, re-authorizes, validates, applies, records outcome
→ Client updates local status (Confirmed/Conflict/Needs review/Saved on device)
→ After push, client pulls change feed: GET /offline/api/pull?cursor=N
→ Server returns changes since cursor (max 500)
→ If changes detected, download fresh snapshot
```

### Protocol Contracts (must be preserved)

**Push request** (`PwaGateway.cs:327-341` → `SyncPushService.cs:35-80`):
```json
{
  "deviceId": "...",
  "clientSentAtUtc": "...",
  "deviceUptimeTicks": 0,
  "events": [{
    "eventId": "uuid-v7",
    "deviceSequence": 1,
    "eventType": "PwaPurchaseDraft | PwaReceivingCount | PwaRestockDraft",
    "payload": { ... },
    "occurredAtUtc": "..."
  }]
}
```

**Pull request** (`SyncChangeLog.cs:79-140`):
```
GET /api/v1/sync/pull?cursor={cursor}&limit=500
→ { "changes": [...], "nextCursor": N }
→ HTTP 410 when cursor outside retained window (rebaseline signal)
```

**Baseline request** (`SyncBaselineService.cs:16-164`):
```
GET /api/v1/sync/baseline
→ { cursor, products, categories, units, suppliers, stockLevels, orders, downloadedAtUtc }
```

**Conflict taxonomy** (`SyncPushService.cs:891-939`): `Accepted`, `Duplicate`, `Rejected`, `RequiresReview`, `Conflict`, `Deferred`.

**Idempotency** (`SyncPushService.cs:152-214`): `sync.processed_event` table keyed by EventId; payload hash (SHA-256) verified; `UNIQUE (device_id, device_sequence)` constraint; different hash with same EventId → `sync.idempotency_key_reuse`.

**Offline event types** (`PwaGateway.cs:321`): `PwaPurchaseDraft`, `PwaReceivingCount`, `PwaRestockDraft`.

**MAUI outbox event types** (`OutboxEvent.cs:15-28`): `ShiftOpened`, `ShiftSuspended`, `ShiftResumed`, `SaleCompleted`.

**Status:** CONFIRMED — `src/Pos.Web/Pwa/PwaGateway.cs:312-344`, `src/Pos.Infrastructure/Sync/SyncPushService.cs:23-80`, `src/Pos.Web.Client/wwwroot/js/offline-store.js:485-535`, `tests/Pos.Sync.Tests/SyncPushServiceTests.cs` (21 tests).

---

## 7. Existing Backend Business Rules and Service Boundaries

**Clean Architecture layers** with inward-only dependencies (enforced by `Pos.Architecture.Tests`):

| Layer | Responsibility | Key Files |
|---|---|---|
| `Pos.Domain` | Invariants, state transitions, arithmetic | `InventoryMovementGroup.cs:119-186` (zero-sum, batch rules), `InventoryBalance.cs:107-147` (weighted avg cost), `Sale.cs:216-329` (VAT/totals), `Transfer.cs:443-1222` (12-status state machine), `PurchaseOrder.cs:207-552` (9-status state machine) |
| `Pos.Application` | Orchestration, re-derivation, ledger posting, audit | `CompleteSaleCommandHandler.cs:55-572`, `CreateSalesReturnCommandHandler.cs:49-338`, `ReceiveTransferCommandHandler.cs` |
| `Pos.Infrastructure` | Persistence, concurrency, ledger staging | `InventoryLedger.cs:76-293`, `BalanceReconciler.cs:35-174` |
| `Pos.Api` | HTTP endpoints, auth, authorization | `Program.cs:275-305` (~150+ endpoints) |

**CQRS-lite dispatcher** with ordered pipeline behaviors (`DependencyInjection.cs:32-36`):
1. `LoggingBehaviour` — correlation scope
2. `ValidationBehaviour` — FluentValidation
3. `AuthorizationBehaviour` — permission + location scope
4. `NegativeStockAttemptBehaviour` — flush refused draws
5. `UnitOfWorkBehaviour` — DB transaction + 5x concurrency retry

**Key business rules:**
- **Prime directive:** No code path outside the inventory ledger may set a stock quantity. Enforced in 4 layers (domain types, EF interceptor, DB triggers, DB roles).
- **Ledger posting:** `InventoryLedger.PostAsync` — idempotency check, structural validation, zero-sum validation, state-transition validation, authorization, availability validation, append, project, audit — all in one transaction.
- **Document numbering:** Atomic upsert on `core.document_counter` (`DocumentNumberGenerator.cs:88-95`). Device-scoped numbers embed device short code.
- **Approval tiers:** 5k/50k/250k PHP ceilings; self-approval refused; segregation of duties.
- **Offline command whitelist:** `OfflineCommandCatalogue.cs:78` — 3 Registered (shift open/suspend/resume), 28 Pending.
- **Permissions:** 83 permissions in `Permissions.cs`; 16 governance permissions; `IsOfflineCapable` flag on each.

**Status:** CONFIRMED — `src/Pos.Application/DependencyInjection.cs:32-36`, `src/Pos.Application/Common/Offline/OfflineCommandCatalogue.cs:78`, `src/Pos.Infrastructure/Inventory/InventoryLedger.cs:76-293`, `src/Pos.Application/Identity/Permissions.cs`.

---

## 8. PostgreSQL Schema and Ledger Structure

**PostgreSQL 17** (`postgres:17-alpine` in `compose.yaml:14`). **9 schemas:** `core`, `catalog`, `inventory`, `purchasing`, `transfers`, `quarantine`, `sales`, `sync`, `audit`.

### Core Ledger Tables

**`inventory.inventory_movement`** — append-only, immutable, double-entry ledger:
- Key columns: `id`, `event_id`, `movement_group_id`, `leg_number`, `product_id`, `batch_id`, `location_id`, `state`, `quantity_delta`, `unit_cost`, `total_value_delta`, `movement_type`, `reference_document_type/id/number`, `created_by_user_id`, `approved_by_user_id`, `device_id`, `occurred_at_utc`, `recorded_at_utc`, `business_date`, `reason_code`, `reverses_movement_group_id`, `change_sequence`
- Invariants: zero-sum per (product, batch); minimum 2 legs; no zero quantities; `total_value_delta = unit_cost * quantity_delta`; batch-tracked products require batch_id
- Global assertion: `SELECT SUM(quantity_delta) FROM inventory_movement = 0`

**`inventory.inventory_balance`** — materialized projection:
- PK: `(location_id, product_id, batch_key, state)`; `batch_key` = `uuid_nil()` when not batch-tracked
- Columns: `quantity`, `average_unit_cost`, `total_value`, `last_movement_id`, `last_movement_at_utc`, `version` (optimistic concurrency)
- Fully rebuildable from ledger via `BalanceReconciler`

### 4-Layer Immutability

1. **Domain:** `InventoryMovement` has init-only properties, no public constructor
2. **EF Core:** `AppendOnlyInterceptor.cs:51` rejects Modified/Deleted
3. **DB triggers:** `trg_inventory_movement_immutable`, `trg_inventory_balance_guard` (DEFERRABLE), `trg_inventory_balance_no_delete` — SQL in `Persistence/Sql/Postgres/01-06*.sql`
4. **DB roles:** `pos_app` has only `SELECT, INSERT` on ledger tables (`build/docker/initdb/02-grants.sql:50-70`)

### Sync Tables

- `sync.change_log` — append-only, `change_sequence` (bigint identity), `location_scope_id`
- `sync.processed_event` — idempotency inbox, PK `event_id`, unique `(device_id, device_sequence)`
- `sync.sync_checkpoint` — last accepted contiguous sequence per device
- `sync.sync_failure` — operator-visible failures with retry scheduling

### Other Key Tables

- `core.app_user` — ASP.NET Identity with PBKDF2-SHA512, 600k iterations
- `core.permission` / `role_permission` / `user_permission_override` — 83 permissions
- `core.refresh_token` — hashed, rotated, family reuse detection
- `core.device` / `device_enrolment_code` / `device_session` — device management
- `audit.audit_log` — append-only, immutable, written in same transaction
- `sales.*` — POS sales, shifts, returns, payments, customers (historical)

**EF Core mappings:** 36 PostgreSQL migrations, 8 SQLite device migrations, 29 entity configuration classes in `Persistence/Configurations/`. Provider-agnostic context with `IsSqlite` flag.

**Status:** CONFIRMED — `docs/DATABASE.md:1-623`, `docs/INVENTORY_LEDGER.md:1-450`, `src/Pos.Infrastructure/Persistence/PosDbContext.cs:29-373`, `src/Pos.Infrastructure/Persistence/Migrations/Postgres/` (36 files).

---

## 9. Authentication and Authorization Design

**Authentication:**
- **Online web:** Cookie authentication (`Pos.Web/Program.cs:33`)
- **API + PWA:** JWT RS256 Bearer (`Pos.Api/Program.cs:77-78`), 10-min access / 30-day refresh
- **Password hashing:** ASP.NET Identity v3, PBKDF2-HMAC-SHA512, **600,000 iterations**, 128-bit salt (`Options.cs:82`, `DependencyInjection.cs:396-399`)
- **Refresh token rotation:** Hashed (SHA-256) in DB; presenting a rotated token → entire family revoked (`AuthenticationService.cs:277-415`)
- **Device binding:** Token bound to `device_id`; must match `X-Device-Id` header
- **2FA:** TOTP; required for accounts holding `user.manage`/`role.manage`
- **Security stamp:** Changes on password change, role change, disable; invalidates all tokens
- **Policy version:** Bumps on any role/permission change; invalidates permission cache

**Authorization:**
- **83 permissions** in `Permissions.cs` across 8 modules (Catalog 10, Purchasing 8, Inventory 12, Transfer 12, Quarantine 5, Receipts 2, Sales 18, Administration 13)
- **Location scoping:** Every check is `(permission, location)` pair; `location.all` bypasses
- **Enforcement:** `[RequirePermission]` on every endpoint + `AuthorizationBehaviour` in pipeline (defense in depth)
- **Approval tiers:** 5k/50k/250k PHP; self-approval refused; segregation of duties
- **Governance:** 16 privileged permissions require holder to also possess
- **Offline-capable:** Marked with `IsOfflineCapable` flag; only these appear in device snapshots

**PWA gateway auth** (`PwaGateway.cs`):
- `PwaSessions` — in-memory server-side session store (ConcurrentDictionary, 7-day expiry)
- Cookie `vaultflow.pwa` — HttpOnly, Secure, SameSite=Strict, 7-day MaxAge
- `RefreshIfNeededAsync` — transparent token refresh before API calls
- `SafeMutation` — requires `X-VaultFlow-PWA: 1` header and same-origin check

**Status:** CONFIRMED — `src/Pos.Api/Program.cs:77-92`, `src/Pos.Api/Authorization/BearerTokenEvents.cs:29-133`, `src/Pos.Infrastructure/Identity/AuthenticationService.cs:277-415`, `src/Pos.Web/Pwa/PwaGateway.cs:18-134`.

---

## 10. Functionality to Preserve

**Must preserve (FULL parity):**
- All 31 online web pages and workflows
- Inventory ledger integrity (4-layer immutability)
- Sync protocol (EventId, DeviceSequence, payloadHash, conflict taxonomy)
- All 83 permissions and location scoping
- All domain invariants and state machines
- Authentication (with password hash compatibility)
- Audit logging (immutable, same-transaction)
- Document numbering (atomic, gapless)
- Balance reconciliation and rebuild
- Historical data (sales, purchasing, transfers, inventory movements)

**Must preserve (READ-ONLY):**
- POS sales history, shift history, payment history
- Customer accounts
- Receipt print logs
- Return/refund history

**Can be removed:**
- POS checkout workflow (new system has no POS)
- Shift management UI (historical data retained)
- Cash drawer management
- MAUI client (`Pos.Client`)
- `TestHttpClient/` (orphaned)
- `tools/Pos.DemoData/` (dev only)

**Status:** CONFIRMED — `WEBSITE_CLEANUP_FINDINGS.md:110-116`, `docs/STATUS.md:21-23`, `AGENTS.md:1-10`.

---

## 11. POS/Desktop/Mobile Components to Remove

**POS-specific (remove UI, preserve data):**
- `CompleteSaleCommandHandler`, `VoidSaleCommandHandler` — keep for historical reconciliation
- `ShiftEndpoints`, `CashierShift` — keep for historical data
- `TerminalEndpoints` — remove
- `cashdrawer.open_without_sale` permission — remove
- `Sales.razor`, `SaleDetail.razor` — remove from new frontend (keep read-only sales history if needed)
- `SalesReturn` workflow — keep (shared with inventory)

**MAUI client (`Pos.Client`):**
- 94 files of MAUI-specific code
- Android + Windows targets
- SQLCipher device database (MAUI-specific)
- `DeviceStatusBanner` component (MAUI-oriented)
- `OfflineCommandCatalogue` — must be preserved for PWA sync protocol

**Shared libraries to retire:**
- `Pos.SharedUI` — Blazor-specific components
- `Pos.Shared` — C# wire contracts (replaced by Laravel API resources + TS types)

**Status:** CONFIRMED — `src/Pos.Client/Pos.Client.csproj:1-78`, `src/Pos.SharedUI/` (12 files), `AGENTS.md:1-10`.

---

## 12. Components That Can Be Reused vs Rewritten

| Component | Action | Notes |
|---|---|---|
| PostgreSQL schema (all tables, triggers, roles) | **RETAIN** | Framework-agnostic; Laravel-compatible |
| DB triggers + grants | **RETAIN** | Database-level |
| `offline-store.js` (IndexedDB + crypto) | **ADAPT** | Extract from Blazor interop; framework-agnostic core |
| `camera-scanner.js` + `zxing-wasm/` | **RETAIN** | Framework-agnostic |
| CSS design tokens + fonts + icons | **RETAIN** | Standard CSS/WOFF2/SVG |
| Sync protocol contracts (REST API) | **RETAIN** | Framework-agnostic |
| Test fixtures and examples | **PORT** | Adapt to new frameworks |
| `Pos.Domain` (entities, invariants) | **REWRITE** | Port to PHP domain classes |
| `Pos.Application` (use cases) | **REWRITE** | Port to Laravel services |
| `Pos.Infrastructure` (EF Core) | **REWRITE** | Port to Laravel Eloquent + raw SQL |
| `Pos.Api` (endpoints) | **REWRITE** | Port to Laravel routes |
| Authentication | **REWRITE** | Custom PBKDF2-SHA512 hasher + Laravel Auth |
| Authorization | **REWRITE** | Port 83 permissions to Laravel gates/policies |
| SignalR notifications | **REWRITE** | Laravel Echo + Pusher/Ably/Reverb |
| Background workers | **REWRITE** | Laravel Workers |
| Online web frontend (31 pages) | **REWRITE** | Blazor Server → Vue 3 |
| Offline PWA | **REWRITE** | Blazor WASM → Vue 3 + Workbox |

**Status:** CONFIRMED — project inventory table above.

---

## 13. Feasibility of a Unified Laravel + Modern Frontend Architecture

**Yes, technically feasible.** A single **Vue 3 SPA** can replace both `Pos.Web` (Blazor Server) and `Pos.Web.Client` (Blazor WASM):

**Architecture:**
- Laravel 12 API backend (JWT auth, 83 permissions, sync engine, ledger services)
- Vue 3 SPA frontend (31 pages, component-based, Vue Router)
- Workbox service worker (app shell caching, offline fallback)
- IndexedDB + WebCrypto (encrypted snapshots + outbox — reuse `offline-store.js` pattern)
- Laravel Echo + Pusher/Ably/Reverb (real-time notifications, replacing SignalR)

**Advantages:**
- Eliminates dual-build complexity (Blazor Server + Blazor WASM)
- Single codebase for online and offline modes
- Modern, maintainable architecture with large ecosystem
- Laravel's Eloquent is compatible with existing PostgreSQL schema
- Laravel's migration system can manage schema changes
- Laravel's queue system replaces .NET BackgroundService

**Constraints:**
1. The sync engine must be reimplemented in Laravel with exact protocol parity
2. The 4-layer ledger immutability must be replicated (DB triggers + Laravel model observers)
3. The custom PBKDF2-SHA512 password hasher must verify existing .NET Identity hashes
4. All 83 permissions must be mapped exactly to Laravel gates/policies
5. The offline PWA must be rebuilt with equivalent functionality

**Status:** CONFIRMED — feasibility analysis in `VAULTFLOW_MIGRATION_AUDIT.md` Sections 8-9.

---

## 14. Recommended Migration Strategy and Major Risks

**Recommended strategy: Phased migration with temporary interoperability**

| Phase | Objective | Risk |
|---|---|---|
| 1. Database setup | Laravel + existing PostgreSQL | Low |
| 2. Domain port | PHP domain classes, invariants | Medium |
| 3. Auth + password hasher | Custom PBKDF2-SHA512 hasher | High |
| 4. Authorization | 83 permissions, gates, policies | High |
| 5. Catalog | Product, barcode, price CRUD | Medium |
| 6. Inventory ledger | 4-layer immutability, concurrency | **CRITICAL** |
| 7. Purchasing | PO lifecycle, goods receipts | High |
| 8. Transfers | Transfer lifecycle | High |
| 9. Sync engine | Push/pull/baseline, idempotency | **CRITICAL** |
| 10. Vue 3 SPA (online) | 31 pages, components | High |
| 11. Vue 3 PWA (offline) | Service worker, IndexedDB, sync | High |
| 12. Workers + notifications | Background processing | Medium |
| 13. POS removal | Remove UI, preserve data | Medium |
| 14. MAUI retirement | Remove `Pos.Client` | Low |

**Top 5 major risks:**

1. **Password hash compatibility** (CRITICAL) — .NET Identity PBKDF2-SHA512 600k iterations must verify under Laravel. Mitigation: custom hasher + test with real hashes before cutover.
2. **Ledger immutability replication** (CRITICAL) — 4-layer enforcement must be preserved. Mitigation: port DB triggers + add Laravel model observers + thorough testing.
3. **Sync protocol parity** (CRITICAL) — EventId/deviceSequence/payloadHash contract must be preserved exactly. Mitigation: reimplement in Laravel + port all 21 sync tests.
4. **Domain invariant parity** (CRITICAL) — C# domain types must produce identical outcomes in PHP. Mitigation: port all 378 domain tests to PHPUnit.
5. **Offline PWA rebuild** (HIGH) — Blazor WASM → Vue 3 migration must preserve offline functionality. Mitigation: reuse `offline-store.js` pattern + early PoC.

**Proof-of-concept to build first:** Laravel sync engine against the existing database, tested with the PWA's `offline-store.js`. This validates the riskiest component before full migration begins.

**Estimated effort:** 18-36 months for a team of 2-4 developers.

**Status:** CONFIRMED — full roadmap in `VAULTFLOW_MIGRATION_AUDIT.md` Section 9.

---

## 15. Unanswered Questions and Owner Decisions

### Mandatory owner decisions (cannot be answered from code inspection):

1. **Is offline functionality a hard requirement?** Determines whether the PWA must be rebuilt or can be deferred.
2. **What is the acceptable downtime for cutover?** Determines whether temporary interoperability is needed.
3. **Should the MAUI client be retired immediately or retained during transition?** Affects migration complexity.
4. **What is the target timeline?** 6/12/18/24 months — affects team size and phasing.
5. **Is the existing PostgreSQL database permanent?** Affects schema design.
6. **Should POS historical data be migrated?** Yes/No/Read-only — affects data migration scope.
7. **What is the minimum browser support baseline?** Affects PWA and frontend compatibility.

### Optional preferences:

1. Preferred frontend framework: Vue 3 / React / Svelte
2. Preferred real-time solution: Laravel Echo + Pusher / Ably / Reverb
3. Preferred queue backend: Redis / Database / SQS
4. Preferred deployment platform: Docker / Kubernetes / PaaS
5. Should the new system support multi-tenancy?

### Unresolved technical unknowns (require runtime verification):

1. Actual performance baselines for ledger queries, sync operations, and page loading
2. Whether the .NET Identity password hash format can be reliably parsed by a Laravel custom hasher (requires testing with real hashes)
3. Whether the sync protocol can be exactly reimplemented in Laravel without subtle behavioral differences (requires extensive testing)
4. Whether the Vue 3 PWA can achieve equivalent offline functionality to the Blazor WASM PWA (requires browser testing)

---

*End of summary.*
