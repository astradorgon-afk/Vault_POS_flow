namespace Pos.Domain.Sales;

/// <summary>The lifecycle state of a sale document.</summary>
public enum SaleStatus
{
    /// <summary>The sale is complete: paid in full, items posted to the ledger.</summary>
    Completed = 1,

    /// <summary>The sale was voided in full and its ledger effect reversed.</summary>
    Voided = 2,
}