using System.Globalization;
using System.Text.Json.Nodes;
using Pos.Infrastructure.Identity;

namespace Pos.DemoData;

/// <summary>
/// Posts a month of trading at every store the way its registers do. Each store
/// runs two counters: the morning cashier opens the first before the doors open
/// and counts the drawer mid-afternoon, the afternoon cashier opens the second
/// just before and closes it after the doors shut. Every sale is numbered by its
/// register, carries its real business date and time, and is paid in cash, by
/// e-wallet or by card; the supervisor voids the odd sale and refunds returns
/// brought back within the 7-day window printed on the receipt.
/// </summary>
/// <remarks>
/// Each product sells at the daily rate the development seeder sized its opening
/// stock for (<see cref="DevelopmentCatalogue.DailyUnitsAt"/>), so a month of
/// sales leaves most shelves healthy, some low and a few sold out, exactly as
/// the seed planned. The stores trade in parallel, each from its own seeded
/// random source, so a rerun of the same month produces the same history.
/// </remarks>
internal sealed class SalesHistory(DemoWorld world)
{
    private const int Cash = 1;
    private const int Card = 2;
    private const int EWallet = 3;

    /// <summary>The opening float in each drawer.</summary>
    private const decimal OpeningFloat = 3000m;

    /// <summary>The senior citizen and PWD discount.</summary>
    private const decimal ConcessionRate = 0.20m;

    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    /// <summary>
    /// How a neighbourhood grocery's customers arrive through the day, from the
    /// 7:00 opening to the last shoppers before 22:00: a lunchtime peak and a
    /// bigger one on the way home from work.
    /// </summary>
    private static readonly (int Hour, int Weight)[] HourCurve =
    [
        (7, 3), (8, 5), (9, 6), (10, 7), (11, 9), (12, 10), (13, 8),
        (14, 6), (15, 6), (16, 7), (17, 10), (18, 12), (19, 11), (20, 8), (21, 4),
    ];

    /// <summary>How many different products one checkout holds: mostly a few, now
    /// and then a full weekly shop.</summary>
    private static readonly (int Lines, int Weight)[] BasketSizes =
    [
        (1, 28), (2, 22), (3, 16), (4, 11), (5, 8), (6, 5), (7, 3), (8, 2), (10, 2), (12, 1), (15, 1), (20, 1),
    ];

    private static readonly string[] VoidReasons =
    [
        "Customer changed their mind at the counter.",
        "Scanned the wrong item.",
        "Duplicate transaction.",
        "Customer short of cash; sale cancelled.",
    ];

    private readonly Lock _netLock = new();
    private int _salesPosted;
    private int _voids;
    private int _returns;
    private int _shifts;
    private decimal _netSales;

    public int SalesPosted => _salesPosted;

    public int Voids => _voids;

    public int Returns => _returns;

    public int Shifts => _shifts;

    public decimal NetSales => _netSales;

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Manila).DateTime);

    /// <summary>Posts <paramref name="days"/> days of history ending today, every
    /// store at once.</summary>
    public async Task RunAsync(int days)
    {
        await Task.WhenAll(world.Registers.Keys
            .Order(StringComparer.Ordinal)
            .Select(store => new StoreTrading(this, world, store).RunAsync(days)));
    }

    /// <summary>One store's month: its tills, its stock and its own random source.</summary>
    private sealed class StoreTrading(SalesHistory totals, DemoWorld world, string store)
    {
        private readonly DevelopmentLocation _branch = DevelopmentCatalogue.Locations.Single(l => l.Code == store);
        private readonly IReadOnlyList<DemoRegister> _tills = world.Registers[store];
        private readonly Random _random = new(StableSeed(store));

        /// <summary>What the store has left to sell, by SKU, as head office holds it.</summary>
        private readonly Dictionary<string, decimal> _onHand = new(StringComparer.Ordinal);

        /// <summary>The last number each register allocated, per document type. A
        /// register numbers its own documents (offline, it cannot ask head office).</summary>
        private readonly Dictionary<(string ShortCode, string Type), int> _sequences = [];

        /// <summary>Sales a customer will bring something back from, by when.</summary>
        private readonly List<PendingReturn> _returnsDue = [];

        private Guid LocationId => _tills[0].LocationId;

        public async Task RunAsync(int days)
        {
            await LoadStockAsync();

            for (int back = days; back >= 0; back--)
            {
                DateOnly businessDate = Today.AddDays(-back);
                DayResult day = await TradeDayAsync(businessDate);
                if (day.Skipped)
                {
                    Console.WriteLine($"  {store} {businessDate:ddd dd MMM}: already posted, skipped");
                }
                else if (day.Shifts == 0)
                {
                    Console.WriteLine($"  {store} {businessDate:ddd dd MMM}: not open yet");
                }
                else
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {store} {businessDate:ddd dd MMM}: {day.Sales,4} sales  {day.Net,12:N2} net  {day.Voids} void(s)  {day.Returns} return(s)"));
                }
            }
        }

        /// <summary>Reads the store's stock from head office, so a rerun sells what
        /// is really left.</summary>
        private async Task LoadStockAsync()
        {
            JsonNode? report = await world.Api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/stock-levels?locationId={LocationId:D}"),
                world.Owner);
            foreach (JsonNode? row in DemoApi.Items(report?["products"]))
            {
                _onHand[row!["sku"]!.GetValue<string>()] = row["available"]!.GetValue<decimal>();
            }
        }

        private async Task<DayResult> TradeDayAsync(DateOnly businessDate)
        {
            // A store-day that already has sales was posted by an earlier (possibly
            // interrupted) run; skipping it makes the tool safe to run again.
            if (await HasSalesAsync(businessDate))
            {
                return DayResult.Skip;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-5);
            List<Checkout> checkouts = PlanDay(businessDate);
            DayResult result = new();

            // The morning counter, then the afternoon one: each register's shift
            // runs in its own time order, and the two overlap at the handover.
            Shift[] shifts =
            [
                new(_tills[0], At(businessDate, 6, 40 + _random.Next(12)), At(businessDate, 15, 2 + _random.Next(15))),
                new(_tills[1], At(businessDate, 13, 42 + _random.Next(12)), At(businessDate, 22, 3 + _random.Next(15))),
            ];

            foreach (Shift shift in shifts)
            {
                if (shift.OpensAt > now)
                {
                    continue;
                }

                await CloseLeftoverShiftAsync(shift.Register);
                Guid shiftId = await OpenShiftAsync(shift, businessDate);
                result.Shifts++;

                List<Checkout> mine = [.. checkouts.Where(c => c.Counter == shift.Register && c.At < now)];
                List<PendingReturn> returns = [.. _returnsDue
                    .Where(r => r.DueAt >= shift.OpensAt && r.DueAt < shift.ClosesAt && r.DueAt < now)];
                _returnsDue.RemoveAll(returns.Contains);

                IEnumerable<(DateTimeOffset At, Checkout? Sale, PendingReturn? Return)> events = mine
                    .Select(c => (c.At, (Checkout?)c, (PendingReturn?)null))
                    .Concat(returns.Select(r => (r.DueAt, (Checkout?)null, (PendingReturn?)r)))
                    .OrderBy(e => e.Item1);

                foreach ((DateTimeOffset at, Checkout? sale, PendingReturn? pending) in events)
                {
                    if (sale is not null)
                    {
                        shiftId = await CheckoutAsync(shift, shiftId, businessDate, sale, result);
                    }
                    else if (pending is not null)
                    {
                        await ReturnAsync(shift, shiftId, businessDate, pending, at, result);
                    }
                }

                // The shift still trading right now stays open, as the real one would.
                if (shift.ClosesAt < now)
                {
                    await CloseShiftAsync(shift.Register, shiftId, shift.ClosesAt);
                }
            }

            return result;
        }

        /// <summary>
        /// Plans the day's checkouts: each product sells about its daily rate
        /// (busier on weekends and paydays), in the quantities people buy it, and
        /// the day's purchases are split into baskets spread across the hours.
        /// </summary>
        private List<Checkout> PlanDay(DateOnly businessDate)
        {
            double busy = DayFactor(businessDate) * (0.92 + (_random.NextDouble() * 0.16));

            List<(DemoProduct Product, decimal Quantity)> picks = [];
            foreach (DevelopmentProduct item in DevelopmentCatalogue.Products)
            {
                if (!world.Products.TryGetValue(item.Sku, out DemoProduct? product))
                {
                    continue;
                }

                double rate = (double)DevelopmentCatalogue.DailyUnitsAt(item, _branch) * busy;
                for (int units = Poisson(rate); units > 0;)
                {
                    int take = Math.Min(units, LineQuantity(item.Price));
                    picks.Add((product, take));
                    units -= take;
                }
            }

            Shuffle(picks);

            List<Checkout> checkouts = [];
            for (int next = 0; next < picks.Count;)
            {
                int lines = Weighted(BasketSizes);
                Dictionary<string, (DemoProduct Product, decimal Quantity)> basket = new(StringComparer.Ordinal);
                foreach ((DemoProduct product, decimal quantity) in picks.Skip(next).Take(lines))
                {
                    basket[product.Sku] = basket.TryGetValue(product.Sku, out var held)
                        ? (product, held.Quantity + quantity)
                        : (product, quantity);
                }

                next += lines;
                DateTimeOffset at = TimeOfDay(businessDate);
                checkouts.Add(new Checkout(at, CounterAt(at), [.. basket.Values]));
            }

            return [.. checkouts.OrderBy(c => c.At)];
        }

        /// <summary>Weekends and the paydays (the 15th and the month's end) are
        /// busier; the factors average to one over a month, so a month sells what
        /// the seed stocked for.</summary>
        private static double DayFactor(DateOnly date)
        {
            double factor = date.DayOfWeek switch
            {
                DayOfWeek.Saturday => 1.22,
                DayOfWeek.Sunday => 1.18,
                DayOfWeek.Friday => 1.06,
                DayOfWeek.Monday => 0.9,
                _ => 0.89,
            };

            return date.Day is 15 or 16 or 30 or 31 or 1 ? factor * 1.15 : factor;
        }

        /// <summary>How many of one product go in the basket at once: a handful of
        /// instant noodles, one bottle of shampoo.</summary>
        private int LineQuantity(decimal price)
        {
            int roll = _random.Next(100);
            return price switch
            {
                < 25m => roll switch { < 45 => 1, < 65 => 2, < 80 => 3, < 85 => 4, < 90 => 5, _ => 6 },
                < 80m => roll switch { < 60 => 1, < 85 => 2, < 95 => 3, _ => 4 },
                < 300m => roll switch { < 82 => 1, < 97 => 2, _ => 3 },
                _ => roll < 95 ? 1 : 2,
            };
        }

        /// <summary>When a customer comes back: later that day or on one of the next
        /// six, during opening hours.</summary>
        private DateTimeOffset ReturnTime(DateOnly soldOn, DateTimeOffset soldAt)
        {
            DateTimeOffset at = TimeOfDay(soldOn.AddDays(_random.Next(7)));
            return at > soldAt.AddMinutes(30) ? at : TimeOfDay(soldOn.AddDays(1));
        }

        private DateTimeOffset TimeOfDay(DateOnly businessDate)
        {
            int hour = Weighted(HourCurve);
            return At(businessDate, hour, 0).AddSeconds(_random.Next(3600));
        }

        /// <summary>The morning counter until the handover, the afternoon one after,
        /// either while both are open.</summary>
        private DemoRegister CounterAt(DateTimeOffset at)
        {
            int hour = TimeZoneInfo.ConvertTime(at, Manila).Hour;
            return hour < 14 ? _tills[0] : hour >= 15 ? _tills[1] : _tills[_random.Next(2)];
        }

        private async Task<Guid> CheckoutAsync(Shift shift, Guid shiftId, DateOnly businessDate, Checkout checkout, DayResult result)
        {
            DemoRegister register = shift.Register;
            List<(DemoProduct Product, decimal Quantity, decimal Price)> lines = [];
            foreach ((DemoProduct product, decimal quantity) in checkout.Lines)
            {
                decimal left = _onHand.GetValueOrDefault(product.Sku);
                if (left <= 0m || product.PriceAt(checkout.At) is not { } price)
                {
                    continue;
                }

                lines.Add((product, Math.Min(quantity, left), price));
            }

            // About one shopper in twenty-five is a senior citizen or a person with
            // a disability, entitled to 20% off; the store manager authorises it.
            bool concession = _random.Next(25) == 0;
            decimal Discount(decimal quantity, decimal price)
                => concession ? Math.Round(quantity * price * ConcessionRate, 2, MidpointRounding.AwayFromZero) : 0m;

            // A basket can still hit a product head office knows has run out; the
            // shopper puts it back and pays for the rest.
            for (int attempt = 0; attempt < 4 && lines.Count > 0; attempt++)
            {
                decimal total = lines.Sum(l => (l.Quantity * l.Price) - Discount(l.Quantity, l.Price));
                Guid? customerId = world.Customers.Count > 0 && _random.Next(9) == 0
                    ? world.Customers[_random.Next(world.Customers.Count)]
                    : null;
                (object payment, int method) = Payment(total);

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
                            completedAtUtc = checkout.At,
                            lines = lines.Select(l => new
                            {
                                productId = l.Product.Id,
                                quantity = l.Quantity,
                                unitOfMeasureId = l.Product.UnitId,
                                barcode = (string?)null,
                                unitPriceOverride = (decimal?)null,
                                priceOverrideAuthorizedByUserId = (Guid?)null,
                                discount = Discount(l.Quantity, l.Price),
                                discountAuthorizedByUserId = concession ? register.Supervisor.UserId : (Guid?)null,
                                allowExpiredOverride = false,
                                expiredOverrideReason = (string?)null,
                            }),
                            payments = new[] { payment },
                        },
                        register.Cashier));

                    foreach ((DemoProduct product, decimal quantity, _) in lines)
                    {
                        _onHand[product.Sku] = _onHand.GetValueOrDefault(product.Sku) - quantity;
                    }

                    Interlocked.Increment(ref totals._salesPosted);
                    result.Sales++;

                    // About one sale in four hundred is voided at the till moments
                    // later; the supervisor does it, on the cashier's shift.
                    if (_random.Next(400) == 0 && await VoidAsync(register, saleId, shiftId, businessDate, checkout.At))
                    {
                        foreach ((DemoProduct product, decimal quantity, _) in lines)
                        {
                            _onHand[product.Sku] = _onHand.GetValueOrDefault(product.Sku) + quantity;
                        }

                        result.Voids++;
                        return shiftId;
                    }

                    lock (totals._netLock)
                    {
                        totals._netSales += total;
                    }

                    result.Net += total;

                    // About one in three hundred and fifty comes back within the
                    // week: a wrong size, a dented can, a spare pack.
                    if (_random.Next(350) == 0)
                    {
                        (DemoProduct product, decimal quantity, decimal unitPrice) = lines[_random.Next(lines.Count)];
                        decimal paidEach = Math.Floor((((quantity * unitPrice) - Discount(quantity, unitPrice)) / quantity) * 100m) / 100m;
                        _returnsDue.Add(new PendingReturn(saleId, product, paidEach, method, ReturnTime(businessDate, checkout.At)));
                    }

                    return shiftId;
                }
                catch (DemoApiException ex) when (IsOutOfStock(ex))
                {
                    string culprit = lines
                        .Select(l => l.Product)
                        .FirstOrDefault(p => ex.Message.Contains(p.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains(p.Sku, StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains(p.Name, StringComparison.OrdinalIgnoreCase))?.Sku
                        ?? lines.OrderByDescending(l => l.Quantity).First().Product.Sku;

                    _onHand[culprit] = 0m;
                    lines.RemoveAll(l => l.Product.Sku == culprit);
                }
                catch (DemoApiException ex) when (IsShiftClosed(ex))
                {
                    // Head office's shift sweeper closed the drawer under us (a
                    // backdated shift looks long-open to it): open a fresh one.
                    Console.WriteLine($"    {register.ShortCode}: shift closed by head office; opening another");
                    shiftId = await OpenShiftAsync(shift with { OpensAt = checkout.At.AddMinutes(-1) }, businessDate);
                }
            }

            return shiftId;
        }

        /// <summary>Most shoppers pay cash, handing over a round note; e-wallets are
        /// common, cards mostly for bigger baskets.</summary>
        private (object Payment, int Method) Payment(decimal total)
        {
            int roll = _random.Next(100);
            int method = total < 150m
                ? roll < 80 ? Cash : EWallet
                : roll < 58 ? Cash : roll < 85 ? EWallet : Card;

            return method switch
            {
                Cash => (new { method = Cash, amount = total, tendered = (decimal?)Tendered(total), providerReference = (string?)null }, Cash),
                Card => (new { method = Card, amount = total, tendered = (decimal?)null, providerReference = (string?)$"AUTH{_random.Next(100000, 999999)}" }, Card),
                _ => (new { method = EWallet, amount = total, tendered = (decimal?)null, providerReference = (string?)$"GC{_random.NextInt64(1000000000, 9999999999)}" }, EWallet),
            };
        }

        /// <summary>Exact change now and then; otherwise the next 100, 500 or 1,000.</summary>
        private decimal Tendered(decimal total)
        {
            if (_random.Next(6) == 0)
            {
                return total;
            }

            decimal[] steps = total < 100m ? [20m, 50m, 100m] : total < 500m ? [100m, 500m, 1000m] : [500m, 1000m];
            decimal step = steps[_random.Next(steps.Length)];
            return Math.Ceiling(total / step) * step;
        }

        private async Task<bool> VoidAsync(
            DemoRegister register, Guid saleId, Guid shiftId, DateOnly businessDate, DateTimeOffset completedAt)
        {
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
                        voidedAtUtc = completedAt.AddMinutes(1 + _random.Next(4)),
                        reason = VoidReasons[_random.Next(VoidReasons.Length)],
                    },
                    register.Supervisor);
                Interlocked.Increment(ref totals._voids);
                return true;
            }
            catch (DemoApiException ex)
            {
                Console.WriteLine($"    void skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>A customer brings one item back; the cashier accepts the return
        /// and the supervisor refunds it the way it was paid.</summary>
        private async Task ReturnAsync(
            Shift shift, Guid shiftId, DateOnly businessDate, PendingReturn pending, DateTimeOffset at, DayResult result)
        {
            DemoRegister register = shift.Register;
            try
            {
                Guid returnId = await WithNextNumberAsync(register, "RET", businessDate.Year, number => world.Api.CreateAsync(
                    "/api/v1/returns",
                    new
                    {
                        number,
                        eventId = Guid.CreateVersion7(),
                        saleId = pending.SaleId,
                        locationId = register.LocationId,
                        shiftId,
                        deviceId = register.DeviceId,
                        customerId = (Guid?)null,
                        businessDate,
                        returnedAtUtc = at,
                        lines = new[] { new { productId = pending.Product.Id, quantity = 1m } },
                    },
                    register.Cashier));

                await world.Api.PostAsync(
                    string.Create(CultureInfo.InvariantCulture, $"/api/v1/returns/{returnId:D}/refund"),
                    new
                    {
                        saleId = pending.SaleId,
                        eventId = Guid.CreateVersion7(),
                        locationId = register.LocationId,
                        shiftId,
                        deviceId = register.DeviceId,
                        method = pending.Method,
                        amount = pending.UnitPrice,
                        tendered = pending.Method == Cash ? (decimal?)pending.UnitPrice : null,
                        providerReference = pending.Method == Cash ? null : (string?)$"RF{_random.Next(100000, 999999)}",
                        refundedAtUtc = at.AddMinutes(1),
                    },
                    register.Supervisor);

                Interlocked.Increment(ref totals._returns);
                result.Returns++;
            }
            catch (DemoApiException ex)
            {
                Console.WriteLine($"    return skipped: {ex.Message}");
            }
        }

        private async Task<bool> HasSalesAsync(DateOnly businessDate)
        {
            JsonNode? sales = await world.Api.GetAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/v1/sales?locationId={LocationId:D}&from={businessDate:yyyy-MM-dd}&to={businessDate:yyyy-MM-dd}&limit=1"),
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
                Console.WriteLine($"    {register.ShortCode}: closing a shift left open by an earlier run");
                await CloseShiftAsync(register, shiftId, closedAt: null);
            }
        }

        private async Task<Guid> OpenShiftAsync(Shift shift, DateOnly businessDate)
        {
            DemoRegister register = shift.Register;
            Guid shiftId = await WithNextNumberAsync(register, "SHF", businessDate.Year, number => world.Api.CreateAsync(
                "/api/v1/shifts/open",
                new { number, locationId = register.LocationId, businessDate, openingFloat = OpeningFloat, openedAtUtc = shift.OpensAt },
                register.Cashier));
            Interlocked.Increment(ref totals._shifts);
            return shiftId;
        }

        /// <summary>Closes the shift against its expected cash. Most drawers balance;
        /// about one in eight is a little short or over, as real counts are.</summary>
        private async Task CloseShiftAsync(DemoRegister register, Guid shiftId, DateTimeOffset? closedAt)
        {
            JsonNode? summary = await world.Api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/summary"), register.Cashier);
            if (summary?["status"]?.GetValue<string>() is { } status && !status.Equals("Open", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            decimal expected = summary?["expectedCash"]?.GetValue<decimal>() ?? 0m;
            decimal counted = _random.Next(8) == 0 ? expected + (_random.Next(-8, 5) * 5m) : expected;

            await world.Api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/shifts/{shiftId:D}/close"),
                new { locationId = register.LocationId, declaredCash = counted, countedCash = counted, closedAtUtc = closedAt },
                register.Cashier);
        }

        /// <summary>
        /// Allocates the register's next number for a document type and posts with
        /// it, as an offline register does: SHF numbers have four digits, SAL and
        /// RET six. The sale counter starts after the newest sale head office holds
        /// for the register; if a number turns out to be taken (a rerun after an
        /// interruption), the register moves on to the next one.
        /// </summary>
        private async Task<T> WithNextNumberAsync<T>(DemoRegister register, string type, int year, Func<string, Task<T>> post)
        {
            (string, string) key = (register.ShortCode, type);
            if (!_sequences.ContainsKey(key))
            {
                _sequences[key] = type == "SAL" ? await NewestSaleSequenceAsync(register) : 0;
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
                catch (DemoApiException ex) when (attempt < 500 && IsNumberTaken(ex))
                {
                    // Taken by an earlier run: try the next number.
                }
            }
        }

        private async Task<int> NewestSaleSequenceAsync(DemoRegister register)
        {
            string prefix = string.Create(CultureInfo.InvariantCulture, $"-{register.ShortCode}-");
            JsonNode? newest = await world.Api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/sales?locationId={register.LocationId:D}&number={prefix}&limit=1"),
                world.Owner);
            string number = DemoApi.Items(newest).FirstOrDefault()?["number"]?.GetValue<string>() ?? string.Empty;
            int at = number.IndexOf(prefix, StringComparison.Ordinal);
            return at >= 0 && int.TryParse(number[(at + prefix.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out int sequence)
                ? sequence
                : 0;
        }

        /// <summary>A reused document number trips the unique index; the API answers
        /// with a conflict or, for an unmapped database error, a server error.</summary>
        private static bool IsNumberTaken(DemoApiException ex)
            => ex.StatusCode is 409 or 500
               && !IsOutOfStock(ex)
               && !IsShiftClosed(ex)
               && ex.ErrorCode?.Contains("already_open", StringComparison.Ordinal) != true;

        private static bool IsOutOfStock(DemoApiException ex)
            => ex.Message.Contains("stock", StringComparison.OrdinalIgnoreCase)
               || ex.ErrorCode?.Contains("stock", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsShiftClosed(DemoApiException ex)
            => ex.ErrorCode is { } code
               && code.Contains("shift", StringComparison.OrdinalIgnoreCase)
               && (code.Contains("not_open", StringComparison.OrdinalIgnoreCase) || code.Contains("closed", StringComparison.OrdinalIgnoreCase));

        private static DateTimeOffset At(DateOnly date, int hour, int minute)
            => new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), Manila.BaseUtcOffset).AddMinutes(minute).ToUniversalTime();

        private int Weighted((int Value, int Weight)[] options)
        {
            int roll = _random.Next(options.Sum(o => o.Weight));
            foreach ((int value, int weight) in options)
            {
                if ((roll -= weight) < 0)
                {
                    return value;
                }
            }

            return options[^1].Value;
        }

        /// <summary>A day's sales of one product: Knuth's method for slow sellers, a
        /// rounded normal for the fast ones.</summary>
        private int Poisson(double mean)
        {
            if (mean <= 0)
            {
                return 0;
            }

            if (mean > 30)
            {
                double u1 = 1.0 - _random.NextDouble();
                double u2 = _random.NextDouble();
                double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                return Math.Max(0, (int)Math.Round(mean + (Math.Sqrt(mean) * normal)));
            }

            double limit = Math.Exp(-mean);
            double product = _random.NextDouble();
            int count = 0;
            while (product > limit)
            {
                count++;
                product *= _random.NextDouble();
            }

            return count;
        }

        private void Shuffle<T>(List<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        private static int StableSeed(string text)
        {
            unchecked
            {
                int hash = 20260924;
                foreach (char c in text)
                {
                    hash = (hash * 31) + c;
                }

                return hash;
            }
        }
    }

    /// <summary>One register's shift for the day and when its drawer opens and is counted.</summary>
    private sealed record Shift(DemoRegister Register, DateTimeOffset OpensAt, DateTimeOffset ClosesAt);

    /// <summary>One customer's purchase: when, at which counter, and what.</summary>
    private sealed record Checkout(DateTimeOffset At, DemoRegister Counter, IReadOnlyList<(DemoProduct Product, decimal Quantity)> Lines);

    /// <summary>An item a customer will bring back, and when.</summary>
    private sealed record PendingReturn(Guid SaleId, DemoProduct Product, decimal UnitPrice, int Method, DateTimeOffset DueAt);

    private sealed class DayResult
    {
        public static DayResult Skip { get; } = new() { Skipped = true };

        public bool Skipped { get; private init; }

        public int Shifts { get; set; }

        public int Sales { get; set; }

        public decimal Net { get; set; }

        public int Voids { get; set; }

        public int Returns { get; set; }
    }
}
