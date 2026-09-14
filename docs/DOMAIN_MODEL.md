# Domain Model

Aggregates, entities, value objects and invariants. `Pos.Domain` contains these
types and nothing else — no persistence, no DI, no framework references.

---

## 1. Building blocks

```csharp
public abstract class Entity<TId>          // identity equality
public abstract class AggregateRoot<TId>   // + domain event collection
public interface IDomainEvent              // raised inside the aggregate
public abstract record ValueObject         // structural equality
```

Identifiers are strongly typed `readonly record struct` wrappers over `Guid`
(`ProductId`, `LocationId`, `UserId`, …) so a `LocationId` can never be passed
where a `ProductId` is expected. All new ids are **UUIDv7** (`Guid.CreateVersion7()`)
so they are time-ordered — important for index locality on high-volume tables
and for deterministic ordering of offline-generated events.

### 1.1 Core value objects

| Value object | Invariants |
|---|---|
| `Money(decimal Amount, string Currency)` | 4 dp; currency is ISO-4217; arithmetic only between equal currencies; `Round(2)` for presentation |
| `Quantity(decimal Value, UnitOfMeasureId Unit)` | 3 dp; non-negative unless explicitly a delta; conversion only via `ProductUnitConversion` |
| `Barcode(string Value)` | trimmed, uppercase, 4–48 chars, `[A-Z0-9\-]`; EAN/UPC checksum validated when the length matches a known symbology |
| `Sku(string Value)` | 1–32 chars, `[A-Z0-9._-]`, uppercase |
| `DocumentNumber(string Value)` | matches `^[A-Z]{3}-\d{4}(-[A-Z0-9]{2,6})?-\d{6}$` |
| `PercentageRate(decimal Value)` | 0–100, 4 dp |
| `DateRangeUtc(DateTimeOffset From, DateTimeOffset To)` | `From <= To` |
| `AuditStamp(UserId By, DateTimeOffset AtUtc, DeviceId? Device)` | immutable |

### 1.2 Money rules

- Storage `decimal(19,4)`. Never `double`/`float` anywhere in the stack,
  including DTOs and JSON (serialised as a JSON number from `decimal`).
- Line arithmetic: `lineGross = round(unitPrice * qty, 4)`, then discount, then
  tax, each rounded to 4 dp; the **document total is rounded to 2 dp once**, at
  the end, using `MidpointRounding.AwayFromZero` (Philippine retail convention).
- Tax is computed per line from the line's tax code, then summed; the sum is not
  recomputed from the document total. This keeps per-line tax reporting exact.
- Cash tendered/change are rounded to the smallest circulating denomination
  configured per organization (default `0.01`).

---

## 2. Organization and Locations

```
Organization (1)
 └── Location (N)
      ├── kind: MainWarehouse | Store | External
      ├── timezone, currency, address, contact
      ├── isActive, openedOn, closedOn
      └── LocationSettings
           ├── NegativeStockPolicy
           ├── AdjustmentApprovalThresholds
           ├── AllowsDirectSupplierDelivery (default false)
           ├── OfflineGracePeriod
           └── ReceiptHeader / ReceiptFooter
```

**Invariants**

- Exactly one `Location` with `kind = MainWarehouse` may be `IsActive` at a time.
- `External` locations are system-created, cannot be edited, and are excluded
  from every user-facing location picker.
- A `Location` is never deleted. Closing sets `IsActive = false` and requires
  that all balances are zero and no documents are open.

---

## 3. Identity

```
User ──< UserRole >── Role ──< RolePermission >── Permission
 │
 ├── UserLocationAssignment (N)   which locations the user may act in
 ├── UserPermissionOverride (N)   grant/deny on top of roles, with expiry
 ├── RefreshToken (N)
 └── DeviceSession (N)
```

**Invariants**

- A `Permission` is a stable string constant (`inventory.adjust.approve`).
  Permissions are seeded from code, never created by users.
- `UserPermissionOverride` carries `Effect ∈ {Grant, Deny}`; `Deny` always wins.
- Every user must have at least one `UserLocationAssignment`, except users
  holding `location.all` (Owner, Administrator, Auditor, Main Inventory Manager).
- Disabling a user revokes all refresh tokens and increments `SecurityStamp`,
  which invalidates outstanding access tokens within their 10-minute lifetime.

See [PERMISSIONS.md](PERMISSIONS.md) for the full catalogue and the role matrix.

---

## 4. Devices

```
Device
 ├── DeviceId (Guid), ShortCode (e.g. "D03") — used in offline document numbers
 ├── Name, Platform (Windows|Android), AppVersion, OsVersion
 ├── LocationId (assigned store/warehouse)
 ├── Status: PendingEnrolment | Active | Suspended | Revoked
 ├── EnrolledAtUtc, EnrolledByUserId
 ├── LastSeenAtUtc, LastSyncAtUtc, LastSyncCursor
 ├── ClockSkewSeconds (last observed)
 └── PublicKeyThumbprint (for signed sync envelopes)
```

**Invariants**

- `ShortCode` is unique across the organization and immutable once issued.
- Sync and token refresh are rejected for any status other than `Active`.
- Revocation is permanent; a returning device must be re-enrolled and receives a
  new `DeviceId`. Its historical documents keep the old id.

---

## 5. Catalog (Product Master)

```
ProductCategory (self-referencing tree)
Brand
UnitOfMeasure (Piece, Pack, Box, Case, Kilogram, Gram, Liter, Milliliter)
Supplier

Product  ◄── aggregate root
 ├── Sku, Name, Description
 ├── CategoryId, BrandId, PrimarySupplierId
 ├── BaseUnitOfMeasureId              the unit the ledger counts in
 ├── ProductBarcode (N)               many barcodes, one primary
 ├── ProductUnitConversion (N)        1 Case = 24 Piece
 ├── ProductSupplier (N)              supplier SKU, lead time, last cost
 ├── ProductPrice (N)                 effective-dated, per location or global
 ├── TaxCode, IsVatExempt
 ├── DefaultPurchaseCost (Money)
 ├── TracksBatches, TracksExpiry, ShelfLifeDays
 ├── IsActive, DiscontinuedOn
 ├── ImageRef
 └── ProductLocationSetting (N)       min / reorder / target / max / preferred qty
```

**Invariants**

- Products are created **only** at the Main Warehouse or by an administrator
  (`product.create` is not granted to store roles). This is the Centralized
  Product Master rule.
- `Sku` is unique and immutable after first inventory movement.
- A `Barcode` value is unique across the entire catalog; attaching a barcode that
  already belongs to another product is rejected, not silently re-pointed.
- Exactly one `ProductBarcode` per product has `IsPrimary = true`.
- `BaseUnitOfMeasureId` is immutable once any movement exists for the product.
  All ledger quantities are expressed in the base unit; UI units are converted
  at the edge.
- `ProductUnitConversion` factors are `decimal(18,6)`, strictly positive, and may
  not create a cycle. Conversions are immutable once used on a posted document.
- `TracksBatches` cannot be turned off while non-zero batch balances exist.
- Deactivating a product does not remove history and does not zero stock; it
  blocks new sales and new purchase lines.
- Price changes are **never in-place edits**: a new `ProductPrice` row with a new
  `EffectiveFromUtc` supersedes the previous one; the old row is retained.

### 5.1 ProductLocationSetting

Per `(ProductId, LocationId)`:
`MinimumStock`, `ReorderPoint`, `TargetStock`, `MaximumStock`,
`PreferredReplenishmentQuantity`, `IsStocked`.

Invariant: `0 <= MinimumStock <= ReorderPoint <= TargetStock <= MaximumStock`
when all are set. Replenishment recommendations read exclusively from here.

---

## 6. Inventory

Covered in depth by [INVENTORY_LEDGER.md](INVENTORY_LEDGER.md).

```
InventoryMovement    append-only ledger leg
InventoryBalance     materialized projection, ledger-derived
InventoryReservation Sale/transfer soft-holds with expiry
Batch                lot, expiry, supplier, unit cost, origin receipt
IntegrityIncident    raised by reconciliation drift, never auto-resolved
```

### 6.1 Batch

```
Batch
 ├── ProductId, LotNumber, SupplierId?
 ├── ManufacturedOn?, ReceivedOn, ExpiresOn?
 ├── OriginGoodsReceiptId, OriginLocationId
 ├── UnitCost (Money)
 └── Status: Active | Quarantined | Expired | Consumed | Recalled
```

**Invariants**

- `(ProductId, LotNumber, SupplierId)` is unique.
- A batch is created only by a goods receipt, a quarantine release, or an
  authorized opening-balance load.
- `ExpiresOn` is required when the product has `TracksExpiry = true`.
- FEFO: allocation picks the batch with the earliest `ExpiresOn` among batches
  with `Status = Active`, non-zero `Available` balance at the location, and
  `ExpiresOn > now`. Ties break on earliest `ReceivedOn`, then `Id`.
- An expired batch cannot be allocated to a sale. Selling it requires the
  `sale.expired_override` permission, which creates an exception record and
  notifies HQ; there is no silent path.

---

## 7. Purchasing

```
Supplier
 └── PurchaseOrder ◄── aggregate root
      ├── PurchaseOrderLine (N)
      ├── PurchaseApproval (N)
      └── GoodsReceipt (N)
           ├── GoodsReceiptLine (N)
           └── ReceivingDiscrepancy (N)
SupplierReturn
 └── SupplierReturnLine (N)
```

Statuses: `Draft → PendingApproval → Approved → Ordered → PartiallyReceived →
FullyReceived → Closed`, with `Cancelled` reachable from `Draft`,
`PendingApproval`, `Approved`, `Ordered`.

**Invariants**

- A PO line's `ReceivedQuantity` is derived from goods receipt lines, never set.
- `ReceivedQuantity` may exceed `OrderedQuantity` only up to the configured
  over-receipt tolerance; beyond that the excess goes to `Quarantine` and raises
  an incident.
- A goods receipt always records the three quantities separately:
  `QuantityExpected`, `QuantityReceived`, `QuantityRejected`, plus per-line
  `DiscrepancyKind ∈ {None, Shortage, Overage, Damaged, WrongItem, Expired,
  MissingDocuments}`. **Ordered is never assumed to equal received.**
- A PO can only be `Approved` by a user holding `purchase.approve`, and never by
  its creator when the value exceeds `SelfApprovalLimit`.
- Closing a partially received PO requires a reason and is audited.

See [PURCHASING.md](PURCHASING.md).

---

## 8. Transfers

```
TransferOrder ◄── aggregate root
 ├── TransferOrderLine (N)
 ├── TransferApproval (N)
 ├── TransferShipment (N) ── TransferShipmentLine (N)
 ├── TransferReceipt (N)  ── TransferReceiptLine (N)
 ├── TransferDiscrepancy (N)
 └── TransferCustodyEvent (N)   the chain-of-custody trail
```

`TransferKind ∈ { WarehouseToStore, StoreToStore, StoreToWarehouse }`
`TransferMode ∈ { Normal, PreApproved, EmergencyOffline }`

The state machine, custody model and discrepancy rules are in
[TRANSFER_WORKFLOW.md](TRANSFER_WORKFLOW.md).

**Invariants**

- A store user may not approve a transfer that their own location requested.
- `StoreToStore` always requires Main Warehouse approval unless it carries a
  valid, unexpired pre-approval token issued by HQ.
- `EmergencyOffline` transfers are created with status `PendingCentralReview`
  and can never transition directly to `Completed`.
- Quantities are monotone: `QuantityReceived <= QuantitySent` is *not* enforced
  (overages happen); instead any mismatch materialises a `TransferDiscrepancy`.

---

## 9. Quarantine

```
QuarantineIncident ◄── aggregate root
 ├── kind: UnknownBarcode | UnauthorizedSupplierDelivery | OverReceipt
 │       | FailedInspection | ReturnedGoods | Recall
 ├── LocationId, ReportedByUserId, DeviceId, ReportedAtUtc
 ├── ClaimedSupplierId?, ClaimedSupplierName?
 ├── RawBarcode?, ClaimedProductDescription?
 ├── QuarantineIncidentLine (N)  barcode/product, quantity, notes
 ├── QuarantineIncidentPhoto (N)
 ├── status: Open | UnderReview | Approved | PartiallyApproved | Rejected
 │         | UnderInvestigation | ReturnedToSupplier | Closed
 └── ResolutionNotes, ResolvedByUserId, ResolvedAtUtc
```

**Invariants**

- Physical goods behind an incident are always posted to `Quarantine` state at
  the reporting location. They are inventory — visible and valued — but not sellable.
- Only `quarantine.release` at HQ can move quantity from `Quarantine` to
  `Available`, and only up to the approved quantity per line.
- An incident cannot be closed while any of its quantity remains in `Quarantine`.
- Registering a new product from an incident requires `product.create`; the new
  product and barcode are created in the Product Master first, then the release
  posts against it.

See [QUARANTINE.md](QUARANTINE.md).

---

## 10. Counting and Adjustments

```
InventoryCount ◄── aggregate root                    (ADR-0031)
 ├── kind: FullPhysical | Cycle | Category | ProductSpecific
 ├── status: Counting | PendingApproval | Posted | Cancelled   (reject → Counting)
 ├── SnapshotTakenAtUtc              sheet taken from the ledger here
 └── InventoryCountLine (N)
      ├── SystemQuantity             refreshed each time the line is counted
      ├── PhysicalQuantity, Variance (derived), VarianceValue (derived)
      └── IsRepeatVariance           set on submission

StockAdjustment ◄── aggregate root                   (ADR-0031)
 ├── status: Draft | PendingApproval | Rejected | Posted | Reversed
 ├── reason: Damaged | Broken | Contaminated | Spoilage | Loss | Theft | Expired | Other
 └── StockAdjustmentLine (N)        state, signed quantity, captured unit cost,
                                    movement type decided by the reason
```

**Invariants**

- `Variance = PhysicalQuantity − SystemQuantity`, computed, never entered.
- A count **never** writes to the ledger before approval. Approval posts one
  `CountAdjustment*` movement group referencing `CNT-…`.
- Adjustment approval thresholds are evaluated on the **absolute value** of the
  adjustment at the snapshot's valuation, not on quantity.
- `Other` as a reason requires free-text notes of at least 10 characters.
- An adjustment, once posted, is immutable; reversal creates a new adjustment
  with `ReversesAdjustmentId` set.

---

## 11. Sales

```
CashierShift ◄── aggregate root
 ├── LocationId, DeviceId, CashierUserId
 ├── OpenedAtUtc, OpeningFloat (Money)
 ├── ClosedAtUtc?, DeclaredCash?, CountedCash?, CashVariance?
 └── status: Open | Suspended | PendingClose | Closed | Reconciled

Sale ◄── aggregate root
 ├── SaleNumber (DocumentNumber), CashierShiftId, CustomerId?
 ├── status: Draft | Completed | Voided | PartiallyReturned | FullyReturned
 ├── SaleItem (N)  product, batch?, qty, unitPrice, discount, taxCode, lineTotal
 ├── Payment (N)   method, amount, tendered, change, providerRef, token, status
 ├── SaleDiscount (N) document-level discounts with authorizing user
 └── totals: Subtotal, DiscountTotal, TaxTotal, GrandTotal (Money)

Return ◄── aggregate root
 ├── OriginalSaleId?  (strongly preferred; blind returns need `sale.return_blind`)
 ├── ReturnItem (N)   qty, condition, disposition
 └── Refund payments
```

**Invariants**

- A `Sale` may only be `Completed` inside an `Open` shift on an `Active` device.
- Completion is atomic: sale + items + payments + ledger movements + audit in one
  transaction (see [POS.md](POS.md)).
- `Σ Payment.Amount == GrandTotal` at completion, to the cent.
- Void is only permitted on the same business day, within the same shift, by a
  user with `sale.void`; it posts a full reversal movement group.
- A `Return` cannot exceed the original sale's remaining returnable quantity per line.
- Returned goods land in `ReturnPending`, never directly in `Available`. A
  separate, permissioned disposition step decides where they go.
- Card payments store `Provider`, `ProviderTransactionRef`, `PaymentToken`,
  `Amount`, `Status`, `MaskedLast4` only. **Never PAN, never CVV.**

---

## 12. Notifications and Audit

```
Notification
 ├── kind, severity (Info|Warning|Critical), title, body
 ├── target: UserId? / RoleId? / LocationId? / permission-scoped
 ├── ReferenceDocumentType/Id, CreatedAtUtc
 └── NotificationReceipt (N)  per-user read/ack state

AuditLog   append-only
 ├── UserId, UserRoleSnapshot, DeviceId, LocationId, IpAddress, UserAgent
 ├── Action (stable string), EntityType, EntityId
 ├── PreviousValueJson, NewValueJson  (scrubbed, diff-only)
 ├── Reason, ReferenceDocumentType/Id
 ├── OccurredAtUtc (server), CorrelationId
 └── ChangeSequence
```

`AuditLog` is subject to the same immutability triggers as `InventoryMovement`.

---

## 13. Synchronization

```
SyncEvent          server-side record of an accepted client event (idempotency key)
ProcessedEvent     EventId -> stored result envelope + hash
SyncCheckpoint     per device: last acknowledged upload seq, last downloaded cursor
SyncFailure        per event: attempt count, error, next retry, correlation id
ChangeLogEntry     server-side monotonic feed consumed by devices
DeviceSession      login/heartbeat sessions per device
```

Detailed in [OFFLINE_SYNC.md](OFFLINE_SYNC.md).

---

## 14. Domain events

Raised by aggregates, dispatched **after** the transaction commits via a
transactional outbox so a notification can never describe a rolled-back change.

| Event | Raised by | Consumers |
|---|---|---|
| `StockLevelChanged` | ledger | replenishment, low-stock alerts, SignalR |
| `LowStockReached`, `OutOfStock`, `OverstockDetected` | ledger projection | notifications |
| `NegativeStockAttempted` | ledger | exception dashboard, audit |
| `QuarantineIncidentRaised` / `Resolved` | quarantine | HQ notifications |
| `TransferRequested/Approved/Rejected/Dispatched/Received/Discrepancy` | transfers | both locations + HQ |
| `PurchaseOrderApproved`, `GoodsReceived`, `ReceivingDiscrepancyRaised` | purchasing | HQ, buyer |
| `SaleCompleted`, `SaleVoided`, `RefundIssued` | sales | analytics, HQ |
| `AdjustmentSubmitted/Approved`, `CountVarianceDetected` | inventory control | approvers |
| `EmergencyTransferCreated` | transfers | owner dashboard (high visibility) |
| `BatchExpiringSoon`, `BatchExpired` | expiry worker | store + HQ |
| `SyncEventRejected`, `DeviceOfflineTooLong` | sync | HQ |
