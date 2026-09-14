# Progress Log

Running record of the work to close the known gaps and finish Phases 9–18.
Updated at every commit. [STATUS.md](STATUS.md) is the detailed state of the
system; [ROADMAP.md](ROADMAP.md) is the item-by-item plan. The UI design
mockups (the "VaultFlow Screens" canvas) are **on hold** until the phases are done.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done (with commit)

**Working agreement (2026-09-14):** gaps first, then Phases 9 → 18 in roadmap
order. One local commit per finished batch or phase, made only after the build
and the test suites pass. Nothing is pushed.

---

## Current position

**Now:** Phase 10 — batch and expiration — is **complete and ready to commit**. The
groundwork commit `77e1e3d` made `ExpiryQuarantine` justifiable by either
`StockAdjustment` or `ExpiryRun` and added the `ExpiryRun` document type
(`DocumentType.ExpiryRun = 15` / `EXP`, `ReferenceDocumentType.ExpiryRun = 13`).
This session built the expiry run on top of it (details in the log below):
`LocationSettings.ExpiryWarningDays`, `ExpiryErrors`, `ExpiryRunResult`,
`ExpiryRunRecordId`, `AuditActions.Expiry`, the `inventory.expiry.run` permission,
`RunExpiryCommand` + thin handler, the `IExpiryService` / `IExpiryRepository`
ports, the infra `ExpiryService`, `ExpiryRepository`, `ExpiryWorker`
(`Expiry:Enabled`, 6-hour interval, system actor) and the `ExpiryOptions` binding,
plus the docs update. Build green (0 warnings, 0 errors); full solution
**445 passed, 0 failed** (Domain 215, Application 20, Infrastructure 40,
Security 52, Architecture 13, API 105) with Docker running — the batch has
cleared the commit gate.
**Last commit:** `77e1e3d` Phase 10 groundwork (see log).

---

## Plan

### Gap batches (before Phase 9)

- [x] **G1 — Correctness and operations**
  - [x] Quarantine line without a barcode returned 500 → validator, 400 `quarantine.barcode_required`
  - [x] Unreadable request bodies returned 500 (Development) or an empty 400 → 400 `request.malformed` problem document everywhere
  - [x] Printed receipts showed UTC → branch wall-clock time with the zone name
  - [x] Endpoint suite never ran the API on PostgreSQL → `PostgresHostSmokeTests` (proven to fail on the old numbering bug)
  - [x] `PosApiFactory` silently ignored overrides of default settings → overrides now win
  - [x] Grants script covered 4 schemas, one of which does not exist → every existing schema, PostgreSQL test over every table
  - [x] Receipts had no immutability guard → interceptor, triggers (migration `20260914090000_ReceiptIntegrityGuards`), append-only grants
  - [x] `docker compose up` could not start the API (no signing key, grants never applied) → Docker secret + key-per-file configuration, one-shot `grants` service
  - [x] Container images had never built (Dockerfiles omitted `.editorconfig`) → copied; `.dockerignore` keeps `.env`, `.secrets/`, `.git` out of the build context
  - [x] Fresh deployments ran two migrations out of order (15-digit ids sort differently by culture) → renamed to 14-digit ids; `MigrationOrderingTests`
  - [x] Caddy could not complete a TLS handshake (bare `:443` site) → `{$SITE_ADDRESS:localhost}`
  - [x] Whole compose stack verified end to end from a clean volume, over HTTPS, as `pos_app`
  - [x] `init-dev-secrets.ps1` required PowerShell 7 → runs on Windows PowerShell 5.1, writes the compose secret and `.env`
  - [x] Quarantine body without `lines` returned 500 → 400 `quarantine.empty`
  - [x] ADR-0027 (no retrying execution strategy); DEPLOYMENT, DATABASE, API, STATUS, ROADMAP updated (Phase 5 boxes ticked)
- [x] **G2 — Receipts completion:** `GET /api/v1/receipts` with location/kind/date filters and paging, scoped to the caller's locations (folded into the G1 commit)
- [x] **G3 — Phase 1–2 leftovers**
  - [x] User administration: create, update, disable/enable, roles, locations, overrides, password reset, PIN, two-factor reset
  - [x] Role administration: list roles and permissions, replace a role's permissions
  - [x] Safeguards (ADR-0028): no self-administration, hold-it-to-give-it for governance permissions and tiers, no overreach, always an administrator
  - [x] Seeder re-added removed default grants at every start → applied-once record + migration
  - [x] Two-factor enforced for user/role managers: password-authenticated enrolment, recovery codes, administrator reset
  - [x] Identity token writes were discarded under the no-tracking default (recovery codes reusable, reset did not rotate the key) → `TrackingScope`
  - [x] Serilog `SensitiveDataScrubber`
  - [x] Full test run (367 passed) and commit
- [x] **G4 — Phase 3 leftovers (catalog curation)**
  - [x] Product edit (tracking flags, shelf life and base unit stay fixed), deactivate/activate with reason
  - [x] Barcodes: attach, retire (stops scanning, stays reserved), set primary; migration `20260914100000_ProductBarcodeRetirement`
  - [x] Effective-dated prices: supersession, temporary prices that resume, no backdating (ADR-0029)
  - [x] `ProductLocationSetting`, unit conversions, product–supplier links (one preferred)
  - [x] Reference checks (products have no foreign keys to master data)
  - [x] Primary-barcode swap failed on both engines (500) → demotions written first in `PosDbContext`
  - [x] Price overlap check treated the end as inclusive → half-open like the database
  - [x] Cashiers could read purchase cost → `product.cost.view` enforced on product reads
  - [x] Full test run (398 passed) and commit
- [x] **G5 — Phase 4 leftovers**
  - [x] Refused draws recorded after the command's rollback, append-only, with audit entries (ADR-0030); migration `20260914110000_NegativeStockAttempts`
  - [x] Exception report: list and per-product/location summary (`inventory.view.all`)
  - [x] Partitioning of `inventory_movement` and `audit_log` decided against for v1, with revisit thresholds (ADR-0030); DATABASE, DEPLOYMENT, SECURITY corrected
  - [x] Full test run (404 passed) and commit

### Phases

- [x] **Phase 9 — Inventory control** (ADR-0031)
  - [x] Stock adjustments: reasons mapped to write-off, expiry and correction movements; value-tiered approval by someone other than the author; reject; reversal
  - [x] Counts: full, cycle, category, product; sheet from the ledger; per-line re-read of system quantity; stale lines refused at approval; variance-only posting; reject to recount; cancel
  - [x] Variance and repeat-variance reports; repeat variance flagged on submission (90 days)
  - [x] Migration `20260914120000_InventoryControl`; full flow verified on PostgreSQL
  - [x] Removed six empty junk files from the repository root (three created by the local hook, three committed in Phase 1)
  - [x] Full test run (445 passed) and commit
- [~] **Phase 10 — Batch and expiration** (code complete, 445 tests passing, ready to commit)
  - [x] Groundwork: `RequiredReferenceDocuments` set, `ExpiryQuarantine` justifiable by `StockAdjustment` or `ExpiryRun` (`G6`)
  - [x] Configurable expiry warning threshold (`LocationSettings.ExpiryWarningDays`, default 90)
  - [x] Expiry scan: expired, expiring-soon (threshold), FEFO-ordered sellable batches (`IExpiryService`)
  - [x] Expiry run: worker- or command-driven quarantine of past-expiry stock → `Expired`, EXP-numbered `ExpiryQuarantine` groups, system-actor audit
  - [ ] FEFO transfer picking service (pick logic already FEFO-ordered; service extraction deferred)
  - [ ] Sale blocking for expired batches: POS consumers of the sellable-batch query + the authorized override path (Phase 11)
  - [ ] Expiring-soon / expired alerts (Phase 14 notifications)
- [ ] **Phase 11 — POS:** shifts with cash reconciliation, sale lifecycle, pricing/discount/VAT, payments, atomic completion, receipts and reprint, void/return/refund, customers, daily summary
- [ ] **Phase 12 — Offline storage:** `Pos.Client` SQLite store, cache tables, device numbering, permission snapshots
- [ ] **Phase 13 — Synchronization:** outbox, push/pull endpoints, idempotency behaviour, retries, conflict rules
- [ ] **Phase 14 — Notifications:** persistent notifications, SignalR hub, alert generators
- [ ] **Phase 15 — Analytics and reports:** sales, margin, inventory, transfers, purchasing, shrinkage, ageing, audit, export
- [ ] **Phase 16 — Owner dashboard:** KPIs, store comparison, inventory and exception panels, drill-downs
- [ ] **Phase 17 — Testing:** coverage gate and the remaining suites
- [ ] **Phase 18 — Deployment:** production compose overlay, backups/restore, logging/metrics, client packaging, CI publishing

---

## Log

### 2026-09-14

- **Checkpoint commit `d9a97b5`.** Interim payment receipts (ADR-0026); removed
  `EnableRetryOnFailure` (the API crashed at start-up on PostgreSQL); aliased the
  document-counter upsert (every document number failed on PostgreSQL);
  development API explorer at `/scalar`.
- **G1 + G2 finished.** Everything in the two checklists above. Verification:
  full solution **341 passed, 0 failed, 0 skipped** (Domain 160, Application 16,
  Infrastructure 37, Security 33, Architecture 13, API 82); no pending model
  changes; the whole compose stack built from scratch and exercised over HTTPS as
  `pos_app` (sign-in, receipt issue and print, quarantine raise with ledger
  posting, catalog/transfer/purchasing reads, `UPDATE core.receipt` refused).
  Running the real deployment found three problems no test could see: the images
  had never built, migrations applied out of order under invariant globalization,
  and Caddy could not complete a TLS handshake.
- **G1 + G2 committed** as `53346fe`.
- **G3 finished.** User and role administration with the ADR-0028 safeguards,
  two-factor enforcement for user/role managers, log scrubbing. Its tests found two
  more real defects: the seeder re-added default grants an administrator removed,
  and Identity's token writes were silently discarded under the no-tracking default
  (recovery codes reusable, two-factor reset not rotating the key). Verification:
  full solution **367 passed, 0 failed, 0 skipped** (Domain 160, Application 16,
  Infrastructure 37, Security 52, Architecture 13, API 89); no pending model changes.
- **G3 committed** as `58a3085`.
- **G4 finished.** Catalog curation: product edit, activation, barcode lifecycle,
  effective-dated pricing (ADR-0029), location settings, unit conversions and
  supplier links, all audited. Its tests found three defects: a primary-barcode
  swap could never be saved (EF Core wrote the promotion before the demotion and
  both engines refused it — proven on PostgreSQL by disabling the fix: `23505`),
  the domain refused adjacent price periods the database accepts, and every
  product read returned purchase cost to cashiers. Verification: full solution
  **398 passed, 0 failed, 0 skipped** (Domain 182, Application 16, Infrastructure
  39, Security 52, Architecture 13, API 96) with Docker running; no pending model
  changes. Docker Desktop hit the stale-socket start-up crash again and was
  recovered by renaming `%LOCALAPPDATA%\Docker\run` aside.
- **G4 committed** as `f5585c8`.
- **G5 finished.** Refused stock draws are now recorded — the documented
  shrinkage signal had never been written, and could not be written inside the
  refused command because its rollback would erase it; a pipeline behaviour writes
  the attempts after the unit of work ends, through a separate context. Report
  endpoints added; PostgreSQL triggers and grants verified. Partitioning decided
  against for v1 (ADR-0030) and the docs that described partitions corrected.
  Verification: full solution **404 passed, 0 failed, 0 skipped** (Domain 182,
  Application 20, Infrastructure 40, Security 52, Architecture 13, API 97) with
  Docker running; no pending model changes.
- **G5 committed** as `7642a18`.
- **Phase 9 finished.** Stock adjustments and inventory counts with approval,
  posting, reversal, variance and repeat-variance reports (ADR-0031).
  Verification: full solution **445 passed, 0 failed, 0 skipped** (Domain
  215, Application 20, Infrastructure 40, Security 52,
  Architecture 13, API 105) with Docker running; no pending model changes.
  Work stops here until resumed.
- **G6 (groundwork for Phase 10 �?" batch and expiration).** Movement-type rules
  now accept a **set** of reference document types per movement, not a single one:
  record is `RequiredReferenceDocuments` (`IReadOnlySet<ReferenceDocumentType>?`),
  a `Set(params ReferenceDocumentType[])` helper overload was added, every rule is
  set-wrapped, and `ExpiryQuarantine` is justifiable by both `StockAdjustment` and
  `ExpiryRun`. Reports/exports remain single-typed for now. Also adds the
  `ExpiryRun` groundwork in `DocumentNumber.cs` (`DocumentType.ExpiryRun = 15` /
  `EXP`) and `InventoryMovementGroup.cs` / `InventoryMovementType.cs`
  (`ReferenceDocumentType.ExpiryRun = 13`), uncommitted. Verification: full
  solution **445 passed, 0 failed, 0 skipped** (Domain 215, Application 20,
  Infrastructure 40, Security 52, Architecture 13, API 105); build 0 warnings
  0 errors; no pending model changes.
- **Phase 10 batch (expiry run, in progress, uncommitted).** The expiry worker and
  command built on the G6 groundwork: `LocationSettings.ExpiryWarningDays`
  (default 90, location JSON now carries the threshold); `ExpiryErrors`,
  `ExpiryRunResult` (`ExpiredBatchItem`, `ExpiringBatchSummary`, `SellableBatchItem`),
  `ExpiryRunRecordId`, `AuditActions.Expiry`, and the `inventory.expiry.run`
  permission (catalogue + privileged set); `RunExpiryCommand` with a thin handler
  (authorized, location-scoped, delegates to the service, writes an audit entry on
  success); `IExpiryService` / `IExpiryRepository` ports; the infrastructure
  `ExpiryService` (validates location + timezone, allocates one EXP document number
  per run, posts one two-leg `ExpiryQuarantine` group per expired batch
  `Available → Expired` with `AdjustmentReasonCode.Expired`, a shared `ExpiryRunRecordId`
  as the reference-document id, value summed at `Money.StorageScale` with
  `Money.IntermediateRounding`), `ExpiryRepository` (reads location settings,
  external-write-off location, stocking locations, warning days), `ExpiryWorker`
  (`BackgroundService`, `Expiry:Enabled` / `IntervalHours` [1–24] default 6 /
  `RunOnStartup`, per-scope sweep over stocking locations with a system actor
  `UserId.Empty`, skips locations without a timezone and `NoExpiredBatches`,
  tolerates per-batch failure), and   `ExpiryOptions` bound + data-validated in DI.
  Verification: build 0 warnings 0 errors; full solution **445 passed, 0 failed**
  (Domain 215, Application 20, Infrastructure 40, Security 52, Architecture 13,
  API 105) with Docker running. Batch cleared the commit gate.
- **Note for the workstation:** a local hook echoes prompts and commands through
  `cmd`, so any `>` in that text creates an empty stray file in the repository
  root (seen as `,-`, `,session_title`, `%{redirect_url}'`). They were removed each
  time; none were committed.
