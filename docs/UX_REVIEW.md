# Web UX review — design, usability and flow (2026-11-06)

Scope: the `Pos.Web` website and its shell (the MAUI `Pos.Client` is out of
scope, per `AGENTS.md`). The review ran the real API, PostgreSQL and web host
with the development seed and walked the site as **owner**, **warehouse**
(Main Inventory Manager) and **manager** (Store Manager, STORE01) at desktop
(1440×900) and phone (Pixel 7) sizes. Each "fixed" item below was re-checked
the same way after the change.

Priorities: **High** blocks or misleads people in a daily task; **Medium**
costs clicks or causes doubt; **Low** is polish.

## Fixed in this change

| # | Priority | Problem found | What changed |
|---|---|---|---|
| 1 | High | **Single-store staff could not sign in without an extra hunt.** A manager assigned to one store got *"Choose a work location before signing in"*, and the picker was hidden under the collapsed *Additional sign-in options*. | After the password check, an account with exactly one assigned location gets it automatically (`Login.razor`). |
| 2 | High | **The work-location list offered locations the person does not work at**, and only said so after submit (*"That location is not assigned to your account"*). | Once credentials pass, the list shows only that account's locations, with a note explaining when it is needed. |
| 3 | High | **Stock levels failed when opened directly** (refresh, bookmark, link in a new tab): `ERR_TOO_MANY_REDIRECTS` on phone, a blank page on desktop. `NavigateTo` ran during prerender, which became an HTTP redirect to the same page, forever. | The address is updated only once interactive and only when the store changed (`StoreInventory.razor`). |
| 4 | Medium | **Every browser sign-in was interrupted** by a *"Set up offline access"* dialog asking for an enrollment code most people never have. | In a normal browser tab sign-in goes straight to the workspace. The enrollment code moved into *Additional sign-in options* (opt-in). The installed app still shows the dialog. |
| 5 | Medium | **Approving a transfer took an extra mandatory step**: *Mark under review* had to be clicked before *Approve* or *Reject* appeared (old finding 7). | One *Decide the request* panel on Submitted and In-review requests. *Approve* records the review and the approval as two custody events, exactly as before; the trail was checked (`Created → Submitted → Reviewed → Approved`). *Mark under review* remains as an optional secondary action. |
| 6 | Medium | **Two Reject controls, and one could reject without a reason.** In review, the decision panel's *Reject with reason* button submitted the (empty) reason field of a second *Reject the request* panel. | One note field: optional for approval, required for rejection; *Reject* stays disabled until it is filled. |
| 7 | Medium | **Picking → dispatch needed two separate handoffs** even for someone allowed to do both. | For users with dispatch rights the *Picking complete* panel offers **Mark ready and dispatch** (two server transitions, owner confirmation still applies) plus *Mark ready only* for split teams. Verified to reach `Dispatched`. |
| 8 | Medium | **Two sidebar items highlighted at once** (warehouse: *Restock fulfillment* + *Restock requests*; owner: *Products & barcodes* + *Catalogue approvals*) from prefix matching. | `SectionNavLink` excludes nested sibling sections; every page now marks exactly one item. |
| 9 | Medium | **The Overview repeated the sidebar** as up to 15 large *Quick actions* cards at the bottom of the page. | Replaced by up to four role-specific tasks phrased as verbs (*Review restock requests*, *Request stock*, *Receive a delivery*, *Count stock* …) directly under the greeting. |
| 10 | Medium | **Phone tab bar dropped the owner's Approvals tab** (it came fifth and was cut by the four-tab limit). | The role's daily task now follows Home: Approvals (owner), Fulfill (warehouse), Restock (store). |
| 11 | Medium | **Restock approval and fulfillment queues needed manual Refresh**, while the requester's page already updated live. | Both queues reload quietly on the existing transfer-change signal. |
| 12 | Low | **Transfers opened on a large creation form**, above the list, exposing an internal *Pre-approval token* field to everyone. | The form opens from a **New transfer** button; the token sits under *Advanced*. Store staff see a pointer to *Restock requests* for warehouse stock. |
| 13 | Low | **Phone KPI cards ran off-screen**: two stylesheets disagreed (2-column grid vs. a swipe carousel whose negative margin missed the 16px gutter). | One 2×2 grid; long labels wrap. |
| 14 | Low | **Names did not match**: nav *Stock levels* → page *Store inventory*; the line list on a submitted request was titled *Items in transit*; warehouse *Restock requests* actually ask the owner to buy; *Activity log* had a generic "VaultFlow" breadcrumb. | Headings, breadcrumbs and nav labels now agree (*Stock levels*, *Requested items* until dispatch, *Supply requests* at the warehouse, *Activity log*; later *Stock exceptions* and *Sync failures*). |
| 15 | Low | **Sidebar overgrown and loosely grouped**: 7 groups, a one-item *Purchasing* group, *Activity log* under Workspace, *Store movement* under "Stores". | Five task-based groups: **Stock**, **Movement**, **Sales & reports**, **Catalogue & buying**, **Administration**. Breadcrumb sections use the same names. |

## Visual design system

`wwwroot/theme.css` is loaded last and gives the existing page stylesheets one
set of decisions instead of rewriting each:

- **Type**: page titles 1.65rem Manrope (pages ranged 27px–2.3rem), leads .9rem,
  kickers .68rem caps; a single-line breadcrumb (*Section › Page*) instead of
  two stacked caps lines.
- **Controls**: 40px buttons and fields (they ranged 37–44px), 9px corners, one
  focus ring; `search-action.secondary` added for secondary actions.
- **Surfaces**: white cards with a 1px line, 12px corners and a quiet shadow
  (corners ranged 2px–20px, shadows from none to heavy).
- **Tables and pills**: one header style, tabular figures, one pill size.
- **Shell**: deeper sidebar with a mint indicator bar for the current page,
  compact 36px rows so more of the menu fits, and a white top bar.
- The informal 👋 was removed from the sign-in heading.
- The offline PWA (`/offline/`) loads the same layer, and the root service
  worker caches `/theme.css` with `/app.css`, so online and offline look alike.

## Flagged items, now fixed (follow-up)

The eight items first flagged for a decision were then implemented.

| # | Priority | Inconvenience | What changed |
|---|---|---|---|
| A | High | **Two receiving forms for one job** (old finding 3): the owner's purchase order page and the staff receiving page recorded deliveries with different fields and safeguards. | `/receiving/{id}` is now the only receiving form. It gained the order page's safeguards: invoice unit cost per line (shown only with `product.cost.view`; a different cost goes to the owner's approval as before), goods not on the order listed and recorded in quarantine (scanned products not on the order, or uncatalogued codes the receiver chooses to hold), a physical-arrival confirmation (with the same-day wording), no receiving before the order is sent, and a warning when items are not in this delivery. The purchase order page's *Receive a delivery* and *Receive the delivery now* open that form with a breadcrumb back to the order; its separate form and about 280 lines of duplicate code were removed. Verified end to end against the real API. |
| B | Medium | **Store staff seemed to have two ways to ask for stock.** In fact a transfer always *sends* stock out of a location the person works at, while a restock request asks for stock to *come in*; the labels did not say so. | The Transfers page names the direction: **Send stock** (when signed in at a location) next to **Request stock** for store staff, and the form reads *Send stock from {location}*. A single possible source is selected automatically. |
| C | Medium | **Restock was split across three role pages plus Transfers.** | Restock is one module: the menu has one **Restock** entry that opens the person's own tab, and the pages share a tab row (*Approvals*, *Fulfillment*, *Restock/Supply requests*, *All transfers*), showing only what the person may open. |
| D | Medium | **Overlapping analytics pages.** | *Best & worst sellers* and *Store movement* are tabs of one **Store performance** menu entry. *Sales ledger* and *Daily reports* stay separate (different jobs). |
| E | Low | **Stock counts and Goods receiving relied on Refresh.** The server sent no live signal when a count or purchase order changed. | The server's workflow-document signal now also covers stock counts and purchase orders (new, or a status step), and both lists reload quietly on it. Verified: a count opened elsewhere and an order sent elsewhere appeared within seconds without Refresh. |
| F | Low | **Owner sidebar scrolled on a 900px-tall screen.** | Menu groups fold; *Administration* starts folded and the group holding the current page always stays open. On desktop the sidebar no longer repeats the signed-in name (the top bar shows it). The owner's menu now fits at 900px. |
| G | Low | **"Failed attempts"** was unclear. | Renamed **Stock exceptions** in the menu, page, breadcrumb and Overview. |
| H | Low | **Sync failures page was titled "Failure review".** | Titled **Sync failures**. |

## Verification

Follow-up (flagged items):

- Purchase order → *Receive a delivery* opened `/receiving/{id}?from=order`
  with a *Purchase order* breadcrumb; *Record delivery* stayed disabled until
  arrival was confirmed and while a scanned product not on the order was
  listed; *Record in quarantine* filed the incident; an edited invoice cost
  (35.32) was saved on the receipt; leaving one line out showed *Not in this
  delivery: 1 item* and left the order `PartiallyReceived`.
- Owner, warehouse and store manager each see one *Restock* and one *Store
  performance* entry with exactly one active item and the right tabs; the
  store manager's Transfers shows *Request stock* and *Send stock*.
- Stock counts and Goods receiving picked up a count opened and an order sent
  by someone else within seconds, without Refresh.
- New infrastructure test: a new stock count is announced at its location.
  Web 51, architecture 31 and infrastructure 246 tests pass (21 skip without
  Docker). API integration: 202 pass; 11 fail, all of them already failing
  before this work: 9 fail identically at the previous commit (transfer
  activity returns 500, product curation returns 400) and 2 need a Docker
  daemon. Those pre-existing failures are worth a separate look.

First pass:

- `Pos.Web` builds with zero warnings and errors (warnings are errors here).
- `Pos.Web.Tests` 51/51 and `Pos.Architecture.Tests` 31/31 pass.
- Real API + PostgreSQL walkthrough: manager signs in with no location pick
  and no dialog; owner approves a submitted restock request with one click
  (custody `Created, Submitted, Reviewed, Approved`); warehouse picks and uses
  *Mark ready and dispatch* to reach `Dispatched`; *Reject* is disabled
  without a note; each page highlights exactly one nav item; Stock levels
  opens directly; the approvals queue picked up a new store request without
  Refresh.
- PWA browser checks against a Release publish and the fake API:
  `online-preparation.cjs` (updated for opt-in enrollment; it also still
  referred to a *Choose work location* button and an old offline heading),
  `cache-recovery-browser.cjs`, `installability.cjs`, `web-styling.cjs` and
  `startup.cjs` pass. Four checks already failed before this change and still
  do: `unified-login.cjs` and `login-preparation.cjs` look for a *Have a
  two-factor code?* button and a *Welcome, …* heading that no longer exist,
  `offline-workflow.cjs` waits for the same old heading, and `cache-repair.cjs`
  mocks `cache.addAll` although the worker now fetches each file with retries
  (`setTimeout is not defined` in its sandbox, identical at the previous commit).
