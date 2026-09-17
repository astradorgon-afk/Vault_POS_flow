# VaultFlow — System Architecture

> Multi-location Point-of-Sale and Inventory Management for a single business
> operating one Main Warehouse and N retail stores.
>
> **Status:** Architecture baseline (v1). See [ROADMAP.md](ROADMAP.md) for build order
> and [DECISIONS.md](DECISIONS.md) for the decision log.

---

## 1. Purpose and Scope

VaultFlow manages the physical and financial reality of inventory across one
business with:

| Concept | Instances |
|---|---|
| Main Warehouse (Main Inventory) | 1 — the control authority |
| Stores | 3 initially, unbounded thereafter |
| Suppliers | Many |
| Employees / Users | Many |
| POS devices | Many per store (Windows + Android) |
| Owner / Admin | Business-wide visibility |

The Main Warehouse is the **authority** for product registration, supplier
receiving, inter-location transfers, approval thresholds, and business-wide
monitoring. Stores hold their own inventory and run their own POS, but every
inventory-affecting action a store takes is either (a) pre-authorized,
(b) centrally approved, or (c) parked in a reviewable exception state.

### 1.1 Non-goals

- Multi-tenant SaaS (one `Organization` row exists for future-proofing, but the
  system is designed and secured as a single-business deployment).
- Manufacturing / bill-of-materials / work orders.
- Payroll or a general ledger (GL export is a future integration point).
- Storing raw payment card data. Card payments are delegated to a certified
  external provider; only provider, token, reference, amount and status are kept.

---

## 2. Architectural Drivers

These are the qualities the architecture is optimised for, in priority order.
Where two conflict, the higher one wins.

1. **Inventory integrity** — the recorded quantity must always be explainable by
   an unbroken chain of authorized events.
2. **Auditability** — who did what, where, when, on which device, and why.
3. **Authorization correctness** — enforced on the server; never only in the UI;
   never relaxed because the network is down.
4. **Transactional consistency** — a sale either fully happened or did not happen.
5. **Offline capability** — a store keeps selling when the link to the server dies.
6. **Idempotent synchronization** — a retried upload never duplicates money or stock.
7. **Maintainability / modularity** — a new module must not require touching the ledger.
8. **Performance** — POS interactions must feel instant; reporting must not block selling.

### 2.1 The Prime Directive

> **No code path outside the inventory ledger may set a stock quantity.**

There is no `product.StockQuantity = 100`. There is no `UPDATE inventory_balance
SET quantity = @x` reachable from application code. Stock changes *only* as the
arithmetic consequence of appending immutable `InventoryMovement` rows. This is
enforced structurally (see [INVENTORY_LEDGER.md](INVENTORY_LEDGER.md)) by:

- `InventoryBalance` being writable exclusively by the ledger component;
- a database trigger rejecting any balance update whose delta is not matched by
  movements appended in the same transaction;
- architecture tests that fail the build if any assembly outside the inventory
  infrastructure namespace touches the balance entity's setters.

---

## 3. System Context

```
                         +---------------------------------------+
                         |        Owner / Admin (browser)        |
                         |   Pos.Web - Blazor Web App (SSR+WASM) |
                         +------------------+--------------------+
                                            | HTTPS (cookie + antiforgery)
                                            v
 +---------------+   HTTPS/JSON   +----------------------------------------+
 | Supplier      |---(manual)---->|              Pos.Api                   |
 | (out of band) |                |  ASP.NET Core Web API + SignalR hubs   |
 +---------------+                |  AuthN/AuthZ - Sync - Admin - Reports  |
                                  +-------+----------------------+---------+
                                          |                      |
                                  +-------v--------+    +--------v---------+
                                  |  PostgreSQL 17 |    | Redis (optional) |
                                  |  authoritative |    | cache / backplane|
                                  +----------------+    +------------------+
                                          ^
        HTTPS/JSON  (sync push/pull, SignalR when online)
                                          |
 +----------------------------------------+---------------------------------+
 |                         Pos.Client - .NET MAUI Blazor Hybrid             |
 |   Windows POS lane           Android POS / stock terminal                |
 |   |-- Local SQLite (encrypted) - cached master data + local events       |
 |   |-- Outbox / sync engine - idempotent, retrying, ordered               |
 |   +-- Hardware adapters - printer, scanner, cash drawer                  |
 +--------------------------------------------------------------------------+
```

**Trust boundaries**

- The device is **untrusted**. Everything it uploads is re-validated server-side.
- The device's clock is **untrusted**. Server time is authoritative for ordering
  and for business-date assignment.
- The client UI's permission checks are **cosmetic**. They hide buttons; they do
  not protect data.
- The cached permission snapshot on a device is a **ceiling, not a grant**: it can
  only ever be narrower than what the server will accept at sync time.

---

## 4. Solution Structure (Clean Architecture)

Dependencies point inward only. `Pos.Domain` references nothing but the BCL.

```
VaultFlow.sln
|-- src/
|   |-- Pos.Domain/           entities, value objects, enums, domain events,
|   |                         invariants, state machines. No EF, no HTTP, no DI.
|   |-- Pos.Application/      use cases (commands/queries), ports (interfaces),
|   |                         validators, authorization policies, DTO mapping.
|   |-- Pos.Infrastructure/   EF Core (PostgreSQL + SQLite), Identity, sync
|   |                         engine, document numbering, notifications, files.
|   |-- Pos.Api/              HTTP endpoints, auth middleware, SignalR hubs,
|   |                         problem-details, rate limiting, composition root.
|   |-- Pos.Web/              Blazor Web App: owner dashboard, admin, reports.
|   |-- Pos.Client/           .NET MAUI Blazor Hybrid: POS + store inventory.
|   |-- Pos.SharedUI/         Razor Class Library shared by Web and Client.
|   +-- Pos.Shared/           wire contracts (DTOs), sync envelopes, primitives
|                             shared by server and client. No behaviour.
+-- tests/
    |-- Pos.Domain.Tests/            unit - invariants, state machines, money
    |-- Pos.Application.Tests/       unit - use cases with fakes
    |-- Pos.Infrastructure.Tests/    integration - EF, ledger, concurrency
    |-- Pos.Api.IntegrationTests/    integration - endpoints, authz, problem details
    |-- Pos.Sync.Tests/              idempotency, ordering, conflict, offline replay
    |-- Pos.Security.Tests/          authz matrix, tokens, replay, revocation
    +-- Pos.Architecture.Tests/      layering + "no direct stock write" enforcement
```

### 4.1 Reference rules

| Project | May reference |
|---|---|
| `Pos.Domain` | — |
| `Pos.Shared` | — |
| `Pos.Application` | `Pos.Domain`, `Pos.Shared` |
| `Pos.Infrastructure` | `Pos.Application`, `Pos.Domain`, `Pos.Shared` |
| `Pos.Api` | `Pos.Application`, `Pos.Infrastructure`, `Pos.Shared` |
| `Pos.SharedUI` | `Pos.Shared` |
| `Pos.Web` | `Pos.SharedUI`, `Pos.Shared`, `Pos.Application`, `Pos.Infrastructure` |
| `Pos.Client` | `Pos.SharedUI`, `Pos.Shared`, `Pos.Domain`, `Pos.Application`, `Pos.Infrastructure` |

`Pos.Client` and `Pos.Web` never reference `Pos.Api`. `Pos.Domain` never
references `Pos.Application`. These rules are asserted by `Pos.Architecture.Tests`.

> `Pos.Client` referencing `Pos.Application`/`Pos.Infrastructure` is deliberate:
> the offline POS must execute the *same* sale use case and the *same* ledger
> code against SQLite that the server executes against PostgreSQL, so that an
> offline sale and an online sale are the same business event. Only a
> whitelisted subset of use cases is registered in the client container
> (see [OFFLINE_SYNC.md](OFFLINE_SYNC.md)).

### 4.2 Module boundaries inside Application/Infrastructure

Vertical feature folders, not technical folders:

```
Pos.Application/
  Common/           (dispatcher, behaviours, results, abstractions)
  Identity/         Locations/     Catalog/      Suppliers/
  Purchasing/       Inventory/     Transfers/    Quarantine/
  Counting/         Batches/       Sales/        Returns/
  Sync/             Notifications/ Reporting/    Auditing/
```

Each module owns its commands, queries, validators, DTOs and permission
constants. Cross-module communication goes through domain events or through an
explicitly declared port — never by reaching into another module's handlers.

---

## 5. Application Layer Pattern

CQRS-lite with an in-house dispatcher (no MediatR — see ADR-0003).

```csharp
public interface ICommand<TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct);
}
```

Every command flows through an ordered pipeline:

```
Dispatch
  -> CorrelationBehaviour      ensure CorrelationId on the ambient context
  -> LoggingBehaviour          structured, scrubbed
  -> ValidationBehaviour       FluentValidation, fail fast -> 400
  -> AuthorizationBehaviour    permission + location scope + approval threshold
  -> IdempotencyBehaviour      for commands carrying an EventId
  -> UnitOfWorkBehaviour       open tx, dispatch domain events, commit once
  -> Handler
```

`Result<T>` carries success/failure without exceptions for expected failures
(insufficient stock, unknown barcode, permission denied). Exceptions are
reserved for defects and infrastructure faults.

**Controllers and Razor components contain no business logic.** An endpoint
builds a command, dispatches it, and maps `Result<T>` to an HTTP response or UI
state. That is all.

---

## 6. Inventory Core (summary)

Full detail in [INVENTORY_LEDGER.md](INVENTORY_LEDGER.md).

- `InventoryMovement` is **append-only, immutable, double-entry**.
- Every event produces two or more legs whose signed quantities **sum to zero**.
- External counterparties (supplier, customer, write-off) are modelled as
  **virtual locations**, so the whole ledger satisfies `SUM(Quantity) = 0` globally.
- Stock is keyed by `(LocationId, ProductId, BatchId, InventoryState)`.
- `InventoryBalance` is a materialized projection, updated in the same database
  transaction as the movements, and fully rebuildable from the ledger.
- Corrections are **reversals and adjustments**, never edits or deletes.

Inventory states: `Available`, `Reserved`, `InTransit`, `Quarantine`, `Damaged`,
`Expired`, `ReturnPending`, `PendingInspection`, `TransitVariance`, `External`.

---

## 7. Offline and Synchronization (summary)

Full detail in [OFFLINE_SYNC.md](OFFLINE_SYNC.md).

- The client is an **event producer**, not a database replica. Nothing is
  "table-synced"; the client uploads *business events* with globally unique IDs.
- Server processing is **idempotent by `EventId`** with a stored result, so a
  retry after a timeout returns the original outcome instead of double-posting.
- Downstream data reaches devices through a monotonic **change feed + cursor**,
  scoped to the device's location.
- Conflicts are resolved by **domain-specific rules**, never by last-write-wins.
- Offline never widens authority. Actions that need approval online still need
  approval offline — they land in `PendingCentralReview`.

---

## 8. Security (summary)

Full detail in [SECURITY.md](SECURITY.md) and [PERMISSIONS.md](PERMISSIONS.md).

- ASP.NET Core Identity for the user store and password hashing (PBKDF2, 600k iterations).
- Devices authenticate with a registered `DeviceId` bound to their refresh token.
- Short-lived access tokens (10 min) plus rotating refresh tokens with reuse detection:
  presenting a spent token burns the whole family.
- Authorization is **permission-based**; roles are only bundles of permissions, and
  no code anywhere branches on a role name.
- Access tokens carry **no permission list**. Permissions resolve per request from a
  cache keyed by the authorization policy version, so revoking authority takes effect
  immediately rather than at the token's next expiry.
- Every check is a pair of **(permission, location)**. This single mechanism is what
  confines a store manager to their own store; `location.all` is the only bypass.
- Every sensitive endpoint carries a permission requirement, and the application
  pipeline checks again — so a command dispatched by a background worker or the sync
  processor is authorized identically.
- A validated token is re-checked on every request against the account's security
  stamp and the device's status, because a signature only proves what was true at
  issue time.
- Cashier PIN sign-in is device-bound, location-checked, and granted only the
  offline-capable permission subset (ADR-0022).
- Full immutable `AuditLog` with before/after values and correlation IDs, under the
  same four guards as the inventory ledger.
- HTTPS everywhere; HSTS; strict CSP; configurable rate limiting; login throttling
  counted per account **and** per address.

---

## 9. Real-time

SignalR hub `/hubs/notifications` with authenticated groups per user and
location. Users holding `location.all` join the all-locations group:

| Event | Consumers |
|---|---|
| Transfer requested / approved / dispatched / received / discrepancy | source + destination + HQ *(arrival shortages alert since C26)* |
| Quarantine incident raised / resolved | HQ + originating store |
| Low stock, expiry, receiving discrepancy, high-value adjustment | HQ + owning location *(low stock, expiry and receiving discrepancies alert today)* |
| Sync failure, device offline | HQ |

Every real-time message has a **persisted `Notification` row** written first.
SignalR is a delivery accelerator, never the source of truth — the web client
reloads the same scoped notification feed after reconnect. Offline devices will
retrieve it through the Phase 13 change feed when synchronization is built.

---

## 10. Data Architecture

| Store | Role |
|---|---|
| PostgreSQL 17 | Authoritative. All history, ledger, identity, audit. |
| SQLite (per device) | Cached master data, local events, outbox, local sales. |
| Redis *(optional)* | SignalR backplane and permission/price cache when scaled out. |
| Object storage / filesystem | Product images, quarantine incident photos. |

Design rules (enforced in `Pos.Infrastructure`):

- Money is `decimal(19,4)`; quantities `decimal(18,3)`. **No floating point, ever.**
- All timestamps stored as `timestamptz` in UTC. Display converts to
  `Asia/Manila` via a time-zone provider; the timezone is configuration, not a constant.
- Business dates (which trading day a sale belongs to) are computed from the
  *location's* timezone and stored as a separate `date` column.
- Foreign keys are `RESTRICT` by default. Financial and inventory rows are never
  hard-deleted — they are superseded or reversed.
- Optimistic concurrency via `xmin` (PostgreSQL) and a `rowversion` shadow
  property (SQLite).
- Naming: snake_case in the database, PascalCase in C#, mapped by convention.

---

## 11. Deployment

Full detail in [DEPLOYMENT.md](DEPLOYMENT.md).

```
                Internet
                   | 443
            +------v-------+
            | Reverse proxy|  (Caddy/nginx) TLS, HSTS, security headers
            +------+-------+
        +----------+----------+
   +----v----+ +---v----+ +---v-----+
   | pos-api | |pos-web | | (redis) |      docker compose / swarm
   +----+----+ +---+----+ +---------+
        +-----+----+
        +-----v------+
        | postgres   |  volume + WAL archiving + nightly base backup
        +------------+
```

MAUI clients ship as MSIX (Windows) and signed APK/AAB (Android), configured
with the API base URL and enrolled with a one-time device enrolment code.

---

## 12. Observability

- **Serilog** structured logging to console (JSON in production) plus rolling
  file; enrichers for `CorrelationId`, `UserId`, `DeviceId`, `LocationId`.
- Scrubbing removes password, token, refresh-token and card fields before any
  sink sees them.
- Health endpoints: `/health/live`, `/health/ready` (DB, migrations, workers).
- Metrics via `System.Diagnostics.Metrics`: sale latency, sync batch size, sync
  failure rate, ledger append latency, balance drift check result.
- A nightly **ledger reconciliation job** recomputes balances from the ledger and
  raises a critical alert on any drift.

---

## 13. Key Cross-Cutting Rules

1. When convenience conflicts with inventory integrity, integrity wins.
2. When data is inconsistent, raise an exception record; never silently repair.
3. When offline, never bypass an approval that would be required online.
4. Financial and inventory records are append-only; correct with reversals.
5. All authorization is re-evaluated server-side at sync time, using the
   permissions in force **at the moment the server processes the event**, with
   the device's cached snapshot as an additional upper bound.
6. Every business document has both a `Guid` identity and a human-readable number.
7. Every mutation writes an audit entry inside the same transaction.

---

## 14. Document Index

| Document | Contents |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | This document |
| [STATUS.md](STATUS.md) | Where the build stands, what was learned, what is next |
| [DOMAIN_MODEL.md](DOMAIN_MODEL.md) | Aggregates, entities, value objects, invariants |
| [DATABASE.md](DATABASE.md) | Tables, keys, indexes, constraints, numbering |
| [INVENTORY_LEDGER.md](INVENTORY_LEDGER.md) | Movement model, states, posting rules |
| [TRANSFER_WORKFLOW.md](TRANSFER_WORKFLOW.md) | Transfer state machine, custody, discrepancies |
| [PURCHASING.md](PURCHASING.md) | PO lifecycle, receiving, discrepancies, returns |
| [QUARANTINE.md](QUARANTINE.md) | Unauthorized inventory workflow |
| [POS.md](POS.md) | Sale, shift, return and refund flows and rules |
| [OFFLINE_SYNC.md](OFFLINE_SYNC.md) | Sync engine, idempotency, conflicts, cursors |
| [SECURITY.md](SECURITY.md) | Threat model, authn, tokens, devices, hardening |
| [PERMISSIONS.md](PERMISSIONS.md) | Permission catalogue and role matrix |
| [API.md](API.md) | Endpoint surface and conventions |
| [DEPLOYMENT.md](DEPLOYMENT.md) | Docker, environments, backups, runbook |
| [ROADMAP.md](ROADMAP.md) | Phased build plan with status |
| [DECISIONS.md](DECISIONS.md) | Architecture decision records |
