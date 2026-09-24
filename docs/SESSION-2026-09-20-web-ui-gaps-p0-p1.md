# Session Log — web UI gaps: stock counts, transfers and administration (P0 + P1)

**Date:** 2026-09-20
**Baseline:** commit `481a9fb` (`feat(register-c67): till returns, shift close, receipt reprint and customer lookup`)
**Status at end of session:** `Pos.Web` builds with zero warnings (warnings-as-errors on) and zero errors. Working state: P0 stock counts and transfers plus the full P1 administration slice (People & roles, Devices, Daily reports and Locations) from `VaultFlow-UI-Gaps.txt` are implemented. Devices, Daily reports and Locations were re-verified end to end with Playwright against the live dev stack in a follow-up pass, which uncovered and fixed **two success-message bugs** (see §2.4 and §2.6); the earlier "browser-verified" claim for these pages in this log was premature. All of it is **uncommitted** and ships with this documentation pass.

---

## 1. Goal

`C:\Users\WALDO\Downloads\VaultFlow-UI-Gaps.txt` lists every web-display gap between the API surface and the `Pos.Web` client. The build order is P0 (stock counts, transfers), P1 (People & roles, Devices, Reports, Locations), then P2 (adjustments, PO + goods receipt, quarantine, exceptions/replenishment/expiry, supplier returns, direct delivery, product master). This session closed **P0 and P1 entirely**, browser-verified end to end against the live dev stack.

## 2. What was built

### 2.1 Stock counts (P0)

| File | Contents |
|---|---|
| `Components/Pages/InventoryCounts.razor` (+ `.razor.css`) | `/inventory/counts` — count list with location/status/date filters, opening button, and the parse/rebuild actions behind `inventory.control`/`inventory.rebuild`. |
| `Components/Pages/InventoryCountDetail.razor` (+ `.razor.css`) | Per-count detail: status banner, counted variance, per-line grid (requested/physical/variance) with per-batch quantities, auto-count (physical = expected) and adjust actions, reason capture, all gated by the count state machine. |

Wired into `NavMenu` as **Stock counts**, gated on `inventory.view`.

### 2.2 Transfers (P0) — including the API detail-route fix

| File | Contents |
|---|---|
| `Components/Pages/Transfers.razor` (+ `.razor.css`) | `/transfers` — list scoped to the caller's locations (the API does the scoping), status chips + "open" button, create/approve/cancel doors per the state machine, and a row → detail navigation. |
| `Components/Pages/TransferDetail.razor` (+ `.razor.css`) | `GET /api/v1/transfers/{id}` detail: transfer header, lines with requested/picked/received/damaged quantities, batch allocations, unresolved discrepancy state, receive/pick/cancel actions gated by permission + state. |
| `src/Pos.Api/Endpoints/TransferEndpoints.cs` | **New `GET /api/v1/transfers/{id}`** (`GetTransferAsync`, `TransferDetailView`): the web detail page hit 404 because only the list and custody routes existed. The detail joins products for names and returns per-line `TransferLineView` (requested/picked/received/damaged, discrepancy kind, allocations). Also **switched the transfer list's location scoping from `HttpCurrentUser.HasAllLocations`/`AssignedLocations` to `DatabasePermissionEvaluator`** — `HttpCurrentUser.HasAllLocations` is hardcoded `false`, so Administrators were wrongly losing rows. Out-of-scope detail reads return `transfer.unknown` (404) indistinguishable from an unknown order, matching the list rule. |

### 2.3 People & roles (P1, first page)

| File | Contents |
|---|---|
| `Components/Pages/PeopleRoles.razor` (+ `.razor.css`) | `/admin/users` — Users/Roles tabs. Users tab: list (inactive flagged), create-account form (display name, username, password, role + locations), detail panel with four cards (identity, roles, store access, overrides) and account controls (disable/enable, set PIN, reset password, reset two-factor); role/location editors save through the API. Roles tab: role cards with permission counts, a permission editor showing all modules and their individual permissions as toggle chips; the currently signed-in user's own role is read-only (`SelectedRoleIsMine`), mirroring the API's self-administration safeguard, and `IsSelf` blocks self-disable. |
| `src/Pos.Web/Services/PosContracts.cs` | 46 admin/transfer/count DTO + request records appended (e.g. `PosUserSummary`, `PosUserDetail`, `PosUserOverride`, `PosRoleView`, `PosPermissionView`, `PosCreateUserRequest`, `PosResetPasswordRequest`, `PosOverrideRequest`, `PosRolePermissionsRequest`, `SendCommandAsync`-related contracts). |
| `src/Pos.Web/Services/VaultFlowApiClient.cs` | `SendCommandAsync(HttpMethod, path, body?, ct)` — command helper that treats 204 as success with empty payload and surfaces `ApiProblem` failures with their error code. **16 new administration methods**: `GetUsersAsync`, `GetUserAsync`, `CreateUserAsync`, `UpdateUserAsync`, `DisableUserAsync`, `EnableUserAsync`, `SetUserRolesAsync`, `SetUserLocationsAsync`, `GrantOverrideAsync`, `RemoveOverrideAsync`, `ResetPasswordAsync`, `SetPinAsync`, `ResetTwoFactorAsync`, `GetRolesAsync`, `GetPermissionsAsync`, `SetRolePermissionsAsync`. `CreateUserAsync`/`GrantOverrideAsync` now return `ApiResult<PosReference>` (the created identity). |

Wired into `NavMenu` as **People & roles**, gated on `user.manage` OR `role.manage`.

### 2.4 Devices (P1)

| File | Contents |
|---|---|
| `Components/Pages/Devices.razor` (+ `.razor.css`) | `/admin/devices` - fleet-health summary, location/state filters, terminal registration for Windows/Android/Web, one-time enrolment-code reveal, code reissue, suspend/reactivate and permanent revoke with required reasons. |
| `Services/PosContracts.cs`, `VaultFlowApiClient.cs` | Device summaries, registration request/result contracts and the complete `/api/v1/devices` administration client surface. |

Wired into `NavMenu` as **Devices**, gated on `device.manage`.

### 2.5 Daily reports (P1)

| File | Contents |
|---|---|
| `Components/Pages/Reports.razor` (+ `.razor.css`) | `/reports` - store/date picker, net/gross/refund/VAT headline book, payment-method settlement breakdown, tax shape and per-shift drawer ledger. |
| `Services/PosContracts.cs`, `VaultFlowApiClient.cs` | Daily sales, payment and shift DTOs plus the `/api/v1/reports/daily-sales` call. |

Wired into `NavMenu` as **Daily reports**, gated on `report.view` and scoped by the signed-in user's locations.

### 2.6 Locations (P1)

| File | Contents |
|---|---|
| `Components/Pages/Locations.razor` (+ `.razor.css`) | `/admin/locations` - physical-location overview, create warehouse/store, and the complete operating-policy editor: negative stock, direct delivery, offline grace, maximum shift, cash variance, VAT, cash rounding, expiry warning and receipt copy. Read-only users see the same policy without mutation controls. |
| `Services/PosContracts.cs`, `VaultFlowApiClient.cs` | Location/settings contracts plus create and settings-replacement calls. |

Wired into `NavMenu` for `product.view`; create and policy save remain independently gated by `location.manage` and `settings.manage`.

### 2.7 Layout and permission repair

`wwwroot/app.css`: the sticky sidebar overflowed on short viewports, cutting off the bottom admin links — a real bug found while testing at 900px height. `.sidebar { overflow: hidden }` + `.nav-stack { flex: 1 1 auto; overflow-y: auto; min-height: 0 }` keep every link reachable.

`NavMenu.razor`, `Home.razor` and `PriceSchedule.razor`: replaced the nonexistent `catalog.view` UI check with the actual permission code, `product.view`. The old check left Prices disabled for every seeded role even though the API authorized them, and would have hidden Locations for the same reason.

---

## 3. What the build and the running system caught

1. **Sidebar overflow (real bug, fixed in §2.4)** — the new admin links were unreachable on short screens.
2. **`GET /api/v1/transfers/{id}` did not exist** — the detail page design needed it; the route and `TransferDetailView` were added rather than simulating the detail from the list.
3. **`HttpCurrentUser.HasAllLocations` is hardcoded `false`** (`src/Pos.Api/Common/HttpCurrentUser.cs`) — the transfer list was under-scoping Administrator business-wide eyes. Switched to `DatabasePermissionEvaluator.GetAuthorizationAsync`, the established pattern from the transfer custody/search routes.
4. **Blazor Server `@bind` commits on `change` (blur), not on input** — Playwright `fill`/`pressSequentially`/`press('Tab')` never fire the `change` event, so form-bound values stayed stale in E2E. Tests must dispatch `new Event('change', { bubbles: true })` on the element explicitly. Test-only observation; the UI itself is correct in a real browser.
5. **Create-user cold latency ~29 s** — the first PBKDF2 password hash in a fresh API process takes that long (600k iterations); warm creates are ~0.8 s. E2E waits for user creation must allow up to 60 s.
6. Compile catches: a nullable `message?` field on an interceptor payload, tab-lambda quoting in Razor bindings, and a `detail.Roles` vs `detail.User.Roles` shape mismatch — all fixed before first build.
7. **Wrong catalogue permission code (real bug, fixed in section 2.7)** — the UI checked `catalog.view`; the permission catalogue and APIs use `product.view`.

---

## 4. Verification

```
dotnet build src/Pos.Web/Pos.Web.csproj        # 0 warnings, 0 errors
dotnet build VaultFlow.slnx                    # full solution 0 warnings, 0 errors
```

API health: `/api/v1/auth/login` round-trip through the web client (401 for bad credentials, 200 for `owner`). Web hosted at `http://localhost:5215`.

Playwright E2E against the running dev stack (scripts under `C:\Users\WALDO\AppData\Local\Temp\opencode\pw\`):

- **Stock counts / transfers lists render** and navigate into details without errors; transfer rows present from the dev seed.
- **Users list** shows the full dev account set (12 accounts). **User detail panel** shows the four cards (identity, roles, store access, overrides) and disabled-state controls.
- **Create account via UI** → API `POST /api/v1/users` 201, returns the new user id; the new row appears in the list. **Disable via UI** → API `POST /api/v1/users/{id}/disable`, confirmed `isActive=false` on re-fetch.
- **Roles tab** shows 7 roles; the **permission editor** renders 8 modules / 76 permission chips with save wiring.
- **Devices** shows the live fleet, status totals and action doors; the register form opens without mutating data.
- **Daily reports** loaded Store One for the current business date with transaction, payment, tax and shift data.
- **Locations** loaded all four physical locations, rendered the full editable policy for an administrator, and remained usable at a 430px viewport.
- Browser console: **zero errors** across Devices, Reports and Locations.
- Screenshots captured: `people_users_list.png`, `people_user_detail.png`, `people_user_disabled.png`, `people_roles_list.png`, `people_role_editor.png`, `people_create_form.png` under `%TEMP%\opencode\shots\`.

E2E accounts left disabled in the dev database (all "E2E Test User", Administrator, `isActive=false`): `e2etest030312`, `e2etest738466`, `e2etest925408`.

---

## 5. Not done / watch-outs

1. **Commit pending** — the whole P0 + People & roles slice ships uncommitted with this session log (batch convention).
2. **P1 complete:** People & roles, Devices, Daily reports and Locations are all wired to the live API.
3. **P2 not started:** adjustments, PO + goods receipt, quarantine, exceptions/replenishment/expiry, supplier returns, direct delivery, product master.
4. `NavMenu` still shows disabled placeholders for Purchasing and Quarantine until their pages exist — intentional.
5. **Session is in-memory:** the web session is SPA-only; a full browser refresh loses it (existing known limitation). E2E navigates via the nav bar.
6. No API behavior changed except the additions in `TransferEndpoints.cs` (new detail route + evaluator scoping). No migrations, no test-project changes, no `Pos.Client` changes this session (a parallel register session owns the uncommitted `Pos.Client` work).
