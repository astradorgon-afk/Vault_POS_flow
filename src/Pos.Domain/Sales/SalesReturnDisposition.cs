using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Sales;

/// <summary>The inspection decision for returned goods.</summary>
public enum ReturnDispositionKind
{
    /// <summary>Return saleable goods to the shelf.</summary>
    Restock = 1,
    /// <summary>Hold goods for head office review.</summary>
    Quarantine = 2,
    /// <summary>Hold damaged goods outside sellable stock.</summary>
    Damaged = 3,
    /// <summary>Stage goods in Damaged for a separate supplier return.</summary>
    SupplierReturn = 4,
    /// <summary>Write goods off to the waste counterparty.</summary>
    Waste = 5,
}

/// <summary>An immutable inspection decision, keyed by its retry-safe event identifier.</summary>
public sealed class SalesReturnDisposition : Entity<EventId>
{
    private SalesReturnDisposition() { }

    internal SalesReturnDisposition(EventId eventId, SalesReturnId returnId, SalesReturnItemId itemId,
        decimal quantity, ReturnDispositionKind kind, AdjustmentReasonCode reasonCode, string note,
        UserId actor, DateTimeOffset occurredAtUtc)
    {
        Id = eventId;
        SalesReturnId = returnId;
        SalesReturnItemId = itemId;
        Quantity = quantity;
        Kind = kind;
        ReasonCode = reasonCode;
        Note = note;
        CreatedByUserId = actor;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>Gets the return inspected.</summary>
    public SalesReturnId SalesReturnId { get; private set; }
    /// <summary>Gets the line inspected.</summary>
    public SalesReturnItemId SalesReturnItemId { get; private set; }
    /// <summary>Gets the quantity routed by this decision.</summary>
    public decimal Quantity { get; private set; }
    /// <summary>Gets the inspection decision.</summary>
    public ReturnDispositionKind Kind { get; private set; }
    /// <summary>Gets the ledger reason.</summary>
    public AdjustmentReasonCode ReasonCode { get; private set; }
    /// <summary>Gets the inspection explanation.</summary>
    public string Note { get; private set; } = string.Empty;
    /// <summary>Gets the authenticated inspector.</summary>
    public UserId CreatedByUserId { get; private set; }
    /// <summary>Gets the server time of inspection.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }
}

/// <summary>Stable failures for return inspection.</summary>
public static class ReturnDispositionErrors
{
    /// <summary>The return does not exist.</summary>
    public static readonly Error ReturnUnknown = Error.NotFound("sale.return.disposition.return_unknown", "The return does not exist.");
    /// <summary>The request names a different location from the return.</summary>
    public static readonly Error LocationMismatch = Error.Conflict("sale.return.disposition.location_mismatch", "The return belongs to a different location.");
    /// <summary>The request is incomplete or malformed.</summary>
    public static readonly Error Invalid = Error.Validation("sale.return.disposition.invalid", "Provide a valid event, line, quantity, disposition, reason and inspection note (up to 512 characters).");
    /// <summary>The return line does not exist.</summary>
    public static readonly Error LineUnknown = Error.NotFound("sale.return.disposition.line_unknown", "The return line does not exist.");
    /// <summary>The quantity exceeds this line's remaining goods.</summary>
    public static readonly Error QuantityExceeded = Error.Conflict("sale.return.disposition.quantity_exceeded", "The quantity exceeds the return line's pending quantity.");
    /// <summary>Expired stock cannot be restocked.</summary>
    public static readonly Error Expired = Error.Conflict("sale.return.disposition.expired", "An expired batch cannot be returned to sellable stock.");
    /// <summary>The event was previously used for another operation.</summary>
    public static readonly Error EventConflict = Error.Conflict("sale.return.disposition.event_conflict", "The event identifier has already been used for a different operation.");
    /// <summary>Another inspector changed the line while this decision was being saved.</summary>
    public static readonly Error Contention = new("sale.return.disposition.contention",
        "Another request changed the return line. Reload its pending quantity and retry.", ErrorType.ConcurrencyConflict);
}
