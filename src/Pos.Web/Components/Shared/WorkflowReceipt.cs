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
    IReadOnlyList<WorkflowReceiptLine> Lines);

/// <summary>One product row on a printable workflow receipt.</summary>
public sealed record WorkflowReceiptLine(
    string ProductName,
    string? ProductCode,
    IReadOnlyList<string> Quantities);
