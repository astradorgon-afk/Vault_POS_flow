# Project Status

**Last updated:** 2026-09-12 · **Milestone:** Phase 3 (Master Data) — domain, persistence and use cases landed

**Last updated:** 2026-09-12 · **Milestone:** Phase 2 complete — Identity and Authorization

This is the working status document. [ROADMAP.md](ROADMAP.md) holds the full
item-by-item plan; this file says where things actually stand, what was learned,
and what to pick up next.

---

## 1. Where the build is

| | |
|---|---|
| Solution builds | Clean, warnings-as-errors, analyzers on |
| Tests | **103 passing, 0 failing, 0 skipped** |
| Migrations | 5, forward-only, applied cleanly against PostgreSQL 17 |
| Phases complete | 0 (architecture), 1 (foundation), 2 (identity), 4 (inventory core) |
| Phases remaining | 3, 5–18 — see §5 |

```
Pos.Domain.Tests            41 passing   invariants, money, ledger rules
Pos.Infrastructure.Tests    16 passing   ledger posting + real PostgreSQL triggers
Pos.Architecture.Tests      13 passing   layering, ledger isolation, permission catalogue
Pos.Security.Tests          33 passing   authentication, tokens, permission matrix
```

The PostgreSQL suite needs a Docker daemon. It self-skips without one, so a
developer with no Docker still gets a green local run; CI always has one.

---

## 2. What exists today

### Architecture and documentation
Sixteen documents in `docs/`, ~4,500 lines. The load-bearing ones are
[INVENTORY_LEDGER.md](INVENTORY_LEDGER.md), [OFFLINE_SYNC.md](OFFLINE_SYNC.md),
[SECURITY.md](SECURITY.md) and [PERMISSIONS.md](PERMISSIONS.md). Every
significant choice is recorded in [DECISIONS.md](DECISIONS.md) (23 ADRs).

### Phase 1 — Foundation
Clean Architecture solution, 7 source projects and 7 test projects with
inward-only references enforced by tests. Central package management, pinned
transitive packages carrying advisories. CQRS dispatcher with an ordered
behaviour pipeline. Serilog, correlation middleware, security headers, rate
limiting, RFC 9457 problem details that leak no internal detail. Docker images
for the API and a one-shot migrator, compose stack with Caddy, least-privilege
database roles, CI with a secret scan and a migration-drift check.

### Phase 4 — Inventory ledger (pulled forward)
The double-entry, append-only ledger everything else posts through. Immutable
movements, a balance projection that can only change by applying a movement, and
four independent guards against direct stock assignment: domain types with no
setters, an EF interceptor, PostgreSQL triggers, and a database role with only
`SELECT, INSERT` on the ledger. Movement-type rules table, weighted-average
costing, negative-stock policy, idempotency by event id.

### Phase 2 — Identity and authorization (this session)
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

---

## 4. Known gaps in what is marked complete

Stated plainly so they are not mistaken for finished work:

| Gap | Where | Impact |
|---|---|---|
| No optimistic concurrency token on `InventoryBalance` | Phase 4 | Concurrent posts to one bucket abort at the guard rather than retry. Safe failure, but a retry loop is wanted. |
| `inventory_movement` and `audit_log` not yet partitioned | Phase 4 | Fine at current volume; the maintenance job is designed, not built. |
| Reconciliation worker and `rebuild-balances` command not built | Phase 4 | Drift between ledger and projection would go unnoticed. |
| Serilog sensitive-data scrubbing policy not implemented | Phase 1 | No secret is currently logged, but nothing enforces that. |
| Generic idempotency pipeline behaviour not built | Phase 1 | The ledger is idempotent on its own; the generic behaviour lands with sync in Phase 13. |
| Two-factor is supported but not enforced for admins | Phase 2 | `RequireTwoFactorForAdmins` is configured and read, not yet enforced at sign-in. |
| Permission cache is in-process | Phase 2 | Single API instance is exact. Scaling out needs a Redis backplane; revocation would otherwise lag by the 15-second policy-version window. |
| No user-administration endpoints | Phase 2 | Users, roles and overrides are manageable through the database and the seeder only. |

---

## 5. What to do next

**Phase 3 — Master Data** is the natural next step, because almost everything
downstream needs products and locations to exist.

1. **Organization and locations.** Main Warehouse, stores, and the three virtual
   `External` locations the ledger already depends on (`EXT-SUPPLIER`,
   `EXT-CUSTOMER`, `EXT-WRITEOFF`). Location settings carry the negative-stock
   policy, which `StrictLedgerPolicyProvider` currently hard-codes to the
   strictest option — replacing that provider is the first real wiring job.
2. **Suppliers, categories, brands, units of measure**, with unit conversions.
3. **Products and barcodes.** The centralized Product Master rule is already
   enforced in the permission catalogue; this builds the aggregate behind it.
   Barcode uniqueness is global and deliberate.
4. **Effective-dated pricing** with the overlap-exclusion constraint described in
   [DATABASE.md](DATABASE.md).
5. **`ProductLocationSetting`** — min, reorder, target, max, preferred quantity.
6. **Development seed data**: the Main Warehouse, three stores, a supplier, a few
   categories and products, and the staff accounts described in the brief.

Two smaller items worth doing alongside, because later phases assume them:

Two smaller items worth doing alongside, because later phases assume them:

- Wire `ILedgerPolicyProvider` to real location settings.
- Add user-administration endpoints so roles and overrides stop being a
  database-only concern.

### Phase 3 progress (this session)

- **Domain:** `Location` (with JSON `LocationSettings`), `Organization`,
  `Product` aggregate with `ProductBarcode`, `ProductPrice`, `ProductUnitConversion`,
  `ProductLocationSetting`, `ProductSupplier` children, plus `ProductCategory`,
  `Brand`, `UnitOfMeasure`, `Supplier` master data. New strongly-typed ids for
  all child rows.
- **Application:** `CreateLocationCommand`, and create commands with FluentValidation
  validators for category, brand, unit of measure, supplier and product
  (`product.create` permission enforced centrally, per the product-master rule).
- **Infrastructure:** `MasterDataRepository` implements `IMasterDataRepository`;
  EF configurations for all tables including the price overlap exclusion
  constraint (btree_gist) and product-name trigram index; migration
  `20260912074419_MasterDataCatalog` created, drift-checked against a live
  PostgreSQL instance.
- Remaining Phase 3 work: API endpoints exposing the new commands, the
  development seed data (Main Warehouse, stores, staff accounts), and wiring
  `ILedgerPolicyProvider` to real location settings.

- Wire `ILedgerPolicyProvider` to real location settings.
- Add user-administration endpoints so roles and overrides stop being a
  database-only concern.

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
