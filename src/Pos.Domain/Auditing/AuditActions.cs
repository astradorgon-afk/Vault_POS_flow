namespace Pos.Domain.Auditing;

/// <summary>
/// The stable action codes written to the audit log.
/// </summary>
/// <remarks>
/// Codes are part of the reporting contract: an auditor filters on them, and a
/// retention policy is written against them. Renaming one silently breaks every
/// saved query over historical data, so they are constants here rather than
/// free text at each call site.
/// </remarks>
public static class AuditActions
{
    /// <summary>Authentication and session actions.</summary>
    public static class Authentication
    {
        /// <summary>A user signed in successfully.</summary>
        public const string LoginSucceeded = "auth.login.succeeded";

        /// <summary>A sign-in attempt failed.</summary>
        public const string LoginFailed = "auth.login.failed";

        /// <summary>An account was locked out after repeated failures.</summary>
        public const string LockedOut = "auth.lockout";

        /// <summary>A user signed out.</summary>
        public const string Logout = "auth.logout";

        /// <summary>An access token was renewed.</summary>
        public const string TokenRefreshed = "auth.token.refreshed";

        /// <summary>An already-used refresh token was presented, so its family was revoked.</summary>
        public const string RefreshTokenReuseDetected = "auth.token.reuse_detected";

        /// <summary>Every token for a user or device was revoked.</summary>
        public const string TokensRevoked = "auth.token.revoked";

        /// <summary>A password was changed.</summary>
        public const string PasswordChanged = "auth.password.changed";

        /// <summary>A cashier PIN was set or changed.</summary>
        public const string PinChanged = "auth.pin.changed";

        /// <summary>A permission check refused an action.</summary>
        public const string PermissionDenied = "auth.permission.denied";

        /// <summary>A user enrolled an authenticator and turned on two-factor sign-in.</summary>
        public const string TwoFactorEnabled = "auth.two_factor.enabled";

        /// <summary>An administrator cleared a user's authenticator so they must enrol again.</summary>
        public const string TwoFactorReset = "auth.two_factor.reset";
    }

    /// <summary>User, role and permission administration.</summary>
    public static class Administration
    {
        /// <summary>A user was created.</summary>
        public const string UserCreated = "user.created";

        /// <summary>A user's details were changed.</summary>
        public const string UserUpdated = "user.updated";

        /// <summary>A user was disabled.</summary>
        public const string UserDisabled = "user.disabled";

        /// <summary>A user was re-enabled.</summary>
        public const string UserEnabled = "user.enabled";

        /// <summary>A user's roles changed.</summary>
        public const string UserRolesChanged = "user.roles.changed";

        /// <summary>A user's location assignments changed.</summary>
        public const string UserLocationsChanged = "user.locations.changed";

        /// <summary>A per-user permission override was granted, denied or removed.</summary>
        public const string UserOverrideChanged = "user.override.changed";

        /// <summary>A role's permissions changed.</summary>
        public const string RolePermissionsChanged = "role.permissions.changed";

        /// <summary>An organization or location setting changed.</summary>
        public const string SettingsChanged = "settings.changed";
    }

    /// <summary>Device lifecycle.</summary>
    public static class Devices
    {
        /// <summary>An enrolment code was issued.</summary>
        public const string EnrolmentCodeIssued = "device.enrolment_code.issued";

        /// <summary>A device completed enrolment.</summary>
        public const string Enrolled = "device.enrolled";

        /// <summary>A device was suspended.</summary>
        public const string Suspended = "device.suspended";

        /// <summary>A suspended device was reactivated.</summary>
        public const string Reactivated = "device.reactivated";

        /// <summary>A device was permanently revoked.</summary>
        public const string Revoked = "device.revoked";

        /// <summary>A device reported a clock far from the server's.</summary>
        public const string ClockSkewDetected = "device.clock_skew";
    }

    /// <summary>Catalog changes.</summary>
    public static class Catalog
    {
        /// <summary>A product was created.</summary>
        public const string ProductCreated = "product.created";

        /// <summary>A product was modified.</summary>
        public const string ProductUpdated = "product.updated";

        /// <summary>A product was deactivated or reactivated.</summary>
        public const string ProductActivationChanged = "product.activation.changed";

        /// <summary>A barcode was attached or retired.</summary>
        public const string BarcodeChanged = "product.barcode.changed";

        /// <summary>A selling price was superseded.</summary>
        public const string PriceChanged = "product.price.changed";

        /// <summary>A purchase cost was changed.</summary>
        public const string CostChanged = "product.cost.changed";
    }

    /// <summary>Inventory actions.</summary>
    public static class Inventory
    {
        /// <summary>A movement group was posted to the ledger.</summary>
        public const string MovementPosted = "inventory.movement.posted";

        /// <summary>A posting was refused because it would drive stock negative.</summary>
        public const string NegativeStockAttempted = "inventory.negative_stock.attempted";

        /// <summary>An adjustment was requested.</summary>
        public const string AdjustmentCreated = "inventory.adjustment.created";

        /// <summary>An adjustment was approved.</summary>
        public const string AdjustmentApproved = "inventory.adjustment.approved";

        /// <summary>An adjustment was rejected.</summary>
        public const string AdjustmentRejected = "inventory.adjustment.rejected";

        /// <summary>A posted adjustment was reversed.</summary>
        public const string AdjustmentReversed = "inventory.adjustment.reversed";

        /// <summary>A count was opened and its sheet taken from the ledger.</summary>
        public const string CountOpened = "inventory.count.opened";

        /// <summary>A count was submitted for approval.</summary>
        public const string CountSubmitted = "inventory.count.submitted";

        /// <summary>A count variance was approved and posted.</summary>
        public const string CountApproved = "inventory.count.approved";

        /// <summary>A submitted count was sent back for recounting.</summary>
        public const string CountRejected = "inventory.count.rejected";

        /// <summary>A count was abandoned without posting.</summary>
        public const string CountCancelled = "inventory.count.cancelled";

        /// <summary>A product varied again on a count at the same location within the look-back window.</summary>
        public const string RepeatVarianceDetected = "inventory.count.repeat_variance";

        /// <summary>The balance projection was rebuilt from the ledger.</summary>
        public const string BalancesRebuilt = "inventory.balances.rebuilt";

        /// <summary>Reconciliation found the projection disagreeing with the ledger.</summary>
        public const string IntegrityDriftDetected = "inventory.integrity.drift";
    }

    /// <summary>Purchasing actions.</summary>
    public static class Purchasing
    {
        /// <summary>A purchase order was created.</summary>
        public const string OrderCreated = "purchase.order.created";

        /// <summary>A purchase order was approved.</summary>
        public const string OrderApproved = "purchase.order.approved";

        /// <summary>A purchase order was rejected or cancelled.</summary>
        public const string OrderRejected = "purchase.order.rejected";

        /// <summary>Goods were received.</summary>
        public const string GoodsReceived = "purchase.goods.received";

        /// <summary>A receiving discrepancy was raised.</summary>
        public const string DiscrepancyRaised = "purchase.discrepancy.raised";

        /// <summary>A supplier delivered directly to a store.</summary>
        public const string DirectToStoreDelivery = "purchase.direct_to_store";
    }

    /// <summary>Transfer actions.</summary>
    public static class Transfers
    {
        /// <summary>A transfer was requested.</summary>
        public const string Requested = "transfer.requested";

        /// <summary>A transfer was approved.</summary>
        public const string Approved = "transfer.approved";

        /// <summary>A transfer was rejected.</summary>
        public const string Rejected = "transfer.rejected";

        /// <summary>A transfer was dispatched.</summary>
        public const string Dispatched = "transfer.dispatched";

        /// <summary>A transfer was received.</summary>
        public const string Received = "transfer.received";

        /// <summary>A transfer discrepancy was raised.</summary>
        public const string DiscrepancyRaised = "transfer.discrepancy.raised";

        /// <summary>A transfer discrepancy was resolved.</summary>
        public const string DiscrepancyResolved = "transfer.discrepancy.resolved";

        /// <summary>An emergency offline transfer was created.</summary>
        public const string EmergencyCreated = "transfer.emergency.created";

        /// <summary>An emergency transfer was reviewed centrally.</summary>
        public const string EmergencyReviewed = "transfer.emergency.reviewed";
    }

    /// <summary>Quarantine actions.</summary>
    public static class Quarantine
    {
        /// <summary>An incident was raised.</summary>
        public const string IncidentRaised = "quarantine.incident.raised";

        /// <summary>Quantity was released to available stock.</summary>
        public const string Released = "quarantine.released";

        /// <summary>Goods were rejected or returned to the supplier.</summary>
        public const string Rejected = "quarantine.rejected";
    }

    /// <summary>Expiry run actions.</summary>
    public static class Expiry
    {
        /// <summary>An expiry run quarantined past-expiry batches at a location.</summary>
        public const string ExpiryRunPosted = "expiry.run.posted";

        /// <summary>An expiry run found nothing to process.</summary>
        public const string ExpiryRunClean = "expiry.run.clean";

        /// <summary>A sale was blocked because the batch was expired.</summary>
        public const string SaleBlockedExpired = "sale.blocked.expired";
    }

    /// <summary>Point-of-sale actions.</summary>
    public static class Sales
    {
        /// <summary>A sale was completed.</summary>
        public const string SaleCompleted = "sale.completed";

        /// <summary>A sale was voided.</summary>
        public const string SaleVoided = "sale.voided";

        /// <summary>A customer return accepted goods back.</summary>
        public const string ReturnCreated = "sale.return.created";

        /// <summary>Returned goods were inspected and routed out of ReturnPending.</summary>
        public const string ReturnDispositioned = "sale.return.dispositioned";

        /// <summary>A blind return accepted goods back without their original sale (exception record).</summary>
        public const string BlindReturnAccepted = "sale.return.blind.accepted";

        /// <summary>A refund was issued.</summary>
        public const string RefundIssued = "sale.refund.issued";

        /// <summary>A manual discount was applied.</summary>
        public const string DiscountOverride = "sale.discount.override";

        /// <summary>A unit price was overridden.</summary>
        public const string PriceOverride = "sale.price.override";

        /// <summary>An expired batch was sold through the exception path.</summary>
        public const string ExpiredOverride = "sale.expired.override";

        /// <summary>A receipt was reprinted.</summary>
        public const string ReceiptReprinted = "sale.receipt.reprinted";

        /// <summary>A shift was opened.</summary>
        public const string ShiftOpened = "shift.opened";

        /// <summary>A shift was closed.</summary>
        public const string ShiftClosed = "shift.closed";

        /// <summary>A shift was suspended while the cashier stepped away.</summary>
        public const string ShiftSuspended = "shift.suspended";

        /// <summary>A suspended shift was resumed.</summary>
        public const string ShiftResumed = "shift.resumed";

        /// <summary>A closed shift was reconciled by a manager.</summary>
        public const string ShiftReconciled = "shift.reconciled";

        /// <summary>A worker force-closed a shift left open past its maximum hours.</summary>
        public const string ShiftForceClosed = "shift.force_closed";

        /// <summary>The cash drawer was opened without a sale.</summary>
        public const string CashDrawerOpened = "cashdrawer.opened";
    }

    /// <summary>Synchronization actions.</summary>
    public static class Sync
    {
        /// <summary>An uploaded event was rejected.</summary>
        public const string EventRejected = "sync.event.rejected";

        /// <summary>An uploaded event conflicted with server state.</summary>
        public const string EventConflict = "sync.event.conflict";

        /// <summary>An event identifier was reused with different content.</summary>
        public const string IdempotencyKeyReuse = "sync.idempotency.reuse";

        /// <summary>An event was parked for human review.</summary>
        public const string RequiresReview = "sync.event.requires_review";
    }
}
