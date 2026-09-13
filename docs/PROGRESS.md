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

**Now:** Gap batch 5 — `NegativeStockAttempt` record and report; partitioning decision for `inventory_movement`.
**Last commit:** G4 — catalog curation (see log).

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
- [ ] **G5 — Phase 4 leftovers:** `NegativeStockAttempt` record and report; decision on monthly partitioning of `inventory_movement`

### Phases

- [ ] **Phase 9 — Inventory control:** adjustments with reasons and approval thresholds; damage/expiry/spoilage/loss/theft; counts (full, cycle, category, product) with snapshot, variance, approval, posting; repeat-variance detection
- [ ] **Phase 10 — Batch and expiration:** FEFO allocation service, expiry thresholds, expiry worker, sale blocking with authorized override
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
- **Note for the workstation:** a local hook echoes prompts and commands through
  `cmd`, so any `>` in that text creates an empty stray file in the repository
  root (seen as `,-`, `,session_title`, `%{redirect_url}'`). They were removed each
  time; none were committed.
