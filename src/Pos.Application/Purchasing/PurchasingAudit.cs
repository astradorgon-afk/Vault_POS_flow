using System.Text.Json;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Stages audit entries for the purchasing module.
/// </summary>
/// <remarks>
/// Buying is the one way stock enters the business from outside, and money
/// leaves it. Until these were written the activity log showed stock arriving at
/// the warehouse with no record of who ordered it, at what price, or who signed
/// for the delivery — while the log's own "Purchasing" filter sat there matching
/// a <c>purchase.</c> prefix nothing produced.
/// </remarks>
internal static class PurchasingAudit
{
    /// <summary>Stages one purchasing audit entry.</summary>
    /// <param name="audit">The writer.</param>
    /// <param name="action">The stable action code.</param>
    /// <param name="entityType">The affected record's type.</param>
    /// <param name="entityId">The affected record.</param>
    /// <param name="locationId">The location the action applies to.</param>
    /// <param name="details">What changed, serialized as the new value.</param>
    /// <param name="reason">The reason given, where one was required.</param>
    /// <param name="documentType">The related document type.</param>
    /// <param name="documentId">The related document, when it differs from the entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the entry is staged.</returns>
    internal static Task WriteAsync(
        IAuditWriter audit,
        string action,
        string entityType,
        Guid entityId,
        LocationId? locationId,
        object? details,
        string? reason,
        ReferenceDocumentType documentType,
        Guid? documentId,
        CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                entityType,
                entityId,
                NewValueJson: details is null ? null : JsonSerializer.Serialize(details),
                Reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ReferenceDocumentType: documentType,
                ReferenceDocumentId: documentId ?? entityId,
                LocationId: locationId),
            cancellationToken);

    /// <summary>Stages an entry about a purchase order.</summary>
    /// <param name="audit">The writer.</param>
    /// <param name="action">The stable action code.</param>
    /// <param name="order">The order.</param>
    /// <param name="details">What changed.</param>
    /// <param name="reason">The reason given, where one was required.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the entry is staged.</returns>
    internal static Task OrderAsync(
        IAuditWriter audit,
        string action,
        PurchaseOrder order,
        object? details,
        string? reason,
        CancellationToken cancellationToken)
        => WriteAsync(
            audit,
            action,
            nameof(PurchaseOrder),
            order.Id.Value,
            order.DestinationLocationId,
            details,
            reason,
            ReferenceDocumentType.PurchaseOrder,
            order.Id.Value,
            cancellationToken);

    /// <summary>The facts worth keeping about an order at the moment it changed.</summary>
    /// <param name="order">The order.</param>
    /// <returns>A small anonymous record for the entry's new value.</returns>
    internal static object OrderFacts(PurchaseOrder order) => new
    {
        order.Number,
        Status = order.Status.ToString(),
        SupplierId = order.SupplierId.Value,
        Lines = order.Lines.Count,
        Units = order.Lines.Sum(line => line.OrderedQuantity),
        order.GrandTotal,
        order.CurrencyCode,
    };
}
