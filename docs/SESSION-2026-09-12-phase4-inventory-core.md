# Session Log — Phase 4: Inventory Core (hardening)

**Date:** 2026-09-12
**Baseline:** commit `5a548ef` (`feat(master-data): Phase 3 ...`) — Phases 0, 1, 2, 3 complete
**Status at end of session:** solution builds clean (0 warnings, warnings-as-errors on), **144 / 144 tests passing** (41 domain, 29 infrastructure, 13 architecture, 33 security, 12 application, 16 API integration), PostgreSQL concurrency suite green against a `postgres:17-alpine` container.

---

## 1. Goal

Finish the deferred Phase 4 rows from `docs/ROADMAP.md`: close the
optimistic-concurrency gap on the balance projection (lost-write window between
two racing postings), land the reconciliation worker and `rebuild-balances`
command, and prove contention behaviour with real concurrency tests against
both SQLite and PostgreSQL.

---

## 2. What was built

### 2.1 Domain (`Pos.Domain`)

| File | Contents |
|---|---|
| `Inventory/InventoryBalance.cs` | `Version` optimistic-concurrency token: `long`, starts at 1 in `CreateEmpty`, bumped on every `Apply` |
| `Common/ConcurrencyConflictException.cs` | Marks a lost projection write as a single, catchable failure for the ledger retry |

### 2.2 Application (`Pos.Application`)

| File | Contents |
|---|---|
| `Inventory/ReconciliationContracts.cs` | `IBalanceReconciler` (detect/rebuild), `ReconciliationReport`, `BalanceDiscrepancy`, `BucketReference`, `BalanceDiscrepancyKind` (Missing, Quantity, AverageUnitCost, TotalValue, LastMovement, Unexpected) |

### 2.3 Infrastructure (`Pos.Infrastructure`)

| File | Contents |
|---|---|
| `Inventory/InventoryLedger.cs` | Contention retry in `PostAsync`: `DbUpdateConcurrencyException` (or SQLite error 19 / Npgsql 23505 wrapped in it) on the **projection step only** → re-read the bucket, re-apply deltas, exponential backoff (`MaxStandaloneAttempts` from `ReconciliationOptions`-adjacent `LedgerOptions`, default 10). The ledger append is never re-attempted — `event_id` idempotency makes inserts a no-op but re-reading the movements is still waste. Idempotent-projection adjustments (corrections) sit in the same loop. Exhaustion → `inventory.balance_contention` (`Error.ConcurrencyConflict` → HTTP 412) |
| `Inventory/BalanceReconciler.cs` | Detect: replay ledger → fresh projection → compare. Rebuild: force `OriginalValue` of the version token, `DISABLE` deferred guard trigger → `DELETE` → `ENABLE` (before inserts, so no pending events) → `AddRange` → commit where the still-armed guard validates; `ChangeTracker.Clear()` first so a long-lived context has no identity-map collision |
| `Inventory/BalanceReconcilerWorker.cs` | Optional read-only tripwire `BackgroundService` (`Reconciliation:Enabled`): detect pass on startup + `PeriodicTimer` interval; logs clean / drift / failure. Never writes — repair is a deliberate, separately authorized rebuild |
| `Configuration/Options.cs` | `ReconciliationOptions` (Enabled, RunOnStartup, Interval), `MaintenanceOptions` (AllowBalanceRebuild), `LedgerOptions` (MaxStandaloneAttempts) — all bound, `ValidateOnStart` |
| `DependencyInjection.cs` | `TryAddScoped<IBalanceReconciler, BalanceReconciler>()`, hosted worker when enabled, options binding |
| `Persistence/Configurations/InventoryBalanceConfiguration.cs` | `Version` as `IsConcurrencyToken().IsRequired()` |
| `Persistence/Migrations/Postgres/20260912122753_BalanceConcurrencyToken` | Add `version bigint NOT NULL DEFAULT 1` to `inventory_balance` (migration #7) |

### 2.4 API (`Pos.Api`)

| File | Contents |
|---|---|
| `Endpoints/InventoryEndpoints.cs` | `POST /api/v1/inventory/reconcile` (read-only), `POST /api/v1/inventory/rebuild-balances` (maintenance-gated); both require `inventory.rebuild_balances`, no scope. Rebuild returns 503 `maintenance.disabled` when `Maintenance:AllowBalanceRebuild` is false |
| `Program.cs` | `app.MapInventoryEndpoints()` |

### 2.5 Tests (new)

| Project | Files | Count |
|---|---|---|
| `Pos.Infrastructure.Tests` | `Inventory/BalanceConcurrencyTests.cs` (SQLite), `PostgresBalanceConcurrencyTests.cs` (Docker), `BalanceReconcilerTests.cs` (SQLite) | 10 |
| `Pos.Application.Tests` | `Common/` CQRS unit-of-work behaviour tests | 4 |
| `Pos.Api.IntegrationTests` | `InventoryEndpointTests.cs` | 4 |

---

## 3. The race, and why the guard could not catch it

Two writers both read `Quantity = 100`, both apply +10. The second commits a
projection that never saw the first's movement. The ADR-0016 deferred guard
cannot arbitrate: each writer's transaction is internally consistent; the second
writer's check ran against its own (stale) read. The guard catches bare
`UPDATE`s — the window names no movements, the expected delta is zero — but two
legitimate postings racing on one bucket were a silent lost write.

The `Version` token converts that into `DbUpdateConcurrencyException` on
`SaveChanges`, and the ledger retry re-applies the deltas. Losing a movement was
the failure mode; failing loudly and retrying the pure projection step is the
fix.

---

## 4. Bugs the tests caught

1. **Stale test design vs. SQLite snapshot upgrade.** Holding a read transaction
   across another writer's commit and then writing gets `SQLITE_BUSY` (deferred
   transactions cannot upgrade once a peer committed; no busy timeout helps).
   The stale-write test pins the token's `OriginalValue` to the version the
   stale reader actually saw and stages the write in a fresh transaction —
   exactly what production does on a real stale write.
2. **`SqliteParameter` + `ExecuteSqlRawAsync` → "Value must be set".**
   `SqliteParameter` binds failed before the statement ran. Reconciler raw
   statements use `FormattableString.Invariant` inline SQL (also keeps the
   SQLite/PostgreSQL branches parallel).
3. **Microsoft.Data.Sqlite stores GUIDs uppercase-"D".** `guid.ToString()`
   lowercase matched zero rows — a healthy database looked fully drifted.
   `SqliteGuid(g)` = `.ToString("D").ToUpperInvariant()`.
4. **PG 55006 on rebuild.** Re-enabling the deferred trigger inside a
   transaction that had deleted rows panicked at `SET CONSTRAINTS`. Fixed order:
   `DISABLE → DELETE → ENABLE → INSERT → commit` — nothing pending at ENABLE,
   and the armed guard still validates the rebuilt rows at commit.
5. **EF identity-map collision on rebuild.** Replayed bucket tracked by one
   context, re-added via the reconciler's own context, collided. `ChangeTracker
   .Clear()` before the delete phase.
6. **Contention at 5 writers with `MaxStandaloneAttempts = 3`.** Raised to 10
   (worst case ≈ concurrent writers; backoff keeps fan-in bounded).
7. **Disposing a `WithWebHostBuilder` derived factory kills the shared host.**
   The switch-on endpoint test tore down the collection fixture's RSA → later
   tests 500/401. The test now owns an independent `PosApiFactory` with
   `Maintenance__AllowBalanceRebuild=true`; the factory restores displaced
   environment variables on dispose and exposes only one public constructor so
   xUnit still treats it as a fixture.

---

## 5. Verification

```
dotnet build VaultFlow.slnx                       → 0 warnings, 0 errors
dotnet test VaultFlow.slnx                        → 144 passed, 0 failed, 0 skipped
  Pos.Domain.Tests                               41 passing
  Pos.Infrastructure.Tests                       29 passing  (incl. 10 concurrency/reconciler;
                                                                PostgreSQL suite ran against Docker)
  Pos.Architecture.Tests                         13 passing
  Pos.Security.Tests                             33 passing
  Pos.Application.Tests                          12 passing  (+4 CQRS unit-of-work behaviour tests)
  Pos.Api.IntegrationTests                       16 passing  (incl. 4 inventory endpoint tests;
                                                                shared "api" collection intact)
```

---

## 6. Not done (next session)

1. **Table partitioning** by month on `inventory_movement` (and `audit_log`) —
   maintenance job designed, not built; fine at current volume.
2. **`NegativeStockAttempt` record + exception report** — the guard blocks today;
   the diagnostic record for the owner dashboard awaits.
3. **Phase 5 — Purchasing** now has everything it needs (suppliers, locations,
   products, approval tiers) and is the natural next phase.