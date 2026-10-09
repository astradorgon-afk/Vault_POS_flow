# PWA Offline Risks and Mitigation Tasks

Actionable mitigation plan for the offline risks of the web PWA (`src/Pos.Web.Client`,
served under `/offline/` by `src/Pos.Web`). Scope is the **PWA only**; the MAUI
clients (`src/Pos.Client`) are out of scope per `AGENTS.md`.

Verified against the code on 2026-10-09. Key sources:

- `src/Pos.Web.Client/wwwroot/js/offline-store.js` — encrypted IndexedDB store, outbox, sync
- `src/Pos.Web.Client/wwwroot/js/pwa-boot.js` — startup and cache readiness
- `src/Pos.Web.Client/Pages/Home.razor` — workspace UI, readiness panel, sync page
- `src/Pos.Web/Pwa/PwaGateway.cs` — server gateway, sessions, push/pull
- `docs/OFFLINE_SYNC.md` — offline-first design

Core invariant (from `AGENTS.md`): an offline edit is **Saved on device** until the
server accepts it. Everything below is about protecting that promise and the data
behind it.

## When the installed app stops at “The app could not finish loading”

This screen is shown by `pwa-boot.js` when the browser cannot start the Blazor
runtime. It does **not** prove that the IndexedDB outbox survived. Clearing
VaultFlow site data can remove the app cache, employee profiles, downloaded
snapshot, working forms, and unconfirmed outbox together. An installed app icon
does not keep an independent copy. If the browser is offline after a full site-data
wipe, the app cannot repair itself until it can download its files again.

Recovery on the affected device:

1. Reconnect to the internet and open the normal VaultFlow website. Complete any
   network or tunnel access notice, then retry the installed app. Keep the same
   browser and website address; another browser or origin has different storage.
2. Sign in online and prepare offline access again. If the device enrollment was
   also removed, obtain a new enrollment code from an administrator. Leave the
   page open until offline files and account data finish downloading.
3. Check **Pending work**. Only operations confirmed by the server are known to
   have survived a full local wipe. Re-enter missing drafts or counts from source
   documents and reconcile any uncertain submission with the official server
   records before entering it again.
4. Before the next outage, open the app once without a connection to verify that
   its files and employee profile are available. Sync and confirm pending work
   before clearing browser/site data, changing browser, or removing the app.

If **Try again** still fails while online, test the normal `/login` page in the
same browser. A website access notice or blocked download must be resolved before
the offline app can be repaired. Do not clear more VaultFlow data as a first
troubleshooting step; it can destroy the remaining outbox.

---

## Risk summary

| # | Risk | Cause | Effect | Current mitigation | Severity |
|---|------|-------|--------|--------------------|----------|
| R1 | Offline unlock expires with no warning | Hard 7-day check at unlock (`offline-store.js:287-289`) | Employee is locked out mid-shift; work blocked until online sign-in | Error message only, discovered at unlock time | High |
| R2 | Clearing site data wipes unsent edits | IndexedDB holds the only copy of queued events | Queued purchase drafts / receiving counts permanently lost | Pending-work warnings in queue, readiness, and lock UI; recovery procedure above | High |
| R3 | Browser storage eviction | Chrome may evict storage when persistence is not granted | Snapshot and outbox silently disappear | `navigator.storage.persist()` requested at sign-in and by explicit readiness-panel action; warning remains if denied | Medium |
| R4 | Storage quota exhaustion | Snapshot plus drafts plus outbox grow; no headroom warning | New edits are **rejected**, not queued (`offline-store.js:435`) | Explicit error at the point of failure | Medium |
| R5 | Forgotten password / PIN | Data sealed with AES-GCM; key derived from password/PIN (PBKDF2) | Employee data unrecoverable; rewrap needs the old secret (`offline-store.js:209-228`) | By design; PIN shown once at provisioning | Medium |
| R6 | Stale data and silent sync gaps | Sync runs only on resume/online events and manual action (`offline-store.js:355-367`, `Home.razor:538-549`); offline-unlock sync errors swallowed (`Home.razor:350`) | Employee works from outdated stock/PO data; pending work sits unconfirmed | Lifecycle-triggered sync; manual sync buttons | Medium |
| R7 | App shell missing after site-data wipe or incomplete download | Service worker Cache Storage is browser data; a failed install can leave a partial cache | Installed icon opens a startup failure screen, including when offline | Cache readiness marker and online repair request; startup screen gives recovery steps | High |

---

## R1 — Offline unlock expires with no advance warning

**Cause.** Each profile stores `lastOnlineUtc`, refreshed on every online sign-in,
handoff, or provisioning (`offline-store.js:187,196,220,263,269,330`). Offline unlock
throws if the profile is older than `maxOfflineAge` = 7 days
(`offline-store.js:2,287-289`).

**Effect.** The employee discovers the expiry only when locked out. The data is
still in IndexedDB but unreachable. On a phone in a store with poor connectivity
this can stop work entirely.

**Gap.** Nothing surfaces the remaining window before expiry. The `lastOnlineUtc`
value is already returned by `initialize()` (`offline-store.js:96-97`) but the UI
never renders it.

### Tasks

- [ ] **T1.1 (P0, S)** — Compute days remaining from `lastOnlineUtc` in
  `Home.razor` (data already available after `RefreshStateAsync`) and render a
  countdown banner on the workspace when ≤ 3 days remain, escalating to a stronger
  warning at ≤ 1 day: "Offline access expires in N days. Sign in online to renew."
- [ ] **T1.2 (P0, S)** — On the offline-unlock picker (`Home.razor` login/unlock
  view), show per-employee "expires in N days" next to each saved employee so the
  choice is made with full information before password entry.
- [ ] **T1.3 (P1, S)** — After a successful online sign-in or unlock, show a
  positive confirmation with the new expiry date: "Offline access renewed until
  <date>."
- [ ] **T1.4 (P1, M)** — Verify whether the server can renew profiles without a
  full snapshot download (e.g. a lightweight `/offline/api/renew` that only
  refreshes `lastOnlineUtc` server-side), so a renewal does not force a full data
  refresh on a slow link. Confirm current `login`/`handoff` cost first.
- [ ] **T1.5 (P2)** — See open question Q1: consider driving the 7-day window from
  the location's `OfflineGracePeriod` setting instead of a hardcoded constant.

---

## R2 — Clearing site data wipes unsent edits

**Cause.** Queued outbox events and working forms live only in IndexedDB. Browser/site-data clearing
deletes them; unlike the server's `wipe-local-cache` directive (`docs/OFFLINE_SYNC.md`),
which preserves the outbox, user-initiated clearing has no protection.

**Effect.** Confirmed edits (status `Confirmed`) are safe on the server, but
anything still `Saved on device` / `Syncing` / `Conflict` / `Needs review`, plus
forms not yet queued, can be permanently lost with no recovery path.

**Remaining gap.** Browser clearing cannot be intercepted reliably, and no
browser-only copy survives a full origin/site-data wipe. An independent recovery
copy or server acceptance is required to make unsynced work durable beyond this
browser. Confirmed status must be checked per operation.

### Tasks

- [x] **T2.1 (P0, S)** — On the sync page (`Home.razor` queue view), when any
  operation is not yet `Confirmed`, show a persistent notice: "N edits are saved
  only on this device. Sync and confirm them before clearing browser or site
  data." Reuse `RefreshOperationsAsync` counts already tracked as `PendingCount`;
  include saved working forms, which are also device-only.
- [x] **T2.2 (P1, S)** — Add the same one-line warning to the readiness panel
  (`Home.razor` storage section) whenever the outbox is non-empty, so it is
  visible outside the sync page.
- [x] **T2.3 (P1, S)** — Surface the warning at lock time: when locking the
  workspace with unconfirmed operations, remind the user how many edits remain on
  the device.
- [x] **T2.4 (P1, M)** — Write user-facing guidance (help section or admin
  runbook) on freeing device storage safely: sync first, prefer clearing other
  site data, never "Clear browsing data" for the VaultFlow origin while work is
  pending. The recovery procedure above is the operator runbook.
- [ ] **T2.5 (P2, L)** — Evaluate a browser-level defense: an unload/beforeunload
  guard when unconfirmed operations exist (best-effort only; installed PWAs close
  without prompts on Android, so do not rely on it).

---

## R3 — Browser storage eviction

**Cause.** Without persistent storage, Chrome on Android may evict IndexedDB and
Cache Storage for an unused or pressured origin. The app requests persistence once
at online sign-in (`offline-store.js:204`) and reports `persisted` in the
readiness panel (`Home.razor:429`), but a denied request is only passive text.

**Effect.** Snapshot and outbox can disappear silently. Unsynced edits are lost
(same impact as R2); sync data is re-downloadable.

**Remaining gap.** Persistence reduces automatic eviction risk but does not
protect against user-initiated clearing. On-device behavior still needs testing.

### Tasks

- [x] **T3.1 (P0, S)** — When `persisted() === false`, render a prominent warning
  in the readiness panel with a call to action ("Allow persistent storage") that
  re-calls `navigator.storage.persist()` on explicit user gesture (the gesture
  improves grant likelihood).
- [x] **T3.2 (P0, S)** — Combine states: if `persisted() === false` **and** the
  outbox is non-empty, escalate the message: "Unsynced work may be lost if the
  browser clears storage."
- [ ] **T3.3 (P1, S)** — After every successful online sign-in, check
  `navigator.storage.persisted()` and log/flag state; confirm whether the existing
  `persist()` call at `offline-store.js:204` is reached on renewals, not just
  first login.
- [ ] **T3.4 (P2)** — Test eviction behavior on the pilot Android device
  (low-storage simulation, `chrome://settings/clear browsing data` equivalents)
  and record actual thresholds in `docs/OFFLINE_SYNC.md`.

---

## R4 — Storage quota exhaustion rejects new edits

**Cause.** IndexedDB quota is device- and browser-controlled. Snapshot (up to
20 000 products, `PwaGateway.cs:226-248`), working drafts, and outbox payloads all
consume it. `queue()` surfaces `QuotaExceededError` explicitly
(`offline-store.js:433-437`) — the edit is **not** queued, so it is lost at the
moment of failure rather than deferred.

**Effect.** A receiving count or purchase draft attempted after quota exhaustion
fails with a message; nothing tells the user how close they were before the
attempt.

**Gap.** `StorageText()` already shows usage/quota (`Home.razor:429`) but there
is no threshold warning and no guidance on what frees space.

### Tasks

- [ ] **T4.1 (P1, S)** — In the readiness panel, color/warn when
  `storageUsed / storageQuota >= 0.8`: "Device storage is almost full. Sync and
  confirm pending work."
- [ ] **T4.2 (P1, S)** — In the quota-exceeded error message, add concrete next
  steps: sync pending work (confirmed events' payloads can then be cleaned up —
  see T4.3), close other tabs of this app, free device storage.
- [ ] **T4.3 (P2, M)** — Evaluate garbage-collecting confirmed outbox payloads:
  after an event reaches `Confirmed`, the sealed payload in `data`
  (`${scope}:operation:${eventId}`) is still stored for revision flows
  (`operationPayload`, `Home.razor:470-483`). Define retention (e.g. delete payload
  for `Confirmed` events, keep for `Conflict`/`Needs review`) and implement in
  `offline-store.js` after a successful sync pass.
- [ ] **T4.4 (P2)** — Add a Playwright case in `tests/Pos.Web.Pwa` that stubs
  `navigator.storage.estimate` to near-quota and asserts the warning appears and
  `queue` fails with the friendly message.

---

## R5 — Forgotten password / PIN makes data unrecoverable

**Cause.** Snapshot, drafts, and outbox are sealed with AES-GCM-256; the key is
derived via PBKDF2 (250 000 iterations) from the employee's password or offline
PIN (`offline-store.js:52-75`). The rewrap path (`rewrapProfile`,
`offline-store.js:209-228`) requires the **old** secret. There is no server-side
recovery: the server never sees the key, and the PIN is generated on the device
and shown once (`newPin`, `offline-store.js:304-311`; `Home.razor:406`).

**Effect.** Lost password **and** lost PIN = local data is permanently
unreadable, including queued-but-unsynced edits. The employee must sign in online
and re-provision; unsynced work is lost.

**Gap.** The irreversibility is implicit in the UI. The provisioning flow shows
the PIN once but does not state the recovery consequence. There is no documented
admin path to revoke and rebuild a single employee's device profile when the PIN
is lost (re-provisioning currently throws if a profile already exists,
`offline-store.js:321`).

### Tasks

- [ ] **T5.1 (P0, S)** — At provisioning time (`Home.razor` prepare flow), add
  explicit copy: "This PIN is shown once and cannot be recovered. If it is lost,
  saved offline data for this employee must be re-prepared."
- [ ] **T5.2 (P0, M)** — Implement an admin "reset offline access" action: revoke
  a single employee's profile on a device (purge that scope's IndexedDB rows —
  profile, snapshot, working drafts; decide explicitly whether to also drop
  unconfirmed outbox events or block reset while they exist). Server-side
  revocation of the provisioning state must accompany the local purge.
- [ ] **T5.3 (P1, S)** — In the unlock view, distinguish "wrong PIN/password"
  from "expired" and "unknown profile" (partially done — see the 2026-10-08
  correction in `WEBSITE_CLEANUP_FINDINGS.md:96`); add guidance that a forgotten
  PIN requires an administrator re-prepare.
- [ ] **T5.4 (P1)** — Document the lost-PIN runbook in `docs/OFFLINE_SYNC.md`:
  symptoms, data at risk (unsynced outbox), reset procedure, verification steps.

---

## R6 — Stale data and silent sync gaps

**Cause.** Synchronization runs: after online sign-in (`Home.razor:340`), after
offline unlock with errors swallowed (`Home.razor:350`), on the manual Sync
button (`Home.razor:535-536`), and on browser lifecycle events via
`watchLifecycle` / `OnBrowserResume` (`offline-store.js:355-367`,
`Home.razor:538-549`). There is no periodic retry while online with pending work,
and a failed sync after offline unlock is invisible.

**Effect.** A device that resumes on a flaky connection can sit for hours with a
non-empty outbox and a stale snapshot. Employees may trust outdated stock levels
or open-PO details. Confirmed-edit latency grows.

**Gap.** No "last synced" surface, no background retry timer, swallowed sync
errors on unlock.

### Tasks

- [ ] **T6.1 (P0, S)** — Store a `lastSyncUtc` timestamp per scope in `meta`
  (written at the end of every `synchronize()` pass, `offline-store.js:466-516`)
  and display "Last synced <local time>" on the workspace and sync pages; show it
  in red when stale (> 24 h) or when a sync attempt failed.
- [ ] **T6.2 (P0, S)** — Stop swallowing sync failures after offline unlock
  (`Home.razor:350`): surface the failure message in the status area instead of
  silently catching `JSException`.
- [ ] **T6.3 (P1, M)** — Add a lightweight retry timer: while `navigator.onLine`
  is true and the outbox contains non-`Confirmed` events, retry `synchronize()`
  with backoff (e.g. 60 s doubling to 30 min), pausing when the workspace is
  locked. Reuse the existing lifecycle hooks; do not add background sync APIs
  (noted in `WEBSITE_CLEANUP_FINDINGS.md` as unavailable in the closed-app case).
- [ ] **T6.4 (P1)** — Add an offline-capable "data age" indicator: when unlocked,
  show the snapshot/cursor age ("Stock data is from <time>") so employees can
  judge whether to trust counts before acting.
- [ ] **T6.5 (P2)** — Extend `tests/Pos.Web.Pwa` offline workflow tests to assert
  the stale-sync and failed-sync UI states.

---

## R7 — Installed app cannot start after browser data is cleared

**Cause.** The installed PWA uses Cache Storage for its app files and IndexedDB
for account data. Both belong to the website origin and are subject to browser
data clearing. The icon can remain after those stores are removed. If the app is
opened offline, it has no files to execute; if it is opened online on a weak or
intercepted connection, Blazor may show the startup failure in the screenshot.
In Development builds, the .NET 10 Hot Reload module previously resolved from
`/_framework/` even though this PWA is hosted under `/offline/_framework/`;
that could produce the same screen without any data wipe.

**Effect.** The device needs an online download and may need enrollment and
account preparation again. An outbox removed with IndexedDB cannot be recovered
by reinstalling the app. The previous startup message incorrectly said saved
work had not been cleared; the client cannot know that at the startup stage.

### Tasks

- [x] **T7.1 (P0, S)** — Replace startup and fatal-error copy with conditional,
  accurate recovery instructions. Do not claim that local work survived.
- [x] **T7.2 (P0, S)** — Mark the service worker cache ready only after all app
  files and the startup shell have downloaded. Validate that the shell and two
  startup scripts have the expected content types and nonempty bodies; an
  incomplete or intercepted download must not be reported as ready.
- [x] **T7.3 (P0, S)** — Document the online recovery and reconciliation steps
  at the top of this file.
- [x] **T7.6 (P0, M)** — When a registered worker reports a missing cache,
  request a fresh download from the active worker. Registration alone does not
  run `install` again if the worker script is unchanged.
- [x] **T7.7 (P1, S)** — Verify the published app in a browser: remove its shell
  cache while leaving the worker registered, reopen online to repair it, then
  open `/offline/` again without a connection (`cache-recovery-browser.cjs`).
- [x] **T7.8 (P1, S)** — Disable WebAssembly Hot Reload for this `/offline/`
  client and verify that a Development build starts without requesting the
  module from the wrong root path. The published and Development phone startup
  tests both passed after the change.
- [ ] **T7.4 (P1, M)** — Run a published-build Android test that clears only the
  VaultFlow site's data, verifies offline start fails as expected, then verifies
  online re-download, reenrollment, and offline start. Record whether the browser
  leaves the installed icon and worker registration in place.
- [ ] **T7.5 (P1, L)** — Design a durable recovery route for unconfirmed device
  edits before promising survival across site-data clearing. This requires a
  separate storage/backup mechanism or earlier server acceptance, plus encryption,
  retention, and reconciliation rules. A service worker cannot solve this alone.

---

## Backlog summary

| ID | Priority | Effort | Task |
|----|----------|--------|------|
| T1.1 | P0 | S | Expiry countdown banner (≤3 days) |
| T1.2 | P0 | S | Per-employee expiry on unlock picker |
| T1.3 | P1 | S | "Renewed until" confirmation |
| T1.4 | P1 | M | Lightweight renewal endpoint |
| T1.5 | P2 | M | Align with `OfflineGracePeriod` (Q1) |
| T2.1 | P0 | S | Pending-work "don't clear site data" notice |
| T2.2 | P1 | S | Same notice on readiness panel |
| T2.3 | P1 | S | Lock-time pending count reminder |
| T2.4 | P1 | M | User-facing safe-clearing guidance |
| T2.5 | P2 | L | Best-effort unload guard |
| T3.1 | P0 | S | Persistent-storage escalation prompt |
| T3.2 | P0 | S | Combined eviction + outbox warning |
| T3.3 | P1 | S | Re-check persistence on sign-in |
| T3.4 | P2 | S | On-device eviction testing |
| T4.1 | P1 | S | ≥80% quota warning |
| T4.2 | P1 | S | Actionable quota-exceeded message |
| T4.3 | P2 | M | GC confirmed outbox payloads |
| T4.4 | P2 | S | Quota Playwright test |
| T5.1 | P0 | S | PIN irreversibility copy at provisioning |
| T5.2 | P0 | M | Admin reset of offline profile |
| T5.3 | P1 | S | Wrong-PIN guidance in unlock view |
| T5.4 | P1 | S | Lost-PIN runbook |
| T6.1 | P0 | S | "Last synced" timestamp surface |
| T6.2 | P0 | S | Surface post-unlock sync failures |
| T6.3 | P1 | M | Background retry timer |
| T6.4 | P1 | S | Snapshot data-age indicator |
| T6.5 | P2 | S | Stale/failed sync tests |
| T7.1 | P0 | S | Accurate startup recovery message |
| T7.2 | P0 | S | Complete-cache readiness marker |
| T7.3 | P0 | S | Site-data-loss recovery runbook |
| T7.4 | P1 | M | Published Android wipe and recovery test |
| T7.5 | P1 | L | Durable recovery design for unconfirmed edits |
| T7.6 | P0 | M | Repair a cleared cache with the active worker |
| T7.7 | P1 | S | Browser test for online cache repair and offline restart |
| T7.8 | P1 | S | Fix Development PWA Hot Reload startup path |

---

## Out of scope

- MAUI desktop/phone client (`src/Pos.Client`) — per `AGENTS.md`, web PWA only.
- Cash sales and shifts — outside the offline PWA scope entirely.
- Server-authority model changes — approvals, stock ledger, and account changes
  remain server-confirmed by design; these tasks do not weaken that boundary.
- Encryption scheme changes — the PBKDF2/AES-GCM design is accepted; tasks only
  address lifecycle and communication around it.

---

## Open questions

- **Q1 — Fixed 7 days vs location policy.** The PWA hardcodes a 7-day unlock
  window (`offline-store.js:2`), but locations already have a configurable
  `OfflineGracePeriod` (default 72 h, `src/Pos.Domain/Organizations/LocationSettings.cs:45`)
  that appears unused by the PWA gateway. Should the PWA window derive from the
  location setting (single source of truth, possibly shorter than 7 days), or stay
  a fixed client constant? T1.5 depends on this decision.
- **Q2 — Renewal without full download.** Should renewal of `lastOnlineUtc` be
  decoupled from snapshot refresh so renewal works on very slow links? (T1.4.)
- **Q3 — Confirmed-event payload retention.** How long must confirmed outbox
  payloads be kept for the "Revise purchase draft" flow before garbage
  collection? (T4.3.)
- **Q4 — Reset semantics.** When an admin resets a lost PIN, should unconfirmed
  outbox events for that scope be recoverable (e.g. export before purge) or
  explicitly discarded with confirmation? (T5.2.)
