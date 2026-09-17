# Point of Sale

---

## 1. Shift lifecycle

```
Open (cashier + opening float)
  -> [sales, returns, no-sale drawer opens]
  -> Suspended (cashier steps away; device locked, shift retained)
  -> PendingClose (cash counted, declared)
  -> Closed (variance recorded)
  -> Reconciled (manager review, variance accepted or escalated)
```

- A sale requires an `Open` shift on an `Active` device, and the signed-in
  cashier must own the shift (or hold `shift.close.other` for administrative acts).
- Closing records `DeclaredCash` (what the cashier says) and `CountedCash` (what a
  manager counts, when segregation is enabled). `CashVariance = Counted − (Float +
  CashSales − CashRefunds − Payouts)`.
- Variance beyond `CashVarianceThreshold` raises a notification and blocks
  `Reconciled` until a manager resolves it with a reason.
- A shift left open past `MaxShiftHours` (default 16) is force-closed by a worker
  and flagged, so an abandoned shift cannot silently absorb the next day's sales.
  Force-close is the worker's authority alone: a device uploading a shift it
  claims to have force-closed is refused, because the claim is what would
  suppress the count.
- A shift closed offline uploads its declared and counted cash as the cashier
  entered them — those are facts about a physical drawer. The server re-derives
  the variance from the sales and refunds it accepted, since that is the figure
  `Reconciled` is judged against; when it disagrees with the one printed at the
  till, the closure is still accepted and the audit entry records both
  (OFFLINE_SYNC.md §3.1.1).

---

## 2. Sale flow

```
Scan / search  ->  Cart  ->  (discounts, overrides)  ->  Payment  ->  Complete
```

1. **Barcode scan** resolves through the local cache first, then the server.
   Unknown barcode ⇒ hard stop and the quarantine path (QUARANTINE.md §2). There
   is no manual-price sale of an unregistered item.
2. **Price resolution** uses the effective-dated price for the product at the
   device's location at the sale timestamp. The resolved price and the
   `PriceVersion` are stamped on the line so the receipt and the audit agree
   forever, even if prices change later.
3. **Batch allocation** for batch-tracked products is FEFO at the location,
   skipping expired batches. The allocated `BatchId` is stored on the sale item,
   which is what makes recall traceability work down to the customer receipt.
4. **Stock check** against the location's `Available` balance. Insufficient stock
   is refused under the default `Prohibit` policy (INVENTORY_LEDGER.md §7).
5. **Discounts** — line and document level. A manual discount requires
   `sale.discount`; a price override requires `sale.price_override`. Both record
   the authorizing user on the line, even when a manager authorises on the
   cashier's behalf via a supervisor prompt.
6. **Tax** per line from the product's tax code. Philippine VAT handling: VAT-able
   lines are tax-inclusive by default (`GrossPrice`), with
   `VatBase = Gross / (1 + rate)` and `Vat = Gross − VatBase`, each rounded to
   4 dp; VAT-exempt and zero-rated lines are tracked separately for the receipt's
   statutory summary.
7. **Payment** — cash, card, e-wallet, split. Cash computes change against the
   configured cash rounding increment. Card/e-wallet delegate to the provider and
   store only the token and reference.
8. **Complete** — one atomic transaction (§3).

---

## 3. Atomic completion

`CompleteSaleCommand` writes, in a single database transaction:

```
Sale                    (header, totals, business date, event id)
SaleItem[]              (product, batch, qty, price, discount, tax, line total)
Payment[]               (method, amount, tendered, change, provider refs)
InventoryMovement[]     via IInventoryLedger — Store/Available −q, EXT-CUSTOMER +q
InventoryBalance        updated by the ledger, under the balance-guard trigger
AuditLog                sale.completed, plus one entry per override/discount
OutboxEvent / ChangeLog (device side / server side)
```

A failure anywhere rolls all of it back: there is no half-saved sale, no sale
without movements, and no movements without a sale. On the device the same
transaction also writes the outbox event, so a sale that exists locally is always
queued for upload.

Idempotency: the sale's `EventId` is generated when the sale starts. A retried
upload returns the original result and the original document number.

---

## 4. Void, return, refund

| Operation | Permission | Window | Ledger effect |
|---|---|---|---|
| **Void** | `sale.void` | same shift, same business day | full reversal group referencing the original |
| **Return (referenced)** | `sale.return` | within `ReturnWindowDays` (default 7) | `EXT-CUSTOMER −q → Store/ReturnPending +q` |
| **Return (blind)** | `sale.return_blind` | manager only | same, plus an exception record |
| **Refund** | `sale.refund` | with an approved return | payment reversal via the original method |
| **Reprint** | `sale.reprint` | any | none; logged in `receipt_print_log` with reason |

**Returned goods never go straight back on the shelf.** They land in
`ReturnPending`. A separate disposition step (`inventory.adjust`) routes each
returned unit to exactly one of:

| Disposition | Ledger |
|---|---|
| Restock | `ReturnPending −q → Available +q` |
| Quarantine | `ReturnPending −q → Quarantine +q` (+ incident) |
| Damaged | `ReturnPending −q → Damaged +q` |
| Supplier return | `ReturnPending −q → Damaged +q`, then `SupplierReturn` |
| Waste | `ReturnPending −q → EXT-WRITEOFF +q`, reason required |

Return quantity per line is capped at `SaleItem.Quantity − SaleItem.ReturnedQuantity`.
Refund amount is capped at the line's net paid amount, discounts included.

---

## 5. Expired batch handling

- FEFO never allocates an expired batch.
- When the sellable shelf cannot cover a line but expired stock could, the sale is
  refused with `inventory.expired_only` (409) so the terminal can offer the
  exception path; a genuine shortfall stays `inventory.insufficient_stock`.
- Selling an expired batch requires `sale.expired_override`
  (`sale.expired_override_denied`, 403, when a line asks for the exception path
  without the permission — even offering it is a permission use). The override
  carries a mandatory reason, at most 500 characters
  (`sale.expired_override_reason_required` / `sale.expired_override_reason_too_long`,
  both 400). Completion records a `sale.expired.override` audit entry with the
  reason, the batch consumed, and the authorizing user stamped from the request
  context (not supplied by the caller). There is no silent path.
- A background worker moves batches past expiry from `Available` to `Expired`
  each night, which is itself a posted movement group (`ExpiryQuarantine`), so
  the transition is auditable like everything else.

---

## 6. Offline behaviour

Fully offline-capable: scan, search, cart, cash payment, completion, receipt
print, void within the shift, referenced return against a **local** sale, shift
open/close.

Not available offline: card payment, returns against sales made on another device
or before the local retention window, price changes, and anything requiring
approval. The status bar always shows connection state, pending event count, and
the time of the last successful sync.

`DeviceStatusBanner` (C32) is that surface. It reports one thing at a time — the
earliest state that stops the register trading, then what will stop it soon,
then the ordinary offline case — and it says nothing about how the device stores
or moves anything: its contract lives in `Pos.Shared`, which references nothing,
so there is no field on it that could carry a path, a key, a server address or a
feed position. Being offline is not a warning; expired cached authority is.
Since C34 it also shows how much work head office has not seen — a count, not a
queue: a cashier needs to know whether anything would be lost if the device were
wiped, not what the transport is doing.

---

## 7. UI requirements (touch-first)

- Minimum touch target 48×48 px; primary actions ≥ 64 px tall.
- Barcode-first: the scan field holds focus at all times and re-acquires it after
  every dialog. A scan is just fast keystrokes ending in Enter.
- Product grid with category tabs and favourites for items without barcodes
  (loose produce, bakery).
- The running total is the largest element on screen, always visible.
- Payment is a single screen: amount due, quick-tender buttons (exact, 100, 500,
  1000), numeric keypad, method selector, change displayed in a large font.
- Windows keyboard shortcuts: `F2` search, `F3` quantity, `F4` discount,
  `F6` hold, `F7` recall, `F8` void line, `F9` payment, `F10` complete,
  `Esc` cancel. All are discoverable from an on-screen legend.
- Errors are inline and specific ("Only 3 left at Store 1"), never modal stacks.
- Confirmation dialogs only for irreversible or money-affecting actions: void,
  refund, price override, completing a sale with a manual discount.
- Persistent header: store, device, cashier, shift number, online/offline badge,
  pending-sync count.
- Android and Windows share the same Razor components from `Pos.SharedUI`;
  layout adapts by breakpoint, not by platform branching.

---

## 8. Hardware abstractions

```csharp
public interface IReceiptPrinter      { Task<Result> PrintAsync(ReceiptDocument doc, CancellationToken ct); }
public interface IBarcodeScanner      { IAsyncEnumerable<ScanResult> ScansAsync(CancellationToken ct); }
public interface ICashDrawer          { Task<Result> OpenAsync(CashDrawerOpenReason reason, CancellationToken ct); }
public interface IDeviceInfoService   { DeviceInfo Current { get; } }
public interface INetworkStatusService{ bool IsOnline { get; } IObservable<bool> Changes { get; } }
```

Implementations live in `Pos.Client/Platforms/{Windows,Android}` and are
registered per platform. Razor components depend only on the interfaces, never on
`Windows.Devices.*` or `Android.*`. A `NullReceiptPrinter`/`ConsoleReceiptPrinter`
pair keeps the POS testable in a headless environment.

`ReceiptDocument` is a platform-neutral model (lines, alignment, emphasis, cut,
barcode/QR blocks) rendered by each printer implementation, so receipt layout is
defined once and tested without hardware.

---

## 9. Tests

| Test | Asserts |
|---|---|
| `Sale_ReducesOnlySellingLocationAvailable` | other locations untouched |
| `Sale_PostsLedgerGroupSummingToZero` | double-entry invariant |
| `Sale_WithInsufficientStock_IsRefused` | prohibit policy |
| `Sale_FailureRollsBackEverything` | no orphan sale or movement |
| `Sale_AllocatesFefoBatch` | earliest expiry chosen |
| `Sale_SkipsExpiredBatch` | expired never allocated |
| `Void_PostsExactReversal` | quantities and costs mirror |
| `Return_LandsInReturnPending_NotAvailable` | disposition required |
| `Return_CannotExceedOriginalQuantity` | cap |
| `Refund_CannotExceedNetPaid` | cap |
| `Discount_RequiresPermission` | 403 without `sale.discount` |
| `PriceOverride_RecordsAuthorizingUser` | audit |
| `Tax_VatInclusiveRoundsPerLine` | tax arithmetic |
| `CashChange_RoundsToConfiguredIncrement` | cash handling |
| `Reprint_RequiresPermission_AndIsLogged` | control |
| `OfflineSale_ProducesIdenticalLedgerToOnline` | ADR-0008 |
