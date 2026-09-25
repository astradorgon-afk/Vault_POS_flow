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
- Roughly 2× the row count of a single-row model. Accepted: the integrity
  guarantee is worth the storage. (Monthly partitioning was planned here and
  deferred by ADR-0030.)

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

**Built in C30 (2026-09-17).** The whitelist is `OfflineCommandCatalogue`, an
explicit list of command types, and `AddOfflineClientApplication` is the device
composition root that registers only what it allows. Declaring a use case and
registering it are separate states: an entry stays `Pending` until the device
carries the tables its handler writes, because registering a handler whose ports
are missing would throw on resolve instead of failing closed with
`application.handler_unavailable`. `Pos.Architecture.Tests` asserts the
catalogue against the OFFLINE_SYNC.md §1 table (24 use cases), asserts every declared
permission is offline-capable, and asserts that nothing outside the list
resolves in the device container.

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

**Follow-up (2026-09-17).** The project arrived in C28 with that architecture
test, but C28 left CI restoring the whole solution on a runner without the MAUI
workloads, which fails. Since C29c the server job leaves the client out and
dedicated Android and Windows jobs build it (DEPLOYMENT.md §8).

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
rather than silently racing; the optimistic-concurrency token landed with ADR-0024
and turns that abort into a retry.

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

---

## ADR-0024 — Balance projection concurrency and reconciliation

**Date:** 2026-09-12 · **Status:** Accepted

**Context.** The balance projection must lose no writes under concurrency. Two
writers that both read quantity 100, both apply +10, and commit in turn produced
a projection that never saw the first movement — a permanently lost 10 units
that the ADR-0016 guard could not catch, because the second writer's check ran
against its own stale read. Earlier the guard was the fail-safe for exactly this
class of bug, and it remains so for bare UPDATEs; it cannot arbitrate two
legitimate postings racing on the same bucket.

**Decision.** `InventoryBalance.Version` is an optimistic-concurrency token
(`long`, starts at 1, bumped on every `Apply`, `.IsConcurrencyToken()`). A
writer whose row moved underneath it fails `SaveChanges` with
`DbUpdateConcurrencyException`. `InventoryLedger.PostAsync` catches that specific
failure for the projection step and re-applies the deltas to a fresh row with
exponential backoff, up to `MaxStandaloneAttempts` (10); the ledger append is
never re-attempted. Idempotent-projection adjustments (corrections whose
movement already exists) retry the same way. A reconciler (`IBalanceReconciler`)
replays the ledger into a fresh projection and compares it against the stored
one; a rebuild deletes and re-inserts under the still-armed deferred guard by
running `DISABLE → DELETE → ENABLE (before inserts) → INSERT → commit`, so the
rebuilt rows are validated at commit.

**Rationale.** Projecting is pure: applying the same movements to the same base
yields the same result, so replaying it under contention is always safe and
cheap. Re-inserting a ledger row is provably a no-op only because of `event_id`
idempotency, but it still re-runs validation, transitions, triggers and audit —
retrying the append converts a benign race into a wasteful write. Failing open
without a token was rejected: a lost projection write is silent corruption, and
the POS answers quantity questions from this table. Backing off rather than
spinning keeps unrelated contention low; 10 attempts bound worst-case latency
at the cost of one exception per attempt.

**Consequences.** Concurrent postings to one bucket now either both land
correctly or surface `inventory.balance_contention` (HTTP 412) on exhaustion.
The trigger remains the last line of defence against direct mutation. A rebuild
cannot run while a batch of postings commits because SQLite's writer lock and
PostgreSQL's row lock serialize them — contention on rebuild surfaces the same
412. Cost: one comparison on the version column per projected row; the guard
window (ADR-0016) still names exactly the movements a change claims.

**Lesson.** The race was invisible on the surface and on SQLite in single-writer
tests; only tests with genuinely concurrent writers against both engines found
it. The SQLite suite also exposed engine asymmetry: SQLite refuses to upgrade a
deferred read transaction after a peer commits (tests pin the token's
`OriginalValue` instead), and Microsoft.Data.Sqlite stores GUIDs in uppercase
"D" format, which matters for raw comparisons.

---

## ADR-0025 — Subscription billing is organisation-level with internal settlement

**Date:** 2026-09-13 · **Status:** Superseded by ADR-0026 · **Revised:** 2026-09-13

**Revised.** The first recording scoped the subscription per location and was
briefly marked Revoked. The stores are branches of the one business, so the
licence is for the whole organisation: a single subscription on the
`core.organization` row, one invoice and one receipt per period, with usage
metered organisation-wide. The internal settlement and receipt mechanism from
the original decision is unchanged.

**Superseded.** The subscription is not needed for v1: the stores are branches
of the one business, there is no third-party to license, and no payment
processor is in play. What remained useful was the receipt document — a
printable, emailable record of money taken in (walk-in sales, branch expenses,
owner cash withdrawals). ADR-0026 records that narrower decision. Deciding to
bundle, meter or invoice later is a new decision, not an extension of this one.

**Context.** VaultFlow is a single-organization deployment; multi-tenant SaaS is
an explicit non-goal (ARCHITECTURE.md §1.1), and no billing code exists anywhere
in the product. The stores are branches of the one business, so the commercial
unit is the whole organisation, not each location: there is no need to invoice
the owner for their own branches. A subscription that meters usage needs an
immutable usage record first — without it "usage-based" is a fiction on top of
a flat fee. No payment-processor integration is planned for v1.

**Decision.**

- A new `SubscriptionBilling` module (Domain and Application folders, `billing`
  database schema) models a single, organisation-level subscription:
  - **Subscription** — the licensing contract for the whole business: plan
    (a fixed tier set), status (`Trial`, `Active`, `Suspended`, `Cancelled`),
    the current billing period, a seat cap, and the assigned seat count. Seats
    are derived from the organisation's active user accounts, not a parallel
    list, so seat count cannot drift from who can actually log in.
  - **Price** — effective-dated rows; a new row supersedes an old one and
    nothing is ever edited, the DOMAIN_MODEL §5 pricing precedent.
  - **UsageEvent** — append-only, immutable metering records captured at the
    location grain (POS sales transactions, sync batches, and anything else a
    tier meters) but accumulated organisation-wide for billing; carrying the
    location keeps usage traceable to the branch that produced it. Captured
    with `EventId` idempotency (ADR-0007) so a retry can never double-count.
  - **UsageBalance** — a materialized projection of the period's usage,
    concurrency-guarded exactly like the inventory balance projection
    (ADR-0024): optimistic-concurrency token, retry with backoff on contention,
    replayable from the usage ledger by a reconciler.
  - **Invoice** — generated once per period with three line groups: tier base
    fee, seats × seat price, metered usage × unit price. Numbered `INV-` by the
    document counter (ADR-0015). Money follows ADR-0004 strictly: `decimal`,
    stored `numeric(19,4)`, 4-dp line arithmetic, the document total rounded
    once to 2 dp with `AwayFromZero`.
  - **Payment and Receipt** — internal settlement. A payment is an append-only
    ledger entry against an invoice, never edited or deleted; the same four
    immutability layers that protect the inventory ledger (ADR-0012) — init-only
    types, EF interceptor, DB triggers, least-privilege role — protect the
    financial tables. The receipt is a numbered document (`RCT-`) carrying the
    settled invoice, amount and method, renderable for print and email.
- A `BillingPaymentProvider` port is the seam for a certified external
  processor; the v1 implementation is an internal stub that records the payment
  against the invoice and issues the receipt. Cards remain prohibited by
  SECURITY.md T10; the stub accepts only `Cash`, `BankTransfer` and `Internal`
  methods.
- New permission constants `Permissions.Billing.*` (`billing.manage`,
  `billing.review`), endpoints under `/api/v1/billing` as minimal-API groups
  carrying `RequirePermissionAttribute`, returning `Result<T>` through the
  shared problem-details mapping (ADR-0017). Usage capture is an internal
  `EventId`-keyed `ICommand`, deliberately not an HTTP route a POS must call.
- The multi-tenant non-goal is unchanged: this licences the one Organization.
  What is new, and acknowledged, is a vendor↔organisation relationship, where
  "vendor" is an operational role inside the deployment — not a second tenancy
  model.

**Rationale.** The inventory ledger is the perfect prior art and it is in the
same codebase: append-only event rows, a derived projection, a reconciler, and
optimistic concurrency on the projection (ADR-0001, ADR-0024). Billing has the
same shape — immutable charges, materialized balances, periodic settlement — so
the module reuses a proven pattern instead of inventing a moneyless duplicate.
Organisation-level licensing matches the physical reality the revision
corrected: branches share one P&L, so metering is summed across locations rather
than charged per store, while the location grain on each `UsageEvent` preserves
traceability for whoever reviews the bill. Internal settlement keeps v1
shippable without a merchant account while the provider port buys the option to
go external later; the payments recorded by the stub remain the ledger of record
when a real provider replaces it.

**Consequences.**
- The first commercial capability enters the codebase; its ledger inherits the
  inventory ledger's durability and audit guarantees at the cost of a `billing`
  schema, two new document counters and a permission group.
- Invoices can be marked paid with no money actually moving. That is the point
  of the stub, and it is documented; the Provider seam on a payment
  (`Provider`, `ProviderTransactionRef`, `Status`) keeps the record honest when
  real processing arrives.
- Invoice accuracy is bounded by usage capture: `EventId` idempotency limits the
  failure mode to "an event never arrives" rather than "it arrives twice", and
  the reconciler can replay a period from the ledger to prove the bill.
- Suspension now affects the whole deployment, not one branch: a `Suspended`
  subscription must eventually constrain sign-in and POS sessions everywhere.
  That enforcement is deliberately later-phase; the subscription row carries
  the state, and the follow-up is recorded, not silently assumed.

---

## ADR-0026 — Receipts are standalone payment documents; subscription billing is deferred

**Date:** 2026-09-13 · **Status:** Accepted

**Context.** ADR-0025 considered subscription billing and was set aside: the
stores are branches of the one business, there is no external licensee, and the
"temporary internal billing and receipt" requirement was the only part with a
real v1 need. The deployment genuinely needs a printable, emailable record of
money taken in and paid out — walk-in sales, branch expenses, and owner cash
withdrawals. Standing up a billing domain (subscriptions, plans, prices,
invoices, usage metering) for that is overbuilding.

**Decision.**

- A standalone **Receipt** document type replaces subscription billing, as a
  temporary first build. No `Subscription`, `Invoice`, or `UsageEvent` —
  nothing is ever bundled, metered or invoiced in v1.
- A receipt is created with a `ReceiptKind`: `WalkInSale`, `BranchExpense` or
  `OwnerWithdrawal`. It records the branch (`LocationId`), amount, optional
  counter-party name, purpose note, and optional reference to an existing
  document (a sale, a purchase order) — it does not own a ledger entry.
- Receipts are numbered `RCT-` by the shared document counter (ADR-0015), so
  every receipt has a unique, human-readable number that printer and email
  rendering can reference.
- Money follows ADR-0004: `decimal`, stored `numeric(19,4)`, rendered to 2 dp.
  A single-line document, so there is no multi-line rounding question.
- A `ReceiptRenderer` service renders the receipt for print and email from one
  template; `GET /api/v1/receipts/{id}/print` renders the stored document
  through it, so issuing stays a plain `201 { id }` like every other create.
  Emailing is out of scope for v1 — the render is the seam.
- Permission `receipt.create` gates issuing (with `receipt.view` for reading);
  the endpoint is a minimal-API group under `/api/v1/receipts` using
  `RequirePermissionAttribute` and the shared `Result<T>` problem-details
  mapping (ADR-0017) like every other route.
- If subscription billing is ever wanted, it returns as a new decision on top
  of this one; receipts under this ADR are the durable record it would reconcile
  against. Nothing in this ADR blocks that.

**Rationale.** The minimum requirement is "a recoverable, printable proof of a
cash event". One aggregate, one number counter, one permission, one endpoint
group delivers that with the same shape as every other module. Deferring
billing keeps the codebase honest: there is no half-built subscription engine
to maintain, and the receipt is the auditable ancestor a future invoice can
reference.

**Consequences.**
- No financial ledger is introduced. VaultFlow does not yet track where money
  sits; a receipt records that a cash event happened, not the resulting balance.
- `RCT-` becomes the next shared document counter. Printing and emailing are
  render concerns separated behind `ReceiptRenderer`.
- The ADR-0025 subscription design is preserved in the log for revival, but is
  not carried into code.

---

## ADR-0027 — No retrying execution strategy; transactions stay explicit

**Date:** 2026-09-14 · **Status:** Accepted

**Context.** The PostgreSQL provider was registered with `EnableRetryOnFailure`.
A retrying execution strategy refuses user-initiated transactions unless every
operation in the transaction runs inside `CreateExecutionStrategy().ExecuteAsync`.
The unit-of-work behaviour, the inventory ledger, the balance reconciler and the
development seeder all open their own transactions, so on PostgreSQL the API
crashed at start-up and every command would have failed. No test noticed: the
endpoint suite hosts the API on SQLite (no strategy) and the PostgreSQL suites
build their own context without the option.

**Decision.**

- The PostgreSQL context is registered without a retrying execution strategy.
- Contention is retried where replay is known to be safe: the ledger's
  projection step (ADR-0024). A whole command is never re-run blindly, because a
  handler that allocates a document number or posts a movement is not safe to
  replay without its idempotency key.
- `PersistenceRegistrationTests` asserts the registered context does not retry,
  and `PostgresHostSmokeTests` runs the real host on PostgreSQL end to end.

**Rationale.** Transparent retry is attractive for single-statement reads, but
this system's writes are multi-statement transactions with invariants (zero-sum
ledger groups, gap-free numbering). Replaying one after a transient failure is
only correct when the operation is idempotent, which is an application decision,
not a driver setting. Explicit transactions keep that decision visible.

**Consequences.**
- A transient connection failure surfaces as a failed request instead of being
  retried silently. Clients already retry idempotent operations by event id
  (ADR-0007), and the sync engine will do so by design (Phase 13).
- If transient-failure retry is wanted later, it belongs in a pipeline behaviour
  that wraps the whole unit of work in the execution strategy and requires the
  command to carry an idempotency key; that is a new decision on top of this one.

---
## ADR-0028 — Identity administration safeguards and two-factor for administrators

**Date:** 2026-09-14 · **Status:** Accepted

**Context.** Users, roles, location assignments and permission overrides could
only be changed in the database. Administration endpoints make them manageable,
and also make them the most valuable thing an attacker or a careless
administrator can reach: whoever controls authority controls everything else.
Two latent problems surfaced while building it. The identity seeder, which runs
on every start, re-added any default role grant that was missing, so a
permission an administrator removed would silently return at the next restart.
And `Security:RequireTwoFactorForAdmins` was configured and read, but nothing
enforced it.

**Decision.**

- **Endpoints:** `/api/v1/users` (list, create, detail, update, disable, enable,
  roles, locations, overrides, password, PIN, two-factor reset) under
  `user.manage`; `/api/v1/roles`, `/api/v1/roles/{id}/permissions` and
  `/api/v1/permissions` under `role.manage`. Every change runs in one transaction
  with its audit entry and a policy-version bump, so it takes effect on the next
  request and cannot exist without its record.
- **Governance permissions** are a code-defined set (`Permissions.Privileged`):
  user and role management, settings, locations, all-locations access, audit,
  negative stock, balance rebuild, and every approval authority. Operational
  permissions (selling, counting, receiving) are not in it.
- **Safeguards**, checked against authority resolved fresh from the database:
  1. No self-administration: nobody changes their own roles, locations,
     overrides, approval tier, PIN, two-factor or account status.
  2. No escalation: a governance permission (by role, role edit, override, or by
     lifting a deny) or an approval tier can only be handed out by someone who
     holds it.
  3. No overreach: nobody changes an account or a role that holds governance
     authority, or a tier, the caller lacks — an administrator cannot disable or
     demote the owner.
  4. Always an administrator: a change that would leave no active account able to
     manage both users and roles is rolled back.
- **Default grants are applied once.** The seeder records each default grant it
  applies (`core.role_default_grant_applied`); a removed grant stays removed,
  while a permission new to the catalogue still reaches existing roles.
- **Two-factor for administrators.** When `RequireTwoFactorForAdmins` is on (the
  production default), an account holding `user.manage` or `role.manage` without
  an authenticator is refused sign-in with `auth.two_factor_enrolment_required`.
  It enrols through `POST /api/v1/auth/two-factor/setup` and `/enable`, which
  authenticate with the password, are rate-limited like sign-in, count towards
  lockout, and only work before two-factor is on. Enabling issues eight one-time
  recovery codes, accepted at sign-in in place of the authenticator code. A
  lost authenticator is reset by another administrator, which rotates the key.
  Development and the test hosts turn enforcement off; dedicated tests turn it on.
- **Log scrubbing.** A Serilog enricher masks properties whose names mark them as
  secrets (passwords, tokens, PINs, keys, connection strings, codes), including
  inside destructured objects and dictionaries.

**Rationale.** Decisions about authority are made from what an account can do,
never from role names, which keeps the rules correct when roles are edited. The
"hold it to give it" rule is what stops administration from being a privilege
escalation path, and restricting it to governance permissions keeps ordinary
administration possible: an administrator can set up cashiers and store managers
without being able to sell or to make someone an owner. Password-authenticated
enrolment is no weaker than sign-in was before enforcement, and it closes the
bootstrap problem of an owner who cannot obtain a token to enrol.

**Consequences.**
- The last-administrator rule is a backstop: with rules 1–3 in place it is hard to
  reach, and it exists so a future rule change cannot lock the business out.
- Recovery codes are shown once. An administrator who loses both authenticator
  and codes needs another administrator; a sole owner in that position needs
  database access, which is deliberate.
- Identity's stores need tracking queries to persist changes to existing rows,
  which the context's no-tracking default silently discarded; identity operations
  run inside a `TrackingScope` (see STATUS.md §3).

---
## ADR-0029 — Effective-dated price supersession and barcode retirement

**Date:** 2026-09-14 · **Status:** Accepted

**Context.** The catalogue could create products but not curate them. Two parts
of curation touch history that other records depend on. A selling price is
recorded against sales, and the database refuses two overlapping price periods
for the same product and scope (`ex_product_price_no_overlap`), so "change the
price" has to say what happens to the price already in effect. A barcode is how
every scan finds a product, and barcodes are globally unique because a code
pointing at the wrong product corrupts stock and revenue for two products at once.
The existing overlap check also treated a period's end as inclusive, so a price
ending at T and one starting at T were refused, although the database (which
compares `[from, to)`) accepts them.

**Decision.**

- **Prices never start in the past.** A price may start up to five minutes before
  the server clock (to absorb the delay between choosing "now" and applying it);
  anything earlier is `catalog.price_backdated`. A price's amount and start are
  never edited.
- **Supersession closes the predecessor.** When a new period overlaps nothing it
  is added. When it overlaps exactly one price that started earlier, that price's
  end is closed at the new start. If the new price is temporary and ends before
  the old one would have, a continuation row resumes the old amount from the new
  end until the old end. Anything else — a period that starts with or before a
  scheduled price, or spans several — is refused as `catalog.price_overlap`,
  because it would silently cancel a price someone scheduled.
- **Scopes are independent.** A store price does not close the organization-wide
  price; the price in effect at a store is the store's own row when it has one,
  otherwise the organization-wide row. Periods are half-open everywhere.
- **Barcodes are retired, never deleted or re-pointed.** Retiring keeps the row
  (`retired_at_utc`, `retired_by`), stops the code resolving in the by-barcode
  lookup, catalogue search and quarantine identification, and keeps the value
  reserved: it cannot be attached again, to this product or any other. Retiring
  the primary code promotes the longest-attached active code. The first code
  attached to a product becomes primary even when not asked.
- **Base unit, batch tracking, expiry tracking and shelf life stay fixed** after
  creation; they change how existing stock and ledger history are read.
- **Cost is a permission, not a field.** Product reads return
  `defaultPurchaseCost: null` to callers without `product.cost.view`, and supplier
  links (which carry the last purchase cost) require it.

**Rationale.** Closing the predecessor's open end is the only change to an
existing price row, and it only ever affects time that has not happened yet, so
a sale can always be explained by the price row in effect when it happened.
Refusing ambiguous overlaps costs a manager one extra step in a rare case and
never loses a scheduled price. Reserving retired codes follows the same rule as
re-pointing: a code that once meant one product must never quietly mean another.

**Consequences.**
- Cancellation of a scheduled (pre-effective) price is implemented in C19 —
  `Product.CancelScheduledPrice` plus
  `POST /api/v1/catalog/products/{id}/prices/{priceId}/cancel`. A price already
  in effect is never cancelled (`catalog.price_already_effective`): past prices
  are history, and an effective price is changed by scheduling a replacement.
  Cancellation rewinds only what supersession itself built: the predecessor the
  cancelled price superseded carries the old amount through the cancelled period
  (re-closing at the resumption's end — reopening when that base was open-ended
  — and the single continuation row that existed only to restore the old amount
  is removed with it). Cancellation never renumbers someone else's plan: a
  differing amount, or a chain of more than one same-amount continuation,
  starting where the cancelled price ends refuses
  (`catalog.price_cancel_successor` / `catalog.price_cancel_chain`) so a
  replacement is scheduled instead. The audit records the pre-cancel row under
  `product.price.cancelled`.
- A manufacturer that genuinely reuses a retired GTIN for a new product needs a
  deliberate data correction, which is intended.
- Saving a primary-barcode swap needs the demotion written before the promotion:
  EF Core cannot order updates around a filtered unique index, so `PosDbContext`
  writes demotions first inside the same transaction (see STATUS.md §3).

---

## ADR-0030 — The ledger and audit log are not partitioned in v1; refused draws are recorded after rollback

**Date:** 2026-09-14 · **Status:** Accepted

**Context.** The design documents assumed monthly range partitioning of
`inventory.inventory_movement` and `audit.audit_log`, created a year ahead by a
maintenance job. Neither table is partitioned, and no job exists. Separately,
INVENTORY_LEDGER.md §7 promised that every draw refused under the negative-stock
policy is recorded, but the refusal rolls back the command's transaction, so a
record written inside it disappears with it — nothing was recorded.

**Decision.**

- **No partitioning in v1** for either table.
  - The ledger is never pruned: a balance is the sum of every leg and the global
    `SUM(quantity_delta) = 0` assertion runs over the whole table (ADR-0001). The
    main operational benefit of partitions — detaching or dropping old ones — is
    therefore unavailable to it.
  - PostgreSQL requires every primary key and unique index on a partitioned table
    to include the partition key, so `PK (id)` and
    `UNIQUE (movement_group_id, leg_number)` would widen to include
    `recorded_at_utc`, and the database would no longer enforce them on their own.
  - EF Core cannot model partitioned tables; the DDL would be hand-written outside
    the model the migration drift check compares.
  - Scale does not call for it. Three stores initially; as a planning figure,
    3 stores × 2,000 sale lines a day × 2 legs, plus receiving and transfers, is
    about 15,000 legs a day, some 5–6 million a year — comfortably one table with
    the bucket and product/time indexes it already has.
  - Because both tables are append-only, partitioning later is a copy-forward
    migration with no update logic to preserve.
  - **Revisit** when the ledger passes about 50 million rows, when its bucket
    index no longer fits in memory, or when an archive policy with opening-balance
    snapshots is adopted. The audit log's seven-year cold-storage retention
    (SECURITY.md) is the first requirement that needs partitions; it bites in 2033,
    and the archiving work brings them.
- **Refused draws are recorded after the transaction ends.** The ledger collects
  each refused bucket (`INegativeStockAttemptRecorder`); a pipeline behaviour just
  outside the unit of work writes them once it has committed or rolled back,
  through a separate scope and context so nothing the failed command staged can
  be saved with them. Each becomes a row in `inventory.negative_stock_attempt`
  and an `inventory.negative_stock.attempted` audit entry. The table is
  append-only under the same four guards as the audit log. A message dispatched
  inside an outer transaction leaves its attempts to the outermost message.
  Failing to write them is logged and never changes the command's outcome.
- **Report:** `GET /api/v1/inventory/exceptions/negative-attempts` (newest first,
  filters) and `/summary` (attempts and total shortfall per product and location),
  under `inventory.view.all`.

**Consequences.**
- DATABASE.md, DEPLOYMENT.md and SECURITY.md no longer describe partitions or a
  partition maintenance job.
- Draws refused by code that calls the ledger outside the dispatcher (tests,
  maintenance utilities) are collected but not written; every production path
  goes through the dispatcher.
- Offline sales refused under `AllowOfflineWithReview` are recorded when the
  synchronization processor (Phase 13) posts them through the same pipeline.

---

## ADR-0031 — Stock adjustments and inventory counts post only after approval

**Date:** 2026-09-14 · **Status:** Accepted

**Context.** Phase 9 adds the two ways stock changes outside the normal document
flows: a stock adjustment (damage, spoilage, loss, theft, expiry, a correction)
and a physical count. Both remove or add stock with no supplier, customer or
transfer on the other side, which makes them the easiest routes for stock to
leave a business unnoticed. The ledger rules already required an approver and a
reason for every such movement; the documents had to decide who approves, what
the approval is measured against, and how a count stays correct while the store
keeps trading.

**Decision.**

- **Approval is posting.** An adjustment goes Draft → PendingApproval → Posted
  (or Rejected); a count goes Counting → PendingApproval → Posted (or back to
  Counting when rejected, or Cancelled). Nothing reaches the ledger before
  approval, and approving posts in the same transaction — there is no approved
  but unposted state to leave stock in limbo.
- **Value tiers and segregation of duties.** Approval runs through the approval
  gate on the document's absolute value: the sum of |quantity × unit cost| for an
  adjustment, of |variance × unit cost| for a count. The approver must hold
  `inventory.adjust.approve` or `inventory.count.approve` at the location, stay
  within their tier, and not be the author (the adjustment's creator, the count's
  submitter). Reversing a posted adjustment passes the same gate.
- **The reason decides the movement.** Damaged, Broken and Contaminated post as
  `Damage`; Spoilage, Loss and Theft as themselves; Expired moves available stock
  to the Expired state (`ExpiryQuarantine`) or writes off stock already there
  (`ExpiryWriteOff`). These only remove stock, balanced against EXT-WRITEOFF.
  Only `Other`, with notes of at least 10 characters, may add stock
  (`ApprovedStockAdjustment`). Reasons owned by other documents — count
  correction, supplier return, transit variance, emergency transfers — are refused.
- **Unit cost is captured when the document is raised** (the bucket's average
  cost, else the batch cost, else the product's default cost), so the value an
  approver is measured against cannot move while it waits.
- **Numbers.** ADJ is allocated when an adjustment posts, so rejected drafts do
  not consume numbers; CNT is allocated when a count opens, because staff quote
  it on the count sheet.
- **Counts compare the shelf with the system at the same moment.** Opening a count
  takes a sheet of available buckets in scope. Recording a line reads the bucket
  again and stores that system quantity with the counted one, so a sale between
  opening and counting is not mistaken for shrinkage. Approval refuses any line
  whose bucket moved after it was counted (`count.stock_moved_since_counted`);
  the approver rejects the count and the line is counted again. Only the variance
  posts, as `CountAdjustmentIncrease`/`Decrease` with reason `CountCorrection`.
  Counts cover the Available state.
- **Scope.** A full count may record products found that were not on its sheet;
  category, product and cycle counts accept only products on their sheet (a
  batch-tracked product can gain a line for a newly found batch).
- **Repeat variance.** On submission a varying line is flagged when the same
  product varied on another count posted at the location within 90 days, and an
  `inventory.count.repeat_variance` audit entry is written. The repeat-variance
  report ranks product-location pairs by the number of varying counts.
- **Reversal** posts, for each group the adjustment posted, a `Reversal` group with
  every leg negated at its original cost, referencing the original group.

**Consequences.**
- A busy store counting during trading may need to count some lines twice; the
  alternative, posting a variance that includes sales made mid-count, would
  record theft that never happened.
- Damaged, quarantined and expired stock is adjusted but not yet counted; a count
  of those states arrives when a flow needs it.
- `RequiresApprovalAboveValue` per location (DOMAIN_MODEL.md) is not built: every
  adjustment needs approval, and the tiers bound who may give it.

---

## ADR-0032 — Web-terminal documents are numbered by the server

**Date:** 2026-09-16 · **Status:** Accepted

**Context.** ADR-0015 gives every POS device its own number range keyed by the
device short code, so that a physical device can allocate `SAL-…` locally with
no network round-trip and never collide. C15 adds browser registers
(`DevicePlatform.Web`): a web terminal has no offline counter — the signed-in
cashier's session is its credential, and there is nothing resident on the
device to run between requests. But its documents must still sit in the
device's own sequential range for the receipt, search and reconciliation
experiences to stay uniform with physical terminals.

**Decision.** The server mints `SAL`/`RET`/`SHF` numbers for web terminals from
the same atomic shared-counter store as the central documents (ADR-0015), but
keyed by the register's **short code** rather than organisation-wide, via
`IDocumentNumberGenerator.NextScopedAsync`. The number routes refuse physical
devices with `device.not_web`: a device-owned counter is the only source of
truth for numbers printed while offline, so a server-allocated value would
eventually collide with one the device issued itself.

**Consequences.** A browser register numbers a sale with no local state and no
duplicate risk; gap-free numbering is maintained by the server's atomic
counter. Physical devices keep allocating locally as ADR-0015. The web
terminal's per-device sequence is exactly what the same device would have
produced had it allocated online, which keeps the eventual offline flows
interchangeable rather than a fork.

---

## ADR-0033 — A register signs people in, opens shifts and takes cash without head office

**Date:** 2026-09-25 · **Status:** Accepted

**Context.** Through C70 the desktop register needed head office for every
sign-in, every shift and every sale: the design in OFFLINE_SYNC.md §1 had the
storage, the command boundary, the outbox and the server's replay, but nothing
on the register used them, and `SyncBaselineService` assumed "a register signs
someone in only while it can reach head office". With the API down, the sign-in
screen said *"Head office could not be reached"* and the register was unusable.

**Decision.**

1. *Offline sign-in against a device-held verifier.* After head office accepts
   a password at a register, the register stores a PBKDF2-SHA256 verifier of it
   (device-owned `local_offline_credential`, never feed-owned). When head
   office cannot be reached — and only then — the register checks the password
   against it, requires the person to be active in its last store data, and
   requires unexpired offline authority at its store (SECURITY.md §2.5).
2. *The baseline carries the store's staff.* `GET /api/v1/sync/baseline`
   now includes the `UserChanged` row and offline snapshot of every active
   person assigned to the register's store or acting business-wide who holds an
   offline-capable permission there, not the caller alone. The caller is still
   first and always included. Because a baseline replaces the cached people
   wholesale, anyone disabled or reassigned disappears at the next connected
   sign-in by anyone, and so does their offline sign-in.
3. *Offline trading on the register.* The till answers its business date, cash
   rounding and open shift from its own store data; opens a shift through the
   already-whitelisted `OpenShiftCommand`; and completes **cash** sales through
   `DeviceOfflineSales`, which checks `sale.create` in the snapshot and that the
   cashier owns the open shift, numbers the sale under the register's own SAL
   counter, records it in `local_sale` for the receipt, and queues the
   `SaleCompleted` event in the same transaction. `CompleteSaleCommand` stays
   `Pending` in the offline catalogue: the device does not run the server's
   sale use case, it queues the same command input the online path sends, and
   head office settles price, VAT and stock when it replays it.
4. *Upload.* `DeviceOutboxUploader` sends the signed-in person's queued events
   in sequence to `/api/v1/sync/push` whenever they are signed in while
   connected, and stops at the first event of someone else, which head office
   would refuse in this session.
5. *A sale whose online answer never came back* is queued under the same SAL
   number and event identity, and the server's sale replay recognises a sale it
   already holds under that event instead of posting it twice.
6. The product's base unit of measure now travels in the baseline and is cached
   on `cache_product`, so a product can be rung up with no head-office call.

**Consequences.** A register keeps selling for cash through an outage, and
anything it did reaches head office through the same replay and conflict rules
as every other offline event (OFFLINE_SYNC.md §7). The limits are deliberate:
only people who have signed in on that register while connected can sign in on
it offline; card and e-wallet need a connection; returns, customer lookup and
the office console need a connection; closing a shift needs a connection and
waits until the register has delivered its offline sales, so the drawer is
counted against all of them. Queued events of one person wait for that person
to sign in while connected, because head office accepts a person's events only
in their own session.
