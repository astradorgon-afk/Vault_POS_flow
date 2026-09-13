using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Quarantine;

/// <summary>
/// Every error a quarantine incident can produce, keyed for stable machine
/// handling. Codes are part of the API contract and must never be renumbered.
/// </summary>
public static class QuarantineErrors
{
    /// <summary>An incident must name at least one line.</summary>
    public static Error EmptyIncident => Error.Validation(
        "quarantine.empty",
        "An incident must name at least one quarantined item.");

    /// <summary>The location holding the goods is mandatory.</summary>
    public static Error LocationRequired => Error.Validation(
        "quarantine.location_required",
        "An incident must name the location where the goods were found.");

    /// <summary>A line must carry the barcode that was scanned.</summary>
    public static Error BarcodeRequired(int lineNumber) => Error.Validation(
        "quarantine.barcode_required",
        $"Line {lineNumber} must carry the barcode that was scanned or found.");

    /// <summary>A line quantity must be positive.</summary>
    public static Error LineQuantityInvalid(int lineNumber) => Error.Validation(
        "quarantine.line_quantity_invalid",
        $"Line {lineNumber} carries a non-positive quantity.");

    /// <summary>A line cost cannot be negative.</summary>
    public static Error LineCostInvalid(int lineNumber) => Error.Validation(
        "quarantine.line_cost_invalid",
        $"Line {lineNumber} carries a negative recorded cost.");

    /// <summary>The incident does not exist.</summary>
    public static Error IncidentUnknown(QuarantineIncidentId incidentId) => Error.NotFound(
        "quarantine.unknown",
        $"No quarantine incident exists with identifier '{incidentId.Value}'.");

    /// <summary>The location named on the incident does not exist.</summary>
    public static Error LocationUnknown(LocationId locationId) => Error.NotFound(
        "quarantine.location_unknown",
        $"No location exists with identifier '{locationId.Value}'.");

    /// <summary>The incident's location has no timezone; its business date cannot be computed.</summary>
    public static Error TimeZoneMissing(LocationId locationId) => Error.Conflict(
        "quarantine.timezone_missing",
        $"Location '{locationId.Value}' has no timezone configured; its business date cannot be computed.");

    /// <summary>The product named on a disposition does not exist.</summary>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "quarantine.product_unknown",
        $"No product exists with identifier '{productId.Value}'.");

    /// <summary>A required external counterparty has not been provisioned.</summary>
    public static Error ExternalLocationMissing(string systemCode) => Error.Conflict(
        "quarantine.external_location_missing",
        $"The '{systemCode}' counterparty has not been provisioned; ask an administrator to seed it.");

    /// <summary>The barcode on the line does not belong to the product being linked.</summary>
    public static Error BarcodeNotOwned(int lineNumber) => Error.Validation(
        "quarantine.barcode_not_owned",
        $"Line {lineNumber} cannot be linked: the scanned barcode does not belong to the named product.");

    /// <summary>The caller is not assigned to the incident's location.</summary>
    public static Error IncidentOutsideScope(QuarantineIncidentId incidentId) => Error.Forbidden(
        "quarantine.outside_scope",
        $"Quarantine incident '{incidentId.Value}' belongs to a location outside your scope.");

    /// <summary>The operation is not valid once the incident is resolved.</summary>
    public static Error IncidentResolved => Error.Conflict(
        "quarantine.resolved",
        "The incident is already resolved; no further action is possible.");

    /// <summary>The requested line does not exist.</summary>
    public static Error LineUnknown(int lineNumber) => Error.Conflict(
        "quarantine.line_unknown",
        $"No line '{lineNumber}' exists on the incident.");

    /// <summary>The line was already identified against a product.</summary>
    public static Error LineAlreadyIdentified(int lineNumber) => Error.Conflict(
        "quarantine.line_already_identified",
        $"Line {lineNumber} is already identified against a product.");

    /// <summary>The line cannot be dispositioned before it is identified.</summary>
    public static Error LineNotIdentified(int lineNumber) => Error.Conflict(
        "quarantine.line_not_identified",
        $"Line {lineNumber} must be linked or registered to a product before it can be dispositioned.");

    /// <summary>The line has no quantity left to disposition.</summary>
    public static Error LineAlreadyDispositioned(int lineNumber) => Error.Conflict(
        "quarantine.line_already_dispositioned",
        $"Line {lineNumber} has already been fully dispositioned.");

    /// <summary>More than the remaining line quantity was requested.</summary>
    public static Error QuantityExceedsLine(int lineNumber, decimal requested, decimal remaining) => Error.Conflict(
        "quarantine.quantity_exceeds_line",
        $"Line {lineNumber} has only {remaining} units remaining; {requested} were requested.");

    /// <summary>The line must carry a lot even though it is not batch-tracked, or vice versa.</summary>
    public static Error BatchMismatch(int lineNumber) => Error.Validation(
        "quarantine.batch_mismatch",
        $"Line {lineNumber} names a batch for a product that does not track batches.");

    /// <summary>A batch-tracked product must carry a lot at identification.</summary>
    public static Error BatchRequired(int lineNumber) => Error.Validation(
        "quarantine.batch_required",
        $"Line {lineNumber} belongs to a batch-tracked product and must name a lot.");

    /// <summary>A photo must have a file name.</summary>
    public static Error PhotoFileNameRequired => Error.Validation(
        "quarantine.photo_file_name_required",
        "A photo must carry a file name.");

    /// <summary>A photo must have a content type.</summary>
    public static Error PhotoContentTypeRequired => Error.Validation(
        "quarantine.photo_content_type_required",
        "A photo must carry a content type.");

    /// <summary>A photo must contain bytes.</summary>
    public static Error PhotoDataRequired => Error.Validation(
        "quarantine.photo_data_required",
        "A photo must contain image data.");

    /// <summary>A photo exceeds the size limit.</summary>
    public static Error PhotoTooLarge(int maxBytes) => Error.Validation(
        "quarantine.photo_too_large",
        $"A photo cannot exceed {maxBytes} bytes.");

    /// <summary>The photo bytes are not valid base64.</summary>
    public static Error PhotoDataInvalid => Error.Validation(
        "quarantine.photo_data_invalid",
        "The photo payload is not valid base64-encoded image data.");

    /// <summary>The photograph does not exist on the incident.</summary>
    public static Error PhotoUnknown(QuarantinePhotoId photoId) => Error.NotFound(
        "quarantine.photo_unknown",
        $"No photograph exists with identifier '{photoId.Value}'.");

    /// <summary>The reason given for a write-off or rejection is required.</summary>
    public static Error ReasonRequired => Error.Validation(
        "quarantine.reason_required",
        "A disposition that moves goods out of quarantine must carry a reason.");

    /// <summary>An incident cannot be raised at an external counterparty location.</summary>
    public static Error IncidentLocationExternal => Error.Validation(
        "quarantine.location_external",
        "An incident must be raised at a store or warehouse, not at an external counterparty.");

    /// <summary>The named write-off reason is not accepted for quarantine shrinkage.</summary>
    public static Error WriteOffReasonNotAllowed(AdjustmentReasonCode reason) => Error.Validation(
        "quarantine.write_off_reason_not_allowed",
        $"'{reason}' is not an accepted write-off reason for quarantined goods.");
}