using System.Globalization;
using System.Text.Json.Nodes;

namespace Pos.DemoData;

/// <summary>
/// Posts the back-office side of the demo, each workflow left at a different
/// stage so every list and detail page has something to show: purchase orders
/// (draft, awaiting approval, received, received short), transfers (draft to
/// received), stock counts, stock adjustments, payment receipts and a
/// quarantine incident. Each workflow runs as its own step: one that the
/// server refuses is reported and the rest carry on.
/// </summary>
internal sealed class BackOffice(DemoWorld world)
{
    private const int AvailableState = 0;
    private const int CycleCount = 2;
    private const int CategoryCount = 3;

    private DemoApi Api => world.Api;

    private DemoSession User(string name) => world.Users[name];

    private Guid Location(string code) => world.Locations[code];

    public int Failures { get; private set; }

    /// <summary>True when an earlier run posted the back-office activity. Its
    /// marker is a purchase order to Island Beverages (SUP3), which only the demo raises.</summary>
    public async Task<bool> AlreadyPostedAsync()
    {
        Guid marker = await SupplierAsync("SUP3");
        return DemoApi.Items(await Api.GetAsync("/api/v1/purchasing/orders", world.Owner))
            .Any(order => order?["supplierId"]?.GetValue<Guid>() == marker);
    }

    public async Task RunAsync()
    {
        await StepAsync("Purchase order received in full (Metro Distribution)", () => PurchaseOrderAsync(
            "SUP1", receive: Receive.Full,
            ("NOODLE-55", 600m, 11m), ("TUNA-155", 240m, 33m), ("CHIPS-60", 300m, 27m), ("CHOCBAR-40", 240m, 33m)));

        await StepAsync("Purchase order received short (Island Beverages)", () => PurchaseOrderAsync(
            "SUP3", receive: Receive.Short,
            ("OJ-1L", 120m, 74m), ("ICETEA-500", 300m, 25m), ("ENERGY-250", 240m, 31m)));

        await StepAsync("Purchase order awaiting approval (HomeCare Supply)", () => PurchaseOrderAsync(
            "SUP4", receive: Receive.SubmitOnly,
            ("SHAMPOO-340", 60m, 128m), ("TPASTE-150", 120m, 75m), ("SOAP-135", 200m, 31m)));

        await StepAsync("Purchase order draft (Northern Bakers)", () => PurchaseOrderAsync(
            "SUP5", receive: Receive.DraftOnly,
            ("BREAD-LOAF", 80m, 55m), ("PANDESAL-10", 100m, 36m), ("COOKIES-200", 60m, 64m)));

        await StepAsync("Transfer to Store One, received", () => TransferAsync(
            "STORE01", "manager", Stage.Received,
            ("BUTTER-225", 30m), ("NUGGETS-500", 20m), ("PANDESAL-10", 40m), ("ALCOHOL-500", 30m)));

        await StepAsync("Transfer to Store Two, in transit", () => TransferAsync(
            "STORE02", "admin", Stage.Dispatched,
            ("NOODLE-55", 150m), ("TUNA-155", 60m), ("EGGS-DZ", 24m)));

        await StepAsync("Transfer to Store Three, approved", () => TransferAsync(
            "STORE03", "admin", Stage.Approved,
            ("WATER-500", 200m), ("SODA-1L", 48m), ("CHIPS-60", 60m)));

        await StepAsync("Transfer to Store One, awaiting approval", () => TransferAsync(
            "STORE01", "manager", Stage.Submitted,
            ("RICE-01", 20m), ("OIL-1L", 24m)));

        await StepAsync("Transfer to Store Three, draft", () => TransferAsync(
            "STORE03", "admin", Stage.Draft,
            ("COFFEE-3IN1", 40m)));

        await StepAsync("Cycle count at Store Two, approved", () => CountAsync(
            "STORE02", approve: true, "SODA-1L", "CHIPS-60", "TUNA-155", "SOAP-135", "YOGURT-110"));

        await StepAsync("Stock count at Store Three, in progress", () => CountAsync(
            "STORE03", approve: false, "BREAD-LOAF", "PANDESAL-10", "COOKIES-200"));

        // Runs after the Store One transfer is received, so the nuggets are on hand.
        await StepAsync("Adjustment at Store One: thawed nuggets, approved", () => AdjustmentAsync(
            "STORE01", reason: 1, "Freezer fault overnight; two packs thawed.", Adjust.Approve, ("NUGGETS-500", -2m)));

        await StepAsync("Adjustment at Store Three: spoiled bread, awaiting approval", () => AdjustmentAsync(
            "STORE03", reason: 3, "Past best-before, pulled from the shelf.", Adjust.Submit, ("BREAD-LOAF", -3m), ("PANDESAL-10", -2m)));

        await StepAsync("Adjustment at the warehouse: broken bottles, draft", () => AdjustmentAsync(
            "MAIN", reason: 8, "Pallet dropped at the loading bay.", Adjust.Draft, ("SOY-1L", -4m), ("VINEGAR-1L", -3m)));

        await StepAsync("Payment receipts", ReceiptsAsync);

        await StepAsync("Quarantine incident at Store Two", QuarantineAsync);
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
        Full,
        Short,
    }

    /// <summary>Raises a warehouse purchase order as Inventory Staff, then walks it
    /// to the requested stage: the owner approves and sends it, and the
    /// warehouse receives it in full or short with damage.</summary>
    private async Task PurchaseOrderAsync(string supplierCode, Receive receive, params (string Sku, decimal Quantity, decimal Cost)[] lines)
    {
        Guid supplierId = await SupplierAsync(supplierCode);
        DemoSession inventory = User("inventory");
        DemoSession owner = world.Owner;

        Guid orderId = await Api.CreateAsync(
            "/api/v1/purchasing/orders",
            new
            {
                supplierId,
                destinationLocationId = Location("MAIN"),
                lines = lines.Select(l => new
                {
                    productId = world.Product(l.Sku).Id,
                    unitOfMeasureId = world.Product(l.Sku).UnitId,
                    orderedQuantity = l.Quantity,
                    unitCost = l.Cost,
                }),
                expectedAtUtc = DateTimeOffset.UtcNow.AddDays(3),
            },
            inventory);

        if (receive == Receive.DraftOnly)
        {
            return;
        }

        string order = string.Create(CultureInfo.InvariantCulture, $"/api/v1/purchasing/orders/{orderId:D}");
        await Api.PostAsync($"{order}/submit", new { notes = "Weekly restock." }, inventory);
        if (receive == Receive.SubmitOnly)
        {
            return;
        }

        await Api.PostAsync($"{order}/approve", new { notes = "Approved for this week's delivery." }, owner);
        await Api.PostAsync($"{order}/send", new { }, owner);

        JsonArray orderLines = DemoApi.Items((await Api.GetAsync(order, owner))?["lines"]);
        bool first = true;
        List<object> received = [];
        foreach (JsonNode? line in orderLines)
        {
            decimal ordered = line!["orderedQuantity"]!.GetValue<decimal>();
            decimal short_ = receive == Receive.Short && first ? Math.Round(ordered * 0.15m) : 0m;
            decimal damaged = receive == Receive.Short && first ? 5m : 0m;
            first = false;

            received.Add(new
            {
                purchaseOrderLineId = line["id"]!.GetValue<Guid>(),
                quantityReceived = ordered - short_ - damaged,
                quantityDamaged = damaged,
                quantityWrongItem = 0m,
                quantityExpired = 0m,
                unitCost = line["unitCost"]!.GetValue<decimal>(),
            });
        }

        await Api.PostAsync($"{order}/receipts", new { lines = received, documentsMissing = false }, inventory);
    }

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

    private enum Stage
    {
        Draft,
        Submitted,
        Approved,
        Dispatched,
        Received,
    }

    /// <summary>Moves stock from the Main Warehouse to a store. The store side
    /// requests it, the owner approves it, the warehouse picks and dispatches
    /// it, and the store receives it.</summary>
    private async Task TransferAsync(string store, string requester, Stage stage, params (string Sku, decimal Quantity)[] lines)
    {
        DemoSession storeUser = User(requester);
        DemoSession owner = world.Owner;
        DemoSession warehouse = User("inventory");

        Guid transferId = await Api.CreateAsync(
            "/api/v1/transfers",
            new
            {
                sourceLocationId = Location("MAIN"),
                destinationLocationId = Location(store),
                lines = lines.Select(l => new { productId = world.Product(l.Sku).Id, quantity = l.Quantity, note = (string?)null }),
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
        await Api.PostAsync($"{transfer}/review", new { note = "Checking warehouse availability." }, owner);
        await Api.PostAsync($"{transfer}/approve", new { note = "Approved from the warehouse plan." }, owner);
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

    /// <summary>Opens a count over a few products, records the shelf, and when
    /// asked submits and approves it. One line is recorded two short, so the
    /// approved count posts a small variance.</summary>
    private async Task CountAsync(string store, bool approve, params string[] skus)
    {
        DemoSession counter = User("admin");
        Guid countId = await Api.CreateAsync(
            "/api/v1/inventory/counts",
            new
            {
                locationId = Location(store),
                kind = approve ? CycleCount : CategoryCount,
                productIds = approve ? skus.Select(s => world.Product(s).Id).ToArray() : null,
                categoryIds = approve ? null : new[] { await CategoryOfAsync(skus[0]) },
                note = approve ? "Monthly cycle count." : "Bakery shelf count.",
            },
            counter);

        string count = string.Create(CultureInfo.InvariantCulture, $"/api/v1/inventory/counts/{countId:D}");
        JsonArray lines = DemoApi.Items((await Api.GetAsync(count, counter))?["lines"]);

        List<object> recorded = [];
        bool first = true;
        foreach (JsonNode line in lines.OfType<JsonNode>())
        {
            decimal system = SystemQuantity(line);
            recorded.Add(new
            {
                productId = line["productId"]!.GetValue<Guid>(),
                physicalQuantity = first ? Math.Max(0m, system - 2m) : system,
                batchId = (Guid?)null,
            });
            first = false;

            // An in-progress count has only part of the shelf recorded.
            if (!approve && recorded.Count == 2)
            {
                break;
            }
        }

        await Api.PostAsync($"{count}/lines", new { lines = recorded }, counter);
        if (!approve)
        {
            return;
        }

        await Api.PostAsync($"{count}/submit", new { }, counter);
        await Api.PostAsync($"{count}/approve", new { reason = "Variance checked against the shelf." }, world.Owner);
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

    /// <summary>Raises a stock adjustment; the store raises and submits it, the
    /// owner approves it, which posts it to the ledger.</summary>
    private async Task AdjustmentAsync(string location, int reason, string notes, Adjust stage, params (string Sku, decimal Delta)[] lines)
    {
        DemoSession raiser = location == "STORE01" ? User("manager") : User("admin");
        Guid adjustmentId = await Api.CreateAsync(
            "/api/v1/inventory/adjustments",
            new
            {
                locationId = Location(location),
                reason,
                lines = lines.Select(l => new
                {
                    productId = world.Product(l.Sku).Id,
                    state = AvailableState,
                    quantityDelta = l.Delta,
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

    /// <summary>Cash moving outside a sale today: a walk-in slip, two branch
    /// expenses and an owner withdrawal.</summary>
    private async Task ReceiptsAsync()
    {
        (string Store, int Kind, decimal Amount, string Counterparty, string Note)[] receipts =
        [
            ("STORE01", 2, 1850m, "Meralco", "Electricity bill for the month."),
            ("STORE02", 2, 420m, "Ace Hardware", "Cleaning supplies and a mop."),
            ("STORE01", 1, 350m, "Walk-in customer", "Special order of ice, paid in cash."),
            ("STORE03", 3, 5000m, "Owner", "Weekly owner draw."),
            ("STORE03", 2, 260m, "Water Station", "Two containers of drinking water for staff."),
        ];

        foreach ((string store, int kind, decimal amount, string counterparty, string note) in receipts)
        {
            await Api.PostAsync(
                "/api/v1/receipts",
                new { locationId = Location(store), kind, amount, counterparty, note, referenceNumber = (string?)null },
                world.Owner);
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
            User("admin"));
}
