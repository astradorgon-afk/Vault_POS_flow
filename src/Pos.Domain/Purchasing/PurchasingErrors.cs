using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Purchasing;

/// <summary>The purchasing module's error catalogue. Codes are part of the API contract.</summary>
public static class PurchasingErrors
{
    /// <summary>A referenced purchase order does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error OrderUnknown(PurchaseOrderId id) => Error.NotFound(
        "purchasing.order_unknown",
        FormattableString.Invariant($"Purchase order {id.Value} was not found."));

    /// <summary>An action required a purchase order in one state, but it was in another.</summary>
    /// <param name="expected">The state the action requires.</param>
    /// <param name="actual">The order's current state.</param>
    /// <returns>The error.</returns>
    public static Error InvalidState(PurchaseOrderStatus expected, PurchaseOrderStatus actual) => Error.Conflict(
        "purchasing.invalid_state",
        FormattableString.Invariant(
            $"A purchase order {ExpectedPhrase([expected])}; this one is {actual}."),
        new Dictionary<string, object?>
        {
            ["expectedStatus"] = expected,
            ["actualStatus"] = actual,
        });

    /// <summary>An action allowed several states, and the order was in none of them.</summary>
    /// <param name="expected">The states the action requires.</param>
    /// <param name="actual">The order's current state.</param>
    /// <returns>The error.</returns>
    public static Error InvalidState(IReadOnlyCollection<PurchaseOrderStatus> expected, PurchaseOrderStatus actual)
        => Error.Conflict(
            "purchasing.invalid_state",
            FormattableString.Invariant(
                $"A purchase order {ExpectedPhrase(expected)}; this one is {actual}."),
            new Dictionary<string, object?>
            {
                ["expectedStatuses"] = expected.Select(s => s.ToString()).ToArray(),
                ["actualStatus"] = actual,
            });

    /// <summary>An order must carry a purchase order identifier.</summary>
    /// <returns>The error.</returns>
    public static Error OrderIdRequired => Error.Validation(
        "purchasing.order_required",
        "A purchase order identifier must be specified.");

    /// <summary>An order must name a supplier.</summary>
    /// <returns>The error.</returns>
    public static Error SupplierRequired => Error.Validation(
        "purchasing.supplier_required",
        "A supplier must be specified.");

    /// <summary>An order must name a destination location.</summary>
    /// <returns>The error.</returns>
    public static Error DestinationRequired => Error.Validation(
        "purchasing.destination_required",
        "A destination location must be specified.");

    /// <summary>The currency code is not a valid ISO-4217 code.</summary>
    /// <returns>The error.</returns>
    public static Error CurrencyInvalid => Error.Validation(
        "purchasing.currency_invalid",
        "Currency must be a three-letter ISO-4217 code such as PHP.");

    /// <summary>An order must carry at least one line.</summary>
    /// <returns>The error.</returns>
    public static Error EmptyOrder => Error.Validation(
        "purchasing.empty_order",
        "A purchase order must have at least one line.");

    /// <summary>A line quantity must be positive, in the storage unit.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error InvalidQuantity(int lineNo) => Error.Validation(
        "purchasing.line_quantity_invalid",
        FormattableString.Invariant($"Line {lineNo}: ordered quantity must be greater than zero."));

    /// <summary>A line unit cost must not be negative.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error InvalidUnitCost(int lineNo) => Error.Validation(
        "purchasing.line_unit_cost_invalid",
        FormattableString.Invariant($"Line {lineNo}: unit cost must not be negative."));

    /// <summary>The same product appeared on more than one line.</summary>
    /// <param name="productId">The duplicated product.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateProductLine(ProductId productId) => Error.Validation(
        "purchasing.duplicate_product_line",
        FormattableString.Invariant($"Product {productId.Value} appears on more than one line."),
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A referenced supplier does not exist or is inactive.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error SupplierUnknown(SupplierId id) => Error.NotFound(
        "purchasing.supplier_unknown",
        FormattableString.Invariant($"Supplier {id.Value} was not found or is inactive."));

    /// <summary>An order with no managed supplier must name a new one.</summary>
    /// <returns>The error.</returns>
    public static Error CustomSupplierNameRequired => Error.Validation(
        "purchasing.custom_supplier_required",
        "Choose a managed supplier or type the name of the new supplier.");

    /// <summary>A referenced product does not exist or is inactive.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId id) => Error.NotFound(
        "purchasing.product_unknown",
        FormattableString.Invariant($"Product {id.Value} was not found or is inactive."));

    /// <summary>A line is bought in a unit the product does not count itself in.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error LineUnitMismatch(ProductId productId, int lineNo) => Error.Validation(
        "purchasing.line_uom_mismatch",
        FormattableString.Invariant(
            $"Line {lineNo}: product {productId.Value} cannot be bought in the requested unit of measure."));

    /// <summary>The destination location does not exist or is an external counterparty.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error DestinationUnknown(LocationId id) => Error.NotFound(
        "purchasing.destination_unknown",
        FormattableString.Invariant($"Location {id.Value} was not found or cannot receive goods."));

    /// <summary>Closing an order that has not been fully received needs a reason.</summary>
    /// <returns>The error.</returns>
    public static Error CloseReasonRequired => Error.Validation(
        "purchasing.close_reason_required",
        "Closing a purchase order that has not been fully received requires a reason.");

    /// <summary>Cancelling an order that has already been submitted needs a reason.</summary>
    /// <returns>The error.</returns>
    public static Error CancelReasonRequired => Error.Validation(
        "purchasing.cancel_reason_required",
        "Cancelling a submitted purchase order requires a reason.");

    /// <summary>The order has a number, so it can no longer be withdrawn as a draft.</summary>
    /// <returns>The error.</returns>
    public static Error NumberedCannotBeWithdrawn => Error.Conflict(
        "purchasing.numbered_cannot_be_withdrawn",
        "A purchase order that has been submitted cannot be withdrawn; cancel it instead.");

    /// <summary>A referenced goods receipt does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptUnknown(GoodsReceiptId id) => Error.NotFound(
        "purchasing.receipt_unknown",
        FormattableString.Invariant($"Goods receipt {id.Value} was not found."));

    /// <summary>A receipt must physically receive something: a delivery of nothing
    /// is not a delivery.</summary>
    /// <returns>The error.</returns>
    public static Error NothingReceived => Error.Validation(
        "purchasing.receipt_nothing_received",
        "A goods receipt must record received quantity on at least one line.");

    /// <summary>A receipt line references a line that is not on the purchase order.</summary>
    /// <param name="purchaseOrderLineId">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptLineUnknown(PurchaseOrderLineId purchaseOrderLineId) => Error.Validation(
        "purchasing.receipt_unknown_line",
        FormattableString.Invariant(
            $"Purchase order line {purchaseOrderLineId.Value} is not on this order."));

    /// <summary>The same purchase order line appeared on more than one receipt line.</summary>
    /// <param name="purchaseOrderLineId">The duplicated line.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateReceiptLine(PurchaseOrderLineId purchaseOrderLineId) => Error.Validation(
        "purchasing.receipt_duplicate_line",
        FormattableString.Invariant(
            $"Purchase order line {purchaseOrderLineId.Value} appears on more than one goods receipt line."));

    /// <summary>A line carried a negative received or rejected quantity.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptNegativeQuantity(int lineNo) => Error.Validation(
        "purchasing.receipt_negative_quantity",
        FormattableString.Invariant($"Receipt line {lineNo}: received and rejected quantities cannot be negative."));

    /// <summary>A line rejected more units than it received.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptRejectedExceedsReceived(int lineNo) => Error.Validation(
        "purchasing.receipt_rejected_exceeds_received",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: rejected quantity cannot exceed the received quantity."));

    /// <summary>A batch-tracked product was received without a lot number.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptLotRequired(int lineNo) => Error.Validation(
        "purchasing.receipt_batch_required",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: a batch-tracked product must be received with a lot number."));

    /// <summary>A lot number was supplied for a product that is not batch-tracked.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptLotNotAllowed(int lineNo) => Error.Validation(
        "purchasing.receipt_lot_not_allowed",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: a lot number was supplied for a product that is not batch-tracked."));

    /// <summary>An expiry-tracked product was received without an expiry date.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptExpiryRequired(int lineNo) => Error.Validation(
        "purchasing.receipt_expires_on_required",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: an expiry-tracked product must be received with an expiry date."));

    /// <summary>
    /// The goods were already past their expiry date on arrival. None of the
    /// lot may be accepted: every counted unit must be recorded as refused so
    /// the delivery posts to quarantine.
    /// </summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <param name="expiresOn">The date carried by the delivery.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptExpiredOnArrival(int lineNo, DateOnly expiresOn) => Error.Validation(
        "purchasing.receipt_expired_on_arrival",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: the lot expired on {expiresOn:yyyy-MM-dd}, which has already passed. Every received unit must be recorded as refused (expired on arrival) so the goods post to quarantine."),
        new Dictionary<string, object?> { ["expiresOn"] = expiresOn });

    /// <summary>A receipt line carried an expiry date before its manufacture date.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptExpiryBeforeManufacture(int lineNo) => Error.Validation(
        "purchasing.receipt_expiry_before_manufacture",
        FormattableString.Invariant(
            $"Receipt line {lineNo}: a lot cannot expire before it was manufactured."));

    /// <summary>The system's supplier counterparty location is missing.</summary>
    /// <returns>The error.</returns>
    public static Error ExternalSupplierLocationMissing => Error.Unavailable(
        "purchasing.receipt_external_supplier_missing",
        "The EXT-SUPPLIER counterparty location has not been provisioned; receipts cannot be posted.");

    /// <summary>The receiving location carries no timezone, so its business date cannot be computed.</summary>
    /// <param name="locationId">The receiving location.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptLocationTimeZoneMissing(LocationId locationId) => Error.Unavailable(
        "purchasing.receipt_location_time_zone_missing",
        FormattableString.Invariant(
            $"Receiving location {locationId.Value} has no timezone configured; receipts cannot be posted there."));

    /// <summary>A receipt line's unit cost deviated from the purchase order by more
    /// than the tolerance, and the receiving user does not hold sufficient
    /// purchase-approval authority to approve the variance.</summary>
    /// <param name="lineNo">The offending receipt line.</param>
    /// <param name="poUnitCost">The purchase order unit cost.</param>
    /// <param name="receiptUnitCost">The received unit cost.</param>
    /// <param name="variancePercent">The deviation as a percentage.</param>
    /// <param name="tolerancePercent">The tolerance applied.</param>
    /// <returns>The error.</returns>
    public static Error CostVarianceRequiresApproval(
        int lineNo,
        decimal poUnitCost,
        decimal receiptUnitCost,
        decimal variancePercent,
        decimal tolerancePercent) => Error.ApprovalRequired(
        "purchasing.cost_variance_requires_approval",
        FormattableString.Invariant(
            $"Receipt line {lineNo} carries a unit cost of {receiptUnitCost} against a purchase order cost of {poUnitCost} ({variancePercent:0.##}% variance, tolerance {tolerancePercent:0.##}%). Posting it requires purchase approval authority covering the received value."),
        new Dictionary<string, object?>
        {
            ["purchaseOrderLineNo"] = lineNo,
            ["poUnitCost"] = poUnitCost,
            ["receiptUnitCost"] = receiptUnitCost,
            ["variancePercent"] = variancePercent,
            ["tolerancePercent"] = tolerancePercent,
        });

private static string ExpectedPhrase(IReadOnlyCollection<PurchaseOrderStatus> expected)
        => expected.Count switch
        {
            1 => $"must be {expected.First()}",
            _ => FormattableString.Invariant($"must be one of {string.Join(", ", expected.Select(s => s.ToString()))}"),
        };

    // ---------------------------------------------------------------- Direct delivery authorizations

    /// <summary>A referenced direct-delivery authorization does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error AuthorizationUnknown(DirectDeliveryAuthorizationId id) => Error.NotFound(
        "purchasing.direct_delivery_unknown",
        FormattableString.Invariant($"Direct-delivery authorization {id.Value} was not found."));

    /// <summary>The destination must be a store: direct delivery to a warehouse
    /// is the normal path and needs no authorization.</summary>
    /// <param name="id">The location that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error AuthorizationStoreLocationRequired(LocationId id) => Error.Validation(
        "purchasing.direct_delivery_store_required",
        FormattableString.Invariant(
            $"Location {id.Value} is not a store; a direct-delivery authorization can only name a store."));

    /// <summary>The authorization window must not be inverted.</summary>
    /// <param name="validFrom">The first day.</param>
    /// <param name="validUntil">The last day.</param>
    /// <returns>The error.</returns>
    public static Error InvalidAuthorizationWindow(DateOnly validFrom, DateOnly validUntil) => Error.Validation(
        "purchasing.direct_delivery_invalid_window",
        FormattableString.Invariant(
            $"The authorization window {validFrom:yyyy-MM-dd}..{validUntil:yyyy-MM-dd} is inverted; the first day must not be after the last day."));

    /// <summary>A value cap must be a positive amount when one is given.</summary>
    /// <returns>The error.</returns>
    public static Error AuthorizationValueCapInvalid => Error.Validation(
        "purchasing.direct_delivery_value_cap_invalid",
        "A value cap must be greater than zero.");

    /// <summary>An authorization that has been revoked cannot be revoked again.</summary>
    /// <param name="id">The already-revoked authorization.</param>
    /// <returns>The error.</returns>
    public static Error AuthorizationAlreadyRevoked(DirectDeliveryAuthorizationId id) => Error.Conflict(
        "purchasing.direct_delivery_already_revoked",
        FormattableString.Invariant($"Direct-delivery authorization {id.Value} has already been revoked."));

    // ---------------------------------------------------------------- Supplier returns

    /// <summary>A referenced supplier return does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ReturnUnknown(SupplierReturnId id) => Error.NotFound(
        "purchasing.return_unknown",
        FormattableString.Invariant($"Supplier return {id.Value} was not found."));

    /// <summary>An action required a return in one state, but it was in another.</summary>
    /// <param name="expected">The state the action requires.</param>
    /// <param name="actual">The return's current state.</param>
    /// <returns>The error.</returns>
    public static Error InvalidReturnState(SupplierReturnStatus expected, SupplierReturnStatus actual) => Error.Conflict(
        "purchasing.return_invalid_state",
        FormattableString.Invariant(
            $"A supplier return must be {expected}; this one is {actual}."),
        new Dictionary<string, object?>
        {
            ["expectedStatus"] = expected,
            ["actualStatus"] = actual,
        });

    /// <summary>A return must carry at least one line.</summary>
    /// <returns>The error.</returns>
    public static Error EmptyReturn => Error.Validation(
        "purchasing.return_empty",
        "A supplier return must have at least one line.");

    /// <summary>A line quantity must be positive, in the storage unit.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnQuantityInvalid(int lineNo) => Error.Validation(
        "purchasing.return_quantity_invalid",
        FormattableString.Invariant($"Line {lineNo}: returned quantity must be greater than zero."));

    /// <summary>A line unit cost must not be negative.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnUnitCostInvalid(int lineNo) => Error.Validation(
        "purchasing.return_unit_cost_invalid",
        FormattableString.Invariant($"Line {lineNo}: unit cost must not be negative."));

    /// <summary>
    /// A return sources stock only from Damaged, Expired or Quarantine; it can
    /// never quietly remove sellable stock from Available.
    /// </summary>
    /// <param name="lineNo">The offending line.</param>
    /// <param name="state">The forbidden source state.</param>
    /// <returns>The error.</returns>
    public static Error ReturnSourceStateNotReturnable(int lineNo, InventoryState state) => Error.Validation(
        "purchasing.return_source_state_invalid",
        FormattableString.Invariant(
            $"Line {lineNo}: stock in {state} cannot be returned to the supplier; a return sources only Damaged, Expired or Quarantine stock."));

    /// <summary>A batch-tracked product must name the lot being returned.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnBatchRequired(int lineNo) => Error.Validation(
        "purchasing.return_batch_required",
        FormattableString.Invariant(
            $"Line {lineNo}: a batch-tracked product must name the lot being returned."));

    /// <summary>A lot number was supplied for a product that is not batch-tracked.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnBatchNotAllowed(int lineNo) => Error.Validation(
        "purchasing.return_batch_not_allowed",
        FormattableString.Invariant(
            $"Line {lineNo}: a lot number was supplied for a product that is not batch-tracked."));

    /// <summary>The same product, lot and source state appeared on more than one line.</summary>
    /// <param name="lineNo">The duplicated line.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateReturnLine(int lineNo) => Error.Validation(
        "purchasing.return_duplicate_line",
        FormattableString.Invariant(
            $"Line {lineNo}: the same product, lot and source state appears on more than one line."));

    /// <summary>The 'Other' reason needs an explanation on the line.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnOtherReasonNotesRequired(int lineNo) => Error.Validation(
        "purchasing.return_other_reason_notes_required",
        FormattableString.Invariant($"Line {lineNo}: the 'Other' reason requires explanatory notes."));

    /// <summary>Dispatching a return requires the supplier's return-authorization number.</summary>
    /// <returns>The error.</returns>
    public static Error ReturnSupplierAuthorizationRequired => Error.Validation(
        "purchasing.return_authorization_number_required",
        "Dispatching a supplier return requires the supplier's return-authorization number.");

    /// <summary>A return already carries its SRT number, so it cannot be numbered again.</summary>
    /// <param name="id">The numbered return.</param>
    /// <returns>The error.</returns>
    public static Error ReturnAlreadyNumbered(SupplierReturnId id) => Error.Conflict(
        "purchasing.return_already_numbered",
        FormattableString.Invariant($"Supplier return {id.Value} already carries a number."));

    /// <summary>A line named a lot that belongs to a different product.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error ReturnBatchProductMismatch(int lineNo) => Error.Validation(
        "purchasing.return_batch_product_mismatch",
        FormattableString.Invariant(
            $"Line {lineNo}: the named lot belongs to a different product than the line."));

    /// <summary>A line named a lot that does not exist.</summary>
    /// <param name="batchId">The lot that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ReturnBatchUnknown(BatchId batchId) => Error.NotFound(
        "purchasing.return_batch_unknown",
        FormattableString.Invariant($"Lot {batchId.Value} was not found."));

    /// <summary>The return location does not exist or cannot hold stock.</summary>
    /// <param name="id">The location that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ReturnLocationUnknown(LocationId id) => Error.NotFound(
        "purchasing.return_location_unknown",
        FormattableString.Invariant(
            $"Location {id.Value} was not found or cannot hold stock for a return."));

    // ---------------------------------------------------------------- Receiving discrepancies

    /// <summary>The system's external supplier counterparty is missing, so a dispatch cannot post.</summary>
    /// <returns>The error.</returns>
    public static Error ReturnExternalSupplierLocationMissing => Error.Unavailable(
        "purchasing.return_external_supplier_missing",
        "The EXT-SUPPLIER counterparty location has not been provisioned; supplier returns cannot be dispatched.");

    /// <summary>The return location carries no timezone, so its business date cannot be computed.</summary>
    /// <param name="locationId">The return location.</param>
    /// <returns>The error.</returns>
    public static Error ReturnLocationTimeZoneMissing(LocationId locationId) => Error.Unavailable(
        "purchasing.return_location_time_zone_missing",
        FormattableString.Invariant(
            $"Return location {locationId.Value} has no timezone configured; supplier returns cannot be dispatched there."));

    /// <summary>A referenced receiving discrepancy does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error DiscrepancyUnknown(ReceivingDiscrepancyId id) => Error.NotFound(
        "purchasing.discrepancy_unknown",
        FormattableString.Invariant($"Receiving discrepancy {id.Value} was not found."));

    /// <summary>A discrepancy can only be resolved once.</summary>
    /// <param name="id">The already-resolved discrepancy.</param>
    /// <returns>The error.</returns>
    public static Error DiscrepancyAlreadyResolved(ReceivingDiscrepancyId id) => Error.Conflict(
        "purchasing.discrepancy_already_resolved",
        FormattableString.Invariant($"Receiving discrepancy {id.Value} has already been resolved."));

    /// <summary>The resolution outcome must be one of the known decisions.</summary>
    /// <returns>The error.</returns>
    public static Error DiscrepancyResolutionOutcomeInvalid => Error.Validation(
        "purchasing.discrepancy_resolution_outcome_invalid",
        "The resolution outcome is not a known decision.");

    /// <summary>Discrepancies on a receipt that is not posted cannot be resolved.</summary>
    /// <param name="receiptId">The receipt holding the discrepancy.</param>
    /// <returns>The error.</returns>
    public static Error ReceiptNotPostedForResolution(GoodsReceiptId receiptId) => Error.Conflict(
        "purchasing.receipt_not_posted_for_resolution",
        FormattableString.Invariant(
            $"Goods receipt {receiptId.Value} is not posted, so its discrepancies cannot be resolved."));

    /// <summary>The resolving user does not hold discrepancy-resolution authority
    /// at the receipt's location.</summary>
    /// <param name="locationId">The receipt's destination.</param>
    /// <returns>The error.</returns>
    public static Error DiscrepancyResolveRequiresPermission(LocationId locationId) => Error.Forbidden(
        "purchasing.discrepancy_resolve_forbidden",
        FormattableString.Invariant(
            $"Resolving a discrepancy at location {locationId.Value} requires purchase discrepancy-resolution authority there."));
}