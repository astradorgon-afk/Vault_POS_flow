# Roadmap

Status legend: `[ ]` Pending · `[~]` In progress · `[x]` Complete

Last updated: 2026-09-11

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

- [x] Create solution and all projects with correct references
- [x] Central package management (`Directory.Packages.props`) and build props
- [x] `.editorconfig`, nullable + warnings-as-errors, analyzers
- [x] Domain primitives: `Entity`, `AggregateRoot`, `ValueObject`, strongly-typed ids
- [x] `Money`, `Quantity`, `Barcode`, `Sku`, `DocumentNumber` value objects
- [x] `Result` / `Result<T>` and error catalogue
- [x] CQRS dispatcher + pipeline behaviours (correlation, logging, validation, authorization, idempotency, unit of work)
- [x] Configuration and options validation
- [x] Serilog structured logging + sensitive-data scrubbing
- [x] Global error handling → RFC 9457 ProblemDetails
- [x] EF Core `PosDbContext` (PostgreSQL + SQLite providers)
- [x] Immutability interceptor + audit interceptor
- [x] Initial migrations (Postgres) incl. triggers and grants
- [x] Docker compose for Postgres + API
- [x] Architecture tests (layering, endpoint attribution, ledger write isolation)

## Phase 2 — Identity and Authorization

- [ ] ASP.NET Core Identity integration with Guid keys
- [ ] Permission catalogue seeding; roles and role-permission mapping
- [ ] `UserLocationAssignment`, `UserPermissionOverride`
- [ ] Permission evaluator + cache with policy versioning
- [ ] `RequirePermission` attribute, dynamic policy provider, location scoping
- [ ] JWT issuance, refresh-token rotation with reuse detection
- [ ] Cashier PIN authentication
- [ ] Device enrolment, suspension, revocation
- [ ] Login throttling, lockout, rate-limit policies
- [ ] Approval tiers and `IApprovalGate`
- [ ] Security tests: authorization matrix, token lifecycle, revocation

## Phase 3 — Master Data

- [ ] Organization, locations (main warehouse, stores, external virtual locations)
- [ ] Location settings (negative-stock policy, thresholds, receipt text)
- [ ] Suppliers, categories, brands, units of measure
- [ ] Products, barcodes, unit conversions, product-supplier links
- [ ] Effective-dated pricing with overlap prevention
- [ ] `ProductLocationSetting` (min/reorder/target/max/preferred)
- [ ] Admin UI for master data with search, filters, pagination, audit drill-down

## Phase 4 — Inventory Core

- [ ] `InventoryMovement` entity + EF mapping + partitioning
- [ ] `InventoryBalance` projection + balance-guard trigger
- [ ] `IInventoryLedger.PostAsync` with full validation chain
- [ ] Inventory states and movement-type transition table
- [ ] Weighted-average costing and valuation
- [ ] Negative-stock policy enforcement + `NegativeStockAttempt`
- [ ] Optimistic concurrency and retry on balance contention
- [ ] Reconciliation worker + rebuild command
- [ ] Ledger unit and concurrency tests

## Phase 5 — Purchasing

- [ ] Purchase orders, lines, approval workflow with tiers
- [ ] Goods receipts, lines, batch/expiry capture
- [ ] Receiving discrepancies (shortage, overage, damaged, wrong item)
- [ ] Direct supplier-to-store delivery authorization path
- [ ] Unapproved direct delivery → quarantine + incident
- [ ] Supplier returns
- [ ] Purchasing tests incl. partial receiving

## Phase 6 — Main Warehouse to Store Transfers

- [ ] Transfer aggregate + state machine
- [ ] Request, review, approve/modify/reject
- [ ] Picking with FEFO batch selection
- [ ] Dispatch (ledger: Available → InTransit)
- [ ] Receiving and verification (ledger: InTransit → Available + variance)
- [ ] Discrepancy creation and resolution paths
- [ ] Chain-of-custody events and document timeline
- [ ] Transfer tests incl. partial receipt and discrepancy

## Phase 7 — Store to Store Transfers

- [ ] Store-to-store request with central approval
- [ ] Pre-approval tokens (issue, scope, consume, expire)
- [ ] Emergency offline transfers with dual manager authorization
- [ ] `PendingCentralReview` queue and ratify/correct/reject outcomes
- [ ] Emergency transfer visibility and monthly caps

## Phase 8 — Quarantine and Unauthorized Inventory

- [ ] Unknown-barcode detection at receiving and POS
- [ ] `QuarantineIncident` aggregate, lines, photos
- [ ] Quarantine ledger entries
- [ ] HQ review: register product, link barcode, approve, reject, investigate, return
- [ ] Release to Available with quantity caps
- [ ] Notifications and dashboard exception panel

## Phase 9 — Inventory Control

- [ ] Stock adjustments with reasons and approval thresholds
- [ ] Damage, expiry, spoilage, loss, theft flows
- [ ] Inventory counts: full, cycle, category, product-specific
- [ ] Snapshot, variance calculation, approval, posting
- [ ] Variance reporting and repeat-variance detection

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
