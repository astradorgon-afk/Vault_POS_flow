# Roadmap

Status legend: `[ ]` Pending · `[~]` In progress · `[x]` Complete

Last updated: 2026-09-14

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
      CI does not need the Android SDK before there is anything to build)
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
- [x] Effective-dated pricing endpoints with supersession and temporary prices (ADR-0029)
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

- [ ] Batch aggregate, creation from receipts
- [ ] FEFO allocation service
- [ ] Configurable expiry warning thresholds (90/60/30/14/7/3/1 days)
- [ ] Expiry worker: expiring-soon, expired, expired-but-available alerts
- [ ] Sale blocking for expired batches + authorized exception path

## Phase 11 — POS

- [ ] Shift open/close with cash reconciliation
- [ ] Barcode scan, product search, product grid, cart
- [ ] Pricing, discounts, taxes, rounding
- [ ] Payments (cash, card via provider, split)
- [ ] Atomic sale completion (sale + items + payments + ledger + audit)
- [ ] Receipt rendering and printing; permissioned reprint
- [ ] Void, return, refund with disposition
- [ ] Customer lookup and optional accounts
- [ ] Daily sales summary
- [ ] POS tests: inventory effects, insufficient stock, tax, discount, refund

## Phase 12 — Offline Storage

- [ ] SQLite schema, encryption, migrations
- [ ] Cache tables + read-only enforcement
- [ ] Client DI container with whitelisted command set
- [ ] Local document numbering (device-scoped)
- [ ] Permission snapshot storage and expiry
- [ ] Offline indicators, sync status UI

## Phase 13 — Synchronization

- [ ] Outbox, device sequence, canonical payload hashing
- [ ] Push endpoint with per-event idempotent processing
- [ ] Pull endpoint, change feed, cursors, rebaseline
- [ ] Retry queue with exponential backoff
- [ ] Conflict rules implementation
- [ ] Sync failure dashboard and manual retry
- [ ] Full sync test matrix

## Phase 14 — Notifications

- [ ] Persistent notifications + per-user receipts
- [ ] SignalR hub, groups, reconnection
- [ ] Alert generators (low stock, expiry, discrepancy, emergency, sync failure)
- [ ] Notification centre UI

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
