# Project Status

**Last updated:** 2026-09-12 · **Milestone:** Phase 4 (Inventory Core) — complete, pending final verification

This is the working status document. [ROADMAP.md](ROADMAP.md) holds the full
item-by-item plan; this file says where things actually stand, what was learned,
and what to pick up next.

---

## 1. Where the build is

| | |
|---|---|
| Solution builds | Clean, warnings-as-errors, analyzers on |
| Tests | **144 passing, 0 failing, 0 skipped** (local run, SQLite + PostgreSQL with Docker) |
| Migrations | 7, forward-only, applied cleanly against PostgreSQL 17 |
| Phases complete | 0 (architecture), 1 (foundation), 2 (identity), 3 (master data), 4 (inventory core) |
| Phases remaining | 5–18 — see §5 |

```
Pos.Domain.Tests            41 passing   invariants, money, ledger rules
Pos.Infrastructure.Tests    29 passing   ledger posting + concurrency + reconciler + real PostgreSQL triggers
Pos.Architecture.Tests      13 passing   layering, ledger isolation, permission catalogue
Pos.Security.Tests          33 passing   authentication, tokens, permission matrix
Pos.Application.Tests       12 passing   master-data commands and CQRS unit-of-work behaviours
Pos.Api.IntegrationTests    16 passing   endpoints through the real pipeline (SQLite, incl. maintenance gate)
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
significant choice is recorded in [DECISIONS.md](DECISIONS.md) (24 ADRs).

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

---

## 3. Bugs the tests caught this session

Worth recording, because each was invisible in review and would have been
expensive in production.

**The no-tracking default silently discarded writes.** `PosDbContext` defaults to
`NoTracking` because reads dominate. Command paths that read an entity and then
mutate it — sign-out, session revocation, token rotation, device suspension,
policy-version bumps — were changing detached objects, so `SaveChanges` wrote
nothing. Sign-out appeared to succeed and the session stayed live. Fixed by
opting those paths back into tracking explicitly, which also documents intent at
each call site.

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
| `inventory_movement` and `audit_log` not yet partitioned | Phase 4 | Fine at current volume; the maintenance job is designed, not built. |
| `NegativeStockAttempt` record and exception report not built | Phase 4 | Guard blocks the attempt today; the diagnostic record for the dashboard awaits. |
| Product edit, barcode, price, location-settings and deactivate endpoints not built | Phase 3 | Contracts agreed in `API.md`; land with the catalog curation phase. |
| `ProductLocationSetting` and unit-conversion API not exposed | Phase 3 | Domain and persistence exist; endpoints deferred. |
| Serilog sensitive-data scrubbing policy not implemented | Phase 1 | No secret is currently logged, but nothing enforces that. |
| Generic idempotency pipeline behaviour not built | Phase 1 | The ledger is idempotent on its own; the generic behaviour lands with sync in Phase 13. |
| Two-factor is supported but not enforced for admins | Phase 2 | `RequireTwoFactorForAdmins` is configured and read, not yet enforced at sign-in. |
| Permission cache is in-process | Phase 2 | Single API instance is exact. Scaling out needs a Redis backplane; revocation would otherwise lag by the 15-second policy-version window. |
| No user-administration endpoints | Phase 2 | Users, roles and overrides are manageable through the database and the seeders only. |

---

## 5. What to do next

Phase 3 ships the master data everything downstream reads. The next phases build
on it:

1. **Phase 5 — Purchasing:** purchase orders, goods receipts, batch/expiry
   capture, receiving discrepancies, direct-supplier delivery authorization.
2. **Catalog curation** (deferred Phase 3 rows, now unblocked): product edit,
   barcode management, deactivate/activate, effective-dated pricing endpoints,
   `ProductLocationSetting`, unit conversions, product-supplier links.
3. **User-administration endpoints** so roles, overrides and location
   assignments stop being a database-only concern.

---

## 6. Running it

```bash
# One-time: generate local secrets into the user-secrets store (never the repo)
./scripts/init-dev-secrets.ps1

# Copy the printed POS_APP_PASSWORD into .env, then:
cp .env.example .env
docker compose up -d
```

```bash
# Build and test without Docker; the PostgreSQL suite self-skips
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