using System.Diagnostics;
using Pos.DemoData;

// Fills a development VaultFlow with a month of realistic activity through the
// public API, so the web dashboard, the reports and the desktop register all
// show the same demo business.
//
//   dotnet run --project tools/Pos.DemoData [--api http://localhost:5177] [--days 30] [--force]
//
// The API must be running in Development: its seeder provides the catalogue,
// prices dated back to the start of the history, customers and the shared
// development accounts (password cash1234). Running it again is safe: store-days
// that already have sales are skipped, and the back-office activity is posted
// once unless --force is given.

string apiUrl = "http://localhost:5177";
int days = 30;
bool force = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--api" when i + 1 < args.Length:
            apiUrl = args[++i];
            break;
        case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed) && parsed is >= 0 and <= 34:
            days = parsed;
            i++;
            break;
        case "--force":
            force = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown or invalid argument '{args[i]}'. Usage: [--api URL] [--days 0-34] [--force]");
            return 2;
    }
}

Stopwatch clock = Stopwatch.StartNew();
using DemoApi api = new(new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(60) });

Console.WriteLine($"VaultFlow demo data -> {apiUrl}");
Console.WriteLine("Signing in and loading the catalogue...");

DemoWorld world;
try
{
    world = await DemoWorld.LoadAsync(api);
}
catch (Exception ex) when (ex is DemoApiException or HttpRequestException)
{
    Console.Error.WriteLine($"Could not reach a development API at {apiUrl}: {ex.Message}");
    Console.Error.WriteLine("Start it with scripts/dev-desktop.ps1 and try again.");
    return 1;
}

Console.WriteLine($"  {world.Products.Count} products, {world.Customers.Count} customers, {world.Registers.Count} store registers");

// Store-days that already have sales are skipped, so an interrupted run can
// simply be started again.
SalesHistory sales = new(world, new Random(20260924));
Console.WriteLine($"Posting {days} days of trading at three stores...");
await sales.RunAsync(days);
Console.WriteLine($"  {sales.SalesPosted} sales, {sales.Voids} voids, {sales.Returns} returns across {sales.Shifts} shifts");

BackOffice office = new(world);
if (!force && await office.AlreadyPostedAsync())
{
    Console.WriteLine("Back-office demo activity is already present; use --force to post another round.");
}
else
{
    Console.WriteLine("Posting back-office activity...");
    await office.RunAsync();
}

Console.WriteLine($"Done in {clock.Elapsed:mm\\:ss}. {(office.Failures == 0 ? "Every step succeeded." : $"{office.Failures} step(s) failed; see above.")}");
return office.Failures == 0 ? 0 : 1;
