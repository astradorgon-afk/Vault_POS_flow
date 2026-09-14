# VaultFlow

Multi-location point-of-sale and inventory management for a business running one
Main Warehouse and several stores, with offline-capable POS devices on Windows
and Android.

This is not a CRUD application. Inventory integrity, authorization,
auditability, offline synchronization and transaction consistency are treated as
architecture, not as features.

## The one rule everything else follows

> **No code path outside the inventory ledger may set a stock quantity.**

There is no `product.StockQuantity = 100`. Stock changes only as the arithmetic
consequence of appending immutable, double-entry `InventoryMovement` rows, and
that is enforced in four independent layers:

| Layer | What it stops |
|---|---|
| Domain types with no setters, constructed only by a validating factory | application code writing a movement by hand |
| An EF Core `SaveChanges` interceptor | anything that reaches the change tracker anyway |
| PostgreSQL triggers on the movement and balance tables | raw SQL, migrations, a console |
| A database role granted only `SELECT, INSERT` on those tables | a compromised application, or SQL injection |

A supplier receipt, a transfer, a sale and a stock adjustment are all the same
shape: a set of signed legs that sum to zero. Suppliers, customers and write-offs
are modelled as virtual locations, so the entire ledger satisfies
`SUM(quantity_delta) = 0` and any drift is a single query away.

## Documentation

Read [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) first; it indexes everything
else. The load-bearing documents are
[`INVENTORY_LEDGER.md`](docs/INVENTORY_LEDGER.md),
[`OFFLINE_SYNC.md`](docs/OFFLINE_SYNC.md),
[`SECURITY.md`](docs/SECURITY.md) and
[`PERMISSIONS.md`](docs/PERMISSIONS.md).
Progress is tracked in [`ROADMAP.md`](docs/ROADMAP.md), and every significant
choice is recorded in [`DECISIONS.md`](docs/DECISIONS.md).

## Layout

```
src/
  Pos.Domain/          entities, value objects, invariants     (no dependencies)
  Pos.Shared/          wire contracts                          (no dependencies)
  Pos.Application/     use cases, ports, validators, pipeline
  Pos.Infrastructure/  EF Core, the ledger, persistence guards
  Pos.Api/             HTTP surface, composition root
  Pos.Web/             Blazor owner/admin dashboard
  Pos.SharedUI/        Razor components shared by Web and the device client
tests/
  Pos.Domain.Tests/  Pos.Application.Tests/  Pos.Infrastructure.Tests/
  Pos.Api.IntegrationTests/  Pos.Sync.Tests/  Pos.Security.Tests/
  Pos.Architecture.Tests/
```

`Pos.Client` (.NET MAUI Blazor Hybrid) is created in Phase 12 with the rest of
the offline device work — see ADR-0019.

## Getting started

Requires the .NET 10 SDK (pinned in `global.json`) and Docker.

```bash
cp .env.example .env    # then replace every value
docker compose up -d    # postgres, one-shot migrator, api, reverse proxy
```

Build and test without Docker — the PostgreSQL trigger tests skip themselves
when no daemon is reachable:

```bash
dotnet test VaultFlow.slnx
```

Generate a migration after changing the model:

```bash
dotnet ef migrations add <Name> --project src/Pos.Infrastructure --startup-project src/Pos.Infrastructure --output-dir Persistence/Migrations/Postgres
```

## Conventions worth knowing before you commit

- Money is `decimal`, always. A `double` on a domain type fails an architecture test.
- Timestamps are stored in UTC; business dates are computed in the location's
  timezone (`Asia/Manila` by default) and stored separately.
- Financial and inventory records are append-only. Corrections are reversals.
- Warnings are errors, and a package with a published advisory fails the build.
- No secrets in the repository; `scripts/check-secrets.ps1` runs in CI.
# Vault_POS_flow
