using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A counterparty the business buys from. Suppliers are organisation-wide master
/// data held in the product master (see <c>product_supplier</c> links).
/// </summary>
public sealed class Supplier : AggregateRoot<SupplierId>
{
    /// <summary>Maximum length of a supplier code or name.</summary>
    public const int CodeMaxLength = 16;

    /// <summary>Maximum length of a supplier name.</summary>
    public const int NameMaxLength = 128;

    private Supplier(
        SupplierId id,
        string code,
        string name,
        string? taxId,
        int paymentTermsDays,
        int leadTimeDays)
    {
        Id = id;
        Code = code;
        Name = name;
        TaxId = taxId;
        PaymentTermsDays = paymentTermsDays;
        LeadTimeDays = leadTimeDays;
        IsActive = true;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Supplier()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Gets the short unique code.</summary>
    public string Code { get; private set; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the tax / VAT registration number, if any.</summary>
    public string? TaxId { get; private set; }

    /// <summary>Gets the agreed payment term in days.</summary>
    public int PaymentTermsDays { get; private set; }

    /// <summary>Gets the typical order-to-delivery lead time in days.</summary>
    public int LeadTimeDays { get; private set; }

    /// <summary>Gets whether this supplier may still be linked to new products.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Creates a supplier.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="taxId">Tax registration number, or <see langword="null"/>.</param>
    /// <param name="paymentTermsDays">Payment terms in days.</param>
    /// <param name="leadTimeDays">Typical lead time in days.</param>
    /// <returns>The new supplier, or a validation failure.</returns>
    public static Result<Supplier> Create(
        string? code,
        string? name,
        string? taxId,
        int paymentTermsDays,
        int leadTimeDays)
    {
        string normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalisedCode.Length == 0 || normalisedCode.Length > CodeMaxLength)
        {
            return Result<Supplier>.Failure(Error.Validation(
                "supplier.code_invalid",
                FormattableString.Invariant(
                    $"A supplier code is required and may be at most {CodeMaxLength} characters.")));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result<Supplier>.Failure(Error.Validation(
                "supplier.name_invalid",
                FormattableString.Invariant(
                    $"A supplier name is required and may be at most {NameMaxLength} characters.")));
        }

        if (paymentTermsDays < 0 || paymentTermsDays > 365)
        {
            return Result<Supplier>.Failure(Error.Validation(
                "supplier.payment_terms_invalid",
                "Payment terms must be between 0 and 365 days."));
        }

        if (leadTimeDays < 0 || leadTimeDays > 365)
        {
            return Result<Supplier>.Failure(Error.Validation(
                "supplier.lead_time_invalid",
                "Lead time must be between 0 and 365 days."));
        }

        return Result<Supplier>.Success(new Supplier(
            SupplierId.New(),
            normalisedCode,
            name.Trim(),
            string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim(),
            paymentTermsDays,
            leadTimeDays));
    }
}