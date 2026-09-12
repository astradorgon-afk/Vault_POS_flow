# Architecture Decision Records

Each record: context, decision, consequences. Records are append-only; a
superseded decision is marked, not deleted.

---

## ADR-0001 — Double-entry inventory ledger with virtual external locations

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** The brief requires an immutable movement ledger and forbids direct
stock assignment. A naive ledger stores one row per event with source and
destination columns, which makes balance derivation a two-branch query and makes
"stock appeared from nowhere" events (supplier receipts, count increases)
structurally indistinguishable from bugs.

**Decision.** Each event produces two or more **legs**, each affecting exactly one
`(Location, Product, Batch, State)` bucket with a signed quantity. Legs of an
event share a `MovementGroupId` and must sum to zero **per product and batch**.
External counterparties — supplier, customer, write-off — are modelled as
`External`-kind locations, so every event, including receipts and sales, is
balanced.

`SourceLocationId`/`DestinationLocationId`/`SourceState`/`DestinationState` are
retained on every leg as event context, satisfying the brief's field list while
keeping the bucket semantics unambiguous.

**Consequences.**
- Balance = `SUM(quantity_delta) GROUP BY bucket`. One query, no branches.
- A single global integrity assertion: `SUM(quantity_delta) = 0` over the table.
- Supplier → warehouse → store → customer chains are walkable.
- Roughly 2× the row count of a single-row model. Accepted: the table is
  partitioned monthly and the integrity guarantee is worth the storage.

---

## ADR-0002 — In-transit stock is held at the source location

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** Dispatched goods must leave the source's available stock without
entering the destination's. The in-transit bucket could live at the source, at
the destination, or at a virtual "in transit" location.

**Decision.** `InTransit` is held at the **source** location, with
`DestinationLocationId` recorded on the movement.

**Rationale.** Custody and financial exposure remain with the sender until the
receiver counts the goods. A shortfall is therefore the sender's variance to
explain, which matches both accounting practice and the control intent of the
brief. Reporting "in transit to Store 1" is a filter on
`DestinationLocationId`, so nothing is lost versus a destination-held model.

**Consequences.** Shipment shortfalls need a holding bucket at the source →
ADR-0006.

---

## ADR-0003 — In-house CQRS dispatcher instead of MediatR

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** The application layer needs command/query dispatch with a behaviour
pipeline. MediatR is the common choice but is no longer freely licensed for
commercial use at all scales.

**Decision.** Implement `ICommand<T>`, `IQuery<T>`, handler interfaces, an
`IDispatcher` and an ordered `IPipelineBehaviour<,>` chain in
`Pos.Application.Common`. Roughly 200 lines, resolved through the built-in DI
container.

**Consequences.** No licensing exposure, no version coupling, explicit and
debuggable ordering. We own the (small) maintenance cost and lose MediatR's
notification/streaming extras, neither of which we need — domain events are
dispatched through the transactional outbox instead.

---

## ADR-0004 — Money as `decimal`, rounding rules fixed at the document boundary

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** All monetary values are `decimal`, stored as `numeric(19,4)`.
Quantities are `numeric(18,3)`, conversion factors `numeric(18,6)`. Line-level
arithmetic rounds to 4 dp; the document total rounds once to 2 dp with
`MidpointRounding.AwayFromZero`. Tax is computed per line and summed, never
derived from the document total. Cash tender/change round to the organization's
configured cash increment.

**Consequences.** Line-total sums always reconcile to the document total to the
cent. No binary floating point appears anywhere in the stack, including SQLite,
where `decimal` is stored as TEXT (ADR-0009).

---

## ADR-0005 — UUIDv7 identifiers

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** All entity identifiers are `Guid` generated with
`Guid.CreateVersion7()`.

**Rationale.** Devices must mint ids offline, which rules out database sequences
for primary keys. Random v4 GUIDs cause B-tree fragmentation on high-volume
tables; v7 is time-ordered and keeps index locality close to a sequence while
remaining globally unique and offline-generatable. It also gives a natural
creation-time ordering for events produced offline.

---

## ADR-0006 — `TransitVariance` inventory state

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** When 100 are dispatched and 98 arrive, the ledger must stay balanced
and the 2 units must remain visible until investigated. Writing them off
immediately would destroy evidence and pre-judge the cause.

**Decision.** Add `TransitVariance`, held at the source. The receipt group posts
`Src/InTransit −100`, `Dst/Available +98`, `Src/TransitVariance +2`. Quantity
leaves `TransitVariance` only through an explicit, permissioned resolution
(found at source, found at destination, write-off with reason and approver).

**Consequences.** One state beyond the brief's list, documented here and in
INVENTORY_LEDGER.md. Open variance is directly queryable and is a first-class
dashboard exception.

---

## ADR-0007 — Event-based synchronization with server-side idempotency

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** Devices upload business events, not table rows. Each event has a
client-generated `EventId` (UUIDv7) and a per-device monotonic sequence. The
server records `(event_id → outcome, result_json, payload_hash)` in
`sync.processed_event` **within the same transaction as the business effect**,
and replays the stored result for any repeat.

**Consequences.** Exactly-once semantics without distributed transactions. A
retry after a network timeout is free. A repeated `event_id` with a different
payload hash is treated as tampering rather than as an update. No table-level
replication code exists, so there is no path by which a device can overwrite
server state wholesale.

---

## ADR-0008 — The client runs the same application and ledger code as the server

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** `Pos.Client` references `Pos.Application` and `Pos.Infrastructure`
and executes the same sale/ledger handlers against SQLite. Environment
differences are injected behind ports (`IDocumentNumberGenerator`,
`IPermissionEvaluator`, `IApprovalGate`, `IClock`, `IChangeFeedPublisher`,
`IPriceResolver`). Only a whitelisted set of commands is registered on the client.

**Rationale.** Two implementations of "complete a sale" would drift, and the
drift would be invisible until inventory disagreed. One implementation, two
adapters.

**Consequences.** A slightly heavier client assembly footprint. Accepted. The
whitelist is asserted by a test, so no new command becomes offline-capable by
accident.

---

## ADR-0009 — SQLite stores `decimal` as TEXT

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** SQLite has no decimal type; EF Core's default mapping would use
`REAL` (binary double), which silently corrupts money.

**Decision.** A value converter stores `decimal` as invariant-culture TEXT with
fixed scale. Aggregation and comparison of monetary values happen in C#; SQL-side
ordering uses `CAST(x AS NUMERIC)` only where an approximate sort is acceptable
for display.

**Consequences.** Some aggregate queries materialise more rows on the client.
Local datasets are small (one location, 90-day retention), so this is acceptable.
Correctness of money is non-negotiable.

---

## ADR-0010 — Permissions resolved server-side per request, not embedded in the JWT

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** The catalogue has 60+ permissions. Embedding them as claims bloats
every request and, worse, means a revoked permission stays effective until the
token expires.

**Decision.** Access tokens carry `sub`, `device_id`, `loc`, `sstamp`,
`policy_ver`, `jti`. The API resolves the effective permission set from a cache
keyed by `(userId, policyVersion)`, invalidated on any role, override or user
change.

**Consequences.** Revocation is effective immediately. A cache lookup is added
per request (in-memory, sub-millisecond; Redis when scaled out). Offline devices
use a separate signed snapshot, which the server intersects with live
permissions at sync time (PERMISSIONS.md §5).

---

## ADR-0011 — Offline never widens authority

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** Permissions carry an `IsOfflineCapable` flag. No `*.approve`
permission, and no central-authority permission (product creation, price
management, quarantine release, user management), is offline-capable. Where goods
have physically moved and the books must reflect reality, the event is recorded
and placed in `PendingCentralReview` / `RequiresReview` rather than being either
silently accepted or discarded.

**Consequences.** A store cannot use a network outage to self-approve. Emergency
paths exist but are conspicuous, capped, and reviewed.

---

## ADR-0012 — Immutability enforced in four independent layers

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** Ledger and audit immutability is enforced by (1) init-only domain
types, (2) an EF `SaveChanges` interceptor, (3) database `BEFORE UPDATE OR
DELETE` triggers, and (4) a `pos_app` database role granted only `SELECT, INSERT`
on those tables. A fifth guard — the balance trigger — rejects any
`inventory_balance` change not matched by movements in the same transaction.

**Rationale.** Any single layer can be bypassed: an ORM can be sidestepped with
raw SQL, a trigger can be dropped by a table owner, a code review can miss a
setter. Together they make accidental or casual tampering impossible and
deliberate tampering require database-owner credentials, which the application
does not hold.

---

## ADR-0013 — Product name `Pos.*` assemblies under the `VaultFlow` solution

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** The product and repository are named **VaultFlow**; the solution
file is `VaultFlow.sln`. Assembly and namespace roots follow the project layout
specified in the brief (`Pos.Domain`, `Pos.Application`, …).

**Rationale.** The brief's layout is explicit and is what the team expects to
find. Renaming namespaces mid-project is costly; renaming a solution file is not.

---

## ADR-0014 — Migrations applied by a dedicated job, not by the API

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** `ApplyMigrationsOnStartup` is `true` only in Development. In all
other environments a one-shot `pos-migrator` container runs migrations before
API containers start, using the `pos_migrator` role.

**Rationale.** Multiple API replicas racing on startup migrations is a classic
outage. It also keeps the running application's database role free of DDL rights,
which is what makes ADR-0012's trigger protection meaningful.

---

## ADR-0015 — Device-scoped document numbers for POS documents

**Date:** 2026-09-11 · **Status:** Accepted

**Decision.** Sales, returns and shifts use `SAL-{yyyy}-{deviceCode}-{000000}`
style numbers allocated locally; purchase orders, transfers, receipts,
adjustments, counts and quarantine incidents use centrally allocated
`XXX-{yyyy}-{000000}` numbers via an atomic `INSERT … ON CONFLICT DO UPDATE …
RETURNING` counter.

**Rationale.** A receipt must print a unique number with no network round-trip;
a purchase order must be globally sequential for control purposes. The device
short code makes offline numbers collision-free without coordination.

---

## ADR-0016 — The balance guard checks a bounded window, not the whole bucket

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** The trigger that makes `UPDATE inventory_balance SET quantity = 100`
impossible has to verify that a balance change is backed by movements. The
obvious implementation re-sums the bucket's entire ledger history on every
change, which is correct but turns each POS line into a growing aggregate scan.

**Decision.** The trigger sums only the movements that (a) were inserted by the
current transaction, identified by `xmin`, and (b) fall in the identifier window
`(OLD.last_movement_id, NEW.last_movement_id]`. Because identifiers are UUIDv7
and therefore sort in creation order, that window is exactly the set of legs the
update accounts for. Several posts to the same bucket in one transaction each
verify their own window, which a naive delta check gets wrong.

Whole-bucket agreement between the ledger and the projection is verified
separately by the nightly reconciliation job, which is also where drift from any
other cause would surface.

**Consequences.** The guard is O(legs in this transaction) rather than O(bucket
history), so it stays cheap on the hot path. It catches every unaccounted
balance change, which is its purpose. It would not catch a scenario where an
attacker inserts fabricated movements *and* a matching balance change in one
transaction — but that attacker already needs INSERT on the ledger, and every
fabricated row is permanently visible and attributable.

---

## ADR-0017 — `Result` and `Error` live in the domain

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** `Result<T>` is used by the application layer, the infrastructure,
the API and the client. `Pos.Domain` references nothing, and `Pos.Shared`
references nothing, so the type cannot live in Shared and still be usable from
domain services such as the movement-group factory.

**Decision.** `Result`, `Result<T>`, `Error` and `ErrorType` live in
`Pos.Domain.Common`. Everything else references the domain, so everything else
gets them. `Pos.Shared` keeps only wire contracts and needs no result type.

**Consequences.** Domain validation returns values rather than throwing, which
is what lets the ledger report several problems with one event at once instead
of the first one it hits.

---

## ADR-0018 — Unfinished authorization fails closed

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** Identity lands in Phase 2, but the pipeline that consumes it exists
now. Something has to implement `IPermissionEvaluator` in the meantime.

**Decision.** `DenyAllPermissionEvaluator` returns false for every question, and
`HttpCurrentUser` reports no authenticated user until a real scheme is wired.
Any permission-bearing message therefore fails with an authentication error.

**Rationale.** A development stub that returns `true` is how authorization holes
ship: it works, so nobody revisits it. A stub that denies everything makes the
missing module impossible to ignore, and there is no window in which the system
is accidentally permissive.

**Consequences.** No business endpoint can be exercised end-to-end until Phase 2.
That is the intended trade.

---

## ADR-0019 — `Pos.Client` is created with Phase 12, not Phase 1

**Date:** 2026-09-11 · **Status:** Accepted

**Context.** The plan calls for creating every project in Phase 1. A .NET MAUI
project added now would be an empty shell that nonetheless requires the Android
SDK and a JDK on every build agent.

**Decision.** The MAUI Blazor Hybrid project is created in Phase 12, alongside
the offline storage work it exists to host. `Pos.SharedUI` exists now, so the
components the client will consume have a home from the start.

**Consequences.** CI stays fast and dependency-light until there is device code
to build. The reference rules for `Pos.Client` are already asserted in
ARCHITECTURE.md and will be enforced by an architecture test when the project
appears.

---

## ADR-0020 — The balance guard is a deferred constraint trigger

**Date:** 2026-09-12 · **Status:** Accepted · **Supersedes part of ADR-0016**

**Context.** ADR-0016 described a trigger that fires per statement and matches
movements by `xmin = pg_current_xact_id()` within an identifier window. The
integration suite, running against a real PostgreSQL instance, showed two
defects in it.

First, firing per statement requires movements to be inserted before the balance
rows that summarise them. Nothing guarantees that order: `inventory_balance` has
no foreign key to `inventory_movement`, so Entity Framework is free to write
balances first, and it did. A legitimate supplier receipt was rejected.

Second, the `xmin` filter is defeated by savepoints. EF takes a savepoint around
each `SaveChanges` when one is already inside an explicit transaction, so rows
written there carry the subtransaction's id while `pg_current_xact_id()` returns
the top-level one. Two postings in a single transaction matched nothing.

**Decision.** The guard becomes `DEFERRABLE INITIALLY DEFERRED`, running at
commit, and the `xmin` filter is dropped. The identifier window
`(OLD.last_movement_id, NEW.last_movement_id]` was always the substantive check;
because movement identifiers are UUIDv7 and movements are append-only, the window
names exactly the legs a change claims to account for.

**Consequences.** Statement order stops mattering and the check is strictly
stronger: it sees the transaction's final state. A bare
`UPDATE inventory_balance SET quantity = 100` still leaves `last_movement_id`
untouched, so the window is empty, the expected delta is zero, and the statement
is rejected — which is the whole point. Cost stays proportional to what the
transaction wrote. Concurrent postings to one bucket now abort at the guard
rather than silently racing; adding an optimistic concurrency token to the
balance row, already on the roadmap, turns that abort into a retry.

**Lesson.** Both defects were invisible in review and invisible on SQLite, which
has no such trigger. Testing the guarantee against the engine that enforces it is
what found them.

---

## ADR-0021 — Command paths opt back into change tracking explicitly

**Date:** 2026-09-12 · **Status:** Accepted

**Context.** `PosDbContext` sets `QueryTrackingBehavior.NoTracking` globally,
because reads dominate and a tracked read that accidentally writes back is a
hazard. Command paths that read an entity and then mutate it were therefore
changing detached objects, and `SaveChanges` wrote nothing. Sign-out reported
success while the session stayed live; device suspension left tokens working;
the authorization policy version never advanced, so cached permissions were never
evicted.

**Decision.** The global default stands. Every query whose result is mutated
carries an explicit `.AsTracking()`.

**Rationale.** The alternative — tracking by default — makes the dangerous case
silent and the safe case verbose. This way the annotation appears exactly where
a write is intended, which is also where a reviewer looks for one. Entities
obtained from outside the context, such as a user from `UserManager`, are
attached with only the changed property marked, so a stray update can never
become a blind full-row write.

**Consequences.** A new command path that forgets the annotation fails visibly in
an integration test rather than quietly in production, which is how this was
found in the first place.

---

## ADR-0022 — A PIN session is narrower than a password session

**Date:** 2026-09-12 · **Status:** Accepted

**Context.** Cashiers sign in on a shared terminal with an employee code and a
PIN. A store manager who does the same would otherwise carry their full
authority — including approval of adjustments and expired-batch overrides — into
a session authenticated by six digits typed on a touchscreen in front of
customers.

**Decision.** PIN sign-in issues a token granting only the permissions marked
`IsOfflineCapable`, and a refresh lifetime of hours rather than days. It is
refused unless the request carries an enrolled, operational device whose location
the user is assigned to. Attempts are throttled per device as well as per account.

**Consequences.** A shoulder-surfed PIN buys a till session, not approval
authority. Anything requiring real authority needs a full sign-in. This is the
same principle as ADR-0011 — a convenient path must not widen what someone can
do — applied to credentials rather than to connectivity.

---

## ADR-0023 — The persistence provider is chosen by configuration

**Date:** 2026-09-12 · **Status:** Accepted

**Context.** Integration tests need the API running against SQLite. The first
attempt removed the PostgreSQL `DbContext` registration from the container and
substituted another, which failed: `AddDbContext` registers more than the options
object, and EF refuses two providers in one provider.

**Decision.** `Database:Provider` selects PostgreSQL or SQLite, and the API reads
it during start-up.

**Rationale.** Deleting a host's registrations and rebuilding them is a test that
verifies a rearranged copy of the application. Making the provider a first-class
configuration choice means the tests host the real thing unchanged — real
middleware, real token validation, the real authorization policy provider — and
it is a setting the device client needs anyway.
