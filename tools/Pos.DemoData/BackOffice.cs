using System.Globalization;
using System.Text.Json.Nodes;
using Pos.Infrastructure.Identity;

namespace Pos.DemoData;

/// <summary>
/// Posts the back-office side of the demo, each workflow left at a different
/// stage so every list and detail page has something to show: purchase orders
/// to the distribution centre (draft, awaiting approval, sent, received in
/// full, received short), transfers restocking what each store ran low on
/// (draft to received), stock counts, stock adjustments, payment receipts and a
/// quarantine incident. Quantities follow each product's sales rate, so a
/// purchase order or transfer looks like the one a buyer would raise. Each
/// workflow runs as its own step: one that the server refuses is reported and
/// the rest carry on.
/// </summary>
internal sealed class BackOffice(DemoWorld world)
{
    private const int AvailableState = 0;
    private const int CycleCount = 2;
    private const int CategoryCount = 3;

    /// <summary>The supplier whose purchase order marks the demo's back office as posted.</summary>
    private const string MarkerSupplier = "IBC";

    private DemoApi Api => world.Api;

    private DemoSession User(string name) => world.Users[name];

    private Guid Location(string code) => world.Locations[code];

    /// <summary>The manager assigned to a store.</summary>
    private DemoSession StoreManager(string store) => store switch
    {
        "STORE01" => User("manager"),
        "STORE02" => User("manager2"),
        "STORE03" => User("manager3"),
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, "Not a store."),
    };

    public int Failures { get; private set; }

    /// <summary>True when an earlier run posted the back-office activity: only the
    /// demo raises a purchase order to its marker supplier.</summary>
    public async Task<bool> AlreadyPostedAsync()
    {
        Guid marker = await SupplierAsync(MarkerSupplier);
        return DemoApi.Items(await Api.GetAsync("/api/v1/purchasing/orders", world.Owner))
            .Any(order => order?["supplierId"]?.GetValue<Guid>() == marker);
    }

    public async Task RunAsync()
    {
        await StepAsync("Purchase order received in full (Pacific Grains)", () => PurchaseOrderAsync("PGC", 10, Receive.Full));
        await StepAsync("Purchase order received short (Island Beverage)", () => PurchaseOrderAsync(MarkerSupplier, 12, Receive.Short));
        await StepAsync("Purchase order sent, awaiting delivery (Snackworks)", () => PurchaseOrderAsync("SNX", 14, Receive.SentOnly));
        await StepAsync("Purchase order awaiting approval (HomeCare Supply)", () => PurchaseOrderAsync("HCS", 9, Receive.SubmitOnly));
        await StepAsync("Purchase order draft (DairyFresh)", () => PurchaseOrderAsync("DFP", 6, Receive.DraftOnly));

        // Restocking follows what each store actually ran low on this month.
        Dictionary<string, List<JsonNode>> shortages = [];
        foreach (string store in new[] { "STORE01", "STORE02", "STORE03" })
        {
            shortages[store] = await ShortagesAsync(store);
        }

        await StepAsync("Restock transfer to Legazpi Village, received", () => TransferAsync("STORE01", Stage.Received, shortages["STORE01"], skip: 0, take: 18));
        await StepAsync("Restock transfer to Tomas Morato, in transit", () => TransferAsync("STORE02", Stage.Dispatched, shortages["STORE02"], skip: 0, take: 16));
        await StepAsync("Restock transfer to Kapitolyo, approved", () => TransferAsync("STORE03", Stage.Approved, shortages["STORE03"], skip: 0, take: 14));
        await StepAsync("Restock transfer to Legazpi Village, awaiting approval", () => TransferAsync("STORE01", Stage.Submitted, shortages["STORE01"], skip: 18, take: 10));
        await StepAsync("Restock transfer to Kapitolyo, draft", () => TransferAsync("STORE03", Stage.Draft, shortages["STORE03"], skip: 14, take: 6));

        await StepAsync("Cycle count at Tomas Morato, approved", () => CycleCountAsync("STORE02", "BEVERAGES", 10));
        await StepAsync("Bakery shelf count at Kapitolyo, in progress", () => CategoryCountAsync("STORE03", "BAKERY"));

        await StepAsync("Adjustment at Legazpi Village: thawed frozen goods, approved", () => AdjustmentAsync(
            "STORE01", reason: 1, "Freezer tripped overnight; packs thawed and were pulled.", Adjust.Approve, "FROZEN", 2, -2m));
        await StepAsync("Adjustment at Kapitolyo: bread past best-before, awaiting approval", () => AdjustmentAsync(
            "STORE03", reason: 3, "Past best-before, pulled from the shelf.", Adjust.Submit, "BAKERY", 3, -3m));
        await StepAsync("Adjustment at the distribution centre: broken bottles, draft", () => AdjustmentAsync(
            "MAIN", reason: 8, "Pallet dropped at the loading bay.", Adjust.Draft, "CONDIMENTS", 2, -6m));

        await StepAsync("Payment receipts", ReceiptsAsync);
        await StepAsync("Quarantine incident at Tomas Morato", QuarantineAsync);
    }

    private async Task StepAsync(string name, Func<Task> step)
    {
        try
        {
            await step();
            Console.WriteLine($"  ok    {name}");
        }
        catch (DemoApiException ex)
        {
            Failures++;
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
        }
    }

    private enum Receive
    {
        DraftOnly,
        SubmitOnly,
        SentOnly,
        Full,
        Short,
    }

    /// <summary>
    /// Raises a distribution-centre purchase order for a supplier's fastest
    /// sellers, about two weeks of the chain's sales in whole cases, then walks
    /// it to the requested stage: the owner approves and sends it, and the
    /// warehouse receives it in full, or short with a damaged case.
    /// </summary>
    private async Task PurchaseOrderAsync(string supplierCode, int lines, Receive receive)
    {
        Guid supplierId = await SupplierAsync(supplierCode);
        DemoSession inventory = User("inventory");
        DemoSession owner = world.Owner;

        List<(DemoProduct Product, decimal Quantity, decimal Cost)> order = [.. DevelopmentCatalogue.Products
            .Where(p => p.Supplier == supplierCode && world.Products.ContainsKey(p.Sku))
            .OrderByDescending(ChainDailyUnits)
            .Take(lines)
            .Select(p => (world.Product(p.Sku), Cases(ChainDailyUnits(p) * 14m), p.UnitCost))];

        Guid orderId = await Api.CreateAsync(
            "/api/v1/purchasing/orders",
            new
            {
                supplierId,
                destinationLocationId = Location("MAIN"),
                lines = order.Select(l => new
                {
                    productId = l.Product.Id,
                    unitOfMeasureId = l.Product.UnitId,
                    orderedQuantity = l.Quantity,
                    unitCost = l.Cost,
                }),
                expectedAtUtc = DateTimeOffset.UtcNow.AddDays(DevelopmentCatalogue.Suppliers.Single(s => s.Code == supplierCode).LeadTimeDays),
            },
            inventory);

        if (receive == Receive.DraftOnly)
        {
            return;
        }

        string path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/purchasing/orders/{orderId:D}");
        await Api.PostAsync($"{path}/submit", new { notes = "Fortnightly replenishment for the distribution centre." }, inventory);
        if (receive == Receive.SubmitOnly)
        {
            return;
        }

        await Api.PostAsync($"{path}/approve", new { notes = "Approved against this fortnight's sales." }, owner);
        await Api.PostAsync($"{path}/send", new { }, owner);
        if (receive == Receive.SentOnly)
        {
            return;
        }

        JsonArray orderLines = DemoApi.Items((await Api.GetAsync(path, owner))?["lines"]);
        List<object> received = [];
        int index = 0;
        foreach (JsonNode? line in orderLines)
        {
            decimal ordered = line!["orderedQuantity"]!.GetValue<decimal>();

            // Received short: the supplier was out of part of two lines, and one
            // case arrived crushed.
            decimal shortBy = receive == Receive.Short && index < 2 ? Math.Round(ordered * 0.25m / 12m) * 12m : 0m;
            decimal damaged = receive == Receive.Short && index == 0 ? Math.Min(12m, ordered - shortBy) : 0m;
            index++;

            received.Add(new
            {
                purchaseOrderLineId = line["id"]!.GetValue<Guid>(),
                quantityReceived = ordered - shortBy - damaged,
                quantityDamaged = damaged,
                quantityWrongItem = 0m,
                quantityExpired = 0m,
                unitCost = line["unitCost"]!.GetValue<decimal>(),
            });
        }

        await Api.PostAsync($"{path}/receipts", new { lines = received, documentsMissing = false }, inventory);
    }

    /// <summary>A product's expected daily sale across the three stores.</summary>
    private static decimal ChainDailyUnits(DevelopmentProduct product)
        => DevelopmentCatalogue.Locations.Where(l => l.SizeFactor > 0m).Sum(l => DevelopmentCatalogue.DailyUnitsAt(product, l));

    /// <summary>Rounds a quantity up to whole cases of twelve.</summary>
    private static decimal Cases(decimal units) => Math.Max(12m, Math.Ceiling(units / 12m) * 12m);

    private async Task<Guid> SupplierAsync(string code)
    {
        foreach (JsonNode? supplier in DemoApi.Items(await Api.GetAsync("/api/v1/catalog/suppliers", world.Owner)))
        {
            if (supplier!["code"]?.GetValue<string>() == code)
            {
                return supplier["id"]!.GetValue<Guid>();
            }
        }

        throw new DemoApiException($"Supplier {code} is not in the catalogue; restart the API so the seeder adds it.");
    }

    /// <summary>The store's sold-out and low lines, fastest sellers first.</summary>
    private async Task<List<JsonNode>> ShortagesAsync(string store)
    {
        JsonNode? report = await Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/stock-levels?locationId={Location(store):D}"),
            world.Owner);

        return [.. DemoApi.Items(report?["products"])
            .OfType<JsonNode>()
            .Where(row => row["status"]?.GetValue<string>() is "Out" or "Low" && world.Products.ContainsKey(row["sku"]!.GetValue<string>()))
            .OrderByDescending(row => row["dailySales"]?.GetValue<decimal>() ?? 0m)];
    }

    private enum Stage
    {
        Draft,
        Submitted,
        Approved,
        Dispatched,
        Received,
    }

    /// <summary>
    /// Restocks a store from the distribution centre: the store manager requests
    /// its low lines back up to their target, the owner approves, the warehouse
    /// picks and dispatches, and the store receives.
    /// </summary>
    private async Task TransferAsync(string store, Stage stage, List<JsonNode> shortages, int skip, int take)
    {
        List<(Guid ProductId, decimal Quantity)> lines = [.. shortages
            .Skip(skip)
            .Take(take)
            .Select(row =>
            {
                decimal target = row["targetStock"]?.GetValue<decimal?>() ?? 12m;
                decimal available = Math.Max(0m, row["available"]!.GetValue<decimal>());
                return (row["productId"]!.GetValue<Guid>(), Math.Max(6m, Math.Ceiling((target - available) / 6m) * 6m));
            })];

        if (lines.Count == 0)
        {
            Console.WriteLine($"        ({store} has nothing running low; no transfer needed)");
            return;
        }

        DemoSession storeUser = StoreManager(store);
        DemoSession owner = world.Owner;
        DemoSession warehouse = User("inventory");

        Guid transferId = await Api.CreateAsync(
            "/api/v1/transfers",
            new
            {
                sourceLocationId = Location("MAIN"),
                destinationLocationId = Location(store),
                lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity, note = (string?)null }),
            },
            storeUser);

        if (stage == Stage.Draft)
        {
            return;
        }

        string transfer = string.Create(CultureInfo.InvariantCulture, $"/api/v1/transfers/{transferId:D}");
        await Api.PostAsync($"{transfer}/submit", new { }, storeUser);
        if (stage == Stage.Submitted)
        {
            return;
        }

        // A submitted request is taken into review before it can be approved.
        await Api.PostAsync($"{transfer}/review", new { note = "Checking distribution centre availability." }, owner);
        await Api.PostAsync($"{transfer}/approve", new { note = "Approved from this week's restock plan." }, owner);
        if (stage == Stage.Approved)
        {
            return;
        }

        JsonArray transferLines = DemoApi.Items((await Api.GetAsync(transfer, owner))?["lines"]);
        await Api.PostAsync(
            $"{transfer}/pick",
            new
            {
                allocations = transferLines.Select(l => new
                {
                    batchId = (Guid?)null,
                    lineNo = l!["lineNo"]!.GetValue<int>(),
                    quantity = l["requestedQuantity"]!.GetValue<decimal>(),
                }),
            },
            warehouse);
        await Api.PostAsync($"{transfer}/ready", new { }, warehouse);
        await Api.PostAsync($"{transfer}/dispatch", new { }, warehouse);
        if (stage == Stage.Dispatched)
        {
            return;
        }

        await Api.PostAsync(
            $"{transfer}/receive",
            new
            {
                receives = transferLines.Select(l => new
                {
                    lineNo = l!["lineNo"]!.GetValue<int>(),
                    batchId = (Guid?)null,
                    receivedQuantity = l["requestedQuantity"]!.GetValue<decimal>(),
                    damagedQuantity = 0m,
                }),
            },
            storeUser);
    }

    /// <summary>Counts a shelf section at a store: the manager records it, one
    /// line two short, and the owner approves the small variance.</summary>
    private async Task CycleCountAsync(string store, string category, int products)
    {
        DemoSession counter = StoreManager(store);
        Guid[] productIds = [.. DevelopmentCatalogue.Products
            .Where(p => p.Category == category && world.Products.ContainsKey(p.Sku))
            .OrderByDescending(p => p.DailyUnits)
            .Take(products)
            .Select(p => world.Product(p.Sku).Id)];

        Guid countId = await Api.CreateAsync(
            "/api/v1/inventory/counts",
            new { locationId = Location(store), kind = CycleCount, productIds, categoryIds = (Guid[]?)null, note = "Monthly cycle count, drinks aisle." },
            counter);

        string count = string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/counts/{countId:D}");
        await RecordCountAsync(count, counter, all: true);
        await Api.PostAsync($"{count}/submit", new { }, counter);
        await Api.PostAsync($"{count}/approve", new { reason = "Variance checked against the shelf." }, world.Owner);
    }

    /// <summary>Starts a whole-category count and records only part of the shelf,
    /// as a count still in progress would.</summary>
    private async Task CategoryCountAsync(string store, string category)
    {
        DemoSession counter = StoreManager(store);
        DevelopmentProduct sample = DevelopmentCatalogue.Products.First(p => p.Category == category && world.Products.ContainsKey(p.Sku));
        Guid categoryId = await CategoryOfAsync(sample.Sku);

        Guid countId = await Api.CreateAsync(
            "/api/v1/inventory/counts",
            new { locationId = Location(store), kind = CategoryCount, productIds = (Guid[]?)null, categoryIds = new[] { categoryId }, note = "Bakery shelf count." },
            counter);

        await RecordCountAsync(string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/counts/{countId:D}"), counter, all: false);
    }

    private async Task RecordCountAsync(string count, DemoSession counter, bool all)
    {
        JsonArray lines = DemoApi.Items((await Api.GetAsync(count, counter))?["lines"]);

        List<object> recorded = [];
        foreach (JsonNode line in lines.OfType<JsonNode>())
        {
            decimal system = SystemQuantity(line);
            recorded.Add(new
            {
                productId = line["productId"]!.GetValue<Guid>(),
                physicalQuantity = recorded.Count == 0 ? Math.Max(0m, system - 2m) : system,
                batchId = (Guid?)null,
            });

            if (!all && recorded.Count == Math.Max(2, lines.Count / 3))
            {
                break;
            }
        }

        await Api.PostAsync($"{count}/lines", new { lines = recorded }, counter);
    }

    /// <summary>The system quantity a count line was snapshotted at, whatever the
    /// API calls it.</summary>
    private static decimal SystemQuantity(JsonNode line)
    {
        foreach (string name in new[] { "systemQuantity", "expectedQuantity", "snapshotQuantity", "bookQuantity" })
        {
            if (line[name] is JsonValue value && value.TryGetValue(out decimal quantity))
            {
                return quantity;
            }
        }

        return 0m;
    }

    private async Task<Guid> CategoryOfAsync(string sku)
    {
        JsonNode? product = await Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/catalog/products/{world.Product(sku).Id:D}"), world.Owner);
        return product!["categoryId"]!.GetValue<Guid>();
    }

    private enum Adjust
    {
        Draft,
        Submit,
        Approve,
    }

    /// <summary>
    /// Writes off stock that can no longer be sold: the store manager (or the
    /// warehouse, at the distribution centre) raises it for the section's best
    /// sellers the location still holds, and the owner approves it.
    /// </summary>
    private async Task AdjustmentAsync(string location, int reason, string notes, Adjust stage, string category, int products, decimal delta)
    {
        JsonNode? report = await Api.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/stock-levels?locationId={Location(location):D}"),
            world.Owner);
        HashSet<string> inCategory = [.. DevelopmentCatalogue.Products.Where(p => p.Category == category).Select(p => p.Sku)];
        List<Guid> affected = [.. DemoApi.Items(report?["products"])
            .OfType<JsonNode>()
            .Where(row => inCategory.Contains(row["sku"]!.GetValue<string>()) && row["available"]!.GetValue<decimal>() >= -delta)
            .OrderByDescending(row => row["dailySales"]?.GetValue<decimal>() ?? 0m)
            .Take(products)
            .Select(row => row["productId"]!.GetValue<Guid>())];

        DemoSession raiser = location == "MAIN" ? User("inventory") : StoreManager(location);
        Guid adjustmentId = await Api.CreateAsync(
            "/api/v1/inventory/adjustments",
            new
            {
                locationId = Location(location),
                reason,
                lines = affected.Select(productId => new
                {
                    productId,
                    state = AvailableState,
                    quantityDelta = delta,
                    batchId = (Guid?)null,
                }),
                notes,
            },
            raiser);

        if (stage == Adjust.Draft)
        {
            return;
        }

        string adjustment = string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/adjustments/{adjustmentId:D}");
        await Api.PostAsync($"{adjustment}/submit", new { reason = (string?)null }, raiser);
        if (stage == Adjust.Approve)
        {
            await Api.PostAsync($"{adjustment}/approve", new { reason = "Checked on site." }, world.Owner);
        }
    }

    /// <summary>Cash moving outside a sale today, recorded by each store's
    /// manager: a walk-in slip, branch expenses and an owner withdrawal.</summary>
    private async Task ReceiptsAsync()
    {
        (string Store, int Kind, decimal Amount, string Counterparty, string Note)[] receipts =
        [
            ("STORE01", 2, 18450m, "Meralco", "Electricity bill for the month."),
            ("STORE02", 2, 1260m, "Wilcon Depot", "Cleaning supplies, mops and a replacement fan."),
            ("STORE01", 1, 2350m, "Walk-in customer", "Special order of party ice, paid in cash."),
            ("STORE03", 3, 25000m, "Owner", "Weekly owner draw."),
            ("STORE03", 2, 540m, "Water refilling station", "Drinking water containers for staff."),
            ("STORE02", 2, 3800m, "Manila Water", "Water bill for the month."),
        ];

        foreach ((string store, int kind, decimal amount, string counterparty, string note) in receipts)
        {
            await Api.PostAsync(
                "/api/v1/receipts",
                new { locationId = Location(store), kind, amount, counterparty, note, referenceNumber = (string?)null },
                StoreManager(store));
        }
    }

    private async Task QuarantineAsync()
        => await Api.PostAsync(
            "/api/v1/quarantine",
            new
            {
                locationId = Location("STORE02"),
                lines = new[]
                {
                    new { barcode = "4809999000011", quantity = 6m, unitCost = (decimal?)null, claimedProductName = (string?)"Unlabelled canned goods" },
                    new { barcode = "4809999000028", quantity = 2m, unitCost = (decimal?)null, claimedProductName = (string?)"Shampoo sachets, unknown brand" },
                },
                note = "Found in the back room with no delivery record.",
            },
            StoreManager("STORE02"));
}
