namespace Pos.Web.Services;

/// <summary>The authenticated operator's durable notification feed.</summary>
public sealed class PosNotificationFeed
{
    /// <summary>Gets or sets the newest visible notifications.</summary>
    public IReadOnlyList<PosNotification> Items { get; set; } = [];

    /// <summary>Gets or sets the total unread count across the visible feed.</summary>
    public int UnreadCount { get; set; }
}

/// <summary>One operational notification delivered by HTTP or SignalR.</summary>
public sealed class PosNotification
{
    public Guid Id { get; set; }
    public int Kind { get; set; }
    public int Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public Guid? LocationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? BatchId { get; set; }
    public int? ReferenceDocumentType { get; set; }
    public Guid? ReferenceDocumentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

/// <summary>The result of marking the visible feed read.</summary>
public sealed class PosMarkedReadResult
{
    public int MarkedRead { get; set; }
}

/// <summary>A physical location available to the signed-in operator.</summary>
public sealed class PosLocation
{
    /// <summary>Gets or sets the location identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the short location code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the location name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the numeric location kind.</summary>
    public int Kind { get; set; }
}

/// <summary>One page of a store's sales and how many matched in all.</summary>
public sealed record PosSalePage(IReadOnlyList<PosSaleSummary> Sales, int Total);

/// <summary>One completed sale as returned by the sales search.</summary>
public sealed class PosSaleSummary
{
    /// <summary>Gets or sets the sale identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SAL document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the sale status (Completed, Voided).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the business date the sale belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the sale was completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Gets or sets the gross total before discounts.</summary>
    public decimal GrossTotal { get; set; }

    /// <summary>Gets or sets the net amount settled.</summary>
    public decimal NetTotal { get; set; }
}

/// <summary>A product/location pair repeatedly refused by the stock policy.</summary>
public sealed class PosNegativeStockSummary
{
    public Guid LocationId { get; set; }
    public string? LocationCode { get; set; }
    public Guid ProductId { get; set; }
    public string? Sku { get; set; }
    public string? ProductName { get; set; }
    public int Attempts { get; set; }
    public decimal TotalShortfall { get; set; }
}

/// <summary>A product/location pair with repeated physical-count variance.</summary>
public sealed class PosRepeatVariance
{
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    public string? Sku { get; set; }
    public string? ProductName { get; set; }
    public int Occurrences { get; set; }
    public decimal TotalAbsoluteVarianceValue { get; set; }
}

/// <summary>Scoped inventory availability totals for the owner dashboard.</summary>
public sealed class PosInventoryOverview
{
    public decimal AvailableQuantity { get; set; }
    public decimal InTransitQuantity { get; set; }
    public decimal QuarantineQuantity { get; set; }
    public int LowStockItems { get; set; }
    public int OutOfStockItems { get; set; }
    public int OverStockItems { get; set; }
}

/// <summary>An open synchronization failure requiring operator attention.</summary>
public sealed class PosSyncFailure
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>A completed sale as returned by the detail route.</summary>
public sealed class PosSaleDetail
{
    /// <summary>Gets or sets the sale identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SAL document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the sale status (Completed, Voided).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the store the sale was completed at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the customer the sale was settled for, or null.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the business date the sale belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the sale was completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Gets or sets the gross total before discounts.</summary>
    public decimal GrossTotal { get; set; }

    /// <summary>Gets or sets the applied discounts.</summary>
    public decimal DiscountTotal { get; set; }

    /// <summary>Gets or sets the net amount settled.</summary>
    public decimal NetTotal { get; set; }

    /// <summary>Gets or sets the VAT charged on the sale.</summary>
    public decimal VatTotal { get; set; }

    /// <summary>Gets or sets the sale lines, ordered by line number.</summary>
    public IReadOnlyList<PosSaleLineDetail> Lines { get; set; } = [];

    /// <summary>Gets or sets the payments that settled the sale.</summary>
    public IReadOnlyList<PosSalePaymentDetail> Payments { get; set; } = [];
}

/// <summary>One line of a completed sale.</summary>
public sealed class PosSaleLineDetail
{
    /// <summary>Gets or sets the line number on the sale.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the product identifier.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the scanned barcode, or null.</summary>
    public string? Barcode { get; set; }

    /// <summary>Gets or sets the quantity sold.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Gets or sets the selling unit price.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Gets or sets the applied line discount.</summary>
    public decimal Discount { get; set; }

    /// <summary>Gets or sets the gross line amount.</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>Gets or sets the net line amount after discount.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Gets or sets the VAT allocated to the line.</summary>
    public decimal Vat { get; set; }
}

/// <summary>One payment that settled a sale.</summary>
public sealed class PosSalePaymentDetail
{
    /// <summary>Gets or sets the payment method name.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the settled amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the cash tendered, or null.</summary>
    public decimal? Tendered { get; set; }

    /// <summary>Gets or sets the change returned, or null.</summary>
    public decimal? Change { get; set; }

    /// <summary>Gets or sets the card or wallet reference, or null.</summary>
    public string? ProviderReference { get; set; }
}

/// <summary>A customer return as returned by the detail route.</summary>
public sealed class PosReturnDetail
{
    /// <summary>Gets or sets the return identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the RET document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the return has no original sale.</summary>
    public bool IsBlind { get; set; }

    /// <summary>Gets or sets the original sale, or null for a blind return.</summary>
    public Guid? SaleId { get; set; }

    /// <summary>Gets or sets the store the return was accepted at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the customer, or null.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the business date the return belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the return was accepted.</summary>
    public DateTimeOffset ReturnedAtUtc { get; set; }

    /// <summary>Gets or sets the total still refundable.</summary>
    public decimal RefundableTotal { get; set; }

    /// <summary>Gets or sets the total already refunded.</summary>
    public decimal RefundedTotal { get; set; }

    /// <summary>Gets or sets the return lines, ordered by line number.</summary>
    public IReadOnlyList<PosReturnLineDetail> Lines { get; set; } = [];

    /// <summary>Gets or sets the refunds paid, ordered by time.</summary>
    public IReadOnlyList<PosReturnRefundDetail> Refunds { get; set; } = [];
}

/// <summary>One accepted return line.</summary>
public sealed class PosReturnLineDetail
{
    /// <summary>Gets or sets the line number on the return.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the original sale item, or null for a blind return.</summary>
    public Guid? SaleItemId { get; set; }

    /// <summary>Gets or sets the product identifier.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the scanned barcode, or null.</summary>
    public string? Barcode { get; set; }

    /// <summary>Gets or sets the accepted quantity.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Gets or sets the quantity inspected so far.</summary>
    public decimal DispositionedQuantity { get; set; }

    /// <summary>Gets or sets the quantity awaiting inspection.</summary>
    public decimal PendingDispositionQuantity { get; set; }

    /// <summary>Gets or sets the original selling unit price.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Gets or sets the net amount returned.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Gets or sets the amount still refundable on this line.</summary>
    public decimal RefundableAmount { get; set; }

    /// <summary>Gets or sets the batch returned, or null.</summary>
    public string? BatchCode { get; set; }

    /// <summary>Gets or sets the batch expiry, or null.</summary>
    public DateOnly? BatchExpiresOn { get; set; }
}

/// <summary>One refund paid against a return.</summary>
public sealed class PosReturnRefundDetail
{
    /// <summary>Gets or sets the refund identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the payment method name.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the refunded amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the cash tendered, or null.</summary>
    public decimal? Tendered { get; set; }

    /// <summary>Gets or sets the card or wallet reference, or null.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>Gets or sets when the refund was paid.</summary>
    public DateTimeOffset RefundedAtUtc { get; set; }
}

/// <summary>The body of a return-inspection decision. Needs no register context.</summary>
public sealed record PosDisposeReturnRequest(
    Guid EventId,
    Guid LocationId,
    int LineNumber,
    decimal Quantity,
    int Kind,
    int ReasonCode,
    string Note);

/// <summary>A server-issued reference to a created or updated document.</summary>
public sealed class PosReference
{
    /// <summary>Gets or sets the referenced document identifier.</summary>
    public Guid Id { get; set; }
}

/// <summary>An account as listed for administration.</summary>
public sealed record PosUserSummary(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    bool IsActive,
    bool TwoFactorEnabled,
    int ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<Guid> LocationIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

/// <summary>One location a user is assigned to.</summary>
public sealed record PosUserLocationSpec(Guid LocationId, bool IsPrimary);

/// <summary>A per-user permission override shown to an administrator.</summary>
public sealed record PosUserOverride(
    Guid Id,
    string PermissionCode,
    string Effect,
    Guid? LocationId,
    string Reason,
    DateTimeOffset GrantedAtUtc,
    Guid GrantedByUserId,
    DateTimeOffset? ExpiresAtUtc,
    bool IsActive);

/// <summary>An account with everything that shapes its authority.</summary>
public sealed record PosUserDetail(
    PosUserSummary User,
    IReadOnlyList<PosUserLocationSpec> Locations,
    IReadOnlyList<PosUserOverride> Overrides,
    IReadOnlyList<string> EffectivePermissions,
    bool HasPin,
    bool IsLockedOut,
    DateTimeOffset? DisabledAtUtc,
    string? DisabledReason);

/// <summary>The body creating an account.</summary>
public sealed record PosCreateUserRequest(
    string UserName,
    string DisplayName,
    string Password,
    string? Email,
    string? EmployeeCode,
    int ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<PosUserLocationSpec> Locations);

/// <summary>The body updating an account's details.</summary>
public sealed record PosUpdateUserRequest(
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    int ApprovalTier);

/// <summary>The roles an account should hold.</summary>
public sealed record PosUserRolesRequest(IReadOnlyList<string> Roles);

/// <summary>The locations an account should be assigned to.</summary>
public sealed record PosUserLocationsRequest(IReadOnlyList<PosUserLocationSpec> Locations);

/// <summary>An override granted to or withheld from one account.</summary>
public sealed record PosOverrideRequest(
    string PermissionCode,
    int Effect,
    string Reason,
    DateTimeOffset? ExpiresAtUtc = null,
    Guid? LocationId = null);

/// <summary>A new password set by an administrator.</summary>
public sealed record PosResetPasswordRequest(string NewPassword);

/// <summary>A cashier PIN set by an administrator.</summary>
public sealed record PosSetPinRequest(string Pin);

/// <summary>The permissions a role should bundle.</summary>
public sealed record PosRolePermissionsRequest(IReadOnlyList<string> Permissions, string Reason);

/// <summary>A reason recorded for an administration change.</summary>
public sealed record PosReasonRequest(string Reason);

/// <summary>A role as shown to an administrator.</summary>
public sealed record PosRoleView(
    Guid Id,
    string Name,
    string Description,
    bool IsSystemRole,
    IReadOnlyList<string> Permissions,
    int MemberCount);

/// <summary>A catalogue permission as shown to an administrator.</summary>
public sealed record PosPermissionView(
    string Code,
    string Module,
    string Description,
    bool IsOfflineCapable,
    bool IsReadOnly,
    bool IsPrivileged);

// ---- Devices ----

/// <summary>A registered terminal as shown in the fleet console.</summary>
public sealed record PosDeviceSummary(
    Guid Id,
    string ShortCode,
    string Name,
    Guid LocationId,
    int Platform,
    int Status,
    string? AppVersion,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    decimal? ClockSkewSeconds,
    string? StatusReason);

/// <summary>The body used to register a terminal.</summary>
public sealed record PosRegisterDeviceRequest(
    string ShortCode,
    string Name,
    Guid LocationId,
    int Platform);

/// <summary>The one-time result of registering or reissuing a terminal enrolment.</summary>
public sealed record PosDeviceRegistration(
    Guid DeviceId,
    string ShortCode,
    string? EnrolmentCode,
    DateTimeOffset? ExpiresAtUtc);

// ---- Locations ----

/// <summary>Operational policy values attached to one physical location.</summary>
public sealed record PosLocationSettings(
    int NegativeStockPolicy,
    bool AllowsDirectSupplierDelivery,
    TimeSpan OfflineGracePeriod,
    string ReceiptHeader,
    string ReceiptFooter,
    decimal VatRate,
    decimal CashRoundingIncrement,
    int ExpiryWarningDays,
    decimal CashVarianceThreshold,
    TimeSpan MaxShiftHours);

/// <summary>A location with its operating policy.</summary>
public sealed record PosLocationAdmin(
    Guid Id,
    string Code,
    string Name,
    int Kind,
    string TimeZoneId,
    bool IsActive,
    bool IsSystemCreated,
    DateOnly OpenedOn,
    DateOnly? ClosedOn,
    PosLocationSettings Settings);

/// <summary>The body used to add a physical location.</summary>
public sealed record PosCreateLocationRequest(
    string Code,
    string Name,
    int Kind,
    string TimeZoneId,
    PosLocationSettings? Settings = null);

// ---- Reports ----

/// <summary>The daily close-of-trade report for a store.</summary>
public sealed record PosDailySalesReport(
    Guid LocationId,
    string LocationName,
    DateOnly BusinessDate,
    PosDailySalesSummary SalesSummary,
    IReadOnlyList<PosDailyPaymentSummary> PaymentsByMethod,
    IReadOnlyList<PosDailyShiftSummary> Shifts);

/// <summary>Sales and refund totals for a business date.</summary>
public sealed record PosDailySalesSummary(
    int SalesCount,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    decimal VatTotal,
    decimal VatExemptTotal,
    decimal ZeroRatedTotal,
    decimal TaxableBaseTotal,
    decimal RefundTotal);

/// <summary>Collected value through one payment rail.</summary>
public sealed record PosDailyPaymentSummary(
    string Method,
    decimal Amount,
    decimal ChangeGiven);

/// <summary>One cashier shift's contribution to a daily report.</summary>
public sealed record PosDailyShiftSummary(
    Guid ShiftId,
    string ShiftNumber,
    Guid CashierUserId,
    string Status,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal OpeningFloat,
    int SalesCount,
    decimal NetTotal,
    decimal CashSalesTotal,
    decimal CashRefundsTotal);

// ---- Stock adjustments ----

/// <summary>One stock draw the ledger refused.</summary>
public sealed record PosNegativeStockAttemptView(
    Guid Id,
    DateTimeOffset AttemptedAtUtc,
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    Guid? BatchId,
    string State,
    string MovementType,
    decimal RequestedQuantity,
    decimal AvailableQuantity,
    decimal Shortfall,
    string Policy,
    string ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string ReferenceNumber,
    Guid UserId,
    Guid? DeviceId,
    Guid CorrelationId);

/// <summary>Refused draws for one product at one location, ranked by frequency.</summary>
public sealed record PosNegativeStockAttemptSummaryRow(
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    int Attempts,
    decimal TotalShortfall,
    DateTimeOffset FirstAttemptAtUtc,
    DateTimeOffset LastAttemptAtUtc);

/// <summary>Every store's sales and takings over a period of business dates.</summary>
public sealed record PosStorePerformanceReport(DateOnly From, DateOnly To, IReadOnlyList<PosStorePerformance> Stores);

/// <summary>What one store sold and took in: takings are net sales less refunds.</summary>
public sealed record PosStorePerformance(
    Guid LocationId,
    string Code,
    string Name,
    decimal NetSales,
    decimal GrossSales,
    decimal Discounts,
    int Transactions,
    decimal AverageTicket,
    int VoidedTransactions,
    decimal VoidedValue,
    decimal Refunds,
    decimal Takings,
    decimal CashSales,
    decimal CardSales,
    decimal EWalletSales,
    DateTimeOffset? LastSaleAtUtc,
    IReadOnlyList<PosStoreDailySales> Daily,
    IReadOnlyList<PosStoreTopProduct> TopProducts);

/// <summary>One business date's completed sales at a store.</summary>
public sealed record PosStoreDailySales(DateOnly Date, decimal NetSales, int Transactions, decimal GrossSales);

/// <summary>One of a store's best sellers over the period.</summary>
public sealed record PosStoreTopProduct(Guid ProductId, string Name, decimal Quantity, decimal NetSales);

/// <summary>A location's stock per product, scarcest first.</summary>
public sealed record PosStockLevelReport(
    Guid LocationId,
    string Code,
    string Name,
    int SalesRateDays,
    IReadOnlyList<PosStockLevel> Products);

/// <summary>One product's stock at a location; Status is Out, Low, Healthy or Over.</summary>
public sealed record PosStockLevel(
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    decimal Available,
    decimal InTransit,
    decimal OnHold,
    decimal StockValue,
    decimal? MinimumStock,
    decimal? ReorderPoint,
    decimal? TargetStock,
    decimal? MaximumStock,
    decimal DailySales,
    decimal? DaysOfCover,
    string Status);
