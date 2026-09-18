# Roadmap

Status legend: `[ ]` Pending · `[~]` In progress · `[x]` Complete

Last updated: 2026-09-16

> Phases 0–8 are complete except where marked. Phase 4 was pulled forward
> because the ledger is the foundation every other module posts through.
> See [STATUS.md](STATUS.md) for where the build actually stands and
> [PROGRESS.md](PROGRESS.md) for the running log of the gap batches and the
> remaining phases.

---

## Phase 0 — Architecture (foundation for everything else)

- [x] Inspect repository / initialise git
- [x] `docs/ARCHITECTURE.md` — system architecture
- [x] `docs/DOMAIN_MODEL.md` — aggregates, entities, value objects, invariants
- [x] `docs/DATABASE.md` — schema, keys, indexes, constraints, numbering
- [x] `docs/INVENTORY_LEDGER.md` — movement model and posting rules
- [x] `docs/TRANSFER_WORKFLOW.md` — transfer state machine and custody
- [x] `docs/OFFLINE_SYNC.md` — sync engine, idempotency, conflict rules
- [x] `docs/PERMISSIONS.md` — permission catalogue and role matrix
- [x] `docs/SECURITY.md` — threat model and security controls
- [x] `docs/PURCHASING.md` — PO lifecycle and receiving
- [x] `docs/QUARANTINE.md` — unauthorized inventory workflow
- [x] `docs/POS.md` — sale, shift, return, refund flows
- [x] `docs/API.md` — endpoint surface and conventions
- [x] `docs/DEPLOYMENT.md` — Docker, environments, backups
- [x] `docs/DECISIONS.md` — ADR log
- [x] `docs/ROADMAP.md` — this file

## Phase 1 — Solution Foundation

- [x] Create solution and the server/web projects with correct references
      (`Pos.Client` is created in Phase 12 with the rest of the device work, so
      CI does not need the Android SDK before there is anything to build; since
      C29c CI builds it in its own Android and Windows jobs)
- [x] Central package management (`Directory.Packages.props`) and build props
- [x] `.editorconfig`, nullable + warnings-as-errors, analyzers
- [x] Transitive package pins for published advisories, enforced by NU1903
- [x] Domain primitives: `Entity`, `AggregateRoot`, `ValueObject`, strongly typed ids
- [x] `Money`, `Quantity`, `Barcode`, `Sku`, `DocumentNumber` value objects
- [x] `Result` / `Result<T>` and the error catalogue
- [x] CQRS dispatcher + behaviours (logging, validation, authorization, unit of work)
- [x] Correlation identifier middleware and response header
- [x] Serilog structured logging, source-generated log messages
- [x] Global error handling to RFC 9457 ProblemDetails with no internal detail
- [x] EF Core `PosDbContext` (PostgreSQL + SQLite), strongly typed id conversion,
      decimal-as-text on SQLite
- [x] Append-only interceptor
- [x] Initial PostgreSQL migration, plus the ledger immutability and balance-guard
      triggers as a hand-written migration
- [x] Least-privilege database roles and grants (`build/docker/initdb`)
- [x] Docker: API image, one-shot migrator image, compose, reverse proxy
- [x] CI: build, test, pending-model-change check, secret scan
- [x] Architecture tests (layering, ledger write isolation, permission catalogue)
- [x] Options binding with `ValidateOnStart` for every configuration section
- [x] Append-only `AuditLog` table, writer and database guards
- [x] Serilog sensitive-data scrubbing (`SensitiveDataScrubber` enricher)
- [ ] Idempotency pipeline behaviour (the ledger is idempotent today; the generic
      behaviour lands with the sync module in Phase 13)

## Phase 2 — Identity and Authorization

- [x] ASP.NET Core Identity integration with Guid keys and tuned hashing
- [x] Permission catalogue defined in code and seeded; roles as permission bundles
- [x] `UserLocationAssignment`, `UserPermissionOverride` with grant/deny and expiry
- [x] Permission evaluator with a cache keyed by the authorization policy version
- [x] `RequirePermission` attribute, dynamic policy provider, location scoping
- [x] JWT issuance with a two-key ring; refresh rotation with reuse detection
- [x] Cashier PIN authentication, device-bound and narrowed to offline permissions
- [x] Device registration, one-time enrolment codes, suspension, revocation
- [x] Login throttling per account and per address; lockout; configurable rate limits
- [x] Approval tiers, self-approval refusal, eligible-approver lookup
- [x] Bootstrap owner seeder, refused once any user exists
- [x] Security tests: authentication, token lifecycle, revocation, permission matrix
- [x] Enforce two-factor for accounts holding `user.manage`/`role.manage` at sign-in,
      with password-authenticated enrolment, recovery codes and administrator reset
- [x] User, role and override administration endpoints, with the ADR-0028 safeguards
- [ ] Redis backplane so permission revocation stays immediate when scaled out

## Phase 3 — Master Data

- [x] Organization, locations (main warehouse, stores, external virtual locations)
- [x] Location settings (negative-stock policy, receipt text, direct delivery, offline grace)
- [x] Location settings live-wired into `ILedgerPolicyProvider` (replaces the strict provider)
- [x] Suppliers, categories, brands, units of measure — read and create endpoints
- [x] Products and barcodes — read (list / by id / by barcode) and create endpoints
- [x] Product edit, barcode management (attach, retire, primary), deactivate/activate endpoints
- [x] `ProductLocationSetting` (min/reorder/target/max/preferred) endpoints
- [x] Effective-dated pricing endpoints with supersession and temporary prices (ADR-0029); pre-effective cancellation added in C19 (ADR-0029 consequence)
- [x] Unit-conversion and product-supplier link endpoints
- [x] Cost visibility enforced on product reads (`product.cost.view`)
- [x] Development seed data — locations, counterparties, reference data, products, staff accounts
- [ ] Admin UI for master data (client application phase; the API surface this phase builds against is agreed above)

## Phase 4 — Inventory Core

Substantially delivered in Phase 1, because everything else depends on it.

- [x] `InventoryMovement` entity + EF mapping + indexes and check constraints
- [x] `InventoryBalance` projection + balance-guard trigger
- [x] `IInventoryLedger.PostAsync` with idempotency, structural validation,
      state-transition validation and availability checks
- [x] Inventory states and the movement-type transition table
- [x] Weighted-average costing on the projection
- [x] Negative-stock policy enforcement (default: prohibit)
- [x] Ledger unit tests and provider-level integration tests
- [x] Table partitioning by month on `inventory_movement` — decided against for v1 (ADR-0030)
- [x] `NegativeStockAttempt` record and its exception report
- [x] Optimistic concurrency retry on balance contention (`Version` token, in-ledger
      projection retry, `max_standalone_attempts` 10)
- [x] Reconciliation worker and `rebuild-balances` command (`IBalanceReconciler`,
      tripwire worker, maintenance-gated endpoint)
- [x] Concurrency tests (parallel posts to one bucket, stale write, cost drift —
      SQLite and PostgreSQL)

## Phase 5 — Purchasing

- [x] Purchase orders, lines, approval workflow with tiers
- [x] Goods receipts, lines, batch/expiry capture
- [x] Receiving discrepancies (shortage, overage, damaged, wrong item)
- [x] Direct supplier-to-store delivery authorization path
- [ ] Unapproved direct delivery → quarantine + incident
      *(excess stock already posts to `Quarantine` on receipt; the automatic
      `QuarantineIncident` for it is not raised — see Phase 8 automated triggers)*
- [x] Supplier returns
- [x] Purchasing tests incl. partial receiving

## Phase 6 — Main Warehouse to Store Transfers

- [x] Transfer aggregate + state machine
- [x] Request, review, approve/modify/reject
- [x] Picking with FEFO batch selection
- [x] Dispatch (ledger: Available → InTransit)
- [x] Receiving and verification (ledger: InTransit → Available + variance)
- [x] Discrepancy creation and resolution paths
- [x] Chain-of-custody events and document timeline
- [x] Transfer tests incl. partial receipt and discrepancy

## Phase 7 — Store to Store Transfers

- [x] Store-to-store request with central approval
- [x] Pre-approval tokens (issue, scope, consume, expire)
- [x] Emergency offline transfers with dual manager authorization
- [x] `PendingCentralReview` queue and ratify/correct/reject outcomes
- [x] Emergency transfer visibility and monthly caps

## Phase 8 — Quarantine and Unauthorized Inventory

- [ ] Unknown-barcode detection at receiving and POS
      *(not built: incidents are raised through `POST /api/v1/quarantine`; the
      automated scan/over-receipt/return triggers in QUARANTINE.md §1 are a
      future integration)*
- [x] `QuarantineIncident` aggregate, lines, photos
- [x] Quarantine ledger entries
- [x] HQ review: register product, link barcode, approve, reject, investigate, return
- [x] Release to Available with quantity caps
- [ ] Notifications and dashboard exception panel *(Phase 14 notifications;
      Owner-dashboard panel not built)*

## Phase 9 — Inventory Control

- [x] Stock adjustments with reasons and approval thresholds (value tiers, no self-approval, reversal)
- [x] Damage, expiry, spoilage, loss, theft flows
- [x] Inventory counts: full, cycle, category, product-specific
- [x] Snapshot, variance calculation, approval, posting (stale lines refused)
- [x] Variance reporting and repeat-variance detection

## Phase 10 — Batch and Expiration

- [x] Batch aggregate, creation from receipts
- [x] FEFO allocation service
      *(C22 routes transfer pick validation through the shared `FefoBatches` allocator used by sales)*
- [x] Configurable expiry warning thresholds (90/60/30/14/7/3/1 days)
- [x] Expiry worker: expiring-soon, expired, expired-but-available alerts
      *(C23 persists location-scoped alerts with stable deduplication keys before
      quarantining past-expiry stock into `Expired`)*
- [x] Sale blocking for expired batches + authorized exception path
      *(sellable-batch query excludes past-expiry stock; shortfalls covered only by
      expired stock refuse with `inventory.expired_only`, the `sale.expired_override`
      exception path records a mandatory reason in the `sale.expired.override` audit,
      and the web terminal probes-then-confirms (C18))*

## Phase 11 — POS

- [x] Shift open/close with cash reconciliation (C5)
- [x] Barcode scan, product search, product grid, cart (C14)
- [x] Checkout orchestration: register pick-up, shift start, discounts, cash payment, atomic submit (C15)
- [x] Pricing, discounts, taxes, rounding
      *(server-side pricing, VAT classification and discount authorization
      complete since C8/C12; C15 adds `sale.discount`-gated line discounts in
      the web checkout and cash change rounded to the location increment;
      C19 adds scheduled-price cancellation before a price takes effect
      (ADR-0029 consequence); C21 adds the browser price-schedule workspace)*
- [x] Payments (cash, card via provider, split)
      *(C15 landed cash checkout; C17 adds card/e-wallet methods and
      split payment mixes in the web checkout; C20 adds thermal and browser
      print/PDF receipt layouts)*
- [x] Atomic sale completion (sale + items + payments + ledger + audit) + sale detail/read-back (C6)
- [x] Receipt rendering and printing; permissioned reprint
      *(plain-text render + first print landed in C6; web reprint with reason
      and the sale-lifecycle views landed in C16; C20 adds thermal and browser
      print/PDF layouts)*
- [x] Void a completed sale with ledger reversal and reason (C1 domain + C8 route)
- [x] Web sale lifecycle: search, detail, void, returns/refunds/dispositions
      *(backend routes + full web pages completed in C16; C17 adds payment-
      provider methods to the checkout; C20 adds a Printable / PDF receipt action)*
- [x] Return, refund and disposition routes (C9 returns/refunds; C10 line inspection,
  partial dispositions, quarantine incidents, ledger posting and retry safety)
- [x] Customer lookup and optional accounts (C11)
- [x] Daily sales summary (C7)
- [x] POS tests: inventory effects, insufficient stock, tax, discount, refund
  *(inventory effects, insufficient stock and VAT covered in C8; returns/refunds
  covered in C9; discount authorization and totals covered in C12)*

## Phase 12 — Offline Storage

- [x] SQLite schema, SQLCipher encryption with a platform-secure key, and the
  initial device migration (C28); the key applied as a raw key (C29b)
- [x] Cache tables + read-only enforcement: change-feed applier with a
  same-transaction cursor, interceptor and trigger write guards (C29)
- [x] Client DI container with whitelisted command set
      *(C30 declares the 24 offline use cases of OFFLINE_SYNC.md §1 in
      `OfflineCommandCatalogue` and registers only those in
      `AddOfflineClientApplication`. C33 makes the first three live — shift
      open, suspend and resume execute on a device against `local_cashier_shift`
      — and the rest stay `Pending` until the device carries the local sale and
      movement tables their handlers write to)*
- [x] Local document numbering (device-scoped)
      *(C31: `document_counter` on the device, an atomic upsert that joins the
      caller's transaction; a device numbers only under its own enrolled short
      code and refuses every centrally numbered type)*
- [x] Permission snapshot storage and expiry
      *(C31: `DeviceSnapshotPermissionEvaluator` checks expiry at every
      evaluation, scopes a grant to its location, and refuses any permission the
      catalogue does not mark offline-capable; the feed refuses a snapshot that
      widens one and a policy version older than the stored one)*
- [x] Offline indicators, sync status UI
      *(C32: `DeviceStatusProvider` reads storage, enrolment, connectivity,
      last-received store data and cached-authority expiry; `DeviceStatusBanner`
      in `Pos.SharedUI` renders one concern at a time. Pending-upload count
      waits for the Phase 13 outbox)*

## Phase 13 — Synchronization

- [x] Outbox, device sequence, canonical payload hashing
      *(C34: `local_outbox_event` and a single-row `device_sequence`, both
      written in the caller's transaction so a rolled-back event releases its
      number; `CanonicalJson` sorts properties at every depth so declaration
      order cannot change a hash. C38–C42 made every POS use case a producer,
      and C44 added `ShiftClosed`, which C40 had left the device unable to
      report)*
- [x] Push endpoint with per-event idempotent processing
      *(C43: `POST /api/v1/sync/push` — one transaction and one verdict per
      event, the idempotency record committed with the effect, a reused
      identifier carrying a different hash refused as tampering, a per-device
      checkpoint deferring gaps. C44 applies the four shift events centrally,
      replaying each transition through the aggregate; C45 applies the sale by
      replaying it through the same handler the online endpoint runs, and C46
      lets a line name the price row it was charged from so a stale price is
      recorded rather than re-priced; C47 adds void and reprint and C48 the
      return and the refund. Every POS event a device can queue now lands
      centrally, and a coverage test keeps the two lists from drifting)*
- [x] Pull endpoint, change feed, cursors, rebaseline
      *(C50: `sync.change_feed` and the interceptor that fills it, ordered by a
      counter row rather than a database identity so a cursor cannot read past a
      change. C51: the route, scoped from the device's registration, with a
      server-decided cursor and `410 Gone` for a cursor the feed cannot honour)*
- [x] Retry queue with exponential backoff
      *(C49: `SyncUploader` and `SyncRetryPolicy` — jittered backoff to a
      thirty-minute ceiling, eight attempts then escalation, nothing deleted;
      `HttpSyncTransport` treats every non-answer as one, a revoked device
      included. The scheduling loop that calls it is the client's)*
- [x] Conflict rules implementation
      *(C52: a replayed sale posts marked for review, oversells are recorded
      whether permitted or refused, and a sale of a withdrawn product lands
      flagged. C56 closes the last row: FEFO allocates a shortfall onto the batch
      it finished on, so an oversell is accepted end to end and the shelf is left
      saying what it now is)*
- [x] Sync failure dashboard and manual retry
      *(C53: the failure list reads the server's own verdicts, and a retry is a
      directive on the register's feed rather than a re-apply here)*
- [x] Full sync test matrix
      *(C54: a register downloads its catalogue, trades through an outage,
      uploads and catches up, against the real server over HTTP. C55 adds the
      baseline a new register starts from, and the recovery a `410` now runs on
      its own)*

## Phase 14 — Notifications

- [x] Persistent notifications + per-user receipts
- [x] SignalR hub, groups, reconnection
      *(C24 authenticates the hub and derives user/location groups from current database authority)*
- [x] Alert generators (low stock, expiry, discrepancy, emergency, sync failure)
      *(C23 completes expiring-soon and expired-run alerts; C25 adds low-stock alerts;
      C26 adds receiving/transfer discrepancy alerts; C27 adds emergency-transfer alerts;
      C57 adds the sync-failure generator, which waited on Phase 13 and now sweeps
      the server's own verdicts — Critical when a register's queue has stopped,
      Warning when the event landed and needs looking at)*
- [x] Notification centre UI
      *(C24 adds the scoped HTTP feed, read/read-all receipts, live badge and responsive web ledger)*

## Phase 15 — Analytics and Reports

- [ ] Sales reports (product, category, store, cashier, payment method)
- [ ] Gross profit and margin
- [ ] Inventory on hand, valuation, movement history
- [ ] Warehouse distribution, transfer reports
- [ ] Purchase history and supplier performance
- [ ] Adjustments, expiry, damage, spoilage, shrinkage
- [ ] Physical count variance, unauthorized inventory, quarantine
- [ ] Inventory ageing buckets, slow movers, dead stock, turnover
- [ ] Audit activity and sync problem reports
- [ ] Export (CSV/XLSX)

## Phase 16 — Owner Dashboard

- [ ] Business overview KPIs with date and location filters
- [ ] Store comparison
- [ ] Inventory panels (available, in transit, quarantine, low, out, over)
- [ ] Exception panels (unknown products, discrepancies, high-value adjustments,
      negative-stock attempts, expired-still-available, repeated variances,
      failed sync, offline devices, emergency transfers)
- [ ] Document timeline drill-down and movement chain explorer

## Phase 17 — Testing

- [ ] Domain unit tests
- [ ] Application use-case tests
- [ ] Infrastructure/integration tests with Testcontainers
- [ ] API integration tests
- [ ] Synchronization test suite
- [ ] Security test suite
- [ ] Concurrency tests (parallel sales, transfer races, document numbering)
- [ ] Coverage gate in CI

## Phase 18 — Deployment

- [ ] Dockerfiles (API, Web) with non-root users
- [ ] docker compose for dev and a production overlay
- [ ] Reverse proxy with TLS, HSTS, security headers
- [ ] Migration job separate from the API
- [ ] Backup and restore scripts, restore drill documented
- [ ] Production logging/metrics configuration
- [ ] MAUI packaging: MSIX (Windows), signed AAB (Android)
- [ ] CI: build, test, analyze, publish images

---

## Deliberate deferrals

Recorded so they are not mistaken for oversights:

| Item | Why deferred | Tracked in |
|---|---|---|
| Redis backplane | single API instance initially; interface is in place | Phase 18 |
| `sales.sale` partitioning | not needed below ~5M rows; migration prepared | Phase 18 |
| GL / accounting export | out of scope v1 | future |
| Loyalty programme | customer entity exists; scheme undecided | future |
| Multi-currency | single currency (PHP) v1; `Money` already carries currency | future |
| Weighing-scale integration | hardware abstraction exists (`IWeighingScale` planned) | future |
