namespace Pos.Web.Components.Shared;

/// <summary>A printable paper copy of a purchasing or inventory movement.</summary>
public sealed record WorkflowReceipt(
    string Title,
    string Number,
    string Status,
    string SourceHeading,
    string SourceName,
    string DestinationHeading,
    string DestinationName,
    string PrimaryDateHeading,
    string PrimaryDate,
    string? SecondaryDateHeading,
    string? SecondaryDate,
    IReadOnlyList<string> QuantityHeadings,
    IReadOnlyList<WorkflowReceiptLine> Lines,
    IReadOnlyList<WorkflowReceiptDetail>? Details = null);

/// <summary>
/// One labelled fact printed under a document's header — a DR number, a
/// supplier's reference, a release time — that has no column of its own.
/// </summary>
public sealed record WorkflowReceiptDetail(string Label, string Value);

/// <summary>One product row on a printable workflow receipt.</summary>
public sealed record WorkflowReceiptLine(
    string ProductName,
    string? ProductCode,
    IReadOnlyList<string> Quantities);
