# Session Log — POS sale/payment restyle, web receipts and a demo-ready seed

**Date:** 2026-09-19
**Baseline:** commit `0a54a77` (`feat(offline-c64): the desktop register sets itself up, signs in and loads its store`)
**Status at end of session:** `Pos.Client`, `Pos.Web` and `Pos.Api` all build with zero warnings (warnings-as-errors on) and zero errors. The API runs with migrations and the development seed applied, serves `/health/live` and `/api/v1/meta` over plain HTTP with no HTTPS redirect, and `scripts/dev-desktop.ps1` reuses it and starts the Web UI. No test projects were touched this session.

---

## 1. Goal

Three asks, in order:

1. **Restyle the register sale and payment screens** (`Pos.Client` `SalePanel.razor`)
   to match three reference images (`Downloads\PosSale@1x.png`,
   `PosPayment@1x.png`, `Receipts@1x.png`), keeping the existing dark theme.
2. **Add a web Receipts page** (`Pos.Web`) mirroring the receipts reference.
3. **Make the demo runnable end-to-end**: a register sale hit
   `inventory.insufficient_stock` (`Available 0, requested 3.`) because the
   development seed created no stock and no prices, and the demo accounts were
   the awkward `cashier1`/`DevVaultFlow!2026`. Seed real stock, prices and
   simple memorable staff accounts.

---

## 2. What was built

### 2.1 Register sale/payment restyle (`Pos.Client`) — layout only, theme preserved

| File | Contents |
|---|---|
| `Services/RegisterService.cs` | `GetStoreIdentityAsync()`: the signed-in store's name, physical code and the register's till tag (`W01`). |
| `Components/Panels/SalePanel.razor` | Account header shows the store name and a till-tag chip; user/role line simplified to match the reference; category chips (`All`, `GROCERIES`, `DAIRY`, …) with counts; single-payment keypad flow (keypad → amounts → complete on one surface) instead of a separate payment screen; fixed `@onclick="() => Keypad("0")"` quote-breaking markup via a dedicated `KeypadZero()` handler. |
| `wwwroot/app.css` | Dark register chrome (`--panel #172126`, `--signal #e3a84f`, `--cyan #86c5c1`, …): header strip, till-tag chip, chip bar, keypad grid and the sale summary all restyled. |

### 2.2 Web Receipts page (`Pos.Web`)

| File | Contents |
|---|---|
| `Services/PosContracts.cs` | `PosReceiptSummary`, `PosIssueReceiptRequest`, `PosNewReceipt` wire records. |
| `Services/VaultFlowApiClient.cs` | `GetReceiptsAsync(kind, storeCode, limit)` (server clamps `limit` to 200), `IssueReceiptAsync`, `GetReceiptPrintTextAsync` (`text/plain`). |
| `Components/Pages/Receipts.razor` + `.razor.css` | `/receipts`: money-in/out cards, kind tabs with counts, store picker, table, issue form gated by `receipt.create`, print preview with copy-to-clipboard. Forest/paper web theme. |
| `Components/Layout/NavMenu.razor` | **Receipts** link; placeholder (disabled) Operations/Administration items. |

Wire contract facts confirmed while building (serialization details matter):

- `Receipt.Summary` returns `Kind` as an enum **string** (`WalkInSale`,
  `BranchExpense`, `OwnerWithdrawal`) — the client switch is on strings.
- `GET /api/v1/receipts/print/{id}` returns `text/plain`; issuing returns `{ id }`.

### 2.3 Demo seed overhaul (`src/Pos.Infrastructure/Identity/DevelopmentDataSeeder.cs`)

The seeder is the single source of truth for a runnable development database.
It stays governed by `Database:SeedDevelopmentData` +
`Seeding:EnableDevelopmentAccounts`, stays idempotent, and now also writes
prices and opening stock.

- **Accounts** (all share one password, **`cash1234`** — dev only, easy to type
  on a register):

  ```
  owner  admin  main.manager  inv.staff  auditor
  s1.manager  s2.manager  s3.manager        (store managers, tier 1)
  s1.cashier  s2.cashier  s3.cashier        (cashier per store)
  ```

- **Password rotation.** The seeder now re-asserts the shared password on every
  run: an existing dev account that does not currently use it is rotated onto
  it (via `RemovePasswordAsync` + `AddPasswordAsync`, because the
  `AddIdentityCore` setup registers **no identity token providers**, so
  `GeneratePasswordResetTokenAsync` throws). Old demo accounts
  (`cashier1`, `main.mgr`, …) keep existing but end up on the same password.
- **Catalogue** expanded from 6 to **9 products** (adds `WATER-500`,
  `SODA-1L`, `DETER-400` across Beverages/Household), each with a selling
  price — the register's "No price" state is gone.
- **Opening stock.** New `SeedStockAsync` posts one opening-balance ledger
  group per (product × location) — MAIN warehouse plus STORE01/02/03 —
  through `IInventoryLedger.PostAsync` (the only stock writer), an External
  supplier `-qty` leg paired with the target location's `+qty` Available leg,
  valued at acquisition cost. **36 stock events** on first run. Devices never
  receive stock rows (the change feed carries master data only); availability
  is enforced by the head-office ledger on sale sync, so every store starts
  sellable.

### 2.4 Development-launch fixes (why the register said "Head office could not be reached")

- `src/Pos.Api/Program.cs`: `/health*` is now **exempt from
  `UseHttpsRedirection`** (`UseWhen` + `Path.StartsWithSegments`) so plain-HTTP
  liveness probes answer 200 instead of a 307 bounce.
- Root cause of the register failure was the **launch profile**, not the code:
  running the API with the `https` profile binds `https://localhost:7256`,
  which turns on the redirect for *all* plain-HTTP requests. The register's
  `http://localhost:5177` calls then 307 → `https://localhost:7256`, where the
  untrusted dev cert kills the connection (`HeadOfficeClient` → *"Head office
  could not be reached at {server}"*). Local development therefore runs the
  API on the **`http` profile only** (no HTTPS endpoint → the redirect passes
  through), which is exactly what `scripts/dev-desktop.ps1` does. This is now
  documented in `docs/LOCAL_TESTING.md`.

---

## 3. What the build and the running system caught

1. **Ledger change-tracker collision in the seeder.** An opening balance is a
   two-leg group whose External-supplier leg re-touches the *same*
   `(ExternalSupplier, product, External)` bucket on every store call. Under
   the seeder's ambient transaction nothing was saved between groups, so the
   second call tried to stage a second `InventoryBalance` with an identical key
   — `"another instance with the same key value ... is already being tracked"`.
   Fix: `SaveChangesAsync` + `ChangeTracker.Clear()` after each posted group,
   still inside the single seed transaction.
2. **`Security:MinimumPasswordLength` is hard-clamped `[8, 128]`** — setting 6
   for `cash123` failed options validation at startup. Dev sets 8 and the
   password is `cash1234`; production stays 12.
3. **No identity token providers.** `AddIdentityCore` registers none, so
   `GeneratePasswordResetTokenAsync` (used by the first password-rotation
   draft) throws `NotSupportedException: No IUserTwoFactorTokenProvider named
   'Default'`. `RemovePasswordAsync` + `AddPasswordAsync` need no token and
   also rotate the security stamp.
4. **`EventId` ambiguity** in the seeder (`Pos.Domain.Common.EventId` vs
   `Microsoft.Extensions.Logging.EventId`) — fully-qualified
   `Pos.Domain.Common.EventId.New()`.
5. **HTTPS-redirect breakage discovered through `dev-desktop.ps1`**: PowerShell
   `Test-ApiLive` follows the 307 to the HTTPS port and fails on the untrusted
   cert, so the script believed the API dead, started a second instance on the
   taken port, and that instance exited — *"The API exited with code before it
   became healthy."* The `/health` exemption + running the `http` profile
   resolves both the script probe and the register.

---

## 4. Verification

```
dotnet build src/Pos.Infrastructure/   → 0 warnings, 0 errors
dotnet build src/Pos.Api/Pos.Api.csproj     → 0 warnings, 0 errors (http profile
                                             launch: seed runs, then listens)
dotnet build src/Pos.Web/Pos.Web.csproj     → 0 warnings, 0 errors
dotnet build src/Pos.Client/Pos.Client.csproj → 0 warnings, 0 errors
```

Seed logs on the existing development database:

```
Development seed complete: 0 locations, 3 products, 3 prices, 36 stock events,
                          7 accounts.          (first run after the change)
(second run: rotations only)  Rotated the password for development account … (×11)
```

Runtime probes:

```
http://localhost:5177/health/live → 200, no redirect
http://localhost:5177/api/v1/meta → 200, no redirect
scripts/dev-desktop.ps1           → "Reusing the API already running on
                                    http://localhost:5177", then starts the
                                    Web UI on http://localhost:5215
```

---

## 5. Not done / watch-outs

1. **Fresh DB gives the clean account set.** An existing database keeps the
   old demo accounts (`cashier1`, `cashier2`, `main.mgr`, `store1-3.mgr`) —
   they are now rotated onto `cash1234` but still present. Drop `vaultflow`
   once for a purely new-style account list.
2. **Account seeding vs. `BootstrapOwnerSeeder`.** The bootstrap owner runs
   only on an empty database; the seed accounts are created by the development
   seeder. Both use the Development password policy (8) — production keeps 12.
3. **Stock lives at head office only.** The register shows prices/catalogue
   from the baseline download; sellable quantities are validated by the
   ledger at sync time. Nothing in this session pushes balances to devices.
4. **No test-suite changes.** All changes are UI, seeder and launch-path;
   the previous full-solution test baseline is unaffected.
5. **Commit pending** under the batch convention once this documentation pass
   is accepted.