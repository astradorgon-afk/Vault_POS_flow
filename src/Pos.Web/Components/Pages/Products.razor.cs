using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Shared.Scanning;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The product catalogue, and the desk where an unidentified barcode becomes a
/// known one.
/// </summary>
/// <remarks>
/// Every scanning screen in the system — receiving, counting, transfers, the
/// register — resolves a code through the same lookup. Until a code is
/// registered against a product those screens can only report that they do not
/// recognise it, so this page is where that is answered.
/// </remarks>
public sealed partial class Products : IDisposable
{
    [Inject] private VaultFlowApiClient Api { get; set; } = default!;

    [Inject] private UserSession Session { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly List<PosCatalogProduct> products = [];
    private readonly List<PosCategory> categories = [];
    private readonly List<PosUnitOfMeasure> units = [];
    private readonly List<PosBrand> brands = [];
    private readonly List<PosSupplier> suppliers = [];
    private readonly HashSet<Guid> collapsedCategories = [];

    private CancellationTokenSource? inFlight;
    private int searchGeneration;
    private int loadGeneration;
    private int total;
    private int offset;
    private int pageSize = 25;
    private bool loading = true;
    private bool looking;
    private bool working;
    private bool showCreate;
    private bool showBarcodeDesk;
    private bool includeInactive;
    private string search = string.Empty;
    private string? message;

    // Barcode desk.
    private string lookupCode = string.Empty;
    private string? lookupEchoed;
    private string? lookupError;
    private string? lookupMissing;
    private PosCatalogProduct? lookupResult;
    private string? attaching;
    private PosCatalogProduct? attachTarget;
    private string? barcodeTaken;
    private int barcodeCheckGeneration;


    private int PageCount => Math.Max(1, (total + pageSize - 1) / pageSize);

    private int CurrentPage => offset / pageSize + 1;

    private IReadOnlyList<int?> PageLinks
    {
        get
        {
            List<int?> links = [];
            int start = Math.Max(1, Math.Min(CurrentPage - 1, PageCount - 3));
            int end = Math.Min(PageCount, start + 3);
            if (start > 1)
            {
                links.Add(1);
                if (start > 2)
                {
                    links.Add(null);
                }
            }

            for (int page = start; page <= end; page++)
            {
                links.Add(page);
            }
            if (end < PageCount)
            {
                if (end < PageCount - 1)
                {
                    links.Add(null);
                }
                links.Add(PageCount);
            }

            return links;
        }
    }

    private Task GoToPageAsync(int page) => LoadAsync((page - 1) * pageSize);

    private void ToggleCategory(Guid categoryId)
    {
        if (!collapsedCategories.Add(categoryId))
        {
            collapsedCategories.Remove(categoryId);
        }
    }

    private void ToggleBarcodeDesk() => showBarcodeDesk = !showBarcodeDesk;

    private string CategoryName(Guid categoryId)
        => categories.FirstOrDefault(category => category.Id == categoryId)?.Name ?? "Other products";

    private static string CategoryTone(string name)
    {
        if (name.Contains("hair", StringComparison.OrdinalIgnoreCase) || name.Contains("shampoo", StringComparison.OrdinalIgnoreCase))
        {
            return "tone-mint";
        }

        if (name.Contains("personal", StringComparison.OrdinalIgnoreCase) || name.Contains("beauty", StringComparison.OrdinalIgnoreCase))
        {
            return "tone-blue";
        }

        if (name.Contains("house", StringComparison.OrdinalIgnoreCase) || name.Contains("clean", StringComparison.OrdinalIgnoreCase))
        {
            return "tone-lilac";
        }

        if (name.Contains("pet", StringComparison.OrdinalIgnoreCase))
        {
            return "tone-peach";
        }

        if (name.Contains("food", StringComparison.OrdinalIgnoreCase) || name.Contains("beverage", StringComparison.OrdinalIgnoreCase))
        {
            return "tone-rose";
        }

        return "tone-slate";
    }

    private static string CategoryIcon(string name) => CategoryTone(name) switch
    {
        "tone-mint" => "bottle",
        "tone-blue" => "bottle",
        "tone-lilac" => "home",
        "tone-peach" => "paw",
        "tone-rose" => "food",
        _ => "more",
    };

    private Task PreviousAsync() => LoadAsync(Math.Max(0, offset - pageSize));

    private Task NextAsync() => LoadAsync(offset + pageSize);

    private bool CanCreate => Session.HasPermission("product.create");

    private bool CanManageBarcodes => Session.HasPermission("product.barcode.manage");


    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadLookupsAsync();
        await ReloadAsync();
    }

    /// <summary>Stops a pending search, and any load still running.</summary>
    public void Dispose()
    {
        searchGeneration++;
        inFlight?.Cancel();
        inFlight?.Dispose();
        inFlight = null;
    }

    private async Task LoadLookupsAsync()
    {
        CancellationToken token = CancellationToken.None;

        ApiResult<List<PosCategory>> categoryResult = await Api.GetCategoriesAsync(token);
        if (categoryResult is { IsSuccess: true, Value: { } loadedCategories })
        {
            categories.AddRange(loadedCategories.OrderBy(c => c.Name));
        }

        ApiResult<List<PosUnitOfMeasure>> unitResult = await Api.GetUnitsAsync(token);
        if (unitResult is { IsSuccess: true, Value: { } loadedUnits })
        {
            units.AddRange(loadedUnits.OrderBy(u => u.Code));
        }

        // Brands and suppliers are optional on a product, and listing suppliers
        // needs a purchasing permission a catalogue clerk may not hold. A
        // refusal here leaves the dropdown empty rather than blocking the page.
        ApiResult<List<PosBrand>> brandResult = await Api.GetBrandsAsync(token);
        if (brandResult is { IsSuccess: true, Value: { } loadedBrands })
        {
            brands.AddRange(loadedBrands.Where(b => b.IsActive).OrderBy(b => b.Name));
        }

        ApiResult<List<PosSupplier>> supplierResult = await Api.GetSuppliersAsync(token);
        if (supplierResult is { IsSuccess: true, Value: { } loadedSuppliers })
        {
            suppliers.AddRange(loadedSuppliers.Where(s => s.IsActive).OrderBy(s => s.Name));
        }
    }

    /// <summary>Runs the search a moment after typing stops.</summary>
    private async Task OnSearchTypedAsync()
    {
        int generation = ++searchGeneration;
        await Task.Delay(350);
        if (generation == searchGeneration)
        {
            await ReloadAsync();
        }
    }

    /// <summary>Reloads from the first page, as a changed filter should.</summary>
    private Task ReloadAsync() => LoadAsync(0);

    /// <summary>
    /// Loads one page, cancelling whatever was still loading.
    /// </summary>
    /// <remarks>
    /// Two loads can be in the air at once — a debounced search landing just
    /// after Next was pressed. Without the generation check the slower one
    /// wins and the table shows a page nobody asked for.
    /// </remarks>
    private async Task LoadAsync(int nextOffset)
    {
        inFlight?.Cancel();
        inFlight?.Dispose();
        CancellationTokenSource source = new();
        inFlight = source;
        int generation = ++loadGeneration;

        loading = true;
        try
        {
            ApiResult<PosCatalogPage> result = await Api.GetCatalogProductsPageAsync(
                search, includeInactive, Math.Max(0, nextOffset), pageSize, source.Token);
            if (generation != loadGeneration)
            {
                return;
            }

            if (result is not { IsSuccess: true, Value: { } page })
            {
                message = result.Error ?? "The catalogue could not be loaded.";
                return;
            }

            products.Clear();
            products.AddRange(page.Items);
            total = page.Total;
            offset = page.Offset;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or the page is gone.
        }
        finally
        {
            if (generation == loadGeneration)
            {
                loading = false;
            }

            if (ReferenceEquals(inFlight, source))
            {
                inFlight = null;
            }

            source.Dispose();
        }
    }

    /// <summary>
    /// Sends a scan wherever it is wanted: into the create form while it is
    /// open, otherwise to the identify box.
    /// </summary>
    /// <remarks>
    /// Both live on the same page and both are scanned into. Routing every
    /// scan to the identify box would mean a code scanned while filling the
    /// form quietly went somewhere the operator was not looking.
    /// </remarks>
    private Task OnScannedAsync(BarcodeScan scan)
    {
        if (showCreate && CanCreate)
        {
            newBarcode = scan.Code;
            return OnNewBarcodeTypedAsync();
        }

        showBarcodeDesk = true;
        lookupCode = scan.Code;
        return LookupAsync();
    }



    private Task OnLookupKeyAsync(KeyboardEventArgs e)
        => e.Key == "Enter" ? LookupAsync() : Task.CompletedTask;

    /// <summary>
    /// Asks the catalogue what a code is. An unknown code is not an error here
    /// — it is the question this page answers.
    /// </summary>
    private async Task LookupAsync()
    {
        string code = BarcodeText.Clean(lookupCode);
        if (code.Length == 0)
        {
            return;
        }

        looking = true;
        lookupResult = null;
        lookupMissing = null;
        lookupError = null;
        message = null;
        try
        {
            ApiResult<PosScannedProduct> scanned = await Api.FindProductByScanAsync(code, CancellationToken.None);
            if (scanned is { IsSuccess: true, Value: { } hit })
            {
                ApiResult<PosCatalogProduct> full = await Api.GetCatalogProductAsync(hit.Id, CancellationToken.None);
                lookupResult = full.Value;
                lookupEchoed = code;
                if (lookupResult is null)
                {
                    lookupError = full.Error ?? "That product could not be read.";
                }

                return;
            }

            if (scanned.ErrorCode is "catalog.barcode_unknown" or "catalog.barcode_retired")
            {
                lookupMissing = code;
                return;
            }

            lookupError = scanned.Error ?? "That code could not be looked up.";
        }
        finally
        {
            looking = false;
        }
    }

    private void BeginAttach()
    {
        attaching = lookupMissing;
        attachTarget = null;
        showCreate = false;
    }

    private void CancelAttach()
    {
        attaching = null;
        attachTarget = null;
    }

    private Task OnRowAsync(PosCatalogProduct product)
    {
        if (attaching is not null && CanManageBarcodes)
        {
            attachTarget = product;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo($"/catalog/products/{product.Id:D}");
        return Task.CompletedTask;
    }

    private async Task ConfirmAttachAsync()
    {
        if (attaching is not { } code || attachTarget is not { } target)
        {
            return;
        }

        working = true;
        message = null;
        try
        {
            ApiResult<PosReference> result = await Api.AddProductBarcodeAsync(
                target.Id, new PosAddProductBarcode(code), CancellationToken.None);
            if (!result.IsSuccess)
            {
                message = result.Error;
                return;
            }

            message = $"{code} now scans as {target.Name}.";
            attaching = null;
            attachTarget = null;
            lookupMissing = null;
            lookupCode = string.Empty;
            await ReloadAsync();
        }
        finally
        {
            working = false;
        }
    }

}
