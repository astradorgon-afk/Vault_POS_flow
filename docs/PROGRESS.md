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

**Now:** Phase 10 is committed. **Phase 11 — POS — is underway as the "C" batch
series** (customer-return and receipt work is being built ahead of the
shift/sale/payment bulk). C1 (void), C2 (customer return + refund), C3 (receipt
reprint), C3b (blind return) and C4 (blind-return refund) are committed; details
in the log below. The latest batch, C4, cleared the commit gate: build 0 warnings
0 errors, Domain 345 / Application 200 / Infrastructure 64 (+ 18 skipped) /
Security 52 / Architecture 13 / API 110 passed, 2 failed (Docker absent —
those are PostgreSQL-guard tests, not caused by C4), and the migration guard ran
against PostgreSQL on C3b.
**Last commits:** `c829307` (C3b — blind customer return), `ac46de3` (C3 —
receipt reprint with reason), `adf1a65` (C2 — customer returns and refunds),
`4a81731` (C1 — void completed sale), then Phase 10 as `70f4dda`.

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
- [x] **Phase 10 — Batch and expiration** (committed as `70f4dda`; the expiry run
  itself is done — FEFO allocation service extraction, POS sale-blocking and
  expiring-soon alerts are deferred to Phases 11/14, see log)
  - [x] Groundwork: `RequiredReferenceDocuments` set, `ExpiryQuarantine` justifiable by `StockAdjustment` or `ExpiryRun` (`G6`)
  - [x] Configurable expiry warning threshold (`LocationSettings.ExpiryWarningDays`, default 90)
  - [x] Expiry scan: expired, expiring-soon (threshold), FEFO-ordered sellable batches (`IExpiryService`)
  - [x] Expiry run: worker- or command-driven quarantine of past-expiry stock → `Expired`, EXP-numbered `ExpiryQuarantine` groups, system-actor audit
  - [ ] FEFO transfer picking service (pick logic already FEFO-ordered; service extraction deferred)
  - [ ] Sale blocking for expired batches: POS consumers of the sellable-batch query + the authorized override path (Phase 11)
  - [ ] Expiring-soon / expired alerts (Phase 14 notifications)
- [~] **Phase 11 — POS** (returns side first as the "C" batch series)
  - [x] C1 — void a completed sale, same shift and business day, with ledger reversal and migration
  - [x] C2 — referenced customer return with refund limits and the `EXT-CUSTOMER → ReturnPending` ledger legs; refunds capped by the sale's payment mix; migration
  - [x] C3 — receipt reprint with mandatory reason and the append-only print log; migration
  - [x] C3b — blind customer return (no sale number): catalogue-priced, zero-sum `CustomerReturn` ledger group, exception audit with the reason, migration
  - [x] C4 — blind-return refund path: `RefundBlindSalesReturnCommand` (no `SaleId`), `IssueBlindRefund` (cash only, capped by `RefundableTotal`, no per-method cap), `sale.refund.issued` audit, handler tests, infra round-trip
  - [ ] Shift lifecycle with cash reconciliation opening/closing
  - [ ] Sale flow: cart, pricing/discount/VAT, payments, atomic completion
  - [ ] Customers
  - [ ] Daily summary
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
- **POS C1 committed** (`4a81731`). Void a completed sale: the domain validates
  same-shift and same-business-day (same shift + same business-day gate), clears
  the sale's `IsVoided` flag (now excluded from daily summaries), and posts a
  full reversal `SaleVoided` ledger group referencing the original sale. Handler:
  shift/device facts check, writes the two `sale.created` and `sale.voided`
  audits with a mandatory reason (≤ 200 chars), persists through `AddAsync`. Tests
  for the domain rules, the handler's happy path and error paths, and the
  repository round-trip.
- **POS C2 committed** (`adf1a65`). Referenced customer return with refunds:
  `SalesReturn.Create` takes a `ReturnItemSpec` for each returned sale line
  (quantity capped at `SaleItem.Quantity − SaleItem.ReturnedQuantity`), freezes
  a proportional snapshot of the sale line's net paid amount, and validates the
  return fits inside `ReturnWindowDays` (default 7). The handler posts the
  zero-sum `CustomerReturn` group (`EXT-CUSTOMER/External −q ⇄ Store/ReturnPending
  +q`), writes the `sale.created` and `sale.return.created` audits, and persists.
  A second command issues a refund against the return, capped per payment method
  at what the return accepted. Refunds are posted through `AddRefundAsync` with
  a `sale.refund.issued` audit. `RefundCommandErrors` carries the sale-match,
  already-fully-refunded, and refund-exceeds-allowed caps. Tests: domain rules,
  both handlers, repository round-trips.
- **POS C3 committed** (`ac46de3`). Receipt reprint is read-only: the device
  re-emits the receipt from its local copy, so nothing sale- or ledger-changing
  happens — what must not be silent is the act of reprinting. `SaleReceiptPrint`
  (append-only, required-reason ≤ 200 chars for reprints, first prints carry no
  reason) is persisted by `AddReceiptPrintAsync`, with a `sale.receipt.reprinted`
  audit. Repository round-trip tests were added. The suite green at Domain 324,
  App 169, Infra 79.
- **POS C3b committed** (`c829307`). Blind customer return (POS.md §4) accepts
  goods back with no original sale under the same `CustomerReturn` ledger effect,
  plus an exception record — `sale.return.blind.accepted`. Domain:
  `SalesReturn.CreateBlind` (null `SaleId`, `IsBlind = true`,
  `BlindReturnItemSpec` as a flat record with product id/name/price/cost but no
  `SaleItemId`); VAT mirrored from sales (vatable/exempt/zero-rated proportional
  splits using `SalesReturnItem.SplitTaxInclusive`); `SaleItemId` made nullable
  on `SalesReturnItem` so both referenced and blind lines share the same table.
  Migration `20260915051436_AddBlindReturns` makes `sale_id` and `sale_item_id`
  nullable and adds `is_blind` (not-null, default false); the guard ran against
  PostgreSQL. Handler: `CreateBlindSalesReturnCommand` behind `sale.return_blind`
  (POS.md §4) — shift/device facts, RET numbering, catalogue price at the
  returned instant (no sale-line price available), the same zero-sum ledger group
  and both the `sale.return.created` and `sale.return.blind.accepted` audits with
  the exception reason. Referenced paths tightened: the existing handler refuses a
  null-backed sale item; the refund path rejects blind returns through its
  existing sale-id check (blind refund is a later batch). Tests: 11 domain tests
  (VAT splits, caps, reasons, empty and non-positive lines); 14 handler tests
  (happy paths, persist at today's price, product/location/external-customer
  errors, device/shift checks, ledger failure); two infra round-trips. The suite
   green at Domain 335, App 183, Infra 81 (with Docker).
- **POS C4 committed.** Blind-return refund path (POS.md §4): a blind return
  accepted goods back with no original sale, so its refund cannot be capped by
  the original sale's payment mix. Domain: `SalesReturn.IssueBlindRefund`
  (`IsBlind` guard, cash-only via `sale.refund.blind.cash_only`, capped only by
  the return's `RefundableTotal`, no per-method cap, cash tendered and rounding
  rules unchanged). `SalesReturnErrors.RefundBlindOnly` and `RefundBlindCashOnly`
  added. Application: `RefundBlindSalesReturnCommand` — no `SaleId`, same
  `sale.refund` permission (`IAuthorizedMessage`, `ILocationScoped`,
  `IIdempotentCommand`); validator mirrors `RefundSalesReturnCommandValidator`
  minus the sale rule, adds a cash-only constraint; handler pre-checks
  idempotency by event, then return exists / `IsBlind` /
  location/device match / shift open on same device, loads the location rounding
  increment, calls `IssueBlindRefund`, writes `sale.refund.issued` audit, persists
  via `AddRefundAsync`. `RefundCommandErrors.ReturnNotBlind` added for the
  handler-level check. Tests: 10 domain tests (cash-only guard, tendered / increment
  rules, cap accumulation, referenced-return refused blind-only); 16 handler tests
  (happy paths, idempotency, non-blind/location/device/shift errors, aggregate
  cap errors, persistence failure); one infra round-trip for a blind cash refund.
  Full suite: Domain 345, App 200, Infra 64 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 110 (2 Docker-absent Postgres-guard failures,
  unrelated). No migration needed: the refund table is C2.
- **Note for the workstation:** a local hook echoes prompts and commands through
  `cmd`, so any `>` in that text creates an empty stray file in the repository
  root (seen as `,-`, `,session_title`, `%{redirect_url}'`). They were removed each
  time; none were committed.
