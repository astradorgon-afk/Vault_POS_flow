using System.Text.Json;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// A sale the register completed while head office could not be reached. It is
/// the register's own record of what it printed and what it took, kept so the
/// receipt can be shown and reprinted and so the drawer can be accounted for.
/// </summary>
/// <remarks>
/// <para>
/// It is not the sale. The sale is the <see cref="SyncEventType.SaleCompleted"/>
/// event queued in the same transaction, which head office replays through the
/// shared sale use case; that is where prices, VAT and stock allocation are
/// settled (OFFLINE_SYNC.md §7). This row only remembers what the cashier and the
/// customer saw at the counter.
/// </para>
/// <para>
/// Device-owned and append-only: the change feed never writes it and nothing
/// rewrites one.
/// </para>
/// </remarks>
public sealed class DeviceLocalSale
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DeviceLocalSale()
    {
        Number = string.Empty;
        CashierName = string.Empty;
        Currency = string.Empty;
        LinesJson = "[]";
        PaymentsJson = "[]";
    }

    internal DeviceLocalSale(
        EventId eventId,
        string number,
        LocationId locationId,
        CashierShiftId shiftId,
        DeviceId deviceId,
        UserId cashierUserId,
        string cashierName,
        Guid? customerId,
        string? customerName,
        DateOnly businessDate,
        DateTimeOffset completedAtUtc,
        decimal netTotal,
        string currency,
        IReadOnlyList<DeviceLocalSaleLine> lines,
        IReadOnlyList<DeviceLocalSalePayment> payments)
    {
        EventId = eventId;
        Number = number;
        LocationId = locationId;
        ShiftId = shiftId;
        DeviceId = deviceId;
        CashierUserId = cashierUserId;
        CashierName = cashierName;
        CustomerId = customerId;
        CustomerName = customerName;
        BusinessDate = businessDate;
        CompletedAtUtc = completedAtUtc;
        NetTotal = netTotal;
        Currency = currency;
        LinesJson = JsonSerializer.Serialize(lines, Json);
        PaymentsJson = JsonSerializer.Serialize(payments, Json);
    }

    /// <summary>Gets the event the sale travels to head office as; it is also the sale's idempotency key.</summary>
    public EventId EventId { get; private init; }

    /// <summary>Gets the SAL number printed on the receipt, allocated by this register.</summary>
    public string Number { get; private init; }

    /// <summary>Gets the store.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets the shift the sale was rung up in.</summary>
    public CashierShiftId ShiftId { get; private init; }

    /// <summary>Gets this register.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets the cashier.</summary>
    public UserId CashierUserId { get; private init; }

    /// <summary>Gets the cashier's display name, as printed.</summary>
    public string CashierName { get; private init; }

    /// <summary>Gets the customer the sale was attached to, if any.</summary>
    public Guid? CustomerId { get; private init; }

    /// <summary>Gets the customer's display name, as printed.</summary>
    public string? CustomerName { get; private init; }

    /// <summary>Gets the business date the sale belongs to.</summary>
    public DateOnly BusinessDate { get; private init; }

    /// <summary>Gets the device clock when the sale completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; private init; }

    /// <summary>Gets the amount the customer paid for the goods.</summary>
    public decimal NetTotal { get; private init; }

    /// <summary>Gets the currency of the amounts.</summary>
    public string Currency { get; private init; }

    /// <summary>Gets the lines as stored JSON.</summary>
    public string LinesJson { get; private init; }

    /// <summary>Gets the payments as stored JSON.</summary>
    public string PaymentsJson { get; private init; }

    /// <summary>Reads the lines back.</summary>
    /// <returns>The lines, in the order they were rung up.</returns>
    public IReadOnlyList<DeviceLocalSaleLine> ReadLines()
        => JsonSerializer.Deserialize<List<DeviceLocalSaleLine>>(LinesJson, Json) ?? [];

    /// <summary>Reads the payments back.</summary>
    /// <returns>The payments, in the order they were taken.</returns>
    public IReadOnlyList<DeviceLocalSalePayment> ReadPayments()
        => JsonSerializer.Deserialize<List<DeviceLocalSalePayment>>(PaymentsJson, Json) ?? [];
}

/// <summary>One line of an offline sale, as it was rung up and printed.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="UnitOfMeasureId">The unit it was sold in.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="Name">Its name, as printed.</param>
/// <param name="Barcode">The scanned barcode, if any.</param>
/// <param name="Quantity">The quantity sold.</param>
/// <param name="UnitPrice">The cached price charged.</param>
/// <param name="LineTotal">Quantity times price.</param>
public sealed record DeviceLocalSaleLine(
    Guid ProductId,
    Guid UnitOfMeasureId,
    string Sku,
    string Name,
    string? Barcode,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>One payment on an offline sale.</summary>
/// <param name="Method">The rail. Offline, always cash.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">The cash handed over.</param>
/// <param name="Change">The change given.</param>
public sealed record DeviceLocalSalePayment(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    decimal Change);
