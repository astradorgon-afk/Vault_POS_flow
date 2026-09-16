# Progress Log

Running record of the work to close the known gaps and finish Phases 9–18.
Updated at every commit. [STATUS.md](STATUS.md) is the detailed state of the
system; [ROADMAP.md](ROADMAP.md) is the item-by-item plan. The standalone UI
mockup canvas is on hold; product UI now lands in tested implementation slices.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done (with commit)

**Working agreement (2026-09-14):** gaps first, then Phases 9 → 18 in roadmap
order. One local commit per finished batch or phase, made only after the build
and the test suites pass. Nothing is pushed.

---

## Current position

**Now:** Phase 10 is committed. **Phase 11 — POS — is underway as the "C" batch
series** (customer-return and receipt work is being built ahead of the
shift/sale/payment bulk). C1 (void), C2 (customer return + refund), C3 (receipt
reprint), C3b (blind return), C4 (blind-return refund), C5 (shift lifecycle
with cash reconciliation), C6 (sale endpoint surface + receipt render), C7
(daily sales summary report), C8 (sale pipeline tests + void route) and C9
(returns HTTP surface), C10 (return disposition), C11 (customer accounts), C12
(discount HTTP regressions), C13 (authenticated web shell), C14
(API-backed POS cart workspace), C15 (checkout orchestration with
server-numbered web terminals), C16 (web sale lifecycle: sales search,
receipt reprint/void and return/refund/disposition workflows in the browser),
C17 (card/e-wallet and split payment mixes in the web checkout) and C18
(expired-batch sale blocking and the authorized override path — classified
`inventory.expired_only` refusals, per-line override denial checks, mandatory
recorded reason, web probe-then-confirm dialog) are
complete; details are in the log below.
**Last commits:** C18 (expired-batch override contract — 5 new unit tests, 4 new integration tests), C17 (card/e-wallet + split payment checkout — 6 new integration tests, 0 backend changes), C16 (this commit — web sale lifecycle with 9 new backend tests), C15 (this commit — carries the C14 web shell and cart
workspace, which were never committed separately), `262724e` (C13 — authenticated web shell), `37a804f` (C12 — discount HTTP regressions), `d0f9723` (C11 — customer accounts), `7d9cddd` (C10 — return disposition), `66c419b` (C9 — returns/refunds endpoints), `232af28` (C8 — sale pipeline tests + void route), `7293608` (C7 — daily sales summary), `512269c` (C6 — sale endpoints + receipt), `b4ad864` (C5 — shift lifecycle), `c829307` (C3b — blind customer return),
`ac46de3` (C3 — receipt reprint with reason), `adf1a65` (C2 — customer returns
and refunds), `4a81731` (C1 — void completed sale), then Phase 10 as `70f4dda`.

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
  - [x] C5 — shift lifecycle with cash reconciliation: `ForceClose`, `Reconcile(varianceThreshold, reason)` with `IsForceClosed` flag; `Suspend`/`Resume`/`Reconcile` handlers, `ShiftCommandAccess` gate, `ShiftForceCloseWorker`, `GetForceCloseCandidatesAsync`, `AddShiftForceClose` migration; domain/handler/validator/infra tests
  - [x] C6 — sale endpoint surface and receipt render: `POST /api/v1/sales` (atomic full-sale payload through the `CompleteSaleCommand` pipeline), `GET /api/v1/sales/{id}` and `GET /api/v1/sales/{id}/receipt` behind the new `sale.view` permission re-checked against the sale's own location; `SaleReceiptRenderer` plain-text first print logged to `ReceiptPrints`; `SaleErrors.Unknown`/`OutsideScope`; endpoint tests through the real pipeline
  - [x] C7 — daily sales summary report: `GET /api/v1/reports/daily-sales` under the existing `report.view` permission re-checked against the report's location; completed sales + per-method payments + per-shift rows + refund amounts for a location/business date; `DailySalesReportRepository` aggregates via the `Refunds → CashierShift` join (referenced and blind refunds both count); `ReportErrors.LocationUnknown`/`OutsideScope`; endpoint tests (aggregation, refunds vs returns, 403 other-store, 404 unknown location)
  - [x] C8 — sale pipeline tests and the void route: `POST /api/v1/sales/{id}/void` dispatching the C1 `VoidSaleCommand` under `sale.void` (idempotent by `eventId`); integration tests through the real pipeline — insufficient stock 409 before payments, payment mismatch 409, VAT classification at the location rate, void restores the exact shelf quantity
  - [x] C9 — returns HTTP surface: `POST /api/v1/returns` (referenced, `sale.return`), `POST /api/v1/returns/blind` (blind, `sale.return_blind`), `POST /api/v1/returns/{id}/refund` (`sale.refund`, branches on body `saleId` → referenced vs blind); full-command payloads with `DocumentNumber.Parse`, RET-prefixed numbers, `eventId` replay safety, `201`/`200`; integration tests through the real pipeline — return+cash-refund E2E with the second-full-refund cap 409, over-quantity return 409, blind return with cash refund `200` and card refund refused `400`, refund naming a different sale `409 sale.refund.sale_mismatch`. Disposition route (`POST /api/v1/returns/{id}/disposition`) stays pending — no aggregate/command yet
  - [x] C10 — return disposition: partial line inspections into Available, Quarantine, Damaged, supplier-return staging or write-off; immutable retry-safe events, optimistic concurrency, quarantine incidents and zero-sum ledger posts; migration `20260915181631_SalesReturnDispositions`
  - [x] C11 — optional customer accounts: create/search/detail/update/deactivate/reactivate routes; `customer.view` and `customer.manage`; mutation audits without duplicated PII; sale completion accepts active known customers only; migration `20260916015216_CustomerAccounts`
  - [x] C12 — discount HTTP regressions: an authorized manual discount persists gross/discount/net/payment totals and its authorizer; a named authorizer without `sale.discount` gets 403 and leaves inventory unchanged
  - [x] C13 — authenticated Blazor shell: API-backed username/password/two-factor sign-in, circuit-scoped token session, protected routing and safe return URLs, sign-out, responsive operations navigation and workspace overview
  - [x] C14 — API-backed POS cart workspace: assigned-store selection, barcode lookup, product search, effective location/global pricing, quantity editing, removal and live totals
  - [x] C15 — checkout orchestration with server-numbered web terminals (committed with C14): register auto-select, session bootstrap, shift start, line discounts, cash payment and atomic submission; physical terminals refused `device.not_web`; 12 new `TerminalEndpointTests`
  - [x] C16 — web sale lifecycle: sales search (`GET /api/v1/sales` summaries), `GET /api/v1/returns/{id}` detail, `POST /api/v1/sales/{id}/reprint` reason+append-only; 9 new `SaleLifecycleEndpointTests`; web pages: sales search, sale detail (receipt/reprint/void/accept-return), return detail (disposition/refund/refund history); shared `TerminalBar`; `Pos.Web` builds clean
  - [x] C17 — card/e-wallet + split payment checkout: allocated-payment list, method tabs (cash/card/e-wallet), quick tender, provider reference; complete gated until allocated = net at 4 dp; 6 new `SalePaymentEndpointTests`, 0 backend changes
  - [x] C18 — expired-batch sale blocking + authorized override path: `inventory.expired_only` refusal classified against past-expiry coverage (shortfalls stay `inventory.insufficient_stock`); per-line `sale.expired_override` denial check (`sale.expired_override_denied` → 403); mandatory line reason (max 500) recorded in the `sale.expired.override` audit with the authorizing user stamped from context; web probe-then-confirm dialog with reason required; 5 new unit tests (3 validator + 2 handler) and 4 new `ExpiredOverrideEndpointTests`
  - [ ] Sale flow: discounts/VAT, payments, shift/device context and atomic completion wiring
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
- **POS C5 committed — shift lifecycle with cash reconciliation** (POS.md §1).
  Domain: `CashierShift.ForceClose(closedAtUtc)` closes from `Open`/`Suspended`
  and sets `IsForceClosed` (so a worker-closed shift cannot silently absorb the
  next day's sales); `Reconcile(decimal varianceThreshold, string? reason)`
  accepts a variance only when it is within the location's threshold, and a
  force-closed shift (whose `CashVariance` is null — the drawer was never counted)
  always requires a manager reason (`ShiftErrors.ReconcileReasonRequired`).
  `ShiftSuspended`/`ShiftResumed`/`ShiftReconciled`/`ShiftForceClosed` audit
  actions added. Application: `SuspendShiftCommand`, `ResumeShiftCommand`,
  `ReconcileShiftCommand` and their validators; three handlers sharing
  `ShiftCommandAccess` (owner check or `sales.close_other_shift` permission,
  location-scoped, audit + `UpdateAsync`); the reconcile handler resolves the
  location's `CashVarianceThreshold` through `LocationFacts`. Infrastructure:
  `ShiftForceCloseWorker` (`ShiftForceCloseOptions`, `IntervalHours` default 1,
  `RunOnStartup` default false) closing over-age open/suspended shifts via
  `IShiftRepository.GetForceCloseCandidatesAsync`; `is_force_closed` column;
  migration `20260915125042_AddShiftForceClose`. Tests: 13 domain, 25 handler, 12
  validator, 7 infra round-trips/candidates. Full suite: Domain 358, App 237,
  Infra 71 (+ 18 skipped PostgreSQL guards), Security 52, Architecture 13,
  API 110 (2 Docker-absent Postgres-guard failures, unrelated).
- **POS C6 committed — sale endpoint surface and the receipt render** (POS.md §2-3).
  New read-only permission `sale.view` (catalogue `Def(..., offline: true,
  readOnly: true)`, granted to Store Manager, Cashier and Auditor; the
  Administrator retains no `sales.*` grants by design, Owner inherits all via the
  wildcard). Domain: `SaleErrors.Unknown` (404 `sale.unknown`) and
  `SaleErrors.OutsideScope` (403 `sale.outside_scope`). API: `POST /api/v1/sales`
  maps `CompleteSaleBody` onto `CompleteSaleCommand` — `CashierId` comes from the
  authenticated request, `DeviceId` stays on the payload; the command's
  `IAuthorizedMessage` + `ILocationScoped` let the pipeline cap `sale.create` at
  the caller's locations (no body-scope route source exists or is needed), and
  the handler re-derives price, VAT and FEFO as before. `GET /api/v1/sales/{id}`
  and `GET /api/v1/sales/{id}/receipt` take `sale.view` and re-check it against
  the sale's own location. `SaleReceiptRenderer.RenderPlainText` emits the
  branch-wall-clock receipt (line list, totals, payment methods with
  tendered/change, `Thank you.`) — the same wire shape as the payment-receipt
  renderer, so the seam is identical — and the receipt route logs the first print
  (`isReprint: false`, no reason) into `ReceiptPrints`. No migration: permission
  flows through the seeder catalogue, the print table is C3. Tests: 5 endpoint
  tests on the real pipeline (complete + read + render + exactly one first print;
  price-missing 409; insufficient shelf 409; another store's manager 403 on
  detail and receipt while the Auditor reads; unknown sale 404). Full suite:
  Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards), Security 52,
  Architecture 13, API 115 (2 Docker-absent Postgres-guard failures, unrelated).
- **POS C7 committed — daily sales summary report** (POS.md §2-3).
  `GET /api/v1/reports/daily-sales?locationId={id}&date={yyyy-MM-dd}` under the
  existing `report.view` permission (`Administration.ViewReports`) — no new
  permission, no migration. The route uses `Scope = ScopeSource.None` with a
  manual `IPermissionEvaluator` re-check against the location, so an
  other-store manager gets 403 `report.outside_scope`, while the Auditor (grants
  include `Administration.AllLocations`) reaches an unknown location to get 404
  `report.location_unknown`. Domain: `DailySalesReport` + summary/refund/payment
  shift records and `ReportErrors` (404 `report.location_unknown`, 403
  `report.outside_scope`). Infrastructure: `DailySalesReportRepository` in one
  AsNoTracking pass — only `Completed` sales for the location/business date;
  refunds attributed through `Refunds.CashierShiftId → CashierShift` so both
  referenced and blind refunds count toward the day even when the return is
  blind; payments grouped by method through the `SaleId` shadow key with
  change; per-shift cash sales/refunds for cash reconciliation. The wire model
  maps strong IDs to plain GUIDs. Tests: 4 endpoint tests through the real
  pipeline — two-sale aggregation (cash + cash/card split, per-method amounts
  and change, shift row) and the refund-vs-report leg (a referenced return with
  a 45 cash refund seeded through the public `SalesReturn.Create`/`IssueRefund`
  surface shows up in `refundTotal` and the shift's `cashRefundsTotal`), 403
  for another store's manager but OK for the Auditor, 404 unknown location.
  Full suite: Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 119 (2 Docker-absent Postgres-guard
  failures, unrelated).
- **POS C8 committed — sale pipeline tests and the void route.**
  `POST /api/v1/sales/{id}/void` completes the sale lifecycle HTTP surface:
  `VoidSaleBody` (`eventId`, `locationId`, `shiftId`, `deviceId`, `businessDate`,
  `voidedAtUtc`, `reason`) dispatches the C1 `VoidSaleCommand` under `sale.void`
  (the command is `IAuthorizedMessage` + `ILocationScoped`, so the pipeline caps
  it at the caller's assigned locations) with `VoidedByUserId` taken from the
  authenticated request — no migration, no new permission. Idempotent by
  `eventId`, so a retried void replays instead of double-posting the reversal.
  Tests: 4 integration tests through the real pipeline — selling past the
  sellable shelf is refused 409 `inventory.insufficient_stock` before payments
  are considered; payments under-covering the net total are refused 409
  `sale.payment_mismatch`; a non-exempt default product's line records the
  location VAT rate (12%), `isVatExempt` false, `isZeroRated` false ($2.6 — no
  zero-rated product flag yet); and voiding a completed £90 sale of 2 units
  restores the shelf to its exact pre-sale quantity. The suite pinned two facts
  on the way to green: the sale-detail route exposes lines as `lines` (not
  `items`), and PIN sign-in requires the device GUID in `X-Device-Id` — the
  registered device code string is refused with 400 `auth.device_header_required`.
  Full suite: Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 123 (2 Docker-absent Postgres-guard
   failures, unrelated).
- **POS C9 committed — returns HTTP surface.**
  Wires the C2/C3b/C4 returns and refunds up as endpoints through the real
  pipeline. `POST /api/v1/returns` (`sale.return`) dispatches the referenced
  `CreateSalesReturnCommand`, `POST /api/v1/returns/blind` (`sale.return_blind`)
  the blind `CreateBlindSalesReturnCommand`, and `POST /api/v1/returns/{id}/refund`
  (`sale.refund`) branches on the body's optional `saleId` — present dispatches
  `RefundSalesReturnCommand`; absent dispatches `RefundBlindSalesReturnCommand`.
  All three take the full command as one payload including a
  `DocumentNumber.Parse`-validated `number` (`RET-{yyyy}-…`), an `eventId` for
  replay safety, identity fields resolved from the authenticated request, and
  respond `201 Created` (returns) or `200` (refunds). `RequirePermission` is
  `Scope = ScopeSource.None` on all three. No migration, no new permission.
  **Not in C9:** the `POST /api/v1/returns/{id}/disposition` route from the
  plan stays pending — `SalesReturn` has no disposition aggregate method or
  application command yet (only the `InventoryMovementType.ReturnDisposition`
  code and its `MovementTypeRules` entry exist).
  Tests: 4 integration tests through the real pipeline — a referenced return
  `201` then a full cash refund `200`, with a second full refund of the same
  value refused `409 sale.refund.exceeds_paid_for_method` (the per-method
  running total from the sale-wide query plus the return's in-memory refunds
  double-counts the same payment method); returning more than the sale sold
  refused `409 sale.return.quantity_exceeds_available`; a blind return `201`
  with its cash refund `200` and the card refund refused up-front `400
  sale.refund.blind.cash_only` (command validator); and a refund naming a
  *different* sale refused `409 sale.refund.sale_mismatch`. Full suite:
  Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 127 (2 Docker-absent Postgres-guard
  failures, unrelated).
- **Note for the workstation:** a local hook echoes prompts and commands through
  `cmd`, so any `>` in that text creates an empty stray file in the repository
  root (seen as `,-`, `,session_title`, `%{redirect_url}'`). They were removed each
  time; none were committed.

### 2026-09-16 — POS refund follow-up

- Fixed the C9 refund double count: the handler now excludes the active return
  from the repository's prior-refund totals, because `SalesReturn.IssueRefund`
  already includes that return's loaded refunds. Refunds from other returns
  still count toward the original payment-method cap. No migration required.
- Added an HTTP regression covering two returns against a 90 sale, each refunded
  in 30 + 15 installments, with every event replayed. It verifies exactly four
  persisted refunds totaling 90, the per-return cap after the first return, and
  the original-payment cap after the second. Corrected the existing over-refund
  test to expect the per-return error when the original payment still covers it.
- Extended repository coverage for excluding one return while preserving the
  others, and handler coverage for passing the active return's ID.
- Updated `STATUS.md` next steps to reflect the endpoints already delivered.
- Validation: 158 targeted tests passed (Domain 39, Application 69,
  Infrastructure 32, Architecture 13, API 5); `git diff --check` passed.
  The full suite and PostgreSQL/Docker tests were not run for this follow-up.

### 2026-09-16 — C10 return disposition

- Added `POST /api/v1/returns/{id}/disposition` under location-scoped
  `inventory.adjust`, for both referenced and blind returns. The request names
  a return line, positive quantity (three decimal places), inspection kind,
  reason, note and retry event. Actor and dates come from the server.
- Restock, quarantine, damaged, supplier-return staging and waste all post
  zero-sum `ReturnDisposition` ledger legs. Quarantine also creates one
  identified incident without posting a second inventory entry. Expired
  batches cannot be restocked. Disposition leaves refund entitlement unchanged.
- A line's dispositioned quantity is an optimistic-concurrency token. Immutable
  `SalesReturnDisposition` rows retain each event; changed event replays and
  line overshoots are refused. Stale saves become HTTP 412 and roll back the
  operation. Ledger posting, history, incident and audit share one transaction.
- Migration `20260915181631_SalesReturnDispositions` adds the line counter and
  event table, with PostgreSQL mutation/truncate guards. The EF append-only
  interceptor and deployment grants protect the same history. Apply the
  migration before running the updated API.
- Validation: 873 tests passed with PostgreSQL tests excluded (Domain 364,
  Application 237, Infrastructure 72, Security 52, Architecture 13, API 135).
  The subsequent concurrency-handler regression and all 12 returns endpoint
  tests passed. Migration drift and `git diff --check` passed. Docker is not
  running, so PostgreSQL execution remains unverified. The repository secret
  scanner still reports existing development/test password patterns and the
  key-generation script; this batch adds no credentials.

### 2026-09-16 — C11 customer accounts

- Added optional customer accounts with create, paged search, detail, update,
  deactivate and reactivate routes. Search is case-insensitive across display
  name, phone and email and escapes SQL wildcard characters.
- Split access into offline-capable `customer.view` and `customer.manage`
  permissions. Store Managers and Cashiers can manage and view customers;
  Auditors have read-only access.
- Sale completion now rejects unknown or inactive named customers and persists
  an active customer's ID on the sale. Anonymous and unauthorized callers are
  rejected through the normal API authorization pipeline.
- Customer lifecycle changes use the system clock and write mutation audits.
  Contact details are not copied into audit JSON; deactivation retains its
  required reason. Foreign keys prevent deleting customers referenced by sales
  or returns.
- Migration `20260916015216_CustomerAccounts` creates `sales.customer`, search
  indexes and the optional sale/return foreign keys. Historical free-form
  customer IDs are retained as inactive legacy records before the constraints
  are added, so an existing database can upgrade without losing references.
- Validation: clean build with zero warnings; 892 non-PostgreSQL tests passed
  (Domain 369, Application 241, Infrastructure 79, Security 52, Architecture
  13, API 138). Customer-focused tests contributed 5 domain, 7 repository, 36
  sale-handler and 3 HTTP cases. Migration model drift and `git diff --check`
  passed. Docker is unavailable, so PostgreSQL execution remains unverified.

### 2026-09-16 — C12 discount regressions

- Added HTTP coverage proving an authorized line discount is rechecked at the
  sale location, reduces the payment and persisted net total, and retains the
  authorizing user on the sale item.
- Added the negative path for a caller-supplied authorizer who lacks
  `sale.discount`: the API returns 403 `sale.discount_not_authorized` and leaves
  shelf inventory unchanged.
- Validation: clean integration-test build with zero warnings; all 6 sale
  pipeline tests and all 140 non-PostgreSQL API integration tests passed. With
  the broader C11 run immediately before this test-only batch, the current
  non-PostgreSQL total is 894.

### 2026-09-16 — C13 authenticated web shell

- Replaced the untouched Blazor template with the VaultFlow operations shell.
  The web app now has an API base-address option, typed authentication client,
  circuit-scoped token session and a custom `AuthenticationStateProvider`.
- Added username/password sign-in with an optional two-factor code, user-safe API
  failures, protected routes, safe return navigation and local sign-out. The
  authorization principal carries the API's roles, permissions and locations.
- Added responsive sign-in and workspace screens with visible keyboard focus,
  reduced-motion support and mobile layouts. Removed the template counter and
  weather routes.
- Validation: `Pos.Web` builds with zero warnings. Playwright verified anonymous
  redirect to sign-in, field interaction, API-unavailable feedback, desktop and
  mobile layouts, and no browser console errors. Screenshots are under
  `artifacts/vaultflow-login*.png` and remain untracked.

### 2026-09-16 — C14 API-backed POS cart workspace

- Added the protected new-sale workspace and linked it from the navigation and
  operations overview for users with `sale.create`.
- Store choices come from the locations API and are restricted to the signed-in
  user's assigned stores. Product search and barcode lookup use the catalogue
  API; prices resolve the current store override before the organization-wide
  fallback.
- Added an in-memory cart with duplicate-line quantity increments, quantity
  controls, removal, clear, location locking and Philippine-peso totals. Search
  and scanner inputs update on each keystroke so their actions are immediately
  available.
- Validation: `Pos.Web` builds with zero warnings. A headless Playwright flow
  verified sign-in, assigned-store loading, search, pricing, quantity totals,
  removal, barcode entry and a clean browser console against a temporary API.
  No screenshots or design artifacts were generated for this batch.

### 2026-09-16 — C15 checkout orchestration (web-terminal slice)

- **Terminal slice (backend).** `DevicePlatform.Web` registers a browser as an
  ordinary device, and `Device.ActivateForWeb` marks it Active at registration
  because the signed-in cashier session is the register's credential. A web
  terminal has no offline counter, so the server allocates its document
  numbers: `IDocumentNumberGenerator.NextScopedAsync(documentType, shortCode)`
  mints `SAL-…/RET-…/SHF-…` from the shared counter table keyed by the device
  short code — never from a physical device's own counter, which must stay
  with the device (ADR-0015 / ADR-0032).
- **`/api/v1/terminal` surface** (all `sale.create`, location-scoped via the
  scope source):
  - `GET /registers?locationId=` lists the store's active browser registers as
    `{ id, shortCode, name }`.
  - `GET /session?locationId=` (requires `X-Device-Id`) bootstraps the
    checkout: business date in the store's timezone, VAT rate,
    cash-rounding increment and the shift open on that register. Refuses a
    register that is not active (`device.not_active`), belongs to another
    store (`device.wrong_location`) or is absent (`device.required`).
  - `POST /{locationId}/next-number` with `{ documentType }` allocates the
    next SAL/RET/SHF for the calling register. A physical device is refused
    `409 device.not_web` (its numbers are issued offline and a server-minted
    value would collide); `SHF` additionally checks `shift.open`; invalid
    types get `terminal.document_type_invalid`.
- **Web checkout (`Pos.Web`).** Register picker with auto-select when the
  store has exactly one register; session bootstrap; shift strip (open-shift
  banner or "Start shift" with opening float → SHF next-number + `POST
  /api/v1/shifts/open` + refreshed session); per-line discounts gated on
  `sale.discount`, clamped to the line total; a cash payment panel with quick
  tender buttons (Exact/100/500/1000) and change rounded to the location's
  increment; atomic submission to `POST /api/v1/sales` (SAL next-number +
  `CompleteSaleBody` with the cash payment) and a success panel with reset.
  All register context travels on `X-Device-Id`.
- Validation: 12 new `TerminalEndpointTests` through the real pipeline
  (register list and 403 without location access; session bootstrap with and
  without an open shift; wrong-location and suspended registers refused;
  sequential per-type numbering; SHF `shift.open` gate; invalid document
  type; physical devices refused `device.not_web`). Full API suite **152
  passing** — only the two PostgreSQL-guard members fail, and only because
  Docker is unavailable. Domain 369, Architecture 13, Security 52 green;
  `Pos.Web` builds with zero warnings. Docs: STATUS, ROADMAP, API §9 and
  DECISIONS ADR-0032 updated. This commit also carries the C14 web shell:
  the authenticated operations shell and API-backed cart workspace were
   never committed separately.

### 2026-09-16 — C16 web sale lifecycle

- **Backend.** Three routes through the real pipeline: `GET
  /api/v1/sales?locationId&from&to` (`sale.view`, scoped to the caller's
  assigned locations) returning summaries `{ id, number, status, businessDate,
  completedAtUtc, grossTotal, netTotal }`; `POST /api/v1/sales/{id}/reprint`
  (`sale.reprint`,
  reason ≤ 200 chars, append-only print log); `GET /api/v1/returns/{id}`
  (any of `sale.view | sale.return | sale.refund | inventory.adjust`, full
  return detail with lines, batch info and refund history;
  404 `return.unknown`, 403 `return.outside_scope` via `SalesReturnErrors`).
  Web contracts added: `PosSaleSummary`, `PosSaleDetail`, `PosSaleLineDetail`,
  `PosSalePaymentDetail`, `PosReturnDetail`, `PosReturnLineDetail`,
  `PosReturnRefundDetail`, `PosReference`, and the request records
  (`PosReprintSaleRequest`, `PosVoidSaleRequest`, `PosCreateReturnRequest`,
  `PosCreateReturnLine`, `PosRefundReturnRequest`, `PosDisposeReturnRequest`).
  `VaultFlowApiClient` gains `GetTextAsync`, `SearchSalesAsync`, `GetSaleAsync`,
  `GetSaleReceiptAsync`, `ReprintSaleAsync`, `VoidSaleAsync`, `GetReturnAsync`,
  `CreateReturnAsync`, `RefundReturnAsync`, `DisposeReturnAsync`.
- **`UserSession` register context.** `SetTerminal(PosTerminalSession)` now
  caches the terminal's `TerminalBusinessDate` and `OpenShift` alongside the
  device info, so pages can read register/shift context without re-calling the
  session endpoint. `ClearRegister()` wipes stale register identity on location
  mismatch.
- **`TerminalBar` shared component.** Register auto-select (single register in
  the store), shift strip with "Start shift" flow or open-shift banner,
  stale-register cleared on location mismatch; exposes `OnStateChanged` for
  pages to refresh after register or shift changes. Reused on sale detail,
  return detail and (implicitly) the new sale page.
- **`Sales.razor`** (`/sales`) — store/date-range search page gated by
  `sale.view`; results table with number, time, cashier, total, status;
  row click navigates to `/sales/{id}`.
- **`SaleDetail.razor`** (`/sales/{Id}`) — lines/payments/totals, receipt print
  and reprint with reason, void with reason (blocked without open register and
  shift), accept-return with per-line quantity; `TerminalBar` shown for
  Completed sales when the cashier has reprint/void/return permission; after
  accept-return, navigates to `/returns/{id}`.
- **`ReturnDetail.razor`** (`/returns/{Id}`) — return detail with lines/batch
  info, per-line disposition form (restock/quarantine/damaged/supplier-return/
  waste with reason-code mapping, gated by `inventory.adjust`), refund form
  (method, amount, tendered, provider reference; cash-locked for blind returns,
  gated by `sale.refund`), refund history table, back-to-sale link.
- **`NavMenu`** adds a "Sales" NavLink gated by `sale.view`. **`Home.razor`**
  adds a conditional "Review sales" secondary card for
  `CanViewSales`. **`app.css`** reworked: two-column `command-grid`,
  `primary-card` and `secondary-card` both span full width with distinct
  light backgrounds.
- Validation: 9 new `SaleLifecycleEndpointTests` through the real pipeline —
  sales search by store with the location/date filters honoured and another
  store's Store Manager forbidden; reprint logging the print and the audit
  while keeping the sale Completed, refused without `sale.reprint`, and
  refused at another store; return detail showing the lines and the refund
  history (referenced), a blind return's detail describing the return with no
  sale, an unknown return 404, and another store's Store Manager forbidden the
  detail. Full API
  suite **161 passing** — only the two PostgreSQL-guard members fail (Docker
  unavailable). Domain 369, Application 241 green; `Pos.Web` builds 0 warnings
  0 errors. Docs: STATUS, ROADMAP, API §9 updated.

### C17 — card/e-wallet + split payment checkout (`feat(pos-c17)`)
- `NewSale.razor`: the single cash tendered input is replaced by a payment
  allocation flow. A `List<PaymentInput>` tracks added payments (method, amount,
  optional tendered for cash, optional provider reference). The add-payment form
  presents method tabs (Cash / Card / E-wallet), a precise-money amount input
  seeded with the remaining amount (`MoneyPrecise` = `ToString("0.00########",
  InvariantCulture)`) to avoid rounding-induced payment mismatches at the 4 dp
  compare, a cash tendered field with quick-tender buttons (Exact / 100 / 500 /
  1000), and a provider reference (max 128 chars) for card and e-wallet.
  `Complete` is disabled until `RemainingToPay == 0`. Summary shows amount due,
  allocated, remaining (or estimated change for cash per `RoundToIncrement`).
  No backend changes required: the server-side payment validation (method enum,
  amount > 0, cash tendered ≥ amount, `sale.payment_mismatch` sum check at 4 dp,
  max 128-char provider reference) already supports the full payment mix.
- New CSS: `.allocated-list`, `.allocated-row`, `.method-chip` (`.method-1` /
  `.method-2` / `.method-3` with cash green, card blue, e-wallet purple),
  `.add-payment`, `.method-tabs`, `.quick-tender`, `.add-payment-btn`,
  `.remove-payment`, `.optional`, `.field-hint`. `Pos.Web` builds 0 warnings
  0 errors.
- New `SalePaymentEndpointTests` (6 integration tests): card-only with provider
  reference, e-wallet-only with provider reference, split cash + card (asserts
  both payments, cash change of 5 and card provider reference), split cash +
  e-wallet (asserts total allocated equals net total), under-coverage refused
  409 `sale.payment_mismatch`, over-coverage (cash amount > net) refused 409
  `sale.payment_mismatch`. API suite: **167 passing** (2 known Docker-unavailable
  Postgres failures). Domain 369, Application 241 green. Docs: STATUS,
  PROGRESS, ROADMAP updated.

### C18 — expired-batch sale blocking + authorized override path (`feat(pos-c18)`)
- The §5 contract (`inventory.expired_only`, `sale.expired_override`) was dead
  code: every FEFO shortfall returned generic `inventory.insufficient_stock`.
  `CompleteSaleCommandHandler` now classifies after the sellable allocation
  fails — if the sale's business-date expired shelf can cover the shortfall it
  refuses `inventory.expired_only` (409) so the terminal can offer the exception
  path; a genuine shortfall stays `inventory.insufficient_stock`.
- The exception path carries a mandatory reason per line:
  `CompleteSaleLine.ExpiredOverrideReason` (max 500 chars; empty →
  `sale.expired_override_reason_required`, too long →
  `sale.expired_override_reason_too_long`, both 400). Overriding a line without
  `sale.expired_override` is refused up front `sale.expired_override_denied`
  (403) — a cashier cannot even offer the path.
- `sale.expired.override` audit now records the cashier's reason and the
  authorizing user, which is stamped from the request context
  (`AuthorizingUserId`), never caller-supplied; the audit entry is also
  location-scoped (`LocationId: command.LocationId`, previously null).
- Web terminal (`NewSale.razor`): on submit the server's `inventory.expired_only`
  refusal triggers a confirmation dialog (reason textarea, 500-char limit) gated
  on `sale.expired_override` in the session; confirm re-submits with
  `expiredOverrideReason` per affected line. `Pos.Web` builds 0 warnings 0
  errors.
- Tests: 3 new validator rules + 2 handler refusals/reclassifications +
  reworked audit assertion (camelCase `authorizingUserId`); 4 new
  `ExpiredOverrideEndpointTests` through the real API pipeline — refusal from a
  line whose sellable shelf cannot cover it while the expired shelf could
  (balances untouched), override completion (sellable batch 3→0, expired batch
  5→4, audit row with reason + authorizer), missing reason 400, cashier without
  permission 403.
  Totals: Domain 369, Application 245, Infrastructure 80 (18 skipped),
  Security 52, Architecture 13, API **171 passing** (2 known Docker-unavailable
  Postgres failures). Docs: STATUS, PROGRESS, ROADMAP, POS updated.
