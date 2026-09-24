using System.Globalization;
using System.Text.Json.Nodes;

namespace Pos.DemoData;

/// <summary>
/// Posts a month of trading at every store through the browser-register path:
/// one shift per store per day, completed sales with cash, card and e-wallet
/// payments, the odd void and return, and a closed shift with its cash count.
/// Sales carry their real business date and completion time, so reports,
/// dashboards and the sales ledger show history rather than one busy afternoon.
/// </summary>
internal sealed class SalesHistory(DemoWorld world, Random random)
{
    private const int Cash = 1;
    private const int Card = 2;
    private const int EWallet = 3;

    /// <summary>The notes a shopper pays with, smallest first.</summary>
    private static readonly decimal[] CashNotes = [20m, 50m, 100m, 200m, 500m, 1000m];

    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    /// <summary>How busy each store is on an ordinary day, and who works its till.
    /// The seed keeps one account per role, so the Store One cashier runs Store
    /// One and the owner covers the other two stores.</summary>
    private static readonly (string Store, int SalesPerDay, string Cashier)[] Stores =
    [
        ("STORE01", 22, "cashier"),
        ("STORE02", 16, "owner"),
        ("STORE03", 12, "owner"),
    ];

    /// <summary>What shoppers reach for, with a relative weight and the most
    /// units one basket usually holds.</summary>
    private static readonly (string Sku, int Weight, int MaxQuantity)[] Basket =
    [
        ("WATER-500", 10, 6), ("NOODLE-55", 10, 6), ("ICETEA-500", 7, 4), ("CHIPS-60", 7, 3), ("SODA-1L", 6, 3),
        ("MILK-370", 6, 4), ("TUNA-155", 6, 4), ("CBEEF-150", 5, 3), ("EGGS-DZ", 5, 2), ("BREAD-LOAF", 5, 2),
        ("PANDESAL-10", 5, 2), ("COFFEE-3IN1", 5, 2), ("CHOCBAR-40", 5, 3), ("SOAP-135", 4, 3), ("SUGAR-1K", 4, 2),
        ("RICE-01", 4, 1), ("OIL-1L", 4, 2), ("TSAUCE-250", 4, 3), ("SOY-1L", 3, 2), ("VINEGAR-1L", 3, 2),
        ("SALT-500", 3, 2), ("YOGURT-110", 3, 4), ("ENERGY-250", 3, 3), ("PEANUT-100", 3, 3), ("DISH-500", 3, 2),
        ("TISSUE-4", 3, 2), ("DETER-400", 3, 2), ("COFFEE-200", 2, 1), ("PASTA-1K", 2, 2), ("OJ-1L", 2, 2),
        ("CHOCO-1L", 2, 2), ("MILK-1L", 2, 2), ("CHEESE-165", 2, 1), ("BUTTER-225", 1, 1), ("COOKIES-200", 2, 2),
        ("BLEACH-1L", 2, 1), ("TRASH-10", 2, 1), ("SHAMPOO-340", 1, 1), ("TPASTE-150", 2, 1), ("ALCOHOL-500", 1, 1),
        ("HOTDOG-1K", 1, 1), ("NUGGETS-500", 1, 1), ("OATS-800", 1, 1),
    ];

    /// <summary>How the last posted sale was paid; a return is refunded the same way.</summary>
    private int _lastPaymentMethod;

    /// <summary>Products a store has run out of during this run; later baskets skip them.</summary>
    private readonly HashSet<(string Store, string Sku)> _soldOut = [];

    public int SalesPosted { get; private set; }

    public int Voids { get; private set; }

    public int Returns { get; private set; }

    public int Shifts { get; private set; }

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Manila).DateTime);

    /// <summary>Posts <paramref name="days"/> days of history ending today.</summary>
    public async Task RunAsync(int days)
    {
        for (int back = days; back >= 0; back--)
        {
            DateOnly businessDate = Today.AddDays(-back);
            int daySales = 0;

            foreach ((string store, int salesPerDay, string cashier) in Stores)
            {
                daySales += await TradeDayAsync(store, salesPerDay, cashier, businessDate, isToday: back == 0);
            }

            Console.WriteLine($"  {businessDate:ddd dd MMM}: {daySales} sales");
        }
    }

    private async Task<int> TradeDayAsync(string store, int salesPerDay, string cashierName, DateOnly businessDate, bool isToday)
    {
        Guid locationId = world.Locations[store];
        Guid deviceId = world.Registers[store];
        DemoSession cashier = world.Users[cashierName];

        // Weekends and paydays (the 15th and the last days of the month) are busier.
        double factor = businessDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.35
            : businessDate.DayOfWeek == DayOfWeek.Friday ? 1.15
            : 1.0;
        if (businessDate.Day is 15 or 16 or 30 or 31 or 1)
        {
            factor *= 1.2;
        }

        int count = (int)Math.Round(salesPerDay * factor * (0.8 + (random.NextDouble() * 0.4)));

        // A store-day that already has sales was posted by an earlier (possibly
        // interrupted) run; skipping it makes the tool safe to run again.
        if (await HasSalesAsync(locationId, businessDate))
        {
            return 0;
        }

        await CloseLeftoverShiftAsync(locationId, deviceId, cashier);
        List<DateTimeOffset> times = TradingTimes(businessDate, count, isToday);
        if (times.Count == 0)
        {
            return 0;
        }

        string shiftNumber = await world.Api.NextNumberAsync(locationId, deviceId, "SHF", cashier);
        Guid shiftId = await world.Api.CreateAsync(
            "/api/v1/shifts/open",
            new { number = shiftNumber, locationId, businessDate, openingFloat = 2000m },
            cashier,
            deviceId);
        Shifts++;

        int posted = 0;
        foreach (DateTimeOffset completedAt in times)
        {
            (Guid SaleId, List<(DemoProduct Product, decimal Quantity, decimal Price)> Lines)? sale =
                await SellAsync(store, locationId, deviceId, shiftId, cashier, businessDate, completedAt);

            if (sale is null)
            {
                continue;
            }

            posted++;

            // About one sale in seventy is voided at the till moments later;
            // about one cash sale in sixty comes back later that day as a
            // return, refunded in cash as it was paid. A cashier cannot void
            // or refund, so the store's supervisor does both on the cashier's
            // shift; the owner covering a store holds both rights.
            DemoSession supervisor = cashierName == "cashier" ? world.Users["manager"] : cashier;
            if (random.Next(70) == 0)
            {
                await VoidAsync(sale.Value.SaleId, locationId, deviceId, shiftId, supervisor, businessDate, completedAt);
            }
            else if (_lastPaymentMethod == Cash && random.Next(60) == 0)
            {
                await ReturnAsync(sale.Value, locationId, deviceId, shiftId, cashier, supervisor, businessDate, completedAt);
            }
        }

        await CloseShiftAsync(shiftId, locationId, cashier);
        return posted;
    }

    private async Task<bool> HasSalesAsync(Guid locationId, DateOnly businessDate)
    {
        JsonNode? sales = await world.Api.GetAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/sales?locationId={locationId:D}&from={businessDate:yyyy-MM-dd}&to={businessDate:yyyy-MM-dd}"),
            world.Owner);
        return DemoApi.Items(sales).Count > 0;
    }

    /// <summary>Closes a shift an interrupted run left open on the register, so
    /// the next day can open its own.</summary>
    private async Task CloseLeftoverShiftAsync(Guid locationId, Guid deviceId, DemoSession cashier)
    {
        JsonNode? session = await world.Api.GetWithDeviceAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/terminal/session?locationId={locationId:D}"),
            cashier,
            deviceId);

        if (session?["openShift"]?["shiftId"]?.GetValue<Guid>() is { } shiftId)
        {
            Console.WriteLine("    closing a shift left open by an earlier run");
            await CloseShiftAsync(shiftId, locationId, cashier);
        }
    }

    /// <summary>Spreads a day's sales across trading hours (8:00 to 21:00 Manila).
    /// Today's sales stop a few minutes before now.</summary>
    private List<DateTimeOffset> TradingTimes(DateOnly businessDate, int count, bool isToday)
    {
        DateTimeOffset open = new(businessDate.ToDateTime(new TimeOnly(8, 0)), Manila.BaseUtcOffset);
        DateTimeOffset close = new(businessDate.ToDateTime(new TimeOnly(21, 0)), Manila.BaseUtcOffset);
        if (isToday)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-5).ToOffset(Manila.BaseUtcOffset);
            close = cutoff < close ? cutoff : close;
            count = close <= open ? 0 : (int)Math.Ceiling(count * (close - open).TotalHours / 13.0);
        }

        if (count <= 0)
        {
            return [];
        }

        double span = (close - open).TotalSeconds;
        return [.. Enumerable.Range(0, count)
            .Select(_ => open.AddSeconds(random.NextDouble() * span).ToUniversalTime())
            .OrderBy(t => t)];
    }

    private async Task<(Guid SaleId, List<(DemoProduct Product, decimal Quantity, decimal Price)> Lines)?> SellAsync(
        string store, Guid locationId, Guid deviceId, Guid shiftId, DemoSession cashier, DateOnly businessDate, DateTimeOffset completedAt)
    {
        List<(DemoProduct Product, decimal Quantity, decimal Price)> lines = PickBasket(store, completedAt);

        // A basket can hit a product the store has just run out of; drop that
        // product for the rest of the run and try the basket again without it.
        for (int attempt = 0; attempt < 4 && lines.Count > 0; attempt++)
        {
            decimal total = lines.Sum(l => l.Quantity * l.Price);
            string number = await world.Api.NextNumberAsync(locationId, deviceId, "SAL", cashier);
            Guid? customerId = world.Customers.Count > 0 && random.Next(8) == 0
                ? world.Customers[random.Next(world.Customers.Count)]
                : null;

            try
            {
                Guid saleId = await world.Api.CreateAsync(
                    "/api/v1/sales",
                    new
                    {
                        number,
                        eventId = Guid.CreateVersion7(),
                        locationId,
                        cashierShiftId = shiftId,
                        deviceId,
                        customerId,
                        businessDate,
                        completedAtUtc = completedAt,
                        lines = lines.Select(l => new
                        {
                            productId = l.Product.Id,
                            quantity = l.Quantity,
                            unitOfMeasureId = l.Product.UnitId,
                            barcode = (string?)null,
                            unitPriceOverride = (decimal?)null,
                            priceOverrideAuthorizedByUserId = (Guid?)null,
                            discount = 0m,
                            discountAuthorizedByUserId = (Guid?)null,
                            allowExpiredOverride = false,
                            expiredOverrideReason = (string?)null,
                        }),
                        payments = new[] { Payment(total, out int method) },
                    },
                    cashier,
                    deviceId);

                SalesPosted++;
                _lastPaymentMethod = method;
                return (saleId, lines);
            }
            catch (DemoApiException ex) when (ex.Message.Contains("stock", StringComparison.OrdinalIgnoreCase)
                                             || ex.ErrorCode?.Contains("stock", StringComparison.OrdinalIgnoreCase) == true)
            {
                string? culprit = lines
                    .Select(l => l.Product)
                    .FirstOrDefault(p => ex.Message.Contains(p.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                                         || ex.Message.Contains(p.Sku, StringComparison.OrdinalIgnoreCase)
                                         || ex.Message.Contains(p.Name, StringComparison.OrdinalIgnoreCase))?.Sku;

                // Without a named product, the largest line is the likeliest to be short.
                culprit ??= lines.OrderByDescending(l => l.Quantity).First().Product.Sku;
                _soldOut.Add((store, culprit));
                lines.RemoveAll(l => l.Product.Sku == culprit);
            }
        }

        return null;
    }

    private List<(DemoProduct Product, decimal Quantity, decimal Price)> PickBasket(string store, DateTimeOffset at)
    {
        (string Sku, int Weight, int MaxQuantity)[] shelf = [.. Basket.Where(b =>
            world.Products.ContainsKey(b.Sku) && !_soldOut.Contains((store, b.Sku)))];
        if (shelf.Length == 0)
        {
            return [];
        }

        int items = random.Next(100) switch
        {
            < 35 => 1,
            < 65 => 2,
            < 85 => 3,
            < 95 => 4,
            _ => 5,
        };

        int totalWeight = shelf.Sum(s => s.Weight);
        Dictionary<string, (DemoProduct Product, decimal Quantity, decimal Price)> basket = [];

        for (int i = 0; i < items; i++)
        {
            int roll = random.Next(totalWeight);
            (string sku, _, int maxQuantity) = shelf.First(s => (roll -= s.Weight) < 0);
            if (basket.ContainsKey(sku))
            {
                continue;
            }

            DemoProduct product = world.Product(sku);
            if (product.PriceAt(at) is not { } price)
            {
                continue;
            }

            int quantity = maxQuantity == 1 ? 1 : 1 + (int)Math.Floor(Math.Pow(random.NextDouble(), 2) * maxQuantity);
            basket[sku] = (product, quantity, price);
        }

        return [.. basket.Values];
    }

    /// <summary>Most shoppers pay cash with a round note; the rest pay by card or e-wallet.</summary>
    private object Payment(decimal total, out int method)
    {
        int roll = random.Next(100);
        method = roll < 62 ? Cash : roll < 82 ? Card : EWallet;
        if (roll < 62)
        {
            decimal note = CashNotes.First(n => n >= total || n == 1000m);
            decimal tendered = total <= note ? note : Math.Ceiling(total / 1000m) * 1000m;
            return new { method = Cash, amount = total, tendered = (decimal?)tendered, providerReference = (string?)null };
        }

        return roll < 82
            ? new { method = Card, amount = total, tendered = (decimal?)null, providerReference = (string?)$"AUTH{random.Next(100000, 999999)}" }
            : new { method = EWallet, amount = total, tendered = (decimal?)null, providerReference = (string?)$"GC{random.NextInt64(1000000000, 9999999999)}" };
    }

    private async Task VoidAsync(
        Guid saleId, Guid locationId, Guid deviceId, Guid shiftId, DemoSession cashier, DateOnly businessDate, DateTimeOffset completedAt)
    {
        string[] reasons = ["Customer changed their mind at the counter.", "Scanned the wrong item.", "Duplicate transaction."];
        try
        {
            await world.Api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/sales/{saleId:D}/void"),
                new
                {
                    eventId = Guid.CreateVersion7(),
                    locationId,
                    shiftId,
                    deviceId,
                    businessDate,
                    voidedAtUtc = completedAt.AddMinutes(2),
                    reason = reasons[random.Next(reasons.Length)],
                },
                cashier,
                deviceId);
            Voids++;
        }
        catch (DemoApiException ex)
        {
            Console.WriteLine($"    void skipped: {ex.Message}");
        }
    }

    private async Task ReturnAsync(
        (Guid SaleId, List<(DemoProduct Product, decimal Quantity, decimal Price)> Lines) sale,
        Guid locationId, Guid deviceId, Guid shiftId, DemoSession cashier, DemoSession refunder, DateOnly businessDate,
        DateTimeOffset completedAt)
    {
        (DemoProduct product, _, decimal price) = sale.Lines[0];
        DateTimeOffset returnedAt = completedAt.AddMinutes(45);

        try
        {
            string number = await world.Api.NextNumberAsync(locationId, deviceId, "RET", cashier);
            Guid returnId = await world.Api.CreateAsync(
                "/api/v1/returns",
                new
                {
                    number,
                    eventId = Guid.CreateVersion7(),
                    saleId = sale.SaleId,
                    locationId,
                    shiftId,
                    deviceId,
                    customerId = (Guid?)null,
                    businessDate,
                    returnedAtUtc = returnedAt,
                    lines = new[] { new { productId = product.Id, quantity = 1m } },
                },
                cashier,
                deviceId);

            await world.Api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/returns/{returnId:D}/refund"),
                new
                {
                    saleId = sale.SaleId,
                    eventId = Guid.CreateVersion7(),
                    locationId,
                    shiftId,
                    deviceId,
                    method = Cash,
                    amount = price,
                    tendered = (decimal?)price,
                    providerReference = (string?)null,
                    refundedAtUtc = returnedAt.AddMinutes(1),
                },
                refunder,
                deviceId);
            Returns++;
        }
        catch (DemoApiException ex)
        {
            Console.WriteLine($"    return skipped: {ex.Message}");
        }
    }

    /// <summary>Closes the shift against its expected cash. Most drawers balance;
    /// about one in eight is a little short or over, as real counts are.</summary>
    private async Task CloseShiftAsync(Guid shiftId, Guid locationId, DemoSession cashier)
    {
        JsonNode? summary = await world.Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/summary"), cashier);
        decimal expected = summary?["expectedCash"]?.GetValue<decimal>() ?? 0m;
        decimal counted = random.Next(8) == 0 ? expected + (random.Next(-8, 5) * 5m) : expected;

        await world.Api.PostAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/close"),
            new { locationId, declaredCash = counted, countedCash = counted },
            cashier,
            world.Registers.First(r => world.Locations[r.Key] == locationId).Value);
    }
}
