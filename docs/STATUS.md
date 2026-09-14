# Project Status

**Last updated:** 2026-09-14 · **Milestone:** Phase 9 (Inventory Control) complete, gap batches G1–G5 closed; Phase 10 (batch and expiration) **code complete, 445 tests passing, ready to commit**

This is the working status document. [ROADMAP.md](ROADMAP.md) holds the full
item-by-item plan; this file says where things actually stand, what was learned,
and what to pick up next.

---

## 1. Where the build is

| | |
|---|---|
| Solution builds | Clean, warnings-as-errors, analyzers on |
| Tests | **445 passing, 0 failing, 0 skipped** (2026-09-14 full solution run with Docker: Domain 215, Application 20, Infrastructure 40, Security 52, Architecture 13, API 105 — includes the Phase 10 expiry batch) |
| Migrations | 19, forward-only, applied cleanly against PostgreSQL 17 — by the Testcontainers suites, the API host test, and the Alpine migrations bundle in the compose stack |
| API host on PostgreSQL | Covered by `PostgresHostSmokeTests` (start-up, sign-in, numbered documents, ledger posting) and a full compose-stack run through Caddy as `pos_app`. See §3 for what these found. |
| Phases complete | 0 (architecture), 1 (foundation), 2 (identity), 3 (master data), 4 (inventory core), 5 (purchasing: PO lifecycle + goods receipts + returns/direct delivery/discrepancy resolution), 6 (transfers: main warehouse → store), 7 (transfers: store-to-store — central review, pre-approval tokens, emergency transfers with dual-manager authorization, replenishment recommendations), 8 (quarantine and unauthorized inventory — incidents, lines, photos, HQ review, release caps), 9 (inventory control — approved stock adjustments, counts with variance posting, repeat-variance detection) |
| Out-of-phase | Interim payment receipts (ADR-0026) — RCT-numbered cash documents, issue/view/print |
| Phases remaining | 10–18 — see §5 |

```
Pos.Domain.Tests            215 passing   invariants, money, ledger rules, catalog curation and price supersession, stock adjustments and counts, purchasing (PO/receipts/returns/DDA/discrepancies), transfers, payment receipts
Pos.Infrastructure.Tests     40 passing   ledger posting + concurrency + reconciler + PostgreSQL triggers, numbering, role grants, catalog curation, migration order (18 need Docker)
Pos.Architecture.Tests       13 passing   layering, ledger isolation, permission catalogue
Pos.Security.Tests           52 passing   authentication, tokens, permission matrix, log scrubbing
Pos.Application.Tests        20 passing   master-data commands, CQRS unit-of-work and negative-stock-attempt behaviours, receipt rendering
Pos.Api.IntegrationTests    105 passing   endpoints through the real pipeline (SQLite), user/role administration and two-factor, catalog curation, negative-stock report, stock adjustments and counts, API host runs on PostgreSQL (Docker)
```

(Pos.Sync.Tests exists as the Phase 5+ sync shell and currently declares no tests.)

The PostgreSQL suite needs a Docker daemon. It self-skips without one, so a
developer with no Docker still gets a green local run; CI always has one.

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
- 73-permission catalogue defined **in code** and seeded to the database, so a
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

Phase 8 shipped quarantine and unauthorized inventory: the `QuarantineIncident`
aggregate with lines and photos, quarantine ledger postings, HQ review outcomes
(register/link/investigate/release/reject/write-off with per-line caps and
approvers), and scope-exact list/detail reads. Interim payment receipts
(ADR-0026) landed alongside it. Three strands remain:

1. **Phase 10 — batch and expiration** (per ROADMAP): **code complete, 445 tests
   passing, ready to commit.** The groundwork is in (movement rules accept a set
   of reference documents, so `ExpiryQuarantine` is now justifiable by either a
   stock adjustment or an expiry run — `EXP`, `ReferenceDocumentType.ExpiryRun = 13`),
   and the expiry run itself is implemented: `LocationSettings.ExpiryWarningDays`
   (default 90), the `IExpiryService` scans (expired, expiring-soon, FEFO-ordered
   sellable batches), `RunExpiryCommand` behind the `inventory.expiry.run`
   permission, the `ExpiryWorker` background service (`Expiry:Enabled`, default
   6-hour interval) quarantining past-expiry stock `Available → Expired` through
   EXP-numbered `ExpiryQuarantine` groups with a system actor, and the
   `ExpiryOptions` binding. Full-suite re-run confirms the gate. Up next after the
   commit: the FEFO allocation service extraction, the POS sale-blocking override
   path (Phase 11), and expiring-soon/expired alerts (Phase 14 notifications).
2. **Phase 8 tail:** the automated quarantine triggers (unknown barcode at scan,
   over-receipt excess, unclear returns) that would raise incidents without staff
   action, plus notifications and the Owner-dashboard exception panel — both left
   unchecked in ROADMAP §8.
3. **Gap batches** (tracked in [PROGRESS.md](PROGRESS.md)): G1–G5 are done —
   correctness and deployment, receipts, identity administration, catalog
   curation, and the negative-stock record with the partitioning decision.
   Phase 9 followed them.

---

## 6. Running it

There is no user interface yet (the web and POS clients are Phases 11, 12 and
16), so "running it" means the HTTP API. The path verified on 2026-09-14 is a
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
