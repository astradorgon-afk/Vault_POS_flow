# Project Status

**Last updated:** 2026-09-17 · **Milestone:** Phase 12 complete (C28–C33); Phase 13 (synchronization) under way: C34 the device outbox, C35 the ledger running on the device — the same ledger the server runs, not a second one — C36 caching what a sale reads, C37 narrowing the sale's catalogue port, and C38–C42 completing offline POS: open a shift, sell, void, reprint, take a return, refund cash, close and reconcile — every POS entry executes on a device, C43 gives those events somewhere to go, C44 teaches the server what the shift ones mean, C45 lands the sale itself, C46 lets a stale price be recorded honestly, C47 brings the void and the reprint with it, C48 the return and the refund, C49 the retry queue that actually delivers them, and C50 the server's own feed for what comes back down

This is the working status document. [ROADMAP.md](ROADMAP.md) holds the full
item-by-item plan; this file says where things actually stand, what was learned,
and what to pick up next.

---

## 1. Where the build is

| | |
|---|---|
| Solution builds | Server, Windows client and Android client clean; warnings-as-errors and analyzers on. `Pos.Client` verified in Release through C32 on 2026-09-17 on Windows with the MAUI workloads: `net10.0-windows10.0.19041.0` and `net10.0-android` both 0 warnings, 0 errors. The same run found `Pos.Infrastructure.Tests` did not compile in Release (two unused `Microsoft.EntityFrameworkCore` directives, IDE0005, from C29/C31); fixed. |
| Tests | **1,195 passing without PostgreSQL: Domain 378, Application 247, Infrastructure 307, Security 52, Architecture 25, API 186** (2026-09-17, through C50 the server change feed). PostgreSQL tests require Docker; the suites ran in Release through C32 on a machine with Docker (Infrastructure 217 passed, API 176 passed, 0 skipped), including all 21 PostgreSQL tests — after an earlier run under heavy load had silently skipped the 18 Infrastructure ones (see §4). C33–C50 have not been run against PostgreSQL; C43 and C50 add the only PostgreSQL migrations among them. |
| Migrations | 30 PostgreSQL migrations plus 10 independent SQLite device migrations, all forward-only. The device migrations are exercised against encrypted SQLCipher storage. |
| API host on PostgreSQL | Covered by `PostgresHostSmokeTests` (start-up, sign-in, numbered documents, ledger posting) and a full compose-stack run through Caddy as `pos_app`. See §3 for what these found. |
| Phases complete | 0 (architecture), 1 (foundation), 2 (identity), 3 (master data), 4 (inventory core), 5 (purchasing: PO lifecycle + goods receipts + returns/direct delivery/discrepancy resolution), 6 (transfers: main warehouse → store), 7 (transfers: store-to-store — central review, pre-approval tokens, emergency transfers with dual-manager authorization, replenishment recommendations), 8 (quarantine and unauthorized inventory — incidents, lines, photos, HQ review, release caps), 9 (inventory control — approved stock adjustments, counts with variance posting, repeat-variance detection), 10 (batch and expiration — expiry warning thresholds, expiry run quarantining past-expiry stock as `EXP`-numbered groups), 12 (offline storage — encrypted device database, protected change feed, command boundary, device numbering, snapshot expiry, status surface and the shift lifecycle executing on a device) |
| Out-of-phase | Interim payment receipts (ADR-0026) — RCT-numbered cash documents, issue/view/print |
| Phases remaining | 13–18 — see §5 |

```
Pos.Domain.Tests            378 passing   invariants, money, ledger rules, catalog curation and price supersession/cancellation, stock adjustments and counts, purchasing (PO/receipts/returns/DDA/discrepancies), transfers, payment receipts, POS and customer-account rules
Pos.Infrastructure.Tests    307 passing   non-PostgreSQL ledger, numbering, catalog, migration-order, POS, notifications, encrypted device store and its raw keying, change-feed application and its write guards, device document numbering, offline permission evaluation, the device status surface, the device shift lifecycle end to end, the upload queue and the ledger running against the device store, and the upload engine replaying a shift's lifecycle centrally, with a coverage test tying every queueable event to an applier, the retry queue that drains the outbox, the register's own container resolving every use case it is allowed to run, and the server feed recording what those registers download
Pos.Architecture.Tests       25 passing   layering, ledger isolation, permission catalogue, client reference boundary and the device command whitelist
Pos.Security.Tests           52 passing   authentication, tokens, permission matrix, log scrubbing
Pos.Application.Tests       247 passing   master-data commands, CQRS behaviours, receipt rendering, POS handlers and named-customer sale validation
Pos.Api.IntegrationTests    186 passing   endpoints through the real pipeline (SQLite), including customer lifecycle, permissions, audit behavior, discount enforcement, the web-terminal checkout surface, sale-lifecycle read routes, the payment-mix checkout flows, the expired-batch override contract, scheduled-price cancellation, and a register uploading a shift and a sale it rang up through an outage, priced from a row since superseded, reprinted and then voided, and goods taken back and refunded
```

(Pos.Sync.Tests exists as the Phase 5+ sync shell and currently declares no tests.)

The PostgreSQL suite needs a Docker daemon. The local C11 run excluded tests
whose fully-qualified names contain `Postgres`; CI runs them with Docker.

---

## 2. What exists today

### Architecture and documentation
Sixteen documents in `docs/`, ~4,500 lines. The load-bearing ones are
[INVENTORY_LEDGER.md](INVENTORY_LEDGER.md), [OFFLINE_SYNC.md](OFFLINE_SYNC.md),
[SECURITY.md](SECURITY.md) and [PERMISSIONS.md](PERMISSIONS.md). Every
significant choice is recorded in [DECISIONS.md](DECISIONS.md) (31 ADRs).

### Phase 1 — Foundation
Clean Architecture solution, 7 source projects and 7 test projects with
inward-only references enforced by tests. Central package management, pinned
transitive packages carrying advisories. CQRS dispatcher with an ordered
behaviour pipeline. Serilog, correlation middleware, security headers, rate
limiting, RFC 9457 problem details that leak no internal detail. Docker images
for the API and a one-shot migrator, compose stack with Caddy, least-privilege
database roles, CI with a secret scan and a migration-drift check.

### Phase 4 — Inventory ledger (pulled forward, this session)
The double-entry, append-only ledger everything else posts through. Immutable
movements, a balance projection that can only change by applying a movement, and
four independent guards against direct stock assignment: domain types with no
setters, an EF interceptor, PostgreSQL triggers, and a database role with only
`SELECT, INSERT` on the ledger. Movement-type rules table, weighted-average
costing, negative-stock policy, idempotency by event id.

**Hardened this session (balances under contention):**
- `InventoryBalance.Version` optimistic-concurrency token (starts at 1, bumped
  on every `Apply`, `.IsConcurrencyToken()`). A writer whose projected row moved
  underneath it is refused by `DbUpdateConcurrencyException` instead of silently
  overwriting the newer projection.
- In-ledger retry: `InventoryLedger.PostAsync` replays only the projection step
  (`Apply`) under contention with exponential backoff, up to
  `MaxStandaloneAttempts` (10). The ledger append is never re-attempted —
  `event_id` idempotency would make re-inserts a no-op but re-reading the
  movements is still wasteful. The same retry protects idempotent-projection
  adjustments (corrections) whose movement already exists. Exhaustion surfaces
  `inventory.balance_contention` → HTTP 412.
- `IBalanceReconciler` (`BalanceReconciler`): replays the ledger into a fresh
  projection and compares it with the stored one. Detects missing/quantity/cost/
  value/last-movement/unexpected bucket drift. `RebuildAsync` forces
  `OriginalValue` of the version token, deletes, and re-inserts — under the
  still-armed deferred balance-guard trigger, which validates the rebuilt rows
  at commit.
- `BalanceReconcilerWorker`: optional read-only tripwire
  (`Reconciliation:Enabled`), runs a detect pass on startup and on an interval,
  and logs drift. Repair is deliberate and separately authorized via the
  rebuild endpoint.
- Endpoints: `POST /api/v1/inventory/reconcile` (read-only) and
  `POST /api/v1/inventory/rebuild-balances` (gated by `Maintenance:AllowBalanceRebuild`,
  503 `maintenance.disabled` when off). Both require `inventory.rebuild_balances`.
- Concurrency tests against SQLite **and** PostgreSQL (Docker): parallel posts
  to one bucket, a stale read pinned to an earlier version, cost-drift under
  interleaved writes, idempotent-projection repair under contention, and a
  reconciler suite that also proves the rebuilt projection and the deleted-row
  case. Migration `20260912122753_BalanceConcurrencyToken`.

**Completed in gap batch 5 (ADR-0030):**
- Refused draws are recorded: the ledger collects each bucket it refuses, and a
  pipeline behaviour writes them after the unit of work has committed or rolled
  back, through a separate context, as `inventory.negative_stock_attempt` rows
  (append-only: interceptor, triggers, grants) with an
  `inventory.negative_stock.attempted` audit entry. Migration
  `20260914110000_NegativeStockAttempts`.
- Report: `GET /api/v1/inventory/exceptions/negative-attempts` and `/summary`
  (attempts and total shortfall per product and location), `inventory.view.all`.
- Partitioning of `inventory_movement` and `audit_log` decided against for v1,
  with revisit thresholds (ADR-0030).

### Phase 2 — Identity and authorization
- ASP.NET Core Identity with Guid keys, PBKDF2 at 600,000 iterations,
  NIST-style password policy (length over composition rules).
- 74-permission catalogue defined **in code** and seeded to the database, so a
  permission cannot be invented by editing a table.
- Seven roles as permission bundles. No code branches on a role name.
- Per-user overrides with grant/deny, expiry and a mandatory reason. Deny always
  wins; expiry is evaluated at read time rather than trusted to a purge job.
- Location scoping: every check is a pair of (permission, location). This is what
  confines a store manager to their own store.
- Approval tiers with value ceilings, self-approval refusal, and an eligible-
  approver lookup.
- RS256 tokens with a two-key ring for rotation. Access tokens carry **no
  permission list** — permissions resolve per request from a cache keyed by the
  policy version, so revoking authority takes effect at once.
- Refresh-token rotation with reuse detection: presenting a spent token burns the
  whole family.
- Cashier PIN sign-in, bound to an enrolled device at a location the cashier is
  assigned to, granted only the offline-capable permission subset.
- Device lifecycle: register, one-time enrolment code (hashed, single-use,
  burn-on-guessing), suspend, reactivate, revoke.
- Append-only audit log under the same four guards as the ledger, plus sign-in
  attempt history with hashed identifiers.
- Bootstrap owner seeder that refuses to run if any user exists.
- **Administration (2026-09-14, ADR-0028):** `/api/v1/users` (create, update,
  disable/enable, roles, locations, overrides, password, PIN, two-factor reset) and
  `/api/v1/roles`, `/api/v1/permissions`. Every change is audited and bumps the
  policy version in the same transaction. Safeguards resolved fresh from the
  database: no self-administration; governance permissions (`Permissions.Privileged`)
  and approval tiers can only be given by someone holding them; nobody changes an
  account or role that outranks them; a change leaving no active user/role manager
  is rolled back. Default role grants are applied once
  (`core.role_default_grant_applied`), so an administrator's removal survives restarts.
- **Two-factor enforced** for accounts holding `user.manage`/`role.manage` when
  `Security:RequireTwoFactorForAdmins` is on (production default): password-
  authenticated enrolment (`auth/two-factor/setup`, `/enable`), eight one-time
  recovery codes, administrator reset that rotates the key.
- **Log scrubbing:** `SensitiveDataScrubber` masks secret-named properties in every
  log event, including nested objects and dictionaries.

### Phase 3 — Master data (earlier session)
- **Domain and persistence:** `Location` (JSON `LocationSettings`),
  `Organization`, `Product` aggregate with `ProductBarcode`, `ProductPrice`,
  `ProductUnitConversion`, `ProductLocationSetting`, `ProductSupplier` children,
  plus `ProductCategory`, `Brand`, `UnitOfMeasure`, `Supplier`. Price overlap
  exclusion constraint and product-name trigram index in migration
  `20260912074419_MasterDataCatalog`.
- **Locations:** `CreateLocationCommand` and `UpdateLocationSettingsCommand`
  (`settings.manage`, route-scoped). Settings on a system counterparty or a
  closed location are refused. `ILedgerPolicyProvider` now reads the negative-
  stock policy from the location's own settings, failing closed to the strictest
  option on a missing or unreadable row (`LocationSettingsLedgerPolicyProvider`).
- **Endpoints** (all behind the real authorization pipeline): `GET/POST
  /api/v1/locations`, `PUT /api/v1/locations/{id}/settings`, and the
  `/api/v1/catalog/**` group — product list (search/filter/page), by id, by
  barcode, product create, and read+create for categories, brands, units of
  measure and suppliers. List reads use the deliberately relaxed `product.view`
  so POS devices can resolve catalogue and location scope offline; writes sit on
  their `*.manage` permissions.
- **Development seed data:** idempotent `DevelopmentDataSeeder` gated by
  `Database:SeedDevelopmentData`. Seeds the three system counterparties, one Main
  Warehouse, three stores, reference master data, a small product catalogue with
  barcodes, and — only when additionally gated by `Seeding:EnableDevelopmentAccounts`
  — ten staff accounts with a documented development password. The whole seed
  runs in one transaction; the accounts seed is intentionally conservative
  (default location settings stay strict).
- **Tests:** command validator + handler unit tests, `LocationSettingsLedgerPolicyProvider`
  tests against SQLite, and endpoint integration tests through the real pipeline:
  authentication, authorization, validation, persistence, and the 404/409
  contracts the POS and quarantine clients depend on.

- **Catalog curation (gap batch 4, ADR-0029):** product edit (tracking flags,
  shelf life and base unit stay fixed), deactivate/activate with a reason and a
  discontinuation date, barcode attach/retire/primary (retired codes stop
  resolving in the by-barcode lookup, search and quarantine identification but
  stay reserved; migration `20260914100000_ProductBarcodeRetirement`),
  effective-dated prices that supersede their predecessor and resume after a
  temporary price, per-location stocking settings, unit conversions and
  product–supplier links (one preferred). Product reads hide the default cost
  without `product.cost.view`. Every change is audited in its transaction.
  Reference checks for category, brand, supplier, unit and location, because the
  product tables carry no foreign keys to master data.

### Phase 5 — Purchasing (part 1: PO lifecycle, part 2: goods receipts)

- **Purchase order lifecycle:** draft → submit (`purchase.create`) → approve /
  reject (`purchase.approve` + value tier) → send → `Ordered`; cancel, withdraw
  (DELETE, Draft only), and close (reason required unless `FullyReceived`).
  Self-approval above the limit is refused and every decision writes a
  `purchase_approval` row. PO numbers (`PO-…`) are allocated only on submit, so a
  rejected submit never burns a sequence value.
- **Goods receipts (atomic post):** `POST /api/v1/purchasing/orders/{id}/receipts`
  validates and posts in one transaction — GRN number (`GRN-…`), receipt rows,
  discrepancies, ledger movements, batch rows and the order's received totals
  commit together. There is no mutable receipt draft.
- **Disposition planning** (`GoodsReceipt.Create`): expected quantity is derived
  from the PO line minus cumulative receipts; overage within 5% is accepted into
  `PendingInspection`, excess beyond tolerance quarantines; `Damaged`,
  `WrongItem`, `Expired` and `MissingDocuments` produce discrepancy rows with
  value impact; shortages create discrepancies but no ledger rows.
- **Batches and expiry:** batch-tracked products require a lot number
  (unique per product), expiry-tracked products require `ExpiresOn`, and an
  expired-on-arrival lot is refused unless the *entire* lot is rejected (which
  posts it to quarantine). The batch id travels with subsequent movements.
- **Costing:** actual unit cost updates `ProductSupplier.LastCost`; a deviation
  beyond 5% refuses to post unless the receiver holds `purchase.approve` (with
  the usual tier check), then records the approver on each deviating line. The
  tests caught that the order *creator* cannot be that approver — the
  self-approval rule extends to the cost-variance grant, so a receipt on your
  own order must be received by someone else or at the ordered cost.
- **Ledger:** `SupplierReceipt` movement group (external → accepted/damaged/
  quarantined states) under `inventory.receive`, refenced by the GRN number.
- **Migrations:** `20260912145040_PurchasingCore`, `20260912161554_GoodsReceiptCore`
  (`inventory.batch`, `purchasing.goods_receipt`, `purchasing.goods_receipt_line`,
  `purchasing.receiving_discrepancy`, purchase approvals, document counters).
- **Tests:** 24 domain tests for receiving plus 11 endpoint tests through the
  real pipeline — including the location-scoped store-manager receiving path, the
  two-receipts-make-`FullyReceived` accumulation, and both cost-variance
  authority paths — on top of the part-1 PO lifecycle suite.

### Phase 5 (part 3) — direct delivery, supplier returns, discrepancy resolution

- **Direct-delivery authorizations (DDA):** issue, list (newest first,
  `activeOnly` filter) and revoke. An authorization carries a validity window
  (default 180 days, cap < 365 days), an optional per-DDA value cap and the
  revoker's identity. Write permission is `purchase.direct_to_store.authorize`;
  `purchase.view` covers the list. Only `Active` authorizations survive the
  filter; revoking twice is refused (`purchasing.direct_delivery_already_revoked`).
- **Supplier returns (SRT):** full lifecycle draft → submit → approve → dispatch
  → confirm. A return sources only from `Damaged`/`Expired`/`Quarantine` stock
  at the location (`purchasing.return_source_state_invalid` otherwise — returns
  may never source available stock). Discrepancies are recorded on the document.
  The creater cannot self-approve (same separation-of-duties gate as orders);
  approval records the approver. **Dispatch** allocates the SRT number last —
  after every authority and state check — so a refused dispatch can never burn a
  sequence value — and posts a balanced `SupplierReturn` movement group: the
  source state at the location (negative leg) to the `EXT-SUPPLIER` counterparty
  (`External` state, positive leg), both stamped with the SRT number and the
  approver as the ledger actor. Drafts share a blank number, so the unique
  number index is filtered (`number <> ''`) on both the domain entity and the
  database. Confirm closes the document.
- **Receiving-discrepancy resolution:** `POST
  /api/v1/purchasing/discrepancies/{id}/resolve` under
  `purchase.discrepancy.resolve`, with an outcome from the resolution enum
  (`SupplierCredit`, `Replace`, `WriteOff`, `Refund`, `NoAction`, `Other`) and a
  note. Location scope is enforced explicitly on the resolver (`purchasing.discrepancy_resolve_forbidden`
  → 403). Resolution is once-only (`purchasing.discrepancy_already_resolved` → 409).
- **Endpoints:** `POST/GET /api/v1/purchasing/direct-deliveries`, `GET
  /api/v1/purchasing/direct-deliveries/active`, `POST
  .../direct-deliveries/{id}/revoke`; `GET/POST
  /api/v1/purchasing/supplier-returns`, and `submit`/`approve`/`dispatch`/`confirm`
  actions; `POST /api/v1/purchasing/discrepancies/{id}/resolve`.
- **Migrations:** `20260912180239_PurchasingDirectDeliveryAndReturns` adds the
  DDA and supplier-return tables, the return lines, the resolution columns, and
  the filtered `ux_supplier_return_number` index.
- **Tests:** 30 purchasing domain tests (DDA window/cap/revoke rules, the return
  state machine, resolution) and 7 endpoint tests through the real pipeline —
  the full SRT lifecycle asserting the ledger legs carry the SRT number,
  dispatch-before-approval refuses and posts nothing, draft uniqueness against
  the filtered index, sourcing refusals, role refusals, and resolve-once.

### Phase 6 — main warehouse → store transfers

- **Aggregate and state machine:** `TransferOrder` (Draft → Submitted →
  InReview → Approved → Picking → Ready → Dispatched → Received/PartiallyReceived
  → Closed, plus Cancelled) with line items, pick allocations (quantity,
  batch, unit cost from the batch at pick time), arrival discrepancies and
  chain-of-custody events (`TransferCustodyEventKind`). Review can amend lines
  before approval (`transfer.amendment_invalid` otherwise); approval is gated by
  tier and refuses the document creator (`transfer.self_approval_forbidden`).
- **Document numbering:** drafts share the blank number (filtered unique index
  `number <> ''`, same pattern as supplier returns); the TRF number is allocated
  only at dispatch — after every authority and state check — and the TRC receipt
  number only at receive, so refused actions never burn sequence values.
- **Picking and FEFO:** `Pick` validates source scope, per-line totals, and skips
  the FEFO guard for non-transit-tracked products. For tracked products a pick
  that leaves an earlier-expiring lot untouched is refused
  (`transfer.pick_skips_earlier_expiry`). Allocation cost is captured from the
  batch's unit cost at pick time.
- **Ledger:** dispatch posts the balanced `Available@src −q ⇄ InTransit@src +q`
  group (per allocation, batch-aware) under `TransferDispatch`; cancel-dispatch
  reverses it under `TransferCancelDispatch` against the same shipment
  number, requires a reason and an approver, and refuses after any arrival.
  Receive posts `InTransit@src −q ⇄ Available@dest +q` per allocation under
  `TransferReceipt` (shortfall → `TransitVariance`, damage → `Damaged`).
- **Discrepancy resolution:** `resolve` under `transfer.reconcile` is once-only
  (`transfer.discrepancy_already_resolved` → 409), stays on
  `PartiallyReceived`, and posts a zero-sum group: `TransitVariance −q` at the
  destination plus `Available@dest +q` (Found) or `EXT-WRITEOFF +q` (Write-Off).
  The tests caught a genuine single-leg bug here — the resolution group was
  posted unbalanced, violating the ledger invariant — now fixed and covered.
  Verify accepts `Received` and `PartiallyReceived` with all discrepancies
  resolved (`transfer.verify_has_open_variance` → 409 otherwise) and closes.
- **Scoping and permissions:** list view filters transfers touching the caller's
  assigned locations (`transfer.view_location_forbidden` → 403 otherwise); pick
  and dispatch scope to the source, receive to the destination; cancel-dispatch
  additionally requires `transfer.approve`. Store managers hold request/pick/
  dispatch/receive/verify but not approve or reconcile.
- **Migrations:** `20260912203959_AddTransfers` (`transfers` schema: transfer,
  line, allocation, discrepancy, custody-event tables + filtered number indexes).
- **Tests:** 18 transfer domain tests and 8 endpoint tests through the real
  pipeline — full lifecycle posting a balanced ledger and closing, FEFO
  enforcement, partial arrival with shortage resolved Found (stock returns to
  `Available`), write-off posting to the external counterparty, cancel-dispatch
  reversal with reason and approval, cashier refusal, location-scoped lists, and
  resolve-without-permission.

### Phase 8 — quarantine and unauthorized inventory (this session)

- **Aggregate:** `QuarantineIncident` (Open → UnderReview → Approved /
  PartiallyApproved / Rejected / UnderInvestigation; Closed only when every
  line's remaining quantity is zero). Lines carry the raw barcode, quantity,
  unit cost, claimed product name, and identification state (product + optional
  lot). Photos are stored in the database, capped at **5 MB** each
  (`quarantine.photo_too_large` otherwise). Incident numbers are `QRT-{yyyy}-
  {000000}` from a server-side counter.
- **Raising:** `POST /api/v1/quarantine` (`quarantine.create`) posts a
  `QuarantineEntry` ledger group (`EXT-SUPPLIER/External −q ⇄ Loc/Quarantine +q`)
  for every line. Lines whose scanned barcode already resolves to a non-batch-
  tracked product are identified at raise and post immediately; batch-tracked
  and unknown barcodes wait for HQ identification. Raising at a system
  counterparty or an unassigned store is refused (`quarantine.location_external`,
  or 403 from the pipeline's second location-scope check before any business
  rule runs).
- **HQ review:** `investigate` (→ UnderReview), `link-product` and
  `register-product` (identify lines against existing/new products — the barcode
  must already belong to the product: identification never attaches barcodes,
  `quarantine.barcode_not_owned`), `release` (→ `Available`, approver recorded,
  no reason), `reject` (→ `EXT-SUPPLIER`, approver + reason required), and
  `write-off` (→ `EXT-WRITEOFF`, `quarantine.reject` **and**
  `inventory.adjust.approve`, reasons whitelisted). Quantities are capped per
  line at what remains; a resolved incident refuses further disposition (409).
  The supplier bucket nets to zero after a reject (entry −q, reject +q).
- **Scope:** list and detail reads resolve against database authorizations, not
  the token; a user assigned to several stores still sees exactly what the
  assignments cover. Every command is re-scoped against the incident's own
  location in the pipeline (`quarantine.outside_scope` → 403) — the transfer
  pattern carried forward. Documents: `QRT` numbers, ledger movement groups
  `QuarantineEntry`/`QuarantineRelease`/`QuarantineReject` in
  INVENTORY_LEDGER.md, full route table with error codes in API.md §7, design
  intent in QUARANTINE.md.
- **Migrations:** `20260913081458_QuarantineIncidents` (incident, line, photo
  tables + filtered `QRT-` document counter).
- **Tests:** 10 endpoint tests through the real pipeline — raise at external
  location refused, batch-tracked line requiring a lot, register-product and
  link-product identification (barcode ownership, batch consistency,
  already-identified), release to Available with approver recorded and caps,
  reject netting the supplier bucket to zero, write-off gated by the second
  permission and reason whitelist, cross-store 403s, and photo flows.

### Phase 9 — inventory control (ADR-0031)
- **Stock adjustments** (`StockAdjustment`, ADJ): Draft → PendingApproval →
  Posted, Rejected or Reversed. The reason decides the ledger movement — damage,
  spoilage, loss and theft write off to EXT-WRITEOFF, Expired moves stock to the
  Expired state or writes it off, only `Other` (with notes) may add stock. Unit
  costs are captured when raised. Approval posts in the same transaction through
  the approval gate: value tier, location scope, never the author. Reversal posts
  opposite `Reversal` groups at the original costs.
- **Inventory counts** (`InventoryCount`, CNT): full, cycle, category and product
  counts of available stock. The sheet comes from the ledger; recording a line
  re-reads its bucket so mid-count sales are not shrinkage; submission needs
  every line counted and flags products that varied on another posted count in
  the last 90 days; approval refuses lines whose stock moved after counting and
  posts only the variance as count adjustments. Reject returns to counting;
  cancel ends it.
- **Reports:** variance lines of posted counts, and repeat variances ranked by
  occurrences and value. Lists and reports are confined to the caller's locations.
- Migration `20260914120000_InventoryControl`. Tests: domain rules for both
  aggregates; endpoint tests for approval tiers, scope, self-approval, reversal,
  stale counts and repeat variance; the whole flow on PostgreSQL.

### Interim payment receipts (ADR-0026)

ADR-0025 weighed an organisation-level subscription billing module and was
superseded the same day: the stores are branches of one business, so the only
real v1 need was a printable record of cash taken in or paid out.

- **Aggregate:** `Receipt` — `ReceiptKind` (`WalkInSale`, `BranchExpense`,
  `OwnerWithdrawal`), branch, positive amount, optional trimmed counterparty and
  note, optional reference number normalised through `DocumentNumber.Parse`,
  issuer and issue time. No ledger entry, no balance, no child tables.
- **Numbering:** `RCT-{yyyy}-{000000}` from the shared document counter
  (`DocumentType.Receipt = 14`), allocated inside the issuing transaction.
- **Endpoints:** `POST /api/v1/receipts` (`receipt.create`, location-scoped in the
  pipeline, external counterparties refused), `GET /api/v1/receipts/{id}` and
  `GET .../{id}/print` (`receipt.view`, re-scoped to the receipt's location).
  Printing is plain text from `ReceiptRenderer`, the seam for a thermal or PDF
  layout later.
- **Roles:** Owner, Administrator, Main Inventory Manager, Store Manager (scoped)
  issue and view; Auditor views.
- **Migration:** `20260913112917_AddReceipts` (`core.receipt`).
- **Tests:** 9 domain tests on the creation contract and 4 endpoint tests —
  issue/detail/print at the manager's own store, fresh numbers per receipt,
  cashier/auditor/other-store refusals, and the external-location and
  input-validation errors.

### Phase 11 — POS, returns side (the "C" batch series)

Phase 11 is being built in small "C" batches; the customer-return and receipt
work landed before the shift/sale/payment bulk. POS.md §4 is the contract: a
referenced return posts `EXT-CUSTOMER −q → Store/ReturnPending +q`; a **blind**
return does the same plus an exception record; return quantity per line is
capped at `SaleItem.Quantity − SaleItem.ReturnedQuantity`; refunds are capped by
the original payment method; reprints are read-side but never silent.

- **C1 — void** (`4a81731`). Same shift, same business day; `sale.void`; a
  `SaleVoided` reversal group referencing the original sale plus the mandatory
  `sale.voided` audit with reason.
- **C2 — referenced return and refund** (`adf1a65`). `SalesReturn.Create` mirrors
  each returned sale line as a frozen proportional snapshot inside
  `ReturnWindowDays` (default 7); the zero-sum `CustomerReturn` group posts
  `External → ReturnPending`; `RefundSalesReturnCommand` issues refunds capped
  per way the sale was actually paid. Migration `20260914200101_AddSalesReturnRefund`.
- **C3 — receipt reprint** (`ac46de3`). `SaleReceiptPrint` append-only log
  (required reason ≤ 200 chars on reprints, none on first prints), `sale.reprint`,
  `sale.receipt.reprinted` audit. Migration `20260915035139_AddSaleReceiptPrintLog`.
- **C3b — blind return** (`c829307`). `SalesReturn.CreateBlind` with a null
  `SaleId`, `IsBlind`, and `BlindReturnItemSpec` lines (no sale item). Priced at
  the catalogue at the returned instant (the sale's remembered price does not
  exist), VAT split mirrored from sales (vatable/exempt/zero-rated), the same
  zero-sum `CustomerReturn` ledger group, `sale.return.created` plus the
  `sale.return.blind.accepted` exception audit with the reason, and a 200-
  character reason cap. `sale_id` and `sale_item_id` become nullable and
  `is_blind` is added in migration `20260915051436_AddBlindReturns` (the guard
  ran against PostgreSQL). Referenced paths were tightened for the nullable keys;
  a blind return's **refund** is handled by C4 below (the existing refund handler
  rejects it through its sale-id check).
- **C4 — blind-return refund.** `RefundBlindSalesReturnCommand` has no
  `SaleId` — a blind return accepted goods back with no original sale, so its
  refund cannot be capped by a sale's payment mix. `SalesReturn.IssueBlindRefund`
  enforces the return's own `RefundableTotal`, requires `IsBlind`, and returns
  **cash only** (`sale.refund.blind.cash_only`): with no original payment record
  to reverse, there is nothing to hand back through card or wallet, and the
  cashier reconciles the refund to the drawer. The handler pre-checks idempotency
  by event, then the return's existence/blindness/location/device and an open
  shift on the same device, and reuses the referenced refund's
  `sale.refund.issued` audit and persistence. Referenced refunds stay capped per
  method plus per return; the blind refund skips the per-method cap and clears
   the same `sale.refund` permission. No migration: the refund table is C2.

Tests: 10 domain tests for the blind-refund contract (cash-only methods, tendered
and increment rules, cap accumulation, a referenced return refused
`sale.refund.blind_only`); 16 handler tests for `RefundBlindSalesReturnCommandHandler`
(happy path persisting with its audit and loading no sale, replay idempotency,
non-blind/location/device/shift/location-facts errors, aggregate cap errors,
persistence failure); one repository round-trip proving a blind cash refund
persists and reads back from the return and by event. The full suite ran after
C4: Domain 345, Application 200, Infrastructure 64 (+18 skipped PostgreSQL
guards), Security 52, Architecture 13, API 110 (the two PostgreSQL-guard members
need a Docker daemon).

- **C5 — shift lifecycle with cash reconciliation.** `CashierShift` gains
  `IsForceClosed` (bool, private-set, default false), `ForceClose(DateTimeOffset)`
  (allowed from `Open` or `Suspended` → `Closed`, sets the flag), and
  `Reconcile(decimal varianceThreshold, string? reason)`: the variance is only
  accepted when `CashVariance` is within threshold; a force-closed shift (no
  count, so `CashVariance` is null) always requires a reason. Threshold is clamped
  at zero (`Math.Max(0m, ...)`). New audit actions: `ShiftSuspended`,
  `ShiftResumed`, `ShiftReconciled`, `ShiftForceClosed`.
  Application: `SuspendShiftCommand`, `ResumeShiftCommand`,
  `ReconcileShiftCommand` (with `Reason?`, `ReasonMaxLength = 200`),
  `SuspendShiftCommandValidator`, `ResumeShiftCommandValidator`,
  `ReconcileShiftCommandValidator` (reason required when variance exceeds
  threshold, capped at 200 chars), and three handlers behind a shared
  `ShiftCommandAccess` gate (owner match OR `sales.close_other_shift` permission).
  Reconcile handler loads `LocationFacts` for `CashVarianceThreshold`; falls back
  to `LocationSettings.Default` when the location has no threshold.
  Repository: `GetForceCloseCandidatesAsync` (open/suspended shifts inner-joined
  to their location for `MaxShiftHours`). Infrastructure: `ShiftForceCloseWorker`
  (`BackgroundService`, `ShiftForceCloseOptions`, `IntervalHours` default 1,
  `RunOnStartup` default false), `IsForceClosed` column mapped to
  `is_force_closed`. Migration `20260915125042_AddShiftForceClose`.
  Tests: 13 domain tests (opening happy path, suspend/resume transitions,
  `ShiftInvalidState` for each transition, `ReconcileReasonRequired`,
  `ReconcileReasonTooLong`, force-close from Open and Suspended, force-close from
  Closed refused); 25 handler tests (8 Suspend, 7 Resume, 10 Reconcile happy
  paths, owner and `sales.close_other_shift` permission paths, location mismatch,
  shift unknown, wrong state); 12 validator tests (required fields, reason
  length); 7 infra round-trips (force-close persists the flag, force-close
  survives round-trip, suspend-resume round-trips, force-close candidates by
  age).

- **C6 — sale endpoint surface and the receipt render.** The POS sales routes,
  through the real pipeline. New permission `sale.view` (read-only, offline-safe,
  granted to Store Manager, Cashier and Auditor; Owner inherits the catalogue,
  Administrator deliberately stays off the `sales.*` group). Domain gains
  `SaleErrors.Unknown` (404 `sale.unknown`) and `SaleErrors.OutsideScope`
  (403 `sale.outside_scope`). `POST /api/v1/sales` takes the full sale as one
  payload (`CompleteSaleBody`) and dispatches `CompleteSaleCommand` — the 
  command is `IAuthorizedMessage` + `ILocationScoped`, so the pipeline caps
  `sale.create` at the caller's assigned locations and the handler re-derives
  price (via `PriceAt`), VAT and FEFO allocation as before. `GET
  /api/v1/sales/{id}` and `GET /api/v1/sales/{id}/receipt` require `sale.view`
  and re-check it against the *sale's own* location (a Store Manager of another
  store gets `sale.outside_scope`; the Auditor reads business-wide). The receipt
  route renders through the new `SaleReceiptRenderer` plain-text form (same wire
  shape as the payment-receipt renderer: branch wall-clock time, line list,
  totals, payments with tendered/change) and logs the first print via
  `SaleReceiptPrint.Create(..., isReprint: false, reason: null)`. No migration:
  `sale.view` flows through the permission-seeder catalogue and `ReceiptPrints`
  is already the C3 table. Tests: 5 endpoint tests through the real pipeline —
  complete-a-cash-sale-creates-it, reads back the detail, renders the receipt and
  logs exactly one first print; a product with no effective price is refused
  409 `sale.item.price_missing` (the sale requires the EXT-CUSTOMER counterparty
  location before it can post); selling past the sellable shelf is refused
  `inventory.insufficient_stock`; another store's Store Manager is forbidden the
  detail and the receipt while the Auditor still sees it; an unknown sale is
  404. Suite at C6: Domain 358, Application 237, Infrastructure 71 (+18 skipped
  PostgreSQL guards), Security 52, Architecture 13, API 115 (the two 
  PostgreSQL-guard members need a Docker daemon).

- **C7 — daily sales summary report.** One read route for a location's day:
  `GET /api/v1/reports/daily-sales?locationId={id}&date={yyyy-MM-dd}` under the
  existing `report.view` permission (`Administration.ViewReports`) — no new
  permission and no migration. The route is `Scope = ScopeSource.None` and
  re-checks the permission manually against the requested location, so an
  other-store Store Manager gets 403 `report.outside_scope`, while only
  `AllLocations` holders (Auditor/Owner/Administrator) can reach an unknown
  location to get 404 `report.location_unknown`. Domain: `DailySalesReport`
  (with `DailySalesReportSalesSummary`, `DailySalesReportPaymentMethodSummary`,
  `DailySalesReportShiftSummary`) and `ReportErrors` (404
  `report.location_unknown`, 403 `report.outside_scope`). Infrastructure:
  `DailySalesReportRepository.GetDailySalesReportAsync` in one AsNoTracking
  pass — counts/totals over `Completed` sales at
  location + business date; `RefundTotal` joins `Refunds → CashierShift` on the
  shift's location + business date so **referenced and blind refunds both count**
  toward the day; per-method amounts over the `Payments` shadow key `SaleId`
  with change; per-shift stats (status, opening float, sales count/net,
  `CashSalesTotal`, `CashRefundsTotal`) feeding the cash reconciliation view.
  The wire model maps strong IDs to plain GUIDs. Tests: 4 endpoint tests through
  the real pipeline — a two-sale day (cash at £200-tendered and a cash/card
  split) aggregated into summary, per-method amounts/change and one shift row;
  a referenced return with a £45 cash refund (seeded via the public
  `SalesReturn.Create`/`IssueRefund` surface against the HTTP-completed sale)
  appearing in `refundTotal` and the shift's `cashRefundsTotal`; another store's
  Store Manager refused 403 while the Auditor reads; an unknown location 404.
  Suite at C7: Domain 358, Application 237, Infrastructure 71 (+18 skipped
  PostgreSQL guards), Security 52, Architecture 13, API 119 (the two 
PostgreSQL-guard members need a Docker daemon).

- **C8 — sale pipeline tests and the void route.** Completes the sale lifecycle
  HTTP surface and tests it through the real pipeline. `POST
  /api/v1/sales/{id}/void` (`VoidSaleBody`: `eventId`, `locationId`, `shiftId`,
  `deviceId`, `businessDate`, `voidedAtUtc`, `reason`) dispatches the C1
  `VoidSaleCommand` under `sale.void` with `VoidedByUserId` from the
  authenticated request — no migration, no new permission. The command is
  `IIdempotentCommand` so a retried void replays under the same `EventId`
  instead of double-posting. Tests: 4 integration tests through the real
  pipeline — selling past the sellable shelf is refused 409
  `inventory.insufficient_stock` before payments are considered; payments that
  under-cover the net total are refused 409 `sale.payment_mismatch`; a
  non-exempt default product's line records the location VAT rate (12%),
  `isVatExempt` false and `isZeroRated` false (zero-rating has no product flag
  yet, §2.6); and voiding a completed sale restores the shelf to its exact
  pre-sale quantity. The suite caught two test-side facts on the way to green:
  the sale-detail route exposes lines as `lines` (not `items`), and PIN sign-in
  requires the device **GUID** in `X-Device-Id` — the device code string is
  refused. Suite at C8: Domain 358, Application 237, Infrastructure 71 (+18
skipped PostgreSQL guards), Security 52, Architecture 13, API 123 (the two
   PostgreSQL-guard members need a Docker daemon).

- **C9 — the returns HTTP surface.** Wires the C2/C3b/C4 returns and refunds up
  as endpoints through the real pipeline. `POST /api/v1/returns`
  (`sale.return`) dispatches the referenced `CreateSalesReturnCommand`, `POST
  /api/v1/returns/blind` (`sale.return_blind`) the blind
  `CreateBlindSalesReturnCommand`, and `POST /api/v1/returns/{id}/refund`
  (`sale.refund`) branches on the body's optional `saleId` — present and
  matching the return's own sale dispatches the referenced
  `RefundSalesReturnCommand`, absent or mismatched the blind-refund path. All
  three take the full command as one payload (incl. `number` parsed via
  `DocumentNumber.Parse`, `eventId` for replay safety and a RET-prefixed
  `DocumentType.SalesReturn` number), carry `RequirePermission` with `Scope =
  ScopeSource.None`, take identity/`UserId`s from the authenticated request, and
  respond `201 Created` for returns and `200` for refunds. No migration, no new
  permission. **Not in C9:** the `POST /api/v1/returns/{id}/disposition` route
  from the plan stays pending — `SalesReturn` has no disposition aggregate
  method and no application command yet (only `InventoryMovementType.ReturnDisposition`
  and its `MovementTypeRules` entry exist). Tests: 4 integration tests through
  the real pipeline — a referenced return `201` then a cash refund `200` with
  its cap refusal on the second refund of the same value (the per-method
  running total makes a second full refund hit `sale.refund.exceeds_paid_for_method`);
  returning more than the sale sold refused `409
  sale.return.quantity_exceeds_available`; a blind return `201` with its cash
  refund `200` and the card refund refused up-front `400
  sale.refund.blind.cash_only`; and a refund naming a *different* sale refused
  `409 sale.refund.sale_mismatch`. Suite at C9: Domain 358, Application 237,
  Infrastructure 71 (+18 skipped PostgreSQL guards), Security 52, Architecture
  13, API 127 (the two PostgreSQL-guard members need a Docker daemon).

Tests: 11 domain tests for the blind-return rules (VAT splits, quantity and
reason caps, empty and non-positive lines); 14 handler tests for the
`CreateBlindSalesReturnCommand` (happy paths posting the ledger group and both
audits, persist at today's price, unknown-product/price-missing/location and
missing-external-customer errors, device and shift checks, ledger failure); two
repository round-trips proving a blind return persists and reads back with its
sale-free lines. The suit caught three test-side bugs on the way to green: a
price scheduled in the past was correctly refused by the no-backdating rule
(ADR-0029) and silently ignored, the product helper never passed its default
purchase cost to `Product.Create`, and one error-code assertion hardcoded the
wrong string (`sale.external_customer_missing`).

- **C16 — web sale-lifecycle slice.** Completes the web workflows for past
  sales. Backend adds three routes to the real pipeline: `GET
  /api/v1/sales?locationId&from&to` (`sale.view`, scoped to the caller's
  assigned locations) returning a list of summaries (`{ id, number, status,
  businessDate, completedAtUtc, grossTotal, netTotal }`); `POST
  /api/v1/sales/{id}/reprint` (`sale.reprint`, reason ≤ 200 chars, append-only
  print log); `GET /api/v1/returns/{id}` (any of `sale.view | sale.return |
  sale.refund | inventory.adjust`, returns the full return
  detail with lines, batch info and refund history; 404 `return.unknown`,
  403 `return.outside_scope` through `SalesReturnErrors`). Nine new
  `SaleLifecycleEndpointTests` through the real pipeline: sales search by
  store with the location/date filters honoured, and another store's Store
  Manager forbidden; reprint that logs the print and the audit and keeps the
  sale Completed, refused without `sale.reprint`, and refused at another
  store; return detail showing the lines and the refund history (referenced),
  a blind return's detail describing the return with no sale, an unknown
  return 404, and another store's Store Manager forbidden the detail.
  Web (`Pos.Web`): `PosContracts` records for summary/detail/refund shapes;
  `VaultFlowApiClient` search, detail, receipt, reprint, void, return-detail
  and refund/dispose methods; `UserSession` gains `TerminalBusinessDate` and
  `OpenShift` set via `SetTerminal(PosTerminalSession)`. New shared
  `TerminalBar` component: register auto-select, shift strip with start, stale
  register cleared on location mismatch. `Sales.razor` — `/sales` search page
  with location/date-range filters gated by `sale.view`. `SaleDetail.razor` —
  detail with lines/payments/totals, receipt print and reprint with reason,
  void with reason (blocked without open register/shift), accept-return with
  per-line quantity; terminal bar shown for Completed sales when the cashier
  has reprint/void/return permission; navigates to `/returns/{id}` after
  accept. `ReturnDetail.razor` — return detail with lines/batch, per-line
  disposition form (restock/quarantine/damaged/supplier-return/waste with
  reason-code mapping, gated by `inventory.adjust`), refund form (method,
  amount, tendered, provider ref; cash-locked for blind returns, gated by
  `sale.refund`), refund history, back-to-sale link. `TerminalBar` is reused
  in both pages when register/shift context is needed. `NavMenu` adds a
  "Sales" link; `Home.razor` adds a conditional "Review sales" card when
  `sale.view` is present; `app.css` reworked: two-column grid,
  `primary-card` and `secondary-card` both span full width with distinct
  light backgrounds. Pos.Web builds 0 warnings, 0 errors.

- **C17 — card/e-wallet + split payment checkout.** Web checkout
  (`NewSale.razor`) now supports card, e-wallet and split payment mixes
  alongside cash. The single cash tendered input is replaced by an allocated-
  payment list with an add-payment form: method tabs (Cash / Card / E-wallet),
  a precise-money amount input (seeded with the remaining amount using
  `ToString("0.00########", InvariantCulture)` to prevent rounding-induced
  payment mismatches), a cash tendered field with quick-tender buttons
  (Exact / 100 / 500 / 1000), and an optional provider reference (max 128
  chars) for card/e-wallet. Complete is disabled until the allocated total
  equals the net total at 4 dp. Summary shows amount due, allocated,
  remaining (or estimated change for cash). The server's existing payment
  validation (method enum, amount > 0, cash tendered >= amount,
  `sale.payment_mismatch` sum check at 4 dp) is reused as-is; no backend
  changes required. New CSS for the allocated list, method chips (cash green,
  card blue, e-wallet purple), the add-payment panel and quick-tender
  layout. Pos.Web builds 0 warnings, 0 errors.
  New `SalePaymentEndpointTests`: card-only (with provider reference),
  e-wallet-only, split cash + card (asserts both payments, cash change and
  provider ref), split cash + e-wallet, under-coverage refused
  `sale.payment_mismatch`, over-coverage refused `sale.payment_mismatch`.
  Six tests total. API suite runs 167 / 169 (2 known Docker-unavailable
  Postgres failures).

- **C18 — expired-batch sale blocking, enforced end to end.** The §5 contract
  stops being aspirational. `inventory.expired_only` and
  `sale.expired_override_denied` were dead codes — every FEFO shortfall returned
  generic `inventory.insufficient_stock` — and are livelocked against real
  behaviour: the failure classification now probes past-expiry availability
  against the sale's business date and refuses `inventory.expired_only` (409)
  only when the expired shelf could actually cover the shortfall. The
  `sale.expired_override` exception path now carries a mandatory reason on the
  line (`CompleteSaleLine.ExpiredOverrideReason`, max 500 chars;
  `sale.expired_override_reason_required` / `sale.expired_override_reason_too_long`
  → 400), the denial is pre-checked per line so a cashier without the permission
  cannot even offer the path (`sale.expired_override_denied` → 403), and the
  `sale.expired.override` audit now records the cashier's reason and the
  authorizing user (stamped from the request context, never caller-supplied).
  The web terminal probes the server and, on `inventory.expired_only`, offers a
  confirmation dialog with a mandatory reason textarea before re-submitting with
  `allowExpiredOverride: true` (gated on `sale.expired_override` in the session).
  New tests: 3 validator rules + 2 handler refusals/reclassifications + 4
  `ExpiredOverrideEndpointTests` through the real API pipeline — refusal
  `inventory.expired_only` (balances untouched), override completion (sellable
  batch drained, expired batch decremented, audit row with reason + authorizer +
  role snapshot), missing reason → 400, cashier without permission → 403.
  Pos.Web builds 0 warnings, 0 errors.

- **C19 — scheduled-price cancellation, closing the ADR-0029 gap.** ADR-0029
  deferred cancellation of not-yet-effective prices to "POS pricing, Phase 11";
  C19 implements it end to end. `Product.CancelScheduledPrice(priceId, nowUtc)`
  removes a future price row only while it is still pre-effective
  (`catalog.price_already_effective` → 409 otherwise, `catalog.price_unknown` →
  404 for a row the product does not carry) and rewinds the schedule the way
  ADR-0029 describes: the predecessor it superseded carries the old amount
  through the cancelled period (re-closing at a future resumption's end, or
  reopening when that resumption was open-ended), and the continuation row that
  existed only to restore the old amount afterwards is removed with it.
  Cancellation never renumbers someone else's plan — a different amount starting
  exactly where the cancelled price ends refuses `catalog.price_cancel_successor`
  (409), and a chain of same-amount continuations refuses
  `catalog.price_cancel_chain` (409) — so a replacement price is scheduled
  instead. The API surface is
  `POST /api/v1/catalog/products/{id}/prices/{priceId}/cancel` under
  `Permissions.Catalog.ManagePrices`, with the same mandatory-reason and
  length-validated body (`catalog.reason_required` / `catalog.reason_too_long`);
  the response carries the cancelled row's id and the handler audits
  `product.price.cancelled` with the pre-cancel row as `before` and the restored
  row (when one exists) as `after`, exactly like supersession. New tests: 7
  domain (temp+continuation rewind, open-ended rewind, gap-filling removal, the
  three refusals, unknown id) + 1 `ProductStockingEndpointTests` pipeline test
  (cancel via API, base reopens, `product.price.cancelled` audit row with the
  recorded reason, permission, unknown-id 404s). API suite runs 172 / 174
  (2 known Docker-unavailable Postgres failures).

---

### Phase 12 — offline device storage

C28 creates the Windows and Android `Pos.Client` MAUI Blazor Hybrid application
and the first independently migrated device database. The SQLCipher file uses a
random 256-bit key held in platform `SecureStorage`, private connections and no
pooling. Since C29b the key is applied as a SQLCipher raw key, so opening a
connection runs no passphrase derivation, and it is read from the secure store
once per process. `PosDeviceDbContext` exposes only device profile, product, barcode,
price, location, user and time-bounded permission-snapshot tables; server
identity, purchasing, audit and reporting tables cannot enter this database.
Money remains exact decimal text and UTC timestamps use round-trip text.

The initial SQLite migration is separate from the PostgreSQL history. Tests
apply it to an encrypted file, prove the file cannot be queried without its key,
verify the scoped table set and exact decimal storage, and enforce unique global
permission grants despite SQLite's treatment of `NULL` in unique indexes.

C29 makes `ChangeFeedApplier` the only writer of the caches, permission
snapshots and the new `sync_cursor` table. A page is validated whole, then every
change and the cursor commit in one `BEGIN IMMEDIATE` transaction: a replayed
page is recognised and writes nothing, two appliers racing one page apply it once, a page that skips ahead is refused
`sync.feed_cursor_mismatch`, a malformed one `sync.feed_page_invalid`, and a
database fault rolls back the page and the cursor together. Two guards refuse
writes from anywhere else: an EF interceptor for tracked changes, and
`BEFORE INSERT/UPDATE/DELETE` triggers that call a per-connection SQL function
returning 1 only inside the applier's internal write scope. Raw SQL and bulk
statements are refused, and a keyed connection with no device context fails
closed because the function does not exist there (migration
`DeviceChangeFeedGuards`). Disabling both guards turns exactly the 15
guard-dependent tests red.

C29b fixes the cost of opening the device database, found while testing C29.
Keyed as a passphrase, every connection open ran SQLCipher's PBKDF2 and took
650–800 ms; with pooling off, EF opens a connection per operation, so every
offline cache read would have paid it. The key provider now returns the 256 key
bits, the initializer refuses any other length, clears the array, and passes
the bits as a raw key: an open costs about 2 ms and the 49 device tests run in
17 s, where C29's 43 took 3 min 27 s. The key and the context options are
built once per process; a failed secure-store read is retried rather than
remembered. The initializer's pragmas were corrected at the same time:
`cipher_memory_security` is process-wide and the WAL journal mode is stored in
the file, so both reach every connection, but `synchronous = NORMAL` and
`busy_timeout` had only ever applied to the discarded verification connection.
They are removed rather than spread to every connection, so writes stay at
`synchronous = FULL`, the behavior every test has exercised, and a committed
sale survives power loss.

C29c splits the client out of the server's CI job. `build-and-test` removes
`Pos.Client` from its checkout's solution before restoring, which is what stops
`NETSDK1147`; new `build-client-windows` and `build-client-android` jobs build
it with the MAUI workloads pinned to set `10.0.301`, the band that ships the
MAUI 10.0.20 packages `Directory.Packages.props` pins. The Release build of the
Windows target found a real defect on the way: `MauiProgram.cs` imported
`Microsoft.Extensions.Logging` for a Debug-only call, so Release failed on
IDE0005; the directive now sits under `#if DEBUG`. The Android job's Release
build has still never run on Linux (§4).

C30 draws the device command boundary. `Pos.Client` references
`Pos.Application` and executes the same handlers as the server (ADR-0008), so
the only thing between a disconnected terminal and a use case that needs central
authority is which handlers are registered. `OfflineCommandCatalogue` makes that
an explicit list of 24 command types — the executable form of the capability
table in OFFLINE_SYNC.md §1 — and `AddOfflineClientApplication` is the device's
composition root: the same dispatcher and the same five behaviours in the same
order as the server, but only the whitelisted commands' handlers and validators,
and no query handler at all. A command outside the list simply has no handler,
and the dispatcher answers `application.handler_unavailable`
(`ErrorType.Unavailable`) without touching a port, so a device refuses
`ApproveStockAdjustmentCommand` even with nothing else configured.

Two properties hold the whitelist honest. Each entry names the permission its
command is authorized by, and a test asserts every one of them is
`IsOfflineCapable` in the permission catalogue — which is the same flag the
authentication service uses to trim a device's snapshot, so a device could never
be granted what it would need anyway. A second test reads
`IAuthorizedMessage.RequiredPermission` off each declared command and checks it
against the entry, so the two cannot drift. Permissions checked inside a handler
rather than by the pipeline are deliberately unlisted: `sale.expired_override`
is not offline-capable, so it can never appear in a snapshot and the
expired-batch override is dead offline by construction.

Every entry is currently `Pending` rather than `Registered`. Declaring a use
case offline-capable and registering it are separate on purpose: the device
database holds caches, snapshots and the feed cursor, but no `local_*` tables
yet, so no handler's repositories can be satisfied there. Registering one now
would make the container throw on resolve instead of failing closed, which is
strictly worse. Entries flip to `Registered` as the device adapters land, and
the boundary tests cover that path today through a catalogue passed in by the
test: a registered command resolves, reaches the pipeline, and is then refused
by the authorization behaviour rather than by the dispatcher — offline does not
widen authority.

C31 gives the device the two ports its handlers will resolve. Numbering first:
`document_counter` is the one device table the change feed never writes and
application code must. `DeviceDocumentNumberGenerator` allocates from it with a
single atomic upsert — the same insert-on-conflict-returning shape the server
uses — and joins the caller's transaction when there is one, so a sale that
rolls back releases its number rather than leaving a gap. It is the server
generator's mirror image, and the two refuse opposite halves of the port: the
server refuses device-scoped types because it cannot know a device's sequence,
and this refuses central types because a device may never mint a purchase order.
A device also numbers only under its own enrolled short code; minting under
another device's would collide with that device's sequence and the server would
accept it, because the code on the posted number would match a real device.
Twelve parallel allocations produce twelve distinct numbers, a restart continues
the sequence rather than restarting it, and a year boundary opens a second
counter row.

Then authorization. `DeviceSnapshotPermissionEvaluator` answers from the cached
snapshot and only ever narrows. Expiry is re-checked at every evaluation rather
than trusted to a cleanup that might not have run, so a device left in a drawer
loses its authority on the second past expiry. A location grant answers for that
location only, a global grant anywhere. And the permission must be
offline-capable in the catalogue, checked before the database is read — which is
what makes a tampered row useless: the test drops the guard triggers on a raw
keyed connection, the one attack OFFLINE_SYNC.md §2 says the triggers cannot
stop, rewrites a grant to `inventory.adjust.approve`, and the evaluator still
refuses it.

Two more guards keep a bad snapshot from being stored at all. The page validator
refuses a grant naming a permission that is not offline-capable or that the
client does not know, and refuses the whole page rather than the grant, so the
offline-capable grants alongside it do not land either. The applier refuses a
snapshot whose policy version is older than the one held
(`sync.snapshot_policy_rollback`): policy versions only increase, so an older one
is a replay trying to restore authority the server has since narrowed. An equal
version is the ordinary re-issue — a refreshed expiry on the same policy — and is
applied. A refused page rolls back everything committed before it in that page.

Every catalogue entry from C30 is still `Pending`. These two ports exist now, but
a sale handler also needs repositories over `local_sale`, `local_sale_item`,
`local_inventory_movement` and the rest, and those tables are not built.

C32 is what a cashier sees. `DeviceStatusProvider` reads storage readiness,
enrolment, connectivity, when store data last arrived and when the signed-in
user's cached authority runs out, and returns a `DeviceStatusView`. The contract
lives in `Pos.Shared`, which references nothing, and that is the enforcement
rather than a convention: there is no type on it that could carry the database
path, the cipher settings, the feed position or a server address. A test still
checks the produced view against the database path, the directory, `device.db`,
`sqlite`, `cipher`, `http`, `cursor` and the raw location and user identifiers,
because a string field could always be filled in badly.

The view reports one thing at a time, in the order a shop floor cares about:
what stops the register trading, earliest state first, then what will stop it
soon, then the ordinary offline case. Being offline is explicitly not a warning —
selling offline is what the device is for — while authority expiring within a
shift is. That priority is a property of the contract (`Concern`, `Severity`)
rather than of the component, so it is tested without rendering anything; the
component in `Pos.SharedUI` is a lookup from concern to copy. Eighteen tests
walk every concern, and a concern added later without a severity fails rather
than quietly rendering as ready.

Connectivity is a port. `IDeviceConnectivityProbe` is answered in `Pos.Client`
by MAUI's network access, because reachability is a platform question;
infrastructure ships `AssumeOfflineConnectivityProbe`, which reports offline,
the safe answer for a device with no adapter wired up. A reachable link with an
unreachable server still reads as online — the sync client is what discovers
that, and its failures are what the banner will read once Phase 13 lands, along
with the pending-upload count POS.md §6 asks for.

C33 is the first use case a device can actually execute. Until now every
catalogue entry was `Pending`: the boundary, the numbering and the permission
evaluator were all built, but nothing could run, because a device carried no
table it could write a business record to. It now carries two —
`local_cashier_shift` and `local_audit` — and shift open, suspend and resume are
`Registered`.

The substrate underneath them is what any later use case will reuse.
`DeviceUnitOfWork` is the same contract the server implements over PostgreSQL,
against the device's SQLite file. `DeviceSession` holds who is standing at the
register — one drawer, one cashier — and grants nothing on its own: authority
still comes from the cached snapshot, so the same signed-in user is refused the
moment it expires. `DeviceAuditWriter` appends to `local_audit` inside the same
transaction as the change it describes, because the device is the only witness
to what happened on it while it was offline. `DeviceCurrentUser` reports no IP
address, no user agent, no role snapshot and never `HasAllLocations` — a device
acts at its own location and nowhere else, which is the same rule the permission
catalogue states by not marking `location.all` offline-capable.

`DeviceShiftRepository` backs the same handlers the server runs. Three of the
port's members answer questions about sales — what cash a shift took, what a
sale has been refunded, which shifts a worker should force-close — and those
refuse outright rather than improvising, which is why `CloseShiftCommand` stays
`Pending` while open, suspend and resume are live. A closing shift that
reconciled against a zero it could not verify would balance a drawer against a
lie.

Cached locations now carry their settings. `LocationChanged` gained a
`SettingsJson` field, so a device applies its owner's VAT rate, cash rounding,
negative-stock policy and shift caps rather than a guess. Null reads as
`LocationSettings.Default`, which the domain already defines as the strictest
configuration — a register must not become more permissive by losing its
connection.

Eight tests drive a real container, composed the way `MauiProgram` composes one,
over one encrypted store: the shift opens under `SHF-2026-D03-0001`, a number
the device minted itself; a second open on the same drawer is refused; a number
carrying another device's code is refused; suspend and resume round-trip;
without `shift.open` in the snapshot the shift does not open; thirteen hours
later, with the snapshot expired, the same command is refused again; a close
still answers `application.handler_unavailable` rather than reaching the
repository member that would throw; and a failed open leaves neither a shift nor
an audit row behind.

### Phase 13 — synchronization

C34 builds the upload queue. `local_outbox_event` holds the business events a
device has produced but head office has not seen, and a single-row
`device_sequence` numbers them. Both are written in the caller's transaction, so
an event and the rows it describes commit together or not at all: a command the
device refused queues nothing, and a rolled-back event gives its sequence number
back rather than leaving a gap.

The device repositories enqueue, not the handlers. An adapter knows which
business event just happened, and keeping the outbox out of the shared handlers
is exactly what lets the server keep running the same code without one. What is
queued is "a shift opened", never "a row changed" — a device is an event
producer, not a database replica, and the payload is shaped so the server can
replay it through the same use case it would have run online.

`CanonicalJson` is the part worth being careful about. The server compares a
repeated event identifier against the hash of what it first stored and treats a
different hash as tampering rather than as an update (ADR-0007). That comparison
only means anything if the bytes are canonical, and `JsonSerializer` writes
properties in declaration order — so moving a property on a payload type would
silently change every hash and turn honest retries into tamper reports. The
payload is therefore written, re-read as a tree, and re-emitted with object
properties sorted by ordinal name at every depth. Array order is left alone: it
is data, not layout.

`DeviceUptimeTicks` is `Environment.TickCount64`, which keeps increasing across a
wall-clock change. A device whose clock was moved backwards still produces events
in an order the server can see through.

The status banner now shows what is waiting, which closes the gap C32 recorded
against POS.md §6. It is a count, not a queue: a cashier needs to know whether
anything would be lost if the register were wiped, not what the transport is
doing. `Failed` and `RequiresReview` still count as unsent, because retries are
never abandoned; only `Synchronized` and `Conflict` have been decided.

C35 puts the ledger on the device — and deliberately not a second one.
`InventoryLedger` now runs against `ILedgerStore`, which both `PosDbContext` and
`PosDeviceDbContext` implement, so the type that posts a sale on PostgreSQL is
the type that posts it on the device's SQLite file. ADR-0008 exists because two
implementations of double-entry stock would drift and the drift would be
invisible until inventory disagreed; a second ledger would have been exactly
that. The refactor is invisible to the server: every existing ledger, balance and
API test passed unchanged.

The guards are where the two databases genuinely differ, and the difference is
worth stating rather than glossing. Of the four layers that stop
`product.StockQuantity = 100`, three carry to the device. The domain types and
the EF interceptor are the same. The triggers are not: PostgreSQL's balance guard
is a **deferred** constraint trigger that checks at commit that every balance
change is backed by movements in the same transaction, and SQLite has no
deferred triggers — a `BEFORE` trigger there cannot see movements EF has not
inserted yet. That was not a theory: the first version of the guard failed every
post, because the ledger's own insert arrived before its movements. So the device
trigger asks *who* is writing instead, through `vf_ledger_writer()`, the
mechanism C29 already proved on the downloaded caches. The fourth layer, a
least-privilege database role, has no SQLite equivalent at all; what stands in
its place is the encryption key, which is why it lives in the platform secure
store and never in the file. That is now written down rather than implied.

The ledger identifies itself by opening a write window. On the server the handle
does nothing. On the device the unit of work opens it, because a command that
posts stages its rows and leaves the writing to the unit of work, and the window
is internal to infrastructure so client code cannot open it. Movement
immutability and the balance no-delete rule need no window — they are refused
unconditionally, as on the server.

Eight tests post through the real ledger against a real encrypted device file: a
receipt puts stock in the bucket and its counterparty leg keeps the ledger
closed; a sale draws it down; selling stock the device does not have is refused
with `inventory.insufficient_stock`, because an outage does not relax the
negative-stock policy; a repeated event replays instead of posting twice; and
raw SQL on a keyed connection cannot set a quantity, delete a balance, or rewrite
a movement. Every movement group sums to zero.

C36 closes a gap found by reading the sale handler rather than by running it.
`CompleteSaleCommand` reads two things off a product that the device cache did
not carry: `IsVatExempt`, and the batch each line is allocated from. Neither is
optional. An offline sale computes its own tax, so a missing VAT flag puts the
wrong figure on a receipt the customer keeps; and a device selling batch-tracked
stock allocates first-expiry-first-out and refuses expired stock, neither of
which it can decide without each batch's expiry date.

So `cache_product` gained `is_vat_exempt` and the feed a `BatchChanged` kind
behind a new `cache_batch` table, indexed by product and expiry — the only way
FEFO reads it. `cache_batch` is applier-owned like every other downloaded table
and carries the same C29 guards. The page validator refuses a batch that expires
before it was received.

Both C29 change-detector tests fired on this, which is what they are for: one
when the ledger added five triggers it does not own, one when `cache_batch`
joined the applier-owned set. Both were updated deliberately rather than
loosened.

C37 makes the sale's catalogue reachable from a device. `CompleteSaleCommand`
asked its repository for whole `Product` aggregates and called
`product.PriceAt(...)`, which meant every implementation had to be able to
produce a `Product` — and a device cannot. It mirrors master data thinly and has
no category, brand or unit of measure to rebuild one from, and `Product`'s
constructor is private.

`GetSaleProductsAsync` now returns `SaleProduct`: the five fields the handler
actually reads, with the effective price already resolved for the location and
moment. Effective-dated resolution stays on the aggregate, where the rule that a
location row beats a global one lives; the server projects through it, and a
device will project from its cache. The handler is unchanged in behaviour — it
decides what to do when there is no price, nothing more.

The blind return kept the aggregate, on its own `GetReturnProductsAsync`. It
needs barcodes, the base unit of measure and the default purchase cost to
register goods it has no sale for, and it can have them: `sale.return_blind` is
not an offline-capable permission, so a blind return only ever runs on the
server. Splitting the two is what lets the sale path stay inside what a device
can mirror without dragging the return path down to it.

C38 built everything a device sale needs; C39 found why it did not work and
turned it on. `local_sale`, `local_sale_item` and `local_payment` map the
server's own `Sale` configurations, renamed — the C35 trick, so the two databases
cannot drift in shape. `DeviceSalesRepository` projects `SaleProduct` from the
cache with the same precedence the aggregate applies, finds the external customer
location, and enqueues a `SaleCompleted` event. `DeviceCustomerRepository`
answers null, so a named-customer sale is refused offline while an anonymous cash
sale is not. The members belonging to returns, refunds, reprints and the expiry
run refuse outright, the `DeviceShiftRepository` pattern.

The bug was in `DeviceExpiryService`. Driven end to end, a sale was refused
`inventory.insufficient_stock` — available 0 — against a register that
demonstrably held twenty units. The first suspicion was the ledger's enlisted
path, where a command's unit of work owns the transaction and the ledger only
stages, because that is the one path C35's tests did not cover. Covering it
exonerated it: a receipt committed before a transaction is visible to a sale
posted inside it, and that test is now permanent.

The actual cause was a structural mistake in the device adapter. The sale handler
FEFO-allocates **every** line, batch-tracked or not, so `GetSellableBatchesAsync`
has to return untracked stock too — as the single bucket it lives in, with an
empty batch key. The device version had been written as an inner join from
`cache_batch` to the balances, which for an untracked product returns nothing at
all and reads, to the ledger, as no stock. The server's implementation drives off
*balances* and adds the no-batch item when the product is not batch-tracked; the
device now does the same. Inverting that one condition reproduces the original
failure in three of the five sale tests.

Two smaller things the work settled. A batch the register holds but has never
been told about is not sellable — without its expiry date there is no way to know
it is safe. And ringing up the same numbered sale twice is not a retry: an upload
retry is what the event identifier makes safe, while a second sale wearing the
first one's receipt number is stopped by the unique index, before the drawer can
disagree with the ledger.

`CompleteSaleCommand` is now `Registered`. A device opens a shift, sells for
cash, posts to its own ledger, audits locally and queues the sale for upload —
all in one transaction that either happened or did not.

C40 closes the loop. `GetShiftCashTotalsAsync` reads the shift's cash payments
from `local_sale` and `local_payment`, so a device reconciles its own drawer: a
float of 2,000 plus 100 taken in cash balances at 2,100, and a drawer counted at
2,070 against 75 sold records a five-peso shortfall rather than hiding it.
Refunds are reported as zero, and that is a statement rather than a placeholder —
a device cannot refund, because returns and refunds are not registered on one. If
that changes this has to change with it, or a drawer reconciled against cash that
went out but was not counted would report a shortfall the cashier did not cause.

C41 adds void and reprint, the two POS entries that act on a sale the device
already holds. A void returns the goods to the shelf by a reversing post rather
than by editing history — the ledger is append-only on a device too — and the
sale's own status is what tells the repository which business event to enqueue,
rather than a flag threaded through a port the server shares. A reprint gets its
own `local_sale_receipt_print` table and is reported to head office: a second
copy of a receipt can walk out of the shop, so it is an audited act rather than
a display concern.

C42 finishes offline POS with returns and refunds, and pays the debt C40 wrote
down. `GetShiftCashTotalsAsync` had reported zero refunds — true while a device
could not refund, and a trap the moment it could. It now counts cash refunds from
the device's own rows, and the test that proves it is the case that would have
failed: sell 75, refund 25, count a drawer of 2,050 against a float of 2,000 and
get a variance of zero rather than a twenty-five peso shortfall the cashier did
not cause.

`GetRefundedAmountsByMethodAsync` is what stops a sale being refunded past what
it was paid — a second refund of the same cash is refused on the device, and the
server re-checks the same rule when the events arrive. Returned goods land in
`ReturnPending`, not back on the shelf: three sold and one returned leaves
seventeen sellable and one held for inspection, which is what OFFLINE_SYNC.md §1
says and what a first draft of the test got wrong.

C43 is the other half of the outbox: `POST /api/v1/sync/push`. Each event is
processed in its own transaction, so one refusal does not discard the events that
already landed — a batch is a transport convenience, not a unit of work, and the
response carries one verdict per event rather than a single status code, because
a device needs to know which of its events to stop retrying.

The idempotency record is written in the same transaction as the effect it
describes, which closes the window where a sale could be committed without the
record that stops it being applied twice. A retry replays the original verdict
and the applier does not run again. A repeated event identifier carrying a
different payload hash is refused as `sync.idempotency_key_reuse` and applies
nothing — this is where C34's canonical serialization earns its keep, because the
comparison only means anything if an honest retry always hashes the same.

Ordering is enforced by a per-device checkpoint: an event that is not the next in
sequence defers, and everything behind it defers with it, so the server never
acts on a state the device never had. A *refused* event still advances the
checkpoint — it has been answered for, and stalling the queue behind it would
strand the device forever. Clock skew is reported, not corrected: silently
adjusting it would hide a tampered clock.

`ShiftOpenedApplier` is the first applier and sets the pattern. The device is the
authority for the shift's number and float — both were decided at the till and
the number is already printed on every receipt the shift produced — so the server
accepts the record rather than re-deriving it, and keeps the device's identifier
so uploaded sales still reference it. What the server *does* re-check is whether
the device may still do this, evaluated now rather than as of event creation.

`CloseShiftCommand` is `Registered`, which made C33's fail-closed test wrong: it
had used closing as its example of a use case a device does not carry. It now
uses `ReconcileShiftCommand` — settling a variance is a manager's decision taken
centrally — and still asserts the distinction that matters, that the refusal is
*unavailable here* rather than *forbidden to this user*.

C44 finishes the shift's round trip, and found two faults doing it. The first was
in the device: `DeviceShiftRepository.UpdateAsync` mapped a closed shift to no
event at all, so a drawer counted during an outage closed locally and head office
never learned of it — no closure, no variance, nothing. The mapping was right
when it was written in C33, where close was not a device use case; it became a
bug the moment C40 registered `CloseShiftCommand` and nobody revisited the
switch. `ShiftClosed` now carries the closing instant and the drawer figures.

The three appliers replay each transition through the aggregate rather than
writing a status column, so a resume of a shift that was never suspended is
refused centrally by the same rule that would have refused it at the till. A
device claiming `isForceClosed` is refused outright: force-close is the server
worker's authority, and the claim is exactly what would suppress the count.

The close re-derives the variance. Declared and counted cash are facts about a
physical drawer and are taken as entered, but the variance is derived, and the
figure a manager reconciles must come from the sales the server actually holds —
one derived from events it refused would balance the books against sales that
are not there. That is only safe because a device's events are applied in the
order it produced them, so every sale of the shift has landed by the time its
close arrives. When the server's figure and the device's disagree, the close is
still accepted — the money has already moved — and the audit entry records both
with a reason, so the number on the cashier's Z-report can be explained rather
than merely contradicted.

The second fault surfaced only because the tests go through the real processor
rather than straight into an applier: a till locked and unlocked again arrives as
two events in one batch, and the second collided with the first, because an
applier reads untracked and attaches what it read. Each event now starts from a
clean change tracker — a batch is a transport convenience, not a unit of work,
and that had been true of the transaction but not of the change tracker.
Commenting the reset back out turns three tests red.

C45 lands the sale, and the way it lands is the point. The event now carries the
lines and the payments the cashier rang up — a header and a net total were never
a business event the server could replay — and `SaleCompletedApplier` hands them
to `CompleteSaleCommandHandler`, the same handler the online endpoint runs. So
the server re-derives the effective price, the VAT class, the FEFO allocation and
the cash rounding from its own data, automatically rather than through a second
implementation that would drift. The stock moves; it is not paperwork.

Two decisions inside that are worth stating. The handler is called **directly**,
not dispatched, because the pipeline's authorization behaviour evaluates the
principal that uploaded the batch, where the rule is about the cashier who rang
the sale up. That check is made in the applier instead, and it does not refuse:
a cashier since unassigned from the store has the sale recorded and flagged
`RequiresReview`. The goods left the shelf and the money changed hands, and
refusing would destroy the only central record of both. And a sale's identity
across the two sides is its **SAL number**, not its row id: the server mints its
own, and the number is unique, device-scoped and already on the customer's
receipt. A shift is the other way round — its identifier is kept, because the
sales that reference it were uploaded carrying it.

**A gap, pinned by a test rather than papered over — and closed in C46.** As
shipped, C45 refused a sale the server priced differently: it re-priced the line,
and the cash that was tendered no longer settled the new total, so the handler
answered `sale.payment_mismatch` where OFFLINE_SYNC.md §7 says the sale should be
accepted at the price actually charged. A first draft of the applier wrote a
price-variance audit note; it was removed, because the handler refused before it
could ever fire and unreachable code that looks like a safeguard is worse than
none.

C46 closes it the way the gap comment said it should be closed. A sale line may
name the `ProductPriceId` it was priced from, so a register that priced before a
change and synced after it has its sale recorded at what the customer paid,
against the row they paid from. Two properties make that safe rather than merely
convenient. The device sends the **identifier and never the amount** — the server
reads that back off its own row — so a register can say which of head office's
prices it charged but can never assert what that price was. And the row must
price *this* product and be either global or scoped to *this* location, or the
line is refused with `sale.item.quoted_price_not_applicable`: that check is what
stops a crafted line paying biscuit money for a watch, and a test aims one
product's price row at another's line to prove it.

It is deliberately **not** a price override. An override means a person keyed a
number in and another person authorized it; recording a stale price that way
would put an entry nobody authorized on the price-override report, and the
report is what the control is for. A quoted version says the amount came from one
of the server's own rows, which it did. When that row is no longer the effective
one the sale is accepted and a `sale.price.variance` audit entry records both
sides — reported, never re-priced, never refused. `GetQuotedPricesAsync` is
implemented on the device too, because the device runs the same handler, and a
port that refused there would be a lie waiting for the first caller.

C47 takes the two follow-up events that only reference a sale. Both replay
through their own handlers, and both find their sale by **SAL number** — the
server minted its own `SaleId` when it replayed the sale, so the identifier a
device names is meaningless centrally, while the number is the same on both
sides and is what the customer is holding. A follow-up whose sale the server
does not hold is refused with `sync.sale_unknown` rather than dropped: under
per-device ordering the sale was uploaded first, so a missing one means that
upload was refused, and a void floating free of the sale it reverses would put
stock back on a shelf against nothing. A test pushes a void with no sale and
checks the shelf did not move.

`ISyncEventApplier` now receives the device's `EventId`. The void posts a
reversing movement, and a ledger post needs an idempotency key that a retry
reproduces — the protocol already has exactly one, and minting a second would
mean a batch that timed out after the ledger posted put the stock back twice on
the next attempt. The reprint moves neither money nor stock, which is precisely
why it has to arrive: a second copy of a receipt can leave the shop and come
back as a return, and a print log that silently skips the offline copies is
worse than none, because it is trusted.

C48 finishes the upload. The return replays through its own handler and carries
only the products and quantities the cashier accepted, so the server matches each
against the original sale's lines and prices them from the snapshots it holds —
which is what stops a refund being paid out against a figure a register worked
out for itself. A blind return is refused outright: `sale.return_blind` is not
offline-capable, so it can never have reached a device's snapshot, and a payload
claiming one is a back door rather than a case. The refund finds its return by
RET number, and its handler re-checks against the server's own rows that the sale
is not being refunded past what it was paid — the device checked the same rule
but could only see the returns it holds, so a second register refunding the same
sale during one outage is caught here and nowhere else.

**Every POS event a device can queue now lands centrally**, and a coverage test
keeps it that way. `SyncEventType` and the appliers are connected by nothing in
the type system, so an event type added on the device side would ship happily and
be refused as unsupported — with the device's queue stalled behind it until
somebody read a log. The test reads each applier's declared `EventType` through
an uninitialized instance (every one is a constant that touches no state) and
compares the set to the enum, then compares the appliers to the container's own
service descriptors. The first draft of that second check searched
`DependencyInjection.cs` for the registration line, and passed happily when I
commented the line out to test it; reading the descriptors instead is exact.

C49 makes the queue move. `SyncUploader` drains the outbox in sequence order,
stopping at the first `Deferred`, and turns each verdict into a resting place:
accepted and duplicate are done, deferred waits, and rejected is never retried
because the answer would not change. `SyncRetryPolicy` doubles from five seconds
to a thirty-minute ceiling with ±20% jitter — the jitter is not decoration, it is
what stops an estate that failed for one reason coming back on one tick and
arriving at the server together. After eight consecutive failures an event is
escalated rather than retried, and nothing is ever deleted: the refused, the
flagged and the exhausted *are* the sync-failure queue, and a queue that tidies
away its worst entries is one nobody can act on.

`HttpSyncTransport` reports every failure as a failure, never as an empty batch
of verdicts — the whole retry policy rests on telling "the server said nothing
about my events" from "the server said no". That includes 401 and 403: a revoked
device keeps its queue, because re-enrolling it is how that is fixed and the
trading it did must still be there afterwards. The status surface now separates
`UnsentEvents`, which waits for a line to come back, from `EscalatedEvents`,
which never resolves itself; only the second raises a concern, and it warns
rather than blocks, because the events are safe on the device and refusing to
sell would turn a bookkeeping problem into a closed shop.

**And wiring the uploader up found something worse.** `MauiProgram` never
received the sale-side registrations of C38–C42. The device gained sales, voids,
reprints, returns and refunds across five chunks; the test host that exercises
them grew `ISalesRepository`, `ICustomerRepository`, `IExpiryService`,
`IInventoryLedger`, `ILedgerStore` and `ILedgerPolicyProvider`, and the app's own
container was never given any of them. Nothing failed, because every test
composed its own container — the register would simply have thrown the first time
a cashier rang something up. Five chunks of this log say the device "trades fully
offline"; the app could not have sold a biscuit.

The fix is not six more lines in `MauiProgram`. There is now one list, in
`AddDeviceInfrastructure`, and `DeviceCompositionTests` resolves every command
`OfflineCommandCatalogue` marks `Registered` from it, against a real opened
store. `MauiProgram` keeps only what a platform can answer and the assembly
cannot: where the database file lives, where its key is kept, what time it is,
and whether there is a network. Commenting out any one repository turns the test
red.

C50 starts the download half. `sync.change_feed` is append-only and carries each
change exactly as a device will receive it, so serving a page is a read rather
than a re-derivation from rows that have since changed again — a device asking
for last week's change gets what was true last week. `ChangeFeedRecorder` fills
it from a `SaveChanges` interceptor, for the same reason the ledger uses one: a
feed that depends on somebody remembering is a feed that silently stops carrying
the thing nobody remembered. It writes in the caller's transaction, so a change
that rolls back takes its feed row with it.

**The sequence is deliberately not a database identity column.** Identities can
be handed out in one order and committed in another, and the feed is read as
"everything after my cursor": a row numbered 40 committing before one numbered 39
would let a device store 40 and never see 39 again. The number comes from a
single counter row incremented inside the writing transaction, the same shape
C34 used on the device. It serialises master-data writes against each other,
which is a real cost and the right one — master data changes rarely, and a sale
never touches the table.

Writing the tests found two things worth having found. A cancelled scheduled
price produced no feed row at all, because the recorder only looked at added and
modified entries: a register would have gone on selling at a price the server no
longer holds. And every strongly-typed identifier was serializing as
`{"value":"..."}`, since the ids are single-property records — a device would
have needed a matching wrapper to read its own catalogue, and the feed's JSON
would have been unreadable to anyone looking at it in an incident. Both are now
pinned by tests.

**Reviewing the handler for C46 turned up a second engine fault.** An applier can
get several steps in before it refuses — the sale handler writes a price-variance
note and can then hit the stock rule — and the processor was committing whatever
had been staged alongside the refusal. A sale head office turned away could have
left an audit entry saying it happened. A refusal now rolls its transaction back,
discards the change tracker, and records the verdict in a transaction of its own.
Removing the rollback turns two tests red.

`sync.event_type_unsupported` is the one deliberate exception, now said out loud
in code and in OFFLINE_SYNC.md rather than left as an inconsistency: it records
nothing and advances nothing, where every other refusal advances the checkpoint.
The event is not wrong — the server is behind the device that sent it — so
answering for it would destroy a record an upgraded server could still apply. The
cost is that the device's queue stalls behind it until somebody notices, which is
the right way round for a business record.

**Testing it over real HTTP found a C43 bug no unit test could reach.** The sync
checkpoint was read without tracking, and the server context reads no-tracking by
default, so `Advance` mutated a detached object and saved nothing. The first
batch landed; every batch after it would have deferred forever against a
checkpoint frozen at one — a register that syncs once and then silently never
again. The unit fixture had missed it because a bare `PosDbContext` tracks by
default where the container does not. Both sync fixtures now configure
no-tracking as the container does, and a regression test pins it.

`SyncOutcome` also goes on the wire by name now, carried by a converter on the
type rather than by the host's serializer options — OFFLINE_SYNC.md §3 has always
documented `"outcome": "Accepted"`, and a device client that does not share those
options would have read `3`.

---

## 3. Bugs the tests caught this session

Worth recording, because each was invisible in review and would have been
expensive in production.

**Every offline cache read would have cost most of a second.** The device
database was keyed with its 256-bit random key as a passphrase, so SQLCipher ran
PBKDF2 on every connection open (650–800 ms measured), and connections are not
pooled. Nothing failed; C29's device tests were just slow, 3.7 seconds each,
which is what gave it away. On a register every barcode lookup would have paid
it, more on Android. The key is now applied as a raw key (C29b); a test proves
the same bits as a passphrase are refused, so derivation cannot creep back.

**The API had never started on PostgreSQL.** Every endpoint test hosts the API
on SQLite, and the PostgreSQL suites build their own context. Running the real
host against PostgreSQL 17 on 2026-09-14 crashed at start-up:
`EnableRetryOnFailure` installs a retrying execution strategy, which refuses
user-initiated transactions — and the unit-of-work behaviour, the ledger, the
reconciler and the development seeder all open one. Every command would have
failed the same way. The retry option is removed (the ledger already retries the
one step that is safe to replay), and `PersistenceRegistrationTests` asserts the
registered PostgreSQL context does not retry.

**Every document number failed on PostgreSQL.** The counter upsert's
`DO UPDATE SET next_value = next_value + 1` is ambiguous to PostgreSQL (42702:
existing row or `EXCLUDED`), while SQLite silently resolves it — so PO, GRN, TRF,
QRT, RCT and every other central number worked in tests and threw in production.
The PostgreSQL statement now aliases the target (`AS c ... c.next_value + 1`),
matching the SQL already in DATABASE.md §10, and
`PostgresDocumentNumberGeneratorTests` allocates on a real engine.

**Role management would have undone itself at every restart.** The identity
seeder runs on each start so that permissions added to the catalogue reach
existing roles, and it did so by re-adding any default grant a role was missing —
including one an administrator had just removed. Harmless while roles could only
be edited in the database; fatal once `PUT /api/v1/roles/{id}/permissions`
existed. The seeder now records each default grant it applies
(`core.role_default_grant_applied`, migration
`20260913190959_RoleDefaultGrantApplied`) and applies each one once.

**Identity silently discarded changes to its own token rows.** Writing the
two-factor tests showed a recovery code accepted twice: redemption succeeded, but
the code stayed in storage. ASP.NET Core Identity's store loads the existing token
row and edits it, and under the context's no-tracking default that edit was made to
a detached object and never saved — the same trap as the sign-out bug below, in
framework code this time. The same path meant an administrator's two-factor reset
would not have rotated the authenticator key. Identity operations that modify
existing rows now run inside `TrackingScope`, with the sign-in's user instance
attached first; the tests assert the code is consumed and the key is new.

**Swapping a product's primary barcode could never be saved.** Retiring the
primary code (or making another code primary) demotes one row and promotes
another in the same save. EF Core cannot order updates around a filtered unique
index, wrote the promotion first, and both SQLite and PostgreSQL refused it
against `ux_product_barcode_one_primary` — a 500 on every swap. `PosDbContext`
now writes demotions first, inside the same transaction as the rest of the save
(`PostgresProductCurationTests` and the endpoint suite cover both engines).

**The domain refused adjacent price periods the database accepts.** The price
overlap check treated a period's end as inclusive, so a price ending at T blocked
one starting at T, while the exclusion constraint (`tstzrange` is `[from, to)`)
allows it. The check is now half-open, like the constraint.

**Cashiers could read purchase cost.** `product.cost.view` existed in the
catalogue and in the role grants, but no read enforced it: every product read
returned `defaultPurchaseCost` to anyone holding `product.view`. Product reads now
return it as null without the permission, and supplier links (last cost) require it.

**The negative-stock shrinkage signal did not exist.** INVENTORY_LEDGER.md
promised a record of every draw the ledger refused, and nothing wrote one. It could
not simply be added to the ledger: the refusal rolls the whole command back, so a
record staged in its transaction would vanish with it. The attempts are now
written after the transaction ends (ADR-0030), and the endpoint test proves a
refused dispatch leaves two records and two audit entries while posting nothing.

**The container images had never built.** Neither Dockerfile copied
`.editorconfig`, so inside the image the analyzer rules the repository relaxes
(CA1716 on `Error`, CA1000 on `Result<T>`) became warnings-as-errors and the build
failed. The API image also needed no explicit `KeyPerFile` package (the SDK in the
image rejects a reference the shared framework already provides). Both Dockerfiles
now copy `.editorconfig`, and a `.dockerignore` keeps `.env`, `.secrets/`, `.git`
and build output out of the build context.

**The compose stack could not start the API, and the grants script was wrong.**
The `api` service never received a token-signing key (start-up validation refuses
to boot without one), and `02-grants.sql` was never applied. Had it been, it would
have failed on a `sync` schema that does not exist yet and left `pos_app` without
rights on the `catalog`, `purchasing`, `transfers` and `quarantine` schemas. The
key now arrives as a Docker secret read through a key-per-file configuration
source, a one-shot `grants` service applies a per-schema script after the
migrator, and `PostgresRoleGrantsTests` checks the privileges of every table.

**A fresh deployment applied two migrations out of order.** Two hand-written
migrations had fifteen-digit identifiers (`202609120344091_AuditIntegrityGuards`,
`202609120344092_BalanceGuardDeferred`). Entity Framework orders migrations by
string comparison: culture-aware on a Windows host (the underscore sorts first,
so every test passed) and ordinal in the Alpine migrations bundle (invariant
globalization), where the audit trigger ran before the audit schema existed and
the migrator failed with `schema "audit" does not exist`. Renamed to
`20260912034410_…` and `20260912034411_…`; both scripts are idempotent, so a
database that already applied the old names simply re-applies them.
`MigrationOrderingTests` refuses any identifier whose ordinal and culture order
could differ.

**The reverse proxy could not complete a TLS handshake.** Its site block was a
bare `:443`, which gives Caddy no name to issue a certificate for. The site
address is now `{$SITE_ADDRESS:localhost}`. With that, the whole compose stack was
verified end to end on 2026-09-14 from a clean volume: migrator and grants exit 0,
the API connects as `pos_app`, and through `https://` it signs in, issues and prints
a receipt, raises a quarantine incident that posts to the ledger, and reads the
catalog, transfer and purchasing schemas — while `pos_app` is refused an `UPDATE`
on `core.receipt`.

**The integration-test factory ignored overrides of default settings.** Its
environment loop skipped any key already set, so an override of, say, the database
provider silently kept the SQLite default while its comment promised the opposite.
Found while writing the PostgreSQL host test, which needs exactly that override.

**Two request shapes still reached a `NullReferenceException`.** A quarantine line
without a barcode, and a quarantine body without `lines`, both failed with `500`.
They now return `400` (`quarantine.barcode_required`, `quarantine.empty`), and
unreadable bodies in general return `400 request.malformed` in every environment.

**The no-tracking default silently discarded writes.** `PosDbContext` defaults to
`NoTracking` because reads dominate. Command paths that read an entity and then
mutate it — sign-out, session revocation, token rotation, device suspension,
policy-version bumps — were changing detached objects, so `SaveChanges` wrote
nothing. Sign-out appeared to succeed and the session stayed live. Fixed by
opting those paths back into tracking explicitly, which also documents intent at
each call site.

**The same default nearly corrupted the balance projection.** `InventoryLedger`
loads existing balance buckets to apply a posting's deltas. Under the global
`NoTracking` default those rows came back detached, so `Apply` mutated objects
no one was tracking: new buckets were inserted (explicit `Add`), but every
subsequent posting into an existing bucket — a second receipt, a sale off the
shelf, a return — silently wrote nothing, leaving the projection stale while the
movement ledger marched on. A booking appeared healthy; the balances drifted.
The ledger's `LoadBucketsAsync` now opts into `.AsTracking()` so the versioned
apply persists, and the reconciler (which replays the whole ledger and compares)
surfaced this exact failure as a 47/48 integration-suite regression before any
changes shipped.

**The balance-guard trigger depended on statement order.** It fired per statement
and assumed EF would insert movements before the balance rows summarising them.
There is no foreign key between the two, so EF is free to write balances first —
and did. A legitimate posting was rejected. Fixed by making it a deferred
constraint trigger that runs at commit, which removes the assumption and
strengthens the check: it now sees the transaction's final state.

**The same trigger's transaction filter was broken by savepoints.** It matched
movements on `xmin = pg_current_xact_id()`. EF takes a savepoint around each
`SaveChanges` inside an explicit transaction, so those rows carry the
*sub*transaction's id while the function returns the top-level one. Two postings
in one transaction matched nothing and aborted. The identifier window was already
the real check; the filter was removed.

**A `double` had crept into the domain.** `Device.ClockSkewSeconds` was a
`double?`. Harmless in itself, but the architecture rule is deliberately absolute
— an exception list is the first step to a `double` in a money field — so it
became `decimal?`, which costs nothing here.

**SKU partial search did not translate through the value converter.** The product
search filter used `p.Sku.Value.Contains(term)`, which EF cannot translate — the
SKU is a value-converted key. It compiled clean and blew up with a 500 on the
first request. Replaced with a translatable shape: name via `LIKE`, exact SKU
equality on the normalized key, barcode partial via the barcode table. The seeder's
idempotency check used the same broken pattern and was fixed at the same time.

**Disposing an `HttpRequestMessage` before the TestHost read its body.** The
test helpers returned `client.SendAsync(...)` from inside a `using`, disposing
the request content before the server consumed it — every POST/PUT test failed
with `ObjectDisposedException` on `StreamContent`. Awaiting inside the `using`
scope fixed the whole class at once.

**Concurrent posts to one bucket let the stale projection pass the guard.**
The version-free projection meant two writers could both read quantity 100, both
apply +10, and the second would commit a projection that had never seen the
first's movement — a permanently lost 10 units that no guard caught. The
`Version` token makes the second writer's `SaveChanges` throw
`DbUpdateConcurrencyException`, and the ledger now retries the projection step
instead of letting the whole posting fail.

**SQLite cannot upgrade a deferred read transaction once a peer has committed.**
The first stale-write test held a read transaction across another writer's
commit and then tried to write — SQLite refuses with `SQLITE_BUSY` (snapshot
upgrade limitation), no busy timeout helps. The test pins the token's
`OriginalValue` to the version the stale reader actually saw, which is exactly
what the production code does on a genuine stale write. There is no bespoke
time-window.

**Raw balance updates via `SqliteParameter` blow up with "Value must be set".**
EF's `ExecuteSqlRawAsync` + `SqliteParameter` failed with an
`InvalidOperationException` at parameter bind before the statement ever ran. The
reconciler's raw statements use `FormattableString.Invariant` inline SQL
instead, which also keeps the SQLite and PostgreSQL branches visibly parallel.

**Microsoft.Data.Sqlite stores GUIDs in uppercase "D" format.** A reconciler
comparison that interpolated `guid.ToString()` (lowercase) against the stored
uppercase row matched zero rows — a healthy database looked catastrophically
drifted. The `SqliteGuid` helper emits `.ToString("D").ToUpperInvariant()`, and
the PostgreSQL branch relies on EF's parameterization.

**Rebuilding under the deferred balance guard hit PG 55006.** Re-enabling the
deferred constraint trigger inside the same transaction caused any transaction
that had deleted rows to panic at the final `SET CONSTRAINTS`. The rebuild now
runs one transaction: `DISABLE` → `DELETE` → `ENABLE` **before** the inserts
(the trigger events are all `INSERT`, so nothing is left pending) → insert →
commit, where the still-armed guard validates the rebuilt rows.

**The rebuild collided with the EF identity map.** A replayed bucket updated
through one context, then re-added through another in the same long-lived
context, hit a key collision. `ChangeTracker.Clear()` before the delete phase
removes everything the replay tracked so the rebuild sees a pristine context.

**Disposing a derived `WebApplicationFactory` kills the shared host's signing
key.** The switch-on endpoint test built its configuration with
`factory.WithWebHostBuilder(...)`; disposing that derived factory took the
shared host's RSA with it (`ObjectDisposedException: RSABCrypt`, then 401s for
every later test). The switch-on test now owns an independent `PosApiFactory`
instance with `Maintenance__AllowBalanceRebuild=true` in the environment, which
is restored on dispose and never tears down a sibling factory's keys.

---

## 4. Known gaps in what is marked complete

Stated plainly so they are not mistaken for finished work:

| Gap | Where | Impact |
|---|---|---|
| Counts cover the Available state only; no per-location auto-approval threshold | Phase 9 | Damaged, quarantined and expired stock is adjusted, not counted; every adjustment needs an approver (ADR-0031). |
| `inventory_movement` and `audit_log` not partitioned | Phase 4 | By decision (ADR-0030): revisit at about 50 million ledger rows, or with audit archiving (2033). |
| `AutoPassInspection` per location/category not built | Phase 5 | Receipts always land in `PendingInspection`; the trusted-category fast path is a settings-driven follow-up. |
| Cost-variance notification not raised (receiving discrepancies alert since C26; count variances do not) | Phase 5 | The receipt flags `costVariancePendingApproval` and records the approver; the notification/queue item is not built. |
| Supplier performance report not built | Phase 5 | Measurable after returns post; dashboard/analytics phase. |
| Supplier-return dispatch does not automatically raise a quarantine incident for unreported stock | Phase 5 | `QuarantineIncident` (Phase 8) exists; the automated bridge from dispatch is not built. |
| Incident raising is manual — no automatic scan/POS triggers | Phase 8 | `POST /api/v1/quarantine` exists; the automated triggers in QUARANTINE.md §1 (unknown barcode at scan, over-receipt excess, unclear returns) are a future integration. ROADMAP §8 keeps that item unchecked. |
| Register-product is two dispatches; a mid-flow failure could orphan a product | Phase 8 | The unidentified-line pre-check prevents it in the normal path; only a crash between the product creation and the line identification would leave the product in the master with its barcode reserved. |
| Receipts cannot be voided or emailed | ADR-0026 | Issue, list, detail and print exist and receipts are immutable; a mistaken receipt is corrected by issuing another. A void document and email delivery need their own decision. |
| No quarantine notifications or dashboard exception panel | Phase 8 | Raises and resolutions post through the API; age-based escalation and the Owner-dashboard exception panel are Phase 14. |
| A scheduled price cannot be cancelled once it has taken effect | Phase 3 | Past prices are history; change an effective price by scheduling a replacement. Pre-effective cancellation is implemented (C19): `catalog.price_cancel_successor` / `catalog.price_cancel_chain` refuse rewinds that would renumber another plan. |
| Generic idempotency pipeline behaviour not built | Phase 1 | The ledger is idempotent on its own; the generic behaviour lands with sync in Phase 13. |
| Permission cache is in-process | Phase 2 | Single API instance is exact. Scaling out needs a Redis backplane; revocation would otherwise lag by the 15-second policy-version window. |
| The Android client job has never completed a Release build on Linux | CI (C29c) | C29c is committed: the server job drops `Pos.Client` from its checkout and dedicated Windows and Android jobs build it. Both targets' Release builds are verified locally on Windows through C32 (2026-09-17, 0 warnings), so the project itself compiles for Android; what remains unverified is the Linux runner. On Linux `InstallAndroidDependencies` failed in a container with `CommonUtilities.Helpers.UserName must have a valid value` (root, no `USER`), which GitHub runners do set. The first CI run on this branch is the real verification. |
| The API's two PostgreSQL tests fail rather than skip without Docker | Tests (Phase 1) | `PostgresHostSmokeTests` and `PostgresInventoryControlTests` throw `DockerUnavailableException`, where the Infrastructure PostgreSQL classes skip. The two behaviours are opposite and neither is right: one hides a broken container, the other fails a Docker-less run. Settle on one — preferably failing everywhere, with CI guaranteed to have Docker. |
| PostgreSQL suites turn any container start-up failure into a skip | Tests (Phase 1) | The Infrastructure PostgreSQL classes catch every exception from starting the container and skip, as if Docker were absent. On 2026-09-17, under heavy Docker load, all 18 skipped while Docker was running; alone, they all ran and passed. In CI, where Docker is guaranteed, a broken start would pass green without exercising the triggers and grants. Skipping should happen only when no Docker endpoint exists, or CI should fail on skips. |

---

## 5. What to do next

Phase 12 is complete (C28–C33). **Phase 13 is under way:** C34 built the outbox,
so a device now queues the business events it produces — gaplessly, canonically
hashed, and in the same transaction as the records they describe. Nothing moves
those events yet.

1. **The scheduling loop that calls the uploader**, and the device's own
   credentials for it. `SyncUploader` and `HttpSyncTransport` exist and are
   tested; what is missing is a background service that runs one when the
   connectivity probe says the line is back, and a configured `HttpClient` that
   knows the server's address and carries this register's token.
2. **The pull endpoint and rebaseline.** The feed now exists and fills itself
   (C50); `ChangeFeedApplier` (C29) is already the consumer. What is missing is
   the route that serves a page — scope filtering, the cursor that may run ahead
   of the last change served, and the `410 Gone` that sends a long-dark device
   for a fresh baseline.
3. **Conflict rules and the sync-failure dashboard**, which also unblocks Phase
   14's last item — the sync-failure alert generator.

Two smaller things outstanding:

- **`build-client-android` has never completed a Release build on Linux.**
  Locally the dependency step failed in a container with
  `CommonUtilities.Helpers.UserName must have a valid value`, which a GitHub
  runner should not hit because it sets `USER`. If it does, set it in the job.
- **`Pos.Client` has not been compiled since C29c.** No MAUI workloads in the
  cloud environment. Its DI wiring no longer rests on that: `MauiProgram` now
  calls `AddDeviceInfrastructure`, and `DeviceCompositionTests` resolves every
  whitelisted handler from the same list in a suite that does run here — which
  is how the five-chunk gap it was hiding finally surfaced. What still rests on
  CI's client jobs is the MAUI-specific part: that the project compiles for
  Windows and Android, and one Razor page.

Two questions the capability table did not answer were settled on 2026-09-17
and are now rows in it:

- **Transfer pick, dispatch and verify stay online.** The permissions are
  offline-capable, but a device may not use them. A dispatch creates stock in
  transit that nobody else can see until the device syncs, and the receiving
  store would be counting against a transfer the server has never heard of.
- **Customer create and edit go offline; deactivate and reactivate do not.**
  Refusing a walk-in an account at the till during an outage is the worse
  failure, and the new PII lands in the encrypted device store like every other
  local record. Deactivation is administrative and nothing at a till depends on
  it, so it waits for the link.

---

## 6. Running it

The authenticated Blazor operations shell now provides sign-in, protected
routing, an API-backed transaction cart and full checkout orchestration:
register pick-up, shift start with opening float, line discounts, cash
payment and atomic sale submission against the server-numbered terminal
endpoints. The API path verified
on 2026-09-14 is a
development PostgreSQL in Docker plus the API under `dotnet run`, which applies
migrations and seeds in Development:

```bash
# 1. A development database (owner role, so Development can migrate on start-up)
docker run -d --name vaultflow-dev-pg -e POSTGRES_DB=vaultflow -e POSTGRES_USER=pos_migrator \
  -e POSTGRES_PASSWORD=<choose-one> -p 127.0.0.1:55432:5432 postgres:17-alpine

# 2. One-time secrets, kept in the user-secrets store (never the repo)
openssl genrsa -out jwt-dev.pem 2048   # Git Bash ships openssl; keep the file outside the repo
dotnet user-secrets set "Jwt:SigningKeyPem" "$(cat jwt-dev.pem)" --project src/Pos.Api
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=localhost;Port=55432;Database=vaultflow;Username=pos_migrator;Password=<choose-one>" --project src/Pos.Api
dotnet user-secrets set "BootstrapOwner:Enabled" "false" --project src/Pos.Api

# 3. Run: http://localhost:5177, OpenAPI document at /openapi/v1.json
dotnet run --project src/Pos.Api --launch-profile http
```

In Development the API also serves an interactive explorer at
`http://localhost:5177/scalar` (Scalar, `src/Pos.Api/Development/ApiExplorer.cs`):
sign in through `POST /api/v1/auth/login`, paste the `accessToken` into the
Bearer Token field, then use **Test Request** on any route. Its page alone gets a
same-origin Content-Security-Policy; Scalar's cloud features (Ask AI, registry,
Deploy) are blocked by that policy on purpose. It is never mapped outside
Development.

**Or run the whole stack in Docker** (PostgreSQL, migrator, grants, API, Caddy),
verified end to end on 2026-09-14:

```bash
./scripts/init-dev-secrets.ps1
docker compose up -d --build
```

The script works in Windows PowerShell 5.1 and PowerShell 7. It writes the
token-signing key to `.secrets/` (git-ignored, mounted as a Docker secret) and
creates `.env` with database passwords when there is none. The API is then at
`https://localhost` behind Caddy, connecting as the least-privilege `pos_app`.

```bash
# Tests; the PostgreSQL suites self-skip when no Docker daemon is reachable
dotnet test VaultFlow.slnx
```

The bootstrap owner account is created only on an empty database, only when
`BootstrapOwner:Enabled` is true, and its password comes from configuration.
Sign in, change it, then turn the flag off.

The development seed runs only when `Database:SeedDevelopmentData` is true (the
Development configuration sets it). Staff accounts additionally need
`Seeding:EnableDevelopmentAccounts`; their shared password is
`DevVaultFlow!2026`, documented so nobody mistakes it for a deployed credential.
```
