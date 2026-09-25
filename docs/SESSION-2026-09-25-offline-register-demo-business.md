# Session Log — offline register, owner console trim, realistic demo business

**Date:** 2026-09-21 to 2026-09-25
**Branch:** `claude/desktop-offline-web-design-esd5lo` (commits `97331b2` through `1bd648c`, all pushed)
**Status at end of session:** API, Web and the demo tool build with zero warnings. All non-Docker suites pass (Domain 378, Application 260, Infrastructure 239 + 21 skipped PostgreSQL, Sync 22, Security 54, Architecture 25, API 182). The API's 2 Docker-backed PostgreSQL tests could not run (no Docker daemon). `Pos.Client` was compile-checked against plain `net10.0` only; it has **not** been built or run with the MAUI workloads — see §3.

---

## 1. What was done

### 1.1 Desktop register works without head office (`97331b2`)

The register signs in, loads its catalogue and completes cash sales from its encrypted device store when the API is down, and uploads the queued events when it comes back. Card and e-wallet stay online-only, as POS.md §6 says.

### 1.2 Owner console and CSS (`7c3d1bf`, `0054ff1`)

Seven read-only web pages that duplicated other screens were removed, the Owner role lost its till permissions (selling happens at the registers), and duplicate CSS was folded away without changing any rendered style.

### 1.3 Realistic demo business (`cf93ec0`, `1bd648c`)

- **Seed:** "Suki Mart" — a distribution centre and three branches, 749 products in 18 categories from 59 brands and 12 suppliers (`DevelopmentCatalogue`, deterministic), valid EAN-13 barcodes, velocity-based restock levels, 67 customers, 14 named staff accounts, and a receipt header/footer per branch.
- **Demo trading (`tools/Pos.DemoData`):** about 25,500 sales (₱9.3M) over 31 days in about 4½ minutes. There are two registers and two cashier shifts per store per day, an hourly arrival curve, senior citizen/PWD discounts, voids, and returns within the 7-day window. The back office is sized from actual sales: purchase orders, restock transfers, counts, adjustments, receipts and quarantine. See `LOCAL_TESTING.md` → *Load the demo business*.

### 1.4 Web and register now read the same numbers

- The Overview reads the same store-performance report as the Stores page. The report sums in the database, with a fallback for SQLite.
- Sales search pages its results (`offset`/`limit`, `X-Total-Count`) and finds sales by part of the receipt number. The Sales ledger shows the true total and loads more.
- Web pages use the stores' calendar and clock (`StoreClock`, Asia/Manila), not the web server's.
- The till groups products by category, carried in the register baseline and cached on the device (SQLite migration `DeviceProductCategory`).
- Receipts print the branch header and footer, both online and offline.

### 1.5 Bugs found by running the demo against PostgreSQL

| Bug | Fix |
|---|---|
| Two branches selling the same product at once raced on the shared `EXT-CUSTOMER` balance row; one sale failed with a 500. | `UnitOfWorkBehaviour` rolls back and re-runs a command that loses a balance race (5 attempts). `IUnitOfWork` gained `IsConcurrencyConflict` and `DiscardChanges`. |
| The global rate limiter ran before authentication, so its "per user" budget was per IP address (a whole branch, or every web user, shared 300 requests a minute). | The limiter now runs after `UseAuthentication` and is partitioned by the `sub` claim. |
| A return had to be taken at the register that rang the sale; POS.md §6 only restricts *offline* registers. | Any register in the store can take the return. The register's Returns panel covers the 7-day window and searches by receipt number. |
| Shift open and close took head office's clock on arrival, so a shift opened offline showed its upload time. | The register's `OpenedAtUtc`/`ClosedAtUtc` are honoured: never in the future, and a close never before the open. Sync replay passes the time the device already sends. |
| An `IDispatcher` name clash with MAUI's global usings stopped `Pos.Client` compiling. | Aliased in `RegisterService`. |
| The seeder never saved the branch receipt text (the context is no-tracking by default). | `AsTracking()` on that lookup. |

---

## 2. Verified

- A full reset, seed and demo load on PostgreSQL 16: no 500s, no rate-limit waits, every back-office step succeeded. Store-performance reports take about 0.1 s over 30 days, and the register baseline is 641 KB in 0.16 s.
- In a headless browser, signed in as the owner, Overview and Store performance agree (₱1,813,412 net / 4,871 sales for the week). The Sales ledger pages through 2,333 sales for one store-week, and times display in Manila time.
- The register baseline carries a category for all 749 products, plus the branch receipt settings. A thermal receipt for a discounted sale prints the header, the cashier's name, the discounts, VAT and the footer.

---

## 3. What to do next

In priority order.

1. **Build and run `Pos.Client` on Windows with the MAUI workloads.** This session only compile-checked it against plain `net10.0`. Check on a real device:
   - offline sign-in and cash sales with the API stopped, then the upload on reconnect;
   - the new category tabs;
   - the Returns panel's receipt-number search across the 7-day window;
   - the offline receipt header and footer;
   - that the `DeviceProductCategory` migration applies to an existing `device.db`.
2. **Reset the development database on your machine.** The seeder only adds what is missing, so an old database keeps the 43 old products. Follow `LOCAL_TESTING.md` → *Start over with fresh demo data*, re-enrol the desktop register, then run `tools/Pos.DemoData`.
3. **Run the Docker-backed PostgreSQL suites** (the Infrastructure and API PostgreSQL classes). Add a PostgreSQL test that fires concurrent sales of the same product at two stores and proves every one commits through the new unit-of-work retry. The SQLite suites cannot reproduce that race.
4. **Add the missing `ThrottlingTests`.** `Pos.Security.Tests/PosApiFactory.cs` refers to them, but they do not exist. Prove that two signed-in users behind one address get separate global budgets, and that anonymous callers share their address's budget.
5. **Push master-data changes to registers between sign-ins.** `IChangeFeedPublisher` is registered but nothing calls it. Registers only refresh from a full baseline at sign-in or on *Re-download*, so a price change made mid-shift does not reach the till until then. Either publish `ProductChanged`/`ProductPriceChanged`/`LocationChanged` from the catalogue and location commands and have the register pull the feed, or refresh the baseline on a timer while online.
6. **Model the senior citizen/PWD discount properly.** The demo applies it as a plain 20% line discount authorised by the manager. Philippine rules also make those sales VAT-exempt and require the ID number on the receipt; the domain has no concession type for either.
7. **Add a test for `DevelopmentDataSeeder`.** It has none, which is how the untracked-entity bug in §1.5 slipped through. A SQLite test that seeds twice should check that the branch receipt text is set and that nothing is duplicated.
8. **Reduce log noise from handled contention.** EF Core logs `Failed executing DbCommand` at Error for balance races the unit of work now retries successfully. Lower that category, or log the retry instead.
9. **Consider a like-for-like Overview comparison.** "vs previous 7 days" compares a window that includes today's partial trading against seven full days, so early in the day every card shows a drop.
10. **Carried over from `STATUS.md` §5:** the remaining web UI P2 pages (purchase orders + goods receipt, supplier returns, direct delivery, product master, expiry); MAUI store signing; the restore drill; and the production deployment verification.
