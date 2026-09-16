# Session Log — C17: Card/E-wallet and Split Payment Checkout

**Date:** 2026-09-16
**Baseline:** commit `cda01c6` (`feat(pos-c16): web sale lifecycle with search, receipt reprint, void and return/refund/disposition pages`)
**Status at end of session:** solution builds clean (0 warnings, warnings-as-errors on), **921 / 923 tests passing without PostgreSQL** — Domain 369, Application 241, Infrastructure 79, Security 52, Architecture 13, API 167; only the two PostgreSQL-guard members fail and only because Docker is unavailable. `Pos.Web` builds with zero warnings and zero errors.

---

## 1. Goal

Give the web checkout (`NewSale.razor`) the full payment-mix surface the
server has always accepted: cash, card (`PaymentMethod = 2`) and e-wallet
(`PaymentMethod = 3`), including split payments within one sale. The backend
`POST /api/v1/sales` already persists multiple payment rows and validates the
mix (sum equals net total at 4 dp, cash tendered ≥ amount, provider reference
≤ 128 chars); only the web checkout was cash-only.

---

## 2. What was built

### 2.1 Web (`Pos.Web`) — all changes are client-side

| File | Contents |
|---|---|
| `Components/Pages/NewSale.razor` | The single-cash tendered `showPayment` panel is replaced with a payment-allocation flow: an allocated-payment list (method chip, amount, tendered/change or provider reference, remove `×`), an add-payment form (Cash/Card/E-wallet method tabs, a precise-money amount input seeded with the remaining amount, cash tendered + quick-tender buttons Exact/100/500/1000, optional provider reference ≤ 128 chars for card/e-wallet), and a summary (amount due, allocated, remaining to pay or total estimated change). `Complete sale` is disabled until `RemainingToPay == 0`. State: `List<PaymentInput> payments`, `paymentMethod`, `paymentAmount`, `cashTendered`, `providerReference`; the old `tendered`/`TenderedAmount`/`ChangeEstimate`/`RoundedNet` fields are removed |
| `Components/Pages/NewSale.razor.css` | `.allocated-list`, `.allocated-empty`, `.allocated-row`, `.method-chip` with `.method-1` (cash green), `.method-2` (card blue) and `.method-3` (e-wallet purple), `.add-payment`, `.method-tabs`, `.field-hint`, `.optional`, `.add-payment-btn`, `.remove-payment` |

Design decisions worth recording:

- **Precise-money amount seeding.** The amount input defaults to the remaining
  amount formatted with `MoneyPrecise` (`ToString("0.00########",
  CultureInfo.InvariantCulture)`), and the cash "Exact" button reuses the same
  formatter. This guarantees a 4-dp remainder (e.g. 103.1234 after a discount
  or item mix) round-trips exactly, so the server's
  `decimal.Round(payments.Sum, 4) == netTotal` compare never trips
  `sale.payment_mismatch` on a sub-cent representation error.
- **Remaining-to-pay guard.** `RemainingToPay = decimal.Round(NetTotal -
  AllocatedTotal, 4, MidpointRounding.ToEven)` mirrors the server compare; the
  Complete button and `CompleteSaleAsync` both check it.
- **Change estimation is per cash payment.** `EstimatedChange(payment)` =
  `RoundToIncrement(tendered - amount)` using the terminal's
  `CashRoundingIncrement`; `TotalChangeEstimate` sums the cash rows. Card and
  e-wallet rows contribute nothing (their server `Change` is null).
- **No backend changes.** The server already capped methods to the enum, refused
  a short cash tender, enforced the 128-char provider reference via the
  validator, and rebuilt `sale.payment_mismatch` on any sum mismatch.

### 2.2 Tests (new)

| Project | Files | Count |
|---|---|---|
| `Pos.Api.IntegrationTests` | `SalePaymentEndpointTests.cs` | 6 |

The six tests run through the real pipeline and assert the wire shapes the
checkout now exercises:

1. **Card-only** sale with a provider reference: detail shows one payment,
   `method == "Card"`, `tendered` and `change` both JSON null, the provider
   reference echoed back.
2. **E-wallet-only** sale: `method == "EWallet"` with its provider reference.
3. **Split cash + card** (45 cash tendered 50 + 45 card on a 90 sale): both
   payment rows present (located by `method`, not by array index), cash change
   of exactly 5, card provider reference echoed.
4. **Split cash + e-wallet** (35 + 100 on a 135 sale): the detail's net total
   and the sum of the payment amounts both equal 135.
5. **Under-coverage refused**: 40 on a 90 sale → 409 `sale.payment_mismatch`.
6. **Over-coverage refused**: a 50 cash amount on a 45 sale → 409
   `sale.payment_mismatch`.

---

## 3. What the build/tests caught

1. **The `change` field is null for non-cash payments.** The first test draft
   asserted `change == 0m` for the card row; the wire returns JSON null
   (the domain model has no change for a card/e-wallet payment). Assertions now
   check `JsonValueKind.Null`. The cash row still carries its computed change.
2. **The detail's `payments` array order is not guaranteed.** `sale.Payments`
   is mapped without an `ORDER BY`, so a split test that read `payments[0]` as
   cash intermittently found "Card" first. The test now locates each payment by
   `method` (`Single(...)`), which also reads better as an assertion of what a
   payment *is* rather than where it sits.
3. **Anonymous-array anonymous-type mismatch (CS0826).** Two inline payment
   rows whose `tendered` property inferred as `decimal` in one and `decimal?`
   in the other cannot live in one implicitly-typed array; explicit
   `(decimal?)` casts fix the type inference.
4. **`--no-build` test runs mask rebuilds.** After fixing assertions, a
   `--no-build` run kept failing on the stale DLL; the fix only showed once
   the class was actually rebuilt.
5. **`JsonElement` has no LINQ `Sum`.** The split e-wallet aggregate was
   written with `payments.Sum(...)`; `System.Text.Json` offers no such
   extension — looped the array instead.

---

## 4. Verification

```
dotnet build src/Pos.Web/Pos.Web.csproj        → 0 warnings, 0 errors
dotnet build tests/Pos.Api.IntegrationTests/   → 0 warnings, 0 errors
dotnet test tests/Pos.Api.IntegrationTests/    → 167 passed, 2 failed (PostgreSQL-guard members;
                                                Docker unavailable), 0 skipped
dotnet test tests/Pos.Domain.Tests/            → 369 passed
dotnet test tests/Pos.Application.Tests/       → 241 passed
```

The two failing API members are the long-standing environment guards
(`PostgresInventoryControlTests.AdjustmentAndCounts_PostAndReport_OnPostgres`
and `PostgresHostSmokeTests.Host_OnPostgres_SignsIn_NumbersDocuments_AndPostsTheLedger`),
which need a Docker daemon; they fail on Testcontainers' Docker probe, not on
production code.

---

## 5. Not done (next session)

1. **Receipt thermal/PDF layouts** — the plain-text render and the reason-
   logged reprint exist; an 80 mm thermal column layout and a printable
   HTML/PDF view are deferred.
2. **Phase 10 tail** — the FEFO allocation service extraction, the POS
   sale-blocking/`sale.expired_override` path for expired batches, and
   expiring-soon/expired alerts (Phase 14).
3. **POS pricing flow** — the scheduled-price cancellation path (ADR-0029)
   still needs its POS-facing surface.
4. **Stable payment display order** — the detail route maps `sale.Payments`
   without an `ORDER BY`; harmless today (the UI and tests no longer assume
   order), but a deterministic read order would be worth adding if any client
   ever renders payments like a receipt line-by-line.
5. Commit C17 under the batch convention (`feat(pos-c17): ...`) once the
   documentation pass is accepted.