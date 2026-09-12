using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// A received lot of a batch-tracked product. Created by the first goods
/// receipt of a (product, lot) pair and reused by every later receipt of the
/// same lot; the batch identifier then travels with every subsequent movement
/// of those goods, which is what makes recall and FEFO exact rather than
/// estimated.
/// </summary>
public sealed class Batch : AggregateRoot<BatchId>
{
    /// <summary>Maximum length of a lot number.</summary>
    public const int LotNumberMaxLength = 64;

    private Batch(
        BatchId id,
        ProductId productId,
        SupplierId supplierId,
        string lotNumber,
        DateOnly receivedOn,
        DateOnly? manufacturedOn,
        DateOnly? expiresOn,
        decimal unitCost,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ProductId = productId;
        SupplierId = supplierId;
        LotNumber = lotNumber;
        ReceivedOn = receivedOn;
        ManufacturedOn = manufacturedOn;
        ExpiresOn = expiresOn;
        UnitCost = unitCost;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Batch()
    {
        LotNumber = string.Empty;
    }

    /// <summary>Gets the product the lot belongs to.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the supplier the lot was received from.</summary>
    public SupplierId SupplierId { get; private set; }

    /// <summary>Gets the supplier's lot number for this delivery.</summary>
    public string LotNumber { get; private set; }

    /// <summary>Gets the business date the lot was received on.</summary>
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>Gets the manufacture date, where recorded.</summary>
    public DateOnly? ManufacturedOn { get; private set; }

    /// <summary>Gets the expiry date, where the product tracks expiry.</summary>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>Gets the unit cost recorded at first receipt.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the user who recorded the receipt that created the batch.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets when the batch row was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a batch from a goods receipt line. The batch materialises with
    /// the cost of the delivery that created it and is never silently rewritten
    /// by later receipts of the same lot.
    /// </summary>
    /// <param name="productId">The received product.</param>
    /// <param name="supplierId">The supplier the goods came from.</param>
    /// <param name="lotNumber">The supplier's lot number.</param>
    /// <param name="receivedOn">The business date of the receipt.</param>
    /// <param name="manufacturedOn">The manufacture date, if supplied.</param>
    /// <param name="expiresOn">The expiry date, if the product tracks expiry.</param>
    /// <param name="unitCost">The actual unit cost recorded on the receipt.</param>
    /// <param name="createdByUserId">Who recorded the receipt.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The batch, or a validation failure.</returns>
    public static Result<Batch> Create(
        ProductId productId,
        SupplierId supplierId,
        string? lotNumber,
        DateOnly receivedOn,
        DateOnly? manufacturedOn,
        DateOnly? expiresOn,
        decimal unitCost,
        UserId createdByUserId,
        DateTimeOffset now)
    {
        if (productId.IsEmpty)
        {
            return Result<Batch>.Failure(Error.Validation(
                "batch.product_required",
                "A batch must reference a product."));
        }

        if (supplierId.IsEmpty)
        {
            return Result<Batch>.Failure(Error.Validation(
                "batch.supplier_required",
                "A batch must reference the supplier the goods came from."));
        }

        string lot = lotNumber?.Trim() ?? string.Empty;

        if (lot.Length == 0 || lot.Length > LotNumberMaxLength)
        {
            return Result<Batch>.Failure(Error.Validation(
                "batch.lot_required",
                FormattableString.Invariant(
                    $"A batch must carry a lot number of at most {LotNumberMaxLength} characters.")));
        }

        if (unitCost < 0m)
        {
            return Result<Batch>.Failure(Error.Validation(
                "batch.cost_negative",
                "A batch's unit cost cannot be negative."));
        }

        if (manufacturedOn is { } manufactured && expiresOn is { } expires && expires < manufactured)
        {
            return Result<Batch>.Failure(Error.Validation(
                "batch.expiry_before_manufacture",
                "A batch cannot expire before it was manufactured."));
        }

        return Result<Batch>.Success(new Batch(
            BatchId.New(),
            productId,
            supplierId,
            lot,
            receivedOn,
            manufacturedOn,
            expiresOn,
            decimal.Round(unitCost, Money.StorageScale, Money.IntermediateRounding),
            createdByUserId,
            now));
    }
}