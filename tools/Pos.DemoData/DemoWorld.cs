using System.Globalization;
using System.Security.Cryptography;
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
/// A store's till: an enrolled desktop or phone register, with the cashier who
/// works it and the store supervisor (who voids and refunds) signed in at it.
/// </summary>
internal sealed record DemoRegister(
    string Store,
    Guid LocationId,
    Guid DeviceId,
    string ShortCode,
    DemoSession Cashier,
    DemoSession Supervisor);

/// <summary>
/// Everything the demo reads before it writes: signed-in accounts, locations,
/// the catalogue with prices, customers and one enrolled register per store.
/// </summary>
internal sealed class DemoWorld
{
    public const string Password = "cash1234";

    private const int WindowsPlatform = 1;
    private const int AndroidPlatform = 2;
    private const int PendingEnrolment = 0;
    private const int Active = 1;

    /// <summary>
    /// Each store's two demo tills and who works them: the first counter takes
    /// the morning shift, the second the afternoon one. The real desktop
    /// register (W01) is never used: its document counter lives on that machine,
    /// so demo numbers on it would collide with the next real sale.
    /// </summary>
    private static readonly (string Store, string ShortCode, string Name, int Platform, string Cashier, string Supervisor)[] Tills =
    [
        ("STORE01", "W02", "Legazpi counter 2", WindowsPlatform, "cashier", "manager"),
        ("STORE01", "W04", "Legazpi counter 3", WindowsPlatform, "cashier1b", "manager"),
        ("STORE02", "W03", "Morato counter 1", WindowsPlatform, "cashier2", "manager2"),
        ("STORE02", "W05", "Morato counter 2", WindowsPlatform, "cashier2b", "manager2"),
        ("STORE03", "A01", "Kapitolyo phone till 1", AndroidPlatform, "cashier3", "manager3"),
        ("STORE03", "A02", "Kapitolyo phone till 2", AndroidPlatform, "cashier3b", "manager3"),
    ];

    /// <summary>How many price lists load at once.</summary>
    private const int PriceLoadConcurrency = 8;

    public required DemoApi Api { get; init; }

    /// <summary>Back-office sessions (not bound to a register), by user name.</summary>
    public required IReadOnlyDictionary<string, DemoSession> Users { get; init; }

    public required IReadOnlyDictionary<string, Guid> Locations { get; init; }

    public required IReadOnlyDictionary<string, DemoProduct> Products { get; init; }

    public required IReadOnlyList<Guid> Customers { get; init; }

    /// <summary>Each store's registers, by store code: the morning counter first,
    /// then the afternoon one.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<DemoRegister>> Registers { get; init; }

    public DemoSession Owner => Users["owner"];

    public DemoProduct Product(string sku) => Products[sku];

    /// <summary>Signs the accounts in and loads the reference data.</summary>
    public static async Task<DemoWorld> LoadAsync(DemoApi api)
    {
        Dictionary<string, DemoSession> users = [];
        foreach (string userName in new[] { "owner", "warehouse", "inventory", "manager", "manager2", "manager3" })
        {
            users[userName] = await api.SignInAsync(userName, Password);
        }

        DemoSession owner = users["owner"];

        Dictionary<string, Guid> locations = [];
        foreach (JsonNode? location in DemoApi.Items(await api.GetAsync("/api/v1/locations", owner)))
        {
            locations[location!["code"]!.GetValue<string>()] = location["id"]!.GetValue<Guid>();
        }

        List<JsonNode> listed = [];
        for (int offset = 0; ; offset += 200)
        {
            JsonArray page = DemoApi.Items(await api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/catalog/products?offset={offset}&limit=200"), owner));
            listed.AddRange(page.OfType<JsonNode>().Where(p => p["isActive"]?.GetValue<bool>() == true));
            if (page.Count < 200)
            {
                break;
            }
        }

        // A grocery's price lists, a few at a time.
        using SemaphoreSlim gate = new(PriceLoadConcurrency);
        DemoProduct[] loaded = await Task.WhenAll(listed.Select(async product =>
        {
            await gate.WaitAsync();
            try
            {
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

                return new DemoProduct(
                    id,
                    product["sku"]!.GetValue<string>(),
                    product["name"]!.GetValue<string>(),
                    product["baseUnitOfMeasureId"]!.GetValue<Guid>(),
                    prices);
            }
            finally
            {
                gate.Release();
            }
        }));
        Dictionary<string, DemoProduct> products = loaded.ToDictionary(p => p.Sku);

        List<Guid> customers = [];
        for (int page = 1; ; page++)
        {
            JsonArray listedCustomers = DemoApi.Items(await api.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/customers?page={page}&pageSize=100"), owner));
            customers.AddRange(listedCustomers.Select(c => c!["id"]!.GetValue<Guid>()));
            if (listedCustomers.Count < 100)
            {
                break;
            }
        }

        Dictionary<string, List<DemoRegister>> registers = [];
        foreach ((string store, string shortCode, string name, int platform, string cashier, string supervisor) in Tills)
        {
            Guid deviceId = await EnsureEnrolledRegisterAsync(api, owner, locations[store], shortCode, name, platform);
            DemoRegister register = new(
                store,
                locations[store],
                deviceId,
                shortCode,
                await api.SignInAsync(cashier, Password, deviceId),
                await api.SignInAsync(supervisor, Password, deviceId));
            if (!registers.TryGetValue(store, out List<DemoRegister>? tills))
            {
                registers[store] = tills = [];
            }

            tills.Add(register);
        }

        return new DemoWorld
        {
            Api = api,
            Users = users,
            Locations = locations,
            Products = products,
            Customers = customers,
            Registers = registers.ToDictionary(r => r.Key, r => (IReadOnlyList<DemoRegister>)r.Value),
        };
    }

    /// <summary>Finds the store's demo register, or registers and enrols it the
    /// way a real one is: an administrator registers the device and issues a
    /// one-time code, and the device redeems it with its key thumbprint.</summary>
    private static async Task<Guid> EnsureEnrolledRegisterAsync(
        DemoApi api, DemoSession owner, Guid locationId, string shortCode, string name, int platform)
    {
        Guid? deviceId = null;
        int status = -1;
        foreach (JsonNode? device in DemoApi.Items(await api.GetAsync(
                     string.Create(CultureInfo.InvariantCulture, $"/api/v1/devices?locationId={locationId:D}"), owner)))
        {
            if (device!["shortCode"]?.GetValue<string>() == shortCode)
            {
                deviceId = device["id"]!.GetValue<Guid>();
                status = device["status"]!.GetValue<int>();
            }
        }

        if (status == Active)
        {
            return deviceId!.Value;
        }

        string enrolmentCode;
        if (deviceId is null)
        {
            JsonNode registered = await api.PostAsync(
                "/api/v1/devices", new { shortCode, name, locationId, platform }, owner)
                ?? throw new DemoApiException($"Registering {shortCode} returned no body.");
            deviceId = registered["deviceId"]!.GetValue<Guid>();
            enrolmentCode = registered["enrolmentCode"]!.GetValue<string>();
        }
        else if (status == PendingEnrolment)
        {
            JsonNode reissued = await api.PostAsync(
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/devices/{deviceId:D}/enrolment-code"), new { }, owner)
                ?? throw new DemoApiException($"Reissuing a code for {shortCode} returned no body.");
            enrolmentCode = (reissued["enrolmentCode"] ?? reissued["code"])!.GetValue<string>();
        }
        else
        {
            throw new DemoApiException($"Register {shortCode} is suspended or revoked; reactivate it or remove it first.");
        }

        await api.PostAnonymousAsync("/api/v1/devices/enrol", new
        {
            enrolmentCode,
            publicKeyThumbprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            platform,
            appVersion = "1.0.0-demo",
            osVersion = platform == AndroidPlatform ? "Android 14" : "Windows 11",
        });

        Console.WriteLine($"  registered and enrolled register {shortCode} ({name})");
        return deviceId.Value;
    }
}
