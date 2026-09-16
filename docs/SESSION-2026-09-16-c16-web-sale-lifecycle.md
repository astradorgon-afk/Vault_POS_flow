# Session Log — C16: Web Sale Lifecycle

**Date:** 2026-09-16
**Baseline:** commit `da40420` (`feat(pos-c15): add web checkout orchestration with server-numbered terminals`) — Phase 10 committed, Phase 11 POS C1–C15 complete
**Status at end of session:** solution builds clean (0 warnings, warnings-as-errors on), **915 / 917 tests passing without PostgreSQL** — Domain 369, Application 241, Infrastructure 79, Security 52, Architecture 13, API 161; only the two PostgreSQL-guard members fail and only because Docker is unavailable. `Pos.Web` builds with zero warnings and zero errors.

---

## 1. Goal

Close the browser-side of the sale lifecycle for Phase 11: let a signed-in
operator find past sales, read a sale's detail, print/reprint its receipt,
void it, and drive customer-return follow-through (dispositions and refunds)
— all against the existing API routes, with the register/shift context the
checkout slice introduced in C15, and every action gated by the server-side
permission model.

---

## 2. What was built

### 2.1 Domain (`Pos.Domain`)

| File | Contents |
|---|---|
| `Sales/SalesReturnErrors.cs` | Two new failures for the return read route: `Unknown` (404 `return.unknown`) and `OutsideScope` (403 `return.outside_scope`) |

### 2.2 API (`Pos.Api`)

| File | Contents |
|---|---|
| `Endpoints/SaleEndpoints.cs` | `GET /api/v1/sales?locationId&from&to&cashierId` — `SearchSalesAsync` under `sale.view` with `Scope = ScopeSource.QueryValue`, returning `SaleSummary { id, number, status, businessDate, completedAtUtc, grossTotal, netTotal }`; `POST /api/v1/sales/{id}/reprint` — `ReprintSaleReceiptAsync` under `sale.reprint`, body `{ locationId, deviceId, reason, reprintedAtUtc }`, read-side (no shift needed), appends to the `ReceiptPrints` log |
| `Endpoints/ReturnsEndpoints.cs` | `GET /api/v1/returns/{id}` — `GetReturnDetailAsync` returning `ReturnDetail { id, number, isBlind, saleId, locationId, cashierShiftId, deviceId, customerId, businessDate, returnedAtUtc, refundableTotal, refundedTotal, lines[], refunds[] }`; readable by any of `sale.view | sale.return | sale.refund | inventory.adjust` (`ReturnReadingPermissions`, re-checked against the return's location); 404 via `SalesReturnErrors.Unknown`, 403 via `OutsideScope` |

### 2.3 Web (`Pos.Web`)

| File | Contents |
|---|---|
| `Services/PosContracts.cs` | New read models: `PosSaleSummary`, `PosSaleDetail` (+ `PosSaleLineDetail`, `PosSalePaymentDetail`), `PosReturnDetail` (+ `PosReturnLineDetail` with batch code/expiry, `PosReturnRefundDetail`); new request records: `PosReprintSaleRequest`, `PosVoidSaleRequest`, `PosCreateReturnRequest` (+ `PosCreateReturnLine`, using the API's `PaymentMethod`/`ReturnDispositionKind`/`AdjustmentReasonCode` numeric enum values), `PosRefundReturnRequest` (`SaleId` null → blind), `PosDisposeReturnRequest`; `PosReference` for id-only responses |
| `Services/VaultFlowApiClient.cs` | `GetTextAsync` (plain-text receipt), `SearchSalesAsync(locationId, from?, to?)`, `GetSaleAsync`, `GetSaleReceiptAsync`, `ReprintSaleAsync`, `VoidSaleAsync`, `GetReturnAsync`, `CreateReturnAsync`, `RefundReturnAsync`, `DisposeReturnAsync` |
| `Services/UserSession.cs` | `SetTerminal(PosTerminalSession)` caches `TerminalBusinessDate` and `OpenShift` beside the device identity so pages read register/shift context without re-bootstrapping; `ClearRegister()` wipes stale register context on location mismatch |
| `Components/TerminalBar.razor` (+ `.css`) | Shared register/shift strip: auto-selects the session register or the store's single register, clears a stale register (different location), shows the open-shift banner or the "Start shift" flow, and raises `OnStateChanged`; used on sale detail and return detail |
| `Components/Pages/Sales.razor` (+ `.css`) | `/sales` — store + date-range search gated by `sale.view`; results table (number, status, business date, completed, gross, net); row click navigates to `/sales/{id}` |
| `Components/Pages/SaleDetail.razor` (+ `.css`) | `/sales/{Id:guid}` — lines/payments/totals; receipt print and reprint with reason (`sale.reprint`); void with reason (`sale.void`, register + open shift required); accept-return with per-line quantity (`sale.return`); `TerminalBar` shown for Completed sales when any of reprint/void/return is permitted; navigates to `/returns/{id}` after accept |
| `Components/Pages/ReturnDetail.razor` (+ `.css`) | `/returns/{Id:guid}` — lines with batch/pending-disposition quantities; per-line disposition form (Restock/Quarantine/Damaged/SupplierReturn/Waste with reason-code mapping, `inventory.adjust`, no register/shift needed); refund form (method, amount, tendered, provider reference; cash-locked for blind returns, `sale.refund`); refund history; back-to-sale link |
| `Components/Layout/NavMenu.razor` | "Sales" NavLink gated by `sale.view` |
| `Components/Pages/Home.razor` | Conditional "Review sales" card (POS / 02, `primary-card`-adjacent `secondary-card`) when the user holds `sale.view` |
| `wwwroot/app.css` | `command-grid` reworked to `repeat(2, minmax(0,1fr))`; `primary-card` and `secondary-card` both span the full grid width with distinct light panels; card index colours separated per card kind |

### 2.4 Tests (new)

| Project | Files | Count |
|---|---|---|
| `Pos.Api.IntegrationTests` | `SaleLifecycleEndpointTests.cs` | 9 |

The nine tests run through the real pipeline: sales search by store with the
location/date filters honoured, and another store's Store Manager forbidden;
reprint that logs the print and the audit and keeps the sale Completed,
refused without `sale.reprint`, and refused at another location; return
detail showing lines and the refund history (a referenced return refunded
through the HTTP surface), a blind return's detail describing the return with
no sale, an unknown return 404, and another store's Store Manager forbidden
the detail.

---

## 3. What the razor/build caught

Mostly client-size facts, invisible until the component compiled or ran:

1. **Route parameters need an explicit `[Parameter]`.** Blazor components with
   a route template do not infer the parameter type/name from the route; a
   hand-written `[Parameter] public Guid Id` (with `TypeConverter` hints as
   needed) is required for `/sales/{Id:guid}` and `/returns/{Id:guid}` to bind.
2. **Format items with `;` in interpolated Razor attributes break.** An
   `@saleId:D` inside an HTML attribute tag is parsed as multiple attributes
   before reaching the formatter. Worked around with a computed string
   (`@($"sales/{saleId:D}")`) or by pre-formatting in code.
3. **`DateTimeOffset`/`DateOnly` formatting in interpolations needs an explicit
   culture.** `{expiry:MMM d, yyyy}` rendered with the ambient UI culture;
   pinned with `CultureInfo.InvariantCulture` (`@using System.Globalization`).
4. **`GetValueOrDefault` on nullable strings.** `guidDictionary.GetValueOrDefault(key)`
   returns `null` for a missing key, which the compiler flags; the pages use
   `?? string.Empty` when the result feeds display logic.
5. **Unused private field = CS0414.** A `loading` field left over from an
   earlier draft was removed rather than suppressed.
6. **An earlier CSS polish edit mis-anchored.** The `.term-section`
   margin-bottom rule was inserted against `.sale-head h1` context that did
   not exist exactly as expected; corrected by re-reading the stylesheet and
   editing against the actual content.

---

## 4. Verification

```
dotnet build src/Pos.Web/Pos.Web.csproj        → 0 warnings, 0 errors
dotnet test tests/Pos.Api.IntegrationTests/    → 161 passed, 2 failed (PostgreSQL-guard members;
                                                Docker unavailable), 0 skipped
dotnet test tests/Pos.Domain.Tests/            → 369 passed
dotnet test tests/Pos.Application.Tests/       → 241 passed
```

The two failing API members are the long-standing environment guards
(`PostgresInventoryControlTests.AdjustmentAndCounts_PostAndReport_OnPostgres`
and `PostgresHostSmokeTests.Host_OnPostgres_SignsIn_NumbersDocuments_AndPostsTheLedger`),
which need a Docker daemon; they fail on Testcontainers' regex/Docker probe,
not on production code.

---

## 5. Not done (next session)

1. **Payment-provider methods in the web checkout** — card (`PaymentMethod=2`)
   and e-wallet (`PaymentMethod=3`) panels plus split payments. The backend
   `POST /api/v1/sales` already accepts multiple payments and persists the
   payment mix; only the web checkout is cash-only.
2. **Receipt thermal/PDF layouts** — the plain-text render and the reason-
   logged reprint exist; an 80 mm thermal column layout and a printable
   HTML/PDF view are deferred.
3. **Phase 10 tail** — the FEFO allocation service extraction, the POS
   sale-blocking/`sale.expired_override` path for expired batches, and
   expiring-soon/expired alerts (Phase 14).
4. **POS pricing flow** — the scheduled-price cancellation path (ADR-0029)
   still needs its POS-facing surface.
5. Commit C16 under the batch convention (`feat(pos-c16): ...`) once the
   documentation pass is accepted.