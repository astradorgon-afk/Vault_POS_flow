# Project Status

**Last updated:** 2026-09-16 · **Milestone:** Phase 10 complete; Phase 11 (POS) **C1–C17 complete**

This is the working status document. [ROADMAP.md](ROADMAP.md) holds the full
item-by-item plan; this file says where things actually stand, what was learned,
and what to pick up next.

---

## 1. Where the build is

| | |
|---|---|
| Solution builds | Clean, warnings-as-errors, analyzers on |
| Tests | **921 passing without PostgreSQL: Domain 369, Application 241, Infrastructure 79, Security 52, Architecture 13, API 167** (2026-09-16, through C17). PostgreSQL tests require Docker; earlier batches verified them against PostgreSQL 17. |
| Migrations | 28, forward-only. Through C10 applied cleanly against PostgreSQL 17 by Testcontainers, the API host test and the Alpine migrations bundle; C11 model drift is clean, but its migration has not been executed on PostgreSQL because Docker is unavailable. |
| API host on PostgreSQL | Covered by `PostgresHostSmokeTests` (start-up, sign-in, numbered documents, ledger posting) and a full compose-stack run through Caddy as `pos_app`. See §3 for what these found. |
| Phases complete | 0 (architecture), 1 (foundation), 2 (identity), 3 (master data), 4 (inventory core), 5 (purchasing: PO lifecycle + goods receipts + returns/direct delivery/discrepancy resolution), 6 (transfers: main warehouse → store), 7 (transfers: store-to-store — central review, pre-approval tokens, emergency transfers with dual-manager authorization, replenishment recommendations), 8 (quarantine and unauthorized inventory — incidents, lines, photos, HQ review, release caps), 9 (inventory control — approved stock adjustments, counts with variance posting, repeat-variance detection), 10 (batch and expiration — expiry warning thresholds, expiry run quarantining past-expiry stock as `EXP`-numbered groups) |
| Out-of-phase | Interim payment receipts (ADR-0026) — RCT-numbered cash documents, issue/view/print |
| Phases remaining | 11–18 — see §5 |

```
Pos.Domain.Tests            369 passing   invariants, money, ledger rules, catalog curation and price supersession, stock adjustments and counts, purchasing (PO/receipts/returns/DDA/discrepancies), transfers, payment receipts, POS and customer-account rules
Pos.Infrastructure.Tests     79 passing   non-PostgreSQL ledger, numbering, catalog, migration-order, POS and customer repository coverage
Pos.Architecture.Tests       13 passing   layering, ledger isolation, permission catalogue
Pos.Security.Tests           52 passing   authentication, tokens, permission matrix, log scrubbing
Pos.Application.Tests       241 passing   master-data commands, CQRS behaviours, receipt rendering, POS handlers and named-customer sale validation
Pos.Api.IntegrationTests    167 passing   endpoints through the real pipeline (SQLite), including customer lifecycle, permissions, audit behavior, discount enforcement, the web-terminal checkout surface, sale-lifecycle read routes and the payment-mix checkout flows
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

---

## 3. Bugs the tests caught this session

Worth recording, because each was invisible in review and would have been
expensive in production.

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
| Cost-variance notification not raised | Phase 5 | The receipt flags `costVariancePendingApproval` and records the approver; the notification/queue item is not built. |
| Supplier performance report not built | Phase 5 | Measurable after returns post; dashboard/analytics phase. |
| Supplier-return dispatch does not automatically raise a quarantine incident for unreported stock | Phase 5 | `QuarantineIncident` (Phase 8) exists; the automated bridge from dispatch is not built. |
| Incident raising is manual — no automatic scan/POS triggers | Phase 8 | `POST /api/v1/quarantine` exists; the automated triggers in QUARANTINE.md §1 (unknown barcode at scan, over-receipt excess, unclear returns) are a future integration. ROADMAP §8 keeps that item unchecked. |
| Register-product is two dispatches; a mid-flow failure could orphan a product | Phase 8 | The unidentified-line pre-check prevents it in the normal path; only a crash between the product creation and the line identification would leave the product in the master with its barcode reserved. |
| Receipts cannot be voided or emailed | ADR-0026 | Issue, list, detail and print exist and receipts are immutable; a mistaken receipt is corrected by issuing another. A void document and email delivery need their own decision. |
| No quarantine notifications or dashboard exception panel | Phase 8 | Raises and resolutions post through the API; age-based escalation and the Owner-dashboard exception panel are Phase 14. |
| A scheduled future price cannot be cancelled | Phase 3 | A price that would replace it is refused (`catalog.price_overlap`); cancellation of not-yet-effective prices is left for POS pricing (Phase 11, ADR-0029). |
| Generic idempotency pipeline behaviour not built | Phase 1 | The ledger is idempotent on its own; the generic behaviour lands with sync in Phase 13. |
| Permission cache is in-process | Phase 2 | Single API instance is exact. Scaling out needs a Redis backplane; revocation would otherwise lag by the 15-second policy-version window. |

---

## 5. What to do next

Phase 10 (batch and expiration) is committed. Phase 11 POS C1–C16 are committed:
shift lifecycle, sale completion/read/receipt/void, daily sales summary, and
referenced/blind returns and refunds have HTTP endpoints. C15 adds the
server-numbered web-terminal slice (`DevicePlatform.Web`, `/api/v1/terminal`)
and the checkout orchestration on top of the C14 cart workspace: register
pick-up, shift start, line discounts, cash payment and atomic sale
submission; physical terminals stay on their own offline counters
(`device.not_web`). The follow-up refund
correction excludes the current return from the database's prior-refund totals;
the aggregate already counts its own refunds. Partial refunds now reach the
original payment without double counting, while both refund caps stay enforced.
Return disposition is implemented in C10: one-line partial inspections route
goods to Available, Quarantine (with an incident), Damaged, supplier-return
staging, or EXT-WRITEOFF. Immutable event history supports retries; a per-line
concurrency token prevents competing requests consuming the same units.
Customer lookup and optional accounts are implemented in C11: searchable paged
records, detail and lifecycle routes, separate read/manage permissions, audited
mutations, and active-customer validation during sale completion. Migration
`20260916015216_CustomerAccounts` must be applied before running the updated API.
C16 completes the web sale-lifecycle workflows: sales search with location/date
filters, receipt text and reprint with reason, void with reason, accept-return
with per-line quantity, return detail with per-line dispositions and refund
forms (blind returns cash-locked), and refund history — all gated by the
server-side permission model and backed by `TerminalBar` register/shift context.
The remaining work is:

1. **Phase 11 — POS:** the main flow and remaining web surfaces.
   Checkout orchestration is done (C15) and the sale-lifecycle web views are
   done (C16). Immediate items: payment-provider methods (card, e-wallet) and
   split payments in the web checkout, receipt thermal/PDF layouts, and the
   POS pricing/scheduled-price cancellation flow.
2. **Phase 10 tail:** the FEFO allocation service extraction, the POS sale-
   blocking override path for expired batches (Phase 11), and expiring-soon /
   expired alerts (Phase 14 notifications).
3. **Gap batches** (tracked in [PROGRESS.md](PROGRESS.md)): G1–G5 are done —
   correctness and deployment, receipts, identity administration, catalog
   curation, and the negative-stock record with the partitioning decision.
   Phase 9 followed them.

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
