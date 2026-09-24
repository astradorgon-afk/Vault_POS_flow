using System.Globalization;
using System.Text.Json.Nodes;

namespace Pos.DemoData;

/// <summary>
/// Posts a month of trading at every store the way a store register does:
/// the store's cashier signs in at the enrolled till, opens one shift per day,
/// completes sales with cash, card and e-wallet payments under numbers the
/// register allocates itself, the store supervisor voids and refunds the odd
/// sale, and the cashier closes the shift with a cash count. Sales carry their
/// real business date and completion time, so the web dashboard, reports and
/// sales ledger show a month of history rather than one busy afternoon.
/// </summary>
internal sealed class SalesHistory(DemoWorld world, Random random)
{
    private const int Cash = 1;
    private const int Card = 2;
    private const int EWallet = 3;

    /// <summary>The notes a shopper pays with, smallest first.</summary>
    private static readonly decimal[] CashNotes = [20m, 50m, 100m, 200m, 500m, 1000m];

    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    /// <summary>How busy each store is on an ordinary day.</summary>
    private static readonly (string Store, int SalesPerDay)[] Stores =
    [
        ("STORE01", 22),
        ("STORE02", 16),
        ("STORE03", 12),
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

    /// <summary>The last number each register allocated, per document type. A
    /// register numbers its own documents (offline, it cannot ask head office).</summary>
    private readonly Dictionary<(string ShortCode, string Type), int> _sequences = [];

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

            foreach ((string store, int salesPerDay) in Stores)
            {
                daySales += await TradeDayAsync(world.Registers[store], salesPerDay, businessDate, isToday: back == 0);
            }

            Console.WriteLine($"  {businessDate:ddd dd MMM}: {daySales} sales");
        }
    }

    private async Task<int> TradeDayAsync(DemoRegister register, int salesPerDay, DateOnly businessDate, bool isToday)
    {
        Guid locationId = register.LocationId;
        DemoSession cashier = register.Cashier;

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

        await CloseLeftoverShiftAsync(register);
        List<DateTimeOffset> times = TradingTimes(businessDate, count, isToday);
        if (times.Count == 0)
        {
            return 0;
        }

        Guid shiftId = await WithNextNumberAsync(register, "SHF", businessDate.Year, number => world.Api.CreateAsync(
            "/api/v1/shifts/open",
            new { number, locationId, businessDate, openingFloat = 2000m },
            cashier));
        Shifts++;

        int posted = 0;
        foreach (DateTimeOffset completedAt in times)
        {
            (Guid SaleId, List<(DemoProduct Product, decimal Quantity, decimal Price)> Lines)? sale =
                await SellAsync(register, shiftId, businessDate, completedAt);

            if (sale is null)
            {
                continue;
            }

            posted++;

            // About one sale in seventy is voided at the till moments later;
            // about one cash sale in sixty comes back later that day as a
            // return, refunded in cash as it was paid. A cashier cannot void
            // or refund, so the store's supervisor does both at the register,
            // on the cashier's shift.
            if (random.Next(70) == 0)
            {
                await VoidAsync(register, sale.Value.SaleId, shiftId, businessDate, completedAt);
            }
            else if (_lastPaymentMethod == Cash && random.Next(60) == 0)
            {
                await ReturnAsync(register, sale.Value, shiftId, businessDate, completedAt);
            }
        }

        await CloseShiftAsync(register, shiftId);
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
    private async Task CloseLeftoverShiftAsync(DemoRegister register)
    {
        JsonNode? session = await world.Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/terminal/session?locationId={register.LocationId:D}"),
            register.Cashier);

        if (session?["openShift"]?["shiftId"]?.GetValue<Guid>() is { } shiftId)
        {
            Console.WriteLine($"    closing a shift left open on {register.ShortCode} by an earlier run");
            await CloseShiftAsync(register, shiftId);
        }
    }

    /// <summary>
    /// Allocates the register's next number for a document type and posts with
    /// it, as an offline register does: SHF numbers have four digits, SAL and RET
    /// six. The sale counter starts after the highest sale head office already
    /// holds for the register; if a number turns out to be taken (a rerun after
    /// an interruption), the register moves on to the next one.
    /// </summary>
    private async Task<T> WithNextNumberAsync<T>(DemoRegister register, string type, int year, Func<string, Task<T>> post)
    {
        (string, string) key = (register.ShortCode, type);
        if (!_sequences.ContainsKey(key))
        {
            _sequences[key] = type == "SAL" ? await HighestSaleSequenceAsync(register) : 0;
        }

        for (int attempt = 0; ; attempt++)
        {
            int sequence = ++_sequences[key];
            string number = string.Create(
                CultureInfo.InvariantCulture,
                $"{type}-{year:D4}-{register.ShortCode}-{sequence.ToString(type == "SHF" ? "D4" : "D6", CultureInfo.InvariantCulture)}");

            try
            {
                return await post(number);
            }
            catch (DemoApiException ex) when (attempt < 200 && IsNumberTaken(ex))
            {
                // Taken by an earlier run: try the next number.
            }
        }
    }

    /// <summary>A reused document number trips the unique index; the API answers
    /// with a conflict or, for an unmapped database error, a server error.</summary>
    private static bool IsNumberTaken(DemoApiException ex)
        => ex.StatusCode is 409 or 500
           && !ex.Message.Contains("stock", StringComparison.OrdinalIgnoreCase)
           && ex.ErrorCode?.Contains("stock", StringComparison.OrdinalIgnoreCase) != true;

    private async Task<int> HighestSaleSequenceAsync(DemoRegister register)
    {
        string prefix = string.Create(CultureInfo.InvariantCulture, $"-{register.ShortCode}-");
        int highest = 0;
        foreach (JsonNode? sale in DemoApi.Items(await world.Api.GetAsync(
                     string.Create(CultureInfo.InvariantCulture, $"/api/v1/sales?locationId={register.LocationId:D}"),
                     world.Owner)))
        {
            string number = sale?["number"]?.GetValue<string>() ?? string.Empty;
            int at = number.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0 && int.TryParse(number[(at + prefix.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out int sequence))
            {
                highest = Math.Max(highest, sequence);
            }
        }

        return highest;
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
        DemoRegister register, Guid shiftId, DateOnly businessDate, DateTimeOffset completedAt)
    {
        List<(DemoProduct Product, decimal Quantity, decimal Price)> lines = PickBasket(register.Store, completedAt);

        // A basket can hit a product the store has just run out of; drop that
        // product for the rest of the run and try the basket again without it.
        for (int attempt = 0; attempt < 4 && lines.Count > 0; attempt++)
        {
            decimal total = lines.Sum(l => l.Quantity * l.Price);
            Guid? customerId = world.Customers.Count > 0 && random.Next(8) == 0
                ? world.Customers[random.Next(world.Customers.Count)]
                : null;
            object payment = Payment(total, out int method);

            try
            {
                Guid saleId = await WithNextNumberAsync(register, "SAL", businessDate.Year, number => world.Api.CreateAsync(
                    "/api/v1/sales",
                    new
                    {
                        number,
                        eventId = Guid.CreateVersion7(),
                        locationId = register.LocationId,
                        cashierShiftId = shiftId,
                        deviceId = register.DeviceId,
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
                        payments = new[] { payment },
                    },
                    register.Cashier));

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
                _soldOut.Add((register.Store, culprit));
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
        DemoRegister register, Guid saleId, Guid shiftId, DateOnly businessDate, DateTimeOffset completedAt)
    {
        string[] reasons = ["Customer changed their mind at the counter.", "Scanned the wrong item.", "Duplicate transaction."];
        try
        {
            await world.Api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/sales/{saleId:D}/void"),
                new
                {
                    eventId = Guid.CreateVersion7(),
                    locationId = register.LocationId,
                    shiftId,
                    deviceId = register.DeviceId,
                    businessDate,
                    voidedAtUtc = completedAt.AddMinutes(2),
                    reason = reasons[random.Next(reasons.Length)],
                },
                register.Supervisor);
            Voids++;
        }
        catch (DemoApiException ex)
        {
            Console.WriteLine($"    void skipped: {ex.Message}");
        }
    }

    private async Task ReturnAsync(
        DemoRegister register,
        (Guid SaleId, List<(DemoProduct Product, decimal Quantity, decimal Price)> Lines) sale,
        Guid shiftId,
        DateOnly businessDate,
        DateTimeOffset completedAt)
    {
        (DemoProduct product, _, decimal price) = sale.Lines[0];
        DateTimeOffset returnedAt = completedAt.AddMinutes(45);

        try
        {
            Guid returnId = await WithNextNumberAsync(register, "RET", businessDate.Year, number => world.Api.CreateAsync(
                "/api/v1/returns",
                new
                {
                    number,
                    eventId = Guid.CreateVersion7(),
                    saleId = sale.SaleId,
                    locationId = register.LocationId,
                    shiftId,
                    deviceId = register.DeviceId,
                    customerId = (Guid?)null,
                    businessDate,
                    returnedAtUtc = returnedAt,
                    lines = new[] { new { productId = product.Id, quantity = 1m } },
                },
                register.Cashier));

            await world.Api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/returns/{returnId:D}/refund"),
                new
                {
                    saleId = sale.SaleId,
                    eventId = Guid.CreateVersion7(),
                    locationId = register.LocationId,
                    shiftId,
                    deviceId = register.DeviceId,
                    method = Cash,
                    amount = price,
                    tendered = (decimal?)price,
                    providerReference = (string?)null,
                    refundedAtUtc = returnedAt.AddMinutes(1),
                },
                register.Supervisor);
            Returns++;
        }
        catch (DemoApiException ex)
        {
            Console.WriteLine($"    return skipped: {ex.Message}");
        }
    }

    /// <summary>Closes the shift against its expected cash. Most drawers balance;
    /// about one in eight is a little short or over, as real counts are.</summary>
    private async Task CloseShiftAsync(DemoRegister register, Guid shiftId)
    {
        JsonNode? summary = await world.Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/summary"), register.Cashier);
        decimal expected = summary?["expectedCash"]?.GetValue<decimal>() ?? 0m;
        decimal counted = random.Next(8) == 0 ? expected + (random.Next(-8, 5) * 5m) : expected;

        await world.Api.PostAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/close"),
            new { locationId = register.LocationId, declaredCash = counted, countedCash = counted },
            register.Cashier);
    }
}
