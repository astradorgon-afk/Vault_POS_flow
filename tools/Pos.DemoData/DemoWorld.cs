using System.Globalization;
using System.Text.Json.Nodes;

namespace Pos.DemoData;

/// <summary>A product as the demo needs it: identity, unit and price history.</summary>
internal sealed record DemoProduct(Guid Id, string Sku, string Name, Guid UnitId, IReadOnlyList<DemoPrice> Prices)
{
    /// <summary>The business-wide price in effect at an instant, as the server resolves it.</summary>
    public decimal? PriceAt(DateTimeOffset atUtc)
        => Prices
            .Where(p => p.LocationId is null && p.FromUtc <= atUtc && (p.ToUtc is null || atUtc < p.ToUtc))
            .Select(p => (decimal?)p.Amount)
            .FirstOrDefault();
}

/// <summary>One row of a product's price history.</summary>
internal sealed record DemoPrice(Guid? LocationId, decimal Amount, DateTimeOffset FromUtc, DateTimeOffset? ToUtc);

/// <summary>
/// Everything the demo reads before it writes: signed-in accounts, locations,
/// the catalogue with prices, customers and one browser register per store.
/// </summary>
internal sealed class DemoWorld
{
    public const string Password = "cash1234";

    public required DemoApi Api { get; init; }

    public required IReadOnlyDictionary<string, DemoSession> Users { get; init; }

    public required IReadOnlyDictionary<string, Guid> Locations { get; init; }

    public required IReadOnlyDictionary<string, DemoProduct> Products { get; init; }

    public required IReadOnlyList<Guid> Customers { get; init; }

    public required IReadOnlyDictionary<string, Guid> Registers { get; init; }

    public DemoSession Owner => Users["owner"];

    public DemoProduct Product(string sku) => Products[sku];

    /// <summary>Signs every account in and loads the reference data.</summary>
    public static async Task<DemoWorld> LoadAsync(DemoApi api)
    {
        Dictionary<string, DemoSession> users = [];
        foreach (string userName in new[] { "owner", "admin", "manager", "cashier", "inventory" })
        {
            users[userName] = await api.SignInAsync(userName, Password);
        }

        DemoSession owner = users["owner"];

        Dictionary<string, Guid> locations = [];
        foreach (JsonNode? location in DemoApi.Items(await api.GetAsync("/api/v1/locations", owner)))
        {
            locations[location!["code"]!.GetValue<string>()] = location["id"]!.GetValue<Guid>();
        }

        Dictionary<string, DemoProduct> products = [];
        for (int offset = 0; ; offset += 200)
        {
            JsonArray page = DemoApi.Items(await api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/catalog/products?offset={offset}&limit=200"), owner));

            foreach (JsonNode? product in page)
            {
                if (product?["isActive"]?.GetValue<bool>() != true)
                {
                    continue;
                }

                Guid id = product["id"]!.GetValue<Guid>();
                List<DemoPrice> prices = [];
                foreach (JsonNode? price in DemoApi.Items(await api.GetAsync(
                             string.Create(CultureInfo.InvariantCulture, $"/api/v1/catalog/products/{id:D}/prices"), owner)))
                {
                    prices.Add(new DemoPrice(
                        price!["locationId"]?.GetValue<Guid?>(),
                        price["amount"]!.GetValue<decimal>(),
                        price["effectiveFromUtc"]!.GetValue<DateTimeOffset>(),
                        price["effectiveToUtc"]?.GetValue<DateTimeOffset?>()));
                }

                string sku = product["sku"]!.GetValue<string>();
                products[sku] = new DemoProduct(
                    id, sku, product["name"]!.GetValue<string>(), product["baseUnitOfMeasureId"]!.GetValue<Guid>(), prices);
            }

            if (page.Count < 200)
            {
                break;
            }
        }

        List<Guid> customers = [];
        foreach (JsonNode? customer in DemoApi.Items(await api.GetAsync("/api/v1/customers?page=1&pageSize=100", owner)))
        {
            customers.Add(customer!["id"]!.GetValue<Guid>());
        }

        Dictionary<string, Guid> registers = [];
        foreach ((string store, string shortCode) in new[] { ("STORE01", "WB1"), ("STORE02", "WB2"), ("STORE03", "WB3") })
        {
            registers[store] = await EnsureWebRegisterAsync(api, owner, locations[store], shortCode, store);
        }

        return new DemoWorld
        {
            Api = api,
            Users = users,
            Locations = locations,
            Products = products,
            Customers = customers,
            Registers = registers,
        };
    }

    /// <summary>Finds the store's active browser register, or registers one.
    /// Web registers are active on registration: the cashier's session is the
    /// credential, so no enrolment code is involved.</summary>
    private static async Task<Guid> EnsureWebRegisterAsync(
        DemoApi api, DemoSession owner, Guid locationId, string shortCode, string storeCode)
    {
        const int WebPlatform = 3;
        const int ActiveStatus = 1;

        foreach (JsonNode? device in DemoApi.Items(await api.GetAsync(
                     string.Create(CultureInfo.InvariantCulture, $"/api/v1/devices?locationId={locationId:D}"), owner)))
        {
            if (device!["platform"]!.GetValue<int>() == WebPlatform && device["status"]!.GetValue<int>() == ActiveStatus)
            {
                return device["id"]!.GetValue<Guid>();
            }
        }

        Guid id = await api.CreateAsync(
            "/api/v1/devices",
            new { shortCode, name = $"Web till {storeCode[^1]}", locationId, platform = WebPlatform },
            owner);

        Console.WriteLine($"  registered browser register {shortCode} for {storeCode}");
        return id;
    }
}
