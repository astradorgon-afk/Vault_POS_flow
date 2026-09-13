# Permissions and Role Matrix

Authorization is **permission-based**. Roles exist only as named bundles of
permissions so that administration is manageable. No code ever branches on a
role name; it always asks for a permission.

---

## 1. Model

```
User ──< UserRole >── Role ──< RolePermission >── Permission (code)
 └──< UserPermissionOverride (Grant | Deny, optional expiry)
 └──< UserLocationAssignment
```

Effective permission set:

```
effective = (Σ permissions of assigned roles) ∪ (overrides with Effect = Grant)
            \ (overrides with Effect = Deny)
```

`Deny` always wins. Overrides may carry an expiry (e.g. a cashier granted
`sale.void` for one shift). Expired overrides are ignored and are purged nightly.

### 1.1 Location scoping

Every permission check is a pair: **(permission, location)**.

```csharp
bool IsAuthorized(UserContext u, string permission, LocationId? scope)
    => u.HasPermission(permission)
    && (scope is null || u.HasPermission(Permissions.Location.All)
                      || u.AssignedLocations.Contains(scope.Value));
```

A Store Manager holding `inventory.adjust.approve` can approve adjustments **at
their own store only**. The same permission held by the Main Inventory Manager,
who also has `location.all`, applies business-wide. This single mechanism is
what prevents a store manager from touching Main Warehouse inventory.

### 1.2 Enforcement points

1. **Server-side, always** — `AuthorizationBehaviour` in the command pipeline and
   `[RequirePermission]` on every endpoint. This is the only enforcement that
   matters. An endpoint without an explicit permission attribute fails an
   architecture test.
2. **Value thresholds** — `IApprovalGate` additionally checks the monetary value
   of the action against the approver's tier (§4).
3. **Client UI** — hides and disables controls. Cosmetic only.
4. **Sync time** — re-evaluated on the server when an offline event is processed,
   using permissions in force *then*, bounded above by the device's cached snapshot.

---

## 2. Permission catalogue

Codes are stable strings; renaming one requires a migration that rewrites
`role_permission` and `user_permission_override`.

### Catalog

| Code | Grants |
|---|---|
| `product.view` | Read products, barcodes, prices |
| `product.create` | Create products (Main Warehouse / admin only) |
| `product.edit` | Edit product master fields |
| `product.barcode.manage` | Add/retire barcodes |
| `product.price.manage` | Create effective-dated price changes |
| `product.cost.view` | See purchase cost and margin |
| `product.disable` | Deactivate / reactivate a product |
| `category.manage`, `brand.manage`, `uom.manage` | Reference data |

### Suppliers and purchasing

| Code | Grants |
|---|---|
| `supplier.view`, `supplier.manage` | Supplier master |
| `purchase.view` | See POs |
| `purchase.create` | Draft a PO |
| `purchase.approve` | Approve a PO (subject to value tier) |
| `purchase.receive` | Create goods receipts |
| `purchase.discrepancy.resolve` | Close receiving discrepancies |
| `purchase.return` | Raise supplier returns |
| `purchase.direct_to_store.authorize` | Authorize a supplier to deliver directly to a store |

### Inventory

| Code | Grants |
|---|---|
| `inventory.view` | See balances for scoped locations |
| `inventory.view.all` | See balances business-wide (implies `location.all` for reads) |
| `inventory.receive` | Post receipts into inventory |
| `inventory.adjust` | Create an adjustment request |
| `inventory.adjust.approve` | Approve an adjustment (subject to value tier) |
| `inventory.count` | Perform counts |
| `inventory.count.approve` | Approve count variances |
| `inventory.reserve` | Create/release reservations |
| `inventory.negative_stock` | Post a movement that drives stock negative (policy-gated) |
| `inventory.movement.view` | Read the ledger / document timelines |
| `inventory.rebuild_balances` | Run the projection rebuild (admin/ops) |

### Transfers

| Code | Grants |
|---|---|
| `transfer.view` | See transfers touching scoped locations |
| `transfer.request` | Create/submit a transfer request |
| `transfer.approve` | Approve, modify or reject a transfer |
| `transfer.pick` | Pick stock at the source |
| `transfer.dispatch` | Dispatch (posts the ledger) |
| `transfer.receive` | Receive and count at the destination |
| `transfer.verify` | Verify a receipt (segregation of duties) |
| `transfer.reconcile` | Resolve discrepancies |
| `transfer.emergency` | Create an emergency offline transfer |
| `transfer.preapproval.issue` | Issue pre-approval tokens |
| `transfer.auto_replenish` | Let recommendations create requests automatically |

### Quarantine

| Code | Grants |
|---|---|
| `quarantine.view` | See incidents |
| `quarantine.create` | Raise an incident (all store staff have this) |
| `quarantine.investigate` | Move an incident to investigation, add findings |
| `quarantine.release` | Release quantity to Available (HQ only) |
| `quarantine.reject` | Reject / return to supplier |

### Receipts

Interim by ADR-0026: standalone RCT-numbered payment receipts. Neither permission
is offline-capable.

| Code | Grants |
|---|---|
| `receipt.create` | Issue a payment receipt (walk-in sale, branch expense, owner withdrawal) |
| `receipt.view` | See and print payment receipts |

### Sales

| Code | Grants |
|---|---|
| `sale.create` | Ring up and complete a sale |
| `sale.discount` | Apply a manual discount |
| `sale.price_override` | Override a unit price |
| `sale.void` | Void a sale |
| `sale.return` | Accept a return against a sale |
| `sale.return_blind` | Accept a return with no original sale |
| `sale.refund` | Issue a refund |
| `sale.reprint` | Reprint a receipt |
| `sale.expired_override` | Sell from an expired batch (exception path) |
| `shift.open`, `shift.close` | Shift lifecycle |
| `shift.close.other` | Close someone else's shift |
| `cashdrawer.open_without_sale` | No-sale drawer opening |
| `customer.manage` | Customer records |

### Reporting, audit, administration

| Code | Grants |
|---|---|
| `report.view` | Operational reports for scoped locations |
| `report.view.financial` | Cost, margin, valuation, profit |
| `report.export` | Export to CSV/XLSX |
| `audit.view` | Read the audit log |
| `user.manage` | Create users, assign roles and locations |
| `role.manage` | Edit roles and their permissions |
| `device.manage` | Enrol, suspend, revoke devices |
| `sync.manage` | Inspect and retry sync failures |
| `location.manage` | Create/edit locations and their settings |
| `location.all` | Act across every location (scope bypass) |
| `settings.manage` | Organization settings, thresholds, policies |

---

## 3. Role matrix

`✓` granted · `S` granted but scoped to assigned locations · `—` not granted

| Permission group | Owner | Administrator | Main Inventory Mgr | Store Manager | Inventory Staff | Cashier | Auditor |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| `product.view` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `product.create` / `edit` / `barcode.manage` | ✓ | ✓ | ✓ | — | — | — | — |
| `product.price.manage` | ✓ | ✓ | — | — | — | — | — |
| `product.cost.view` | ✓ | ✓ | ✓ | S | — | — | ✓ |
| `product.disable` | ✓ | ✓ | ✓ | — | — | — | — |
| `supplier.manage` | ✓ | ✓ | ✓ | — | — | — | — |
| `purchase.create` | ✓ | ✓ | ✓ | — | — | — | — |
| `purchase.approve` | ✓ | ✓ | ✓ (tier 2) | — | — | — | — |
| `purchase.receive` | ✓ | ✓ | ✓ | S | S | — | — |
| `purchase.direct_to_store.authorize` | ✓ | ✓ | ✓ | — | — | — | — |
| `inventory.view` | ✓ | ✓ | ✓ | S | S | S | ✓ |
| `inventory.view.all` | ✓ | ✓ | ✓ | — | — | — | ✓ |
| `inventory.receive` | ✓ | ✓ | ✓ | S | S | — | — |
| `inventory.adjust` | ✓ | ✓ | ✓ | S | S | — | — |
| `inventory.adjust.approve` | ✓ | ✓ | ✓ (tier 3) | S (tier 1) | — | — | — |
| `inventory.count` | ✓ | ✓ | ✓ | S | S | — | — |
| `inventory.count.approve` | ✓ | ✓ | ✓ | S (tier 1) | — | — | — |
| `inventory.negative_stock` | ✓ | — | — | — | — | — | — |
| `inventory.movement.view` | ✓ | ✓ | ✓ | S | S | — | ✓ |
| `transfer.request` | ✓ | ✓ | ✓ | S | S | — | — |
| `transfer.approve` | ✓ | ✓ | ✓ | — | — | — | — |
| `transfer.pick` / `dispatch` | ✓ | ✓ | ✓ | S | S | — | — |
| `transfer.receive` / `verify` | ✓ | ✓ | ✓ | S | S | — | — |
| `transfer.reconcile` | ✓ | ✓ | ✓ | — | — | — | — |
| `transfer.emergency` | ✓ | ✓ | ✓ | S | — | — | — |
| `transfer.preapproval.issue` | ✓ | ✓ | ✓ | — | — | — | — |
| `quarantine.create` | ✓ | ✓ | ✓ | S | S | S | — |
| `quarantine.investigate` | ✓ | ✓ | ✓ | S | — | — | — |
| `quarantine.release` / `reject` | ✓ | ✓ | ✓ | — | — | — | — |
| `receipt.create` | ✓ | ✓ | ✓ | S | — | — | — |
| `receipt.view` | ✓ | ✓ | ✓ | S | — | — | ✓ |
| `sale.create` | ✓ | — | — | S | — | S | — |
| `sale.discount` / `price_override` | ✓ | — | — | S | — | — | — |
| `sale.void` | ✓ | — | — | S | — | — | — |
| `sale.return` | ✓ | — | — | S | — | S | — |
| `sale.return_blind` / `refund` | ✓ | — | — | S | — | — | — |
| `sale.reprint` | ✓ | — | — | S | — | — | — |
| `sale.expired_override` | ✓ | — | — | S | — | — | — |
| `shift.open` / `shift.close` | ✓ | — | — | S | — | S | — |
| `shift.close.other` | ✓ | — | — | S | — | — | — |
| `cashdrawer.open_without_sale` | ✓ | — | — | S | — | — | — |
| `report.view` | ✓ | ✓ | ✓ | S | S | — | ✓ |
| `report.view.financial` | ✓ | ✓ | ✓ | S | — | — | ✓ |
| `audit.view` | ✓ | ✓ | — | — | — | — | ✓ |
| `user.manage` / `role.manage` | ✓ | ✓ | — | — | — | — | — |
| `device.manage` | ✓ | ✓ | ✓ | — | — | — | — |
| `sync.manage` | ✓ | ✓ | ✓ | — | — | — | — |
| `location.manage` / `settings.manage` | ✓ | ✓ | — | — | — | — | — |
| `location.all` | ✓ | ✓ | ✓ | — | — | — | ✓ |

**Auditor is strictly read-only.** The role holds no permission whose code
implies mutation, and an architecture test asserts that the Auditor role's
permission set is a subset of the declared read-only permission list.

**Cashier** cannot approve transfers, cannot adjust stock, cannot see cost or
margin, and cannot void or refund without a manager override.

**Store Manager** cannot create products, cannot change prices, cannot approve
transfers, and cannot approve adjustments above tier 1 — and every permission
they do hold is confined to their assigned store.

---

## 4. Approval value tiers

Configured per organization, overridable per location:

| Tier | Default ceiling (PHP) | Held by |
|---|---|---|
| Tier 1 | 5,000.00 | Store Manager |
| Tier 2 | 50,000.00 | Main Inventory Manager |
| Tier 3 | 250,000.00 | Administrator |
| Unlimited | — | Owner |

`IApprovalGate.RequireAsync(action, value, actor)` resolves the actor's highest
tier and rejects anything above it with `ApprovalTierExceeded`, naming the tier
required. Tiers apply to stock adjustments, count variances, transfer write-offs,
purchase approvals, and refunds.

**Self-approval** is refused when the approver is the document's creator and the
value exceeds `SelfApprovalLimit` (default 0, i.e. never). Segregation of duties
for transfers (`receive` vs `verify`) is a location setting, on by default at the
Main Warehouse.

---

## 5. Offline permission handling

Devices cache a signed snapshot:

```json
{
  "userId": "...", "deviceId": "...", "locationId": "...",
  "policyVersion": 412,
  "permissions": ["sale.create", "sale.return", "shift.open", "shift.close",
                  "product.view", "inventory.view", "quarantine.create",
                  "transfer.request", "transfer.receive"],
  "issuedAtUtc": "...", "expiresAtUtc": "...",
  "signature": "..."
}
```

Rules:

- Only permissions marked `IsOfflineCapable` appear in a snapshot. Approval-class
  permissions (`*.approve`, `product.create`, `product.price.manage`,
  `quarantine.release`, `user.manage`, …) are **never** offline-capable.
- Snapshots expire (default 72 h). Past expiry the device drops to a
  read-only + emergency mode and shows a prominent banner.
- A snapshot is an upper bound: the server re-checks at sync time and takes the
  **intersection** of the snapshot and the user's live permissions.
- `policyVersion` bumps on any role/permission/user change; the next successful
  pull refreshes affected snapshots, and the change feed can push an immediate
  invalidation while the device is online.
