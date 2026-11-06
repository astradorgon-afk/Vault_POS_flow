using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Application.Identity;
using Pos.Shared.Scanning;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The purchase order composer: who is being bought from, what is being bought,
/// and a review of both before any of it becomes an order.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The component name is its route: /purchasing/new.")]
public partial class PurchaseOrderNew
{
    private const int MaxMatches = 12;

    /// <summary>The dropdown value standing for "no category fits this".</summary>
    private const string OtherCategoryChoice = "other";

    /// <summary>The catalogue code the "Other" category is kept under.</summary>
    private const string OtherCategoryCode = "OTHER";

    /// <summary>
    /// The warehouse supply request this order is being raised against, when the
    /// owner arrived here from the approvals queue.
    /// </summary>
    [SupplyParameterFromQuery(Name = "supplyRequest")]
    public Guid? SupplyRequestId { get; set; }

    private PosSupplyRequest? supplyRequest;

    private readonly List<PosSupplier> suppliers = [];
    private readonly List<PosLocation> destinations = [];
    private readonly List<PosOrderableProduct> catalogue = [];
    private readonly Dictionary<Guid, PosUnitOfMeasure> units = [];
    private readonly List<PosCategory> categories = [];
    private readonly List<DraftLine> lines = [];

    private bool loading = true;
    private bool saving;
    private string? message;
    private string? scanNote;
    private Guid? supplierId;
    private Guid destinationId;
    private DateOnly? expectedOn;
    private string productSearch = string.Empty;
    private bool onlySupplierProducts;
    private bool useCustomSupplier;
    private string customSupplierName = string.Empty;
    private string newProductName = string.Empty;
    private string newProductSku = string.Empty;
    private string newProductBarcode = string.Empty;
    private decimal newProductCost;
    private string newProductCategory = string.Empty;
    private Guid newProductUnitId;

    private bool detailsOpen = true;
    private bool customProductOpen;
    private bool reviewOpen;
    private ElementReference sheetElement;
    private bool focusSheet;

    /// <summary>
    /// Whether this buyer signs their own orders off. The owner is the only
    /// role the approval gate lets approve what they raised, so for them the
    /// review step is a formality that costs a round trip; everyone else still
    /// hands the order to a second pair of eyes.
    /// </summary>
    private bool SkipsApproval => Session.Current?.User.Roles.Contains(Roles.Owner, StringComparer.Ordinal) == true
        && Session.HasPermission("purchase.approve");

    private PosSupplier? Supplier => suppliers.FirstOrDefault(s => s.Id == supplierId);

    private bool HasSupplier => useCustomSupplier ? !string.IsNullOrWhiteSpace(customSupplierName) : supplierId is not null;

    private bool CanAddCustomProduct => !string.IsNullOrWhiteSpace(newProductName)
        && newProductCategory.Length > 0 && newProductUnitId != Guid.Empty && newProductCost >= 0;

    /// <summary>Whether the first stop is settled: a supplier, a destination and a date.</summary>
    private bool DetailsDone => HasSupplier && destinationId != Guid.Empty && expectedOn is not null;

    /// <summary>Whether there is something to buy, at quantities and costs that stand up.</summary>
    private bool ProductsDone => lines.Count > 0 && lines.All(line => line.IsValid);

    private bool IsReady => Missing.Count == 0;

    private decimal TotalUnits => lines.Sum(line => line.Quantity);

    private decimal OrderTotal => lines.Where(line => line.IsValid).Sum(line => line.Total);

    private string SupplierLabel => useCustomSupplier
        ? customSupplierName.Trim() is { Length: > 0 } typed ? typed : "Choose a supplier"
        : Supplier?.Name ?? "Choose a supplier";

    private string DestinationLabel => destinations.FirstOrDefault(d => d.Id == destinationId)?.Name ?? "Choose a destination";

    private string ExpectedLabel => expectedOn?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "Set a date";

    /// <summary>
    /// Whether the product list is worth showing. Nothing is listed until there
    /// is something to narrow the catalogue by, so the page does not open on a
    /// wall of products nobody asked to see.
    /// </summary>
    private bool ShowResults => productSearch.Trim().Length > 0 || onlySupplierProducts;

    private IEnumerable<PosOrderableProduct> Candidates
    {
        get
        {
            string term = productSearch.Trim();
            return catalogue
                .Where(p => p.IsActive)
                .Where(p => !onlySupplierProducts || p.PrimarySupplierId == supplierId)
                .Where(p => term.Length == 0
                    || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<PosOrderableProduct> Matches => ShowResults
        ? [.. Candidates.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(MaxMatches)]
        : [];

    private int MatchTotal => Candidates.Count();

    private List<string> Missing
    {
        get
        {
            List<string> missing = [];
            if (!HasSupplier)
            {
                missing.Add(useCustomSupplier ? "Enter a supplier name" : "Choose a supplier");
            }

            if (destinationId == Guid.Empty)
            {
                missing.Add("Choose where it is delivered");
            }

            if (lines.Count == 0)
            {
                missing.Add("Add at least one product");
            }

            if (lines.Any(l => !l.IsValid))
            {
                missing.Add("Every line needs a quantity above 0 and a cost");
            }

            return missing;
        }
    }

    /// <summary>
    /// Which of the three stops a step is at. One is current at a time: the
    /// first thing still outstanding, so the strip points at the work rather
    /// than merely counting it.
    /// </summary>
    /// <param name="step">The stop, numbered as it is shown.</param>
    /// <returns>The state class, or <see langword="null"/> for a step not yet reached.</returns>
    private string? StepState(int step) => step switch
    {
        1 => DetailsDone ? "done" : "current",
        2 => ProductsDone ? "done" : DetailsDone ? "current" : null,
        3 => IsReady ? "current" : null,
        _ => null,
    };

    protected override async Task OnInitializedAsync()
    {
        try
        {
            Task<ApiResult<List<PosSupplier>>> suppliersTask = Api.GetSuppliersAsync(CancellationToken.None);
            Task<ApiResult<List<PosLocation>>> locationsTask = Api.GetAsync<List<PosLocation>>("/api/v1/locations", CancellationToken.None);
            Task<ApiResult<List<PosUnitOfMeasure>>> unitsTask = Api.GetUnitsAsync(CancellationToken.None);
            Task<ApiResult<List<PosCategory>>> categoriesTask = Api.GetAsync<List<PosCategory>>("/api/v1/catalog/categories", CancellationToken.None);
            Task catalogueTask = LoadCatalogueAsync();
            await Task.WhenAll(suppliersTask, locationsTask, unitsTask, categoriesTask, catalogueTask);

            suppliers.AddRange(suppliersTask.Result.Value ?? []);
            destinations.AddRange((locationsTask.Result.Value ?? [])
                .Where(l => l.Kind is 0 or 1 && l.IsActive)
                .OrderBy(l => l.Kind)
                .ThenBy(l => l.Code, StringComparer.Ordinal));
            foreach (PosUnitOfMeasure unit in unitsTask.Result.Value ?? [])
            {
                units[unit.Id] = unit;
            }

            categories.AddRange((categoriesTask.Result.Value ?? []).Where(c => c.IsActive).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
            newProductCategory = categories.FirstOrDefault()?.Id.ToString() ?? OtherCategoryChoice;
            newProductUnitId = units.Keys.FirstOrDefault();

            destinationId = Session.WorkLocationId is { } preferred && destinations.Any(location => location.Id == preferred)
                ? preferred : destinations.FirstOrDefault()?.Id ?? Guid.Empty;

            await SeedFromSupplyRequestAsync();
            if (suppliersTask.Result is { IsSuccess: false })
            {
                message = suppliersTask.Result.Error;
            }
        }
        catch (HttpRequestException)
        {
            message = "Purchasing is unavailable. Try again shortly.";
        }
        finally
        {
            loading = false;
        }
    }

    // The whole catalogue in pages of 200, so searching is instant afterwards.
    private async Task LoadCatalogueAsync()
    {
        const int pageSize = 200;
        for (int offset = 0; offset < 10_000; offset += pageSize)
        {
            ApiResult<List<PosOrderableProduct>> page = await Api.GetOrderableProductsAsync(null, offset, pageSize, CancellationToken.None);
            if (page is not { IsSuccess: true, Value: { } products })
            {
                message = page.Error;
                return;
            }

            catalogue.AddRange(products);
            if (products.Count < pageSize)
            {
                return;
            }
        }
    }

    private void OnSupplierChanged()
    {
        useCustomSupplier = false;
        customSupplierName = string.Empty;
        onlySupplierProducts = supplierId is not null && catalogue.Any(p => p.PrimarySupplierId == supplierId);
        if (Supplier is { } supplier)
        {
            expectedOn = StoreClock.Today.AddDays(Math.Max(1, supplier.LeadTimeDays));
        }
    }

    private void ToggleCustomSupplier()
    {
        useCustomSupplier = !useCustomSupplier;
        if (useCustomSupplier)
        {
            supplierId = null;
        }
        else
        {
            customSupplierName = string.Empty;
        }

        onlySupplierProducts = false;
        expectedOn = null;
    }

    /// <summary>Puts the product list away and stops it filtering the catalogue.</summary>
    private void CloseResults()
    {
        productSearch = string.Empty;
        onlySupplierProducts = false;
    }

    private void OpenCustomProduct()
    {
        customProductOpen = true;
        focusSheet = true;
    }

    private void CloseCustomProduct() => customProductOpen = false;

    private void OpenReview()
    {
        if (Missing.Count > 0)
        {
            return;
        }

        reviewOpen = true;
        focusSheet = true;
    }

    private void CloseReview() => reviewOpen = false;

    /// <summary>Escape closes whichever sheet is open; nothing typed into it is lost.</summary>
    private void OnSheetKey(KeyboardEventArgs e)
    {
        if (e.Key != "Escape")
        {
            return;
        }

        reviewOpen = false;
        customProductOpen = false;
    }

    /// <summary>Puts the cursor inside a sheet as it opens, so Escape and Tab reach it.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusSheet)
        {
            return;
        }

        focusSheet = false;
        try
        {
            await sheetElement.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // The sheet closed again before the focus call landed.
        }
    }

    /// <summary>
    /// Finds the catalogue's "Other" category, creating it the first time
    /// something is filed under it. A purchase should never stall because the
    /// thing being bought fits none of the categories on hand.
    /// </summary>
    /// <returns>The category identifier, or <see langword="null"/> when it could not be made.</returns>
    private async Task<Guid?> ResolveOtherCategoryAsync()
    {
        if (categories.FirstOrDefault(c => string.Equals(c.Code, OtherCategoryCode, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            return existing.Id;
        }

        ApiResult<PosReference> created = await Api.PostAsync<PosReference>("/api/v1/catalog/categories", new
        {
            code = OtherCategoryCode,
            name = "Other",
            sortOrder = 999,
        }, CancellationToken.None);

        if (created is not { IsSuccess: true, Value: { } reference })
        {
            message = created.Error ?? "The “Other” category could not be created.";
            return null;
        }

        categories.Add(new PosCategory(reference.Id, OtherCategoryCode, "Other", 999, true));
        return reference.Id;
    }

    private async Task AddCustomProductAsync()
    {
        if (!CanAddCustomProduct || saving)
        {
            return;
        }

        string sku = string.IsNullOrWhiteSpace(newProductSku)
            ? $"CUSTOM-{Guid.NewGuid():N}"[..15].ToUpperInvariant()
            : newProductSku.Trim();
        string barcode = BarcodeText.Clean(newProductBarcode);
        saving = true;
        message = null;
        try
        {
            Guid categoryId;
            if (string.Equals(newProductCategory, OtherCategoryChoice, StringComparison.Ordinal))
            {
                if (await ResolveOtherCategoryAsync() is not { } resolved)
                {
                    return;
                }

                categoryId = resolved;
            }
            else if (!Guid.TryParse(newProductCategory, out categoryId))
            {
                message = "Choose a category for this product.";
                return;
            }

            ApiResult<PosReference> created = await Api.PostAsync<PosReference>("/api/v1/catalog/products", new
            {
                sku,
                name = newProductName.Trim(),
                categoryId,
                baseUnitOfMeasureId = newProductUnitId,
                primarySupplierId = useCustomSupplier ? null : supplierId,
                defaultPurchaseCost = newProductCost,
                initialBarcode = barcode.Length == 0 ? null : barcode
            }, CancellationToken.None);
            if (created is not { IsSuccess: true, Value: { } productRef })
            {
                message = created.Error;
                return;
            }

            PosOrderableProduct product = new(productRef.Id, sku, newProductName.Trim(),
                useCustomSupplier ? null : supplierId, newProductUnitId, newProductCost, false, false, true,
                barcode.Length == 0 ? [] : [barcode.ToUpperInvariant()]);
            catalogue.Add(product);
            AddLine(product);
            newProductName = string.Empty;
            newProductSku = string.Empty;
            newProductBarcode = string.Empty;
            newProductCost = 0;
            customProductOpen = false;
            scanNote = $"{product.Name} added to the order.";
        }
        catch (HttpRequestException)
        {
            message = "The product could not be added. Check your connection and product permissions.";
        }
        finally { saving = false; }
    }

    private void OnFinderKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && Matches.Count == 1 && lines.All(l => l.Product.Id != Matches[0].Id))
        {
            AddLine(Matches[0]);
            productSearch = string.Empty;
        }
        else if (e.Key == "Escape")
        {
            productSearch = string.Empty;
        }
    }

    // The catalogue on the page already holds every barcode, so a scan needs no lookup.
    private void OnScanned(BarcodeScan scan)
    {
        IReadOnlyList<string> codes = BarcodeText.LookupVariants(scan.Code);
        PosOrderableProduct? product = catalogue.FirstOrDefault(p => p.IsActive && (p.Barcodes ?? []).Any(code => codes.Contains(code, StringComparer.OrdinalIgnoreCase)))
            ?? catalogue.FirstOrDefault(p => p.IsActive && string.Equals(p.Sku, scan.Code, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            newProductBarcode = scan.Code;
            OpenCustomProduct();
            scanNote = $"Barcode {scan.Code} is not in the catalog. Enter the product details to add it to this order.";
            return;
        }

        DraftLine? line = lines.FirstOrDefault(l => l.Product.Id == product.Id);
        if (line is null)
        {
            AddLine(product);
            line = lines[^1];
        }
        else
        {
            line.Quantity += 1;
        }

        if (supplierId is not null && product.PrimarySupplierId is { } usual && usual != supplierId)
        {
            scanNote = $"{product.Name}: {PurchasingText.Quantity(line.Quantity)} on this order. It usually comes from another supplier.";
        }
        else
        {
            scanNote = $"{product.Name}: {PurchasingText.Quantity(line.Quantity)} on this order.";
        }
    }

    private void AddLine(PosOrderableProduct product)
        => lines.Add(new DraftLine(product) { Quantity = 1, UnitCost = product.DefaultPurchaseCost ?? 0m });

    private static void Step(DraftLine line, int delta) => line.Quantity = Math.Max(1, line.Quantity + delta);

    private string UnitName(Guid id) => units.TryGetValue(id, out PosUnitOfMeasure? unit) ? unit.Name.ToLowerInvariant() : "unit";

    /// <summary>
    /// Fills the composer from a warehouse supply request: the lines the
    /// warehouse asked for, at the quantities still to be bought, delivered to
    /// the warehouse that asked. The supplier and the costs are left blank —
    /// deciding those is the whole reason the request came to the owner.
    /// </summary>
    private async Task SeedFromSupplyRequestAsync()
    {
        if (SupplyRequestId is not { } requestId)
        {
            return;
        }

        ApiResult<PosSupplyRequest> result = await Api.GetSupplyRequestAsync(requestId, CancellationToken.None);

        if (result is not { IsSuccess: true, Value: { } request })
        {
            message = result.Error ?? "That supply request could not be opened.";
            return;
        }

        supplyRequest = request;

        if (destinations.Any(location => location.Id == request.DestinationLocationId))
        {
            destinationId = request.DestinationLocationId;
        }

        List<string> missing = [];

        foreach (PosSupplyRequestLineView line in request.Lines.Where(l => l.OutstandingQuantity > 0m))
        {
            PosOrderableProduct? product = catalogue.FirstOrDefault(p => p.Id == line.ProductId);

            if (product is null)
            {
                // The product was retired after the request was raised. Say so
                // rather than quietly dropping a line the warehouse asked for.
                missing.Add(FormattableString.Invariant($"line {line.LineNo}"));
                continue;
            }

            lines.Add(new DraftLine(product)
            {
                Quantity = line.OutstandingQuantity,
                UnitCost = product.DefaultPurchaseCost ?? 0m,
                RequestLineNo = line.LineNo,
            });
        }

        if (missing.Count > 0)
        {
            message = $"Some products on this request are no longer orderable and were left off: {string.Join(", ", missing)}.";
        }
    }

    private async Task SaveAsync(bool submit)
    {
        if (Missing.Count > 0)
        {
            return;
        }

        saving = true;
        message = null;

        // The sheet has done its job once a decision is taken on it, and an
        // error belongs on the page behind it rather than under the order.
        reviewOpen = false;
        try
        {
            // Nine in the morning where the stores trade, carried as the
            // instant it actually is. Sent at +08:00 the database refuses the
            // write outright, since it keeps these columns in UTC.
            DateTimeOffset? expectedAtUtc = expectedOn is { } day
                ? new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(8)).ToUniversalTime()
                : null;

            // An order raised against a warehouse request is created through the
            // request, so the two are written in one transaction and the request
            // knows what it turned into. Lines typed in on top of a seeded order
            // are ordinary lines and carry no request line of their own.
            ApiResult<PosReference> created = supplyRequest is { } request
                ? await Api.OrderSupplyRequestAsync(
                    request.Id,
                    new PosOrderSupplyRequest(
                        useCustomSupplier ? null : supplierId,
                        [.. lines.Where(l => l.RequestLineNo is not null)
                            .Select(l => new PosSupplyRequestOrderLine(
                                l.RequestLineNo!.Value, l.Product.BaseUnitOfMeasureId, l.Quantity, l.UnitCost))],
                        ExpectedAtUtc: expectedAtUtc,
                        CustomSupplierName: useCustomSupplier ? customSupplierName.Trim() : null),
                    CancellationToken.None)
                : await Api.CreatePurchaseOrderAsync(
                    new PosCreatePurchaseOrder(
                        useCustomSupplier ? null : supplierId,
                        destinationId,
                        [.. lines.Select(l => new PosCreatePurchaseOrderLine(l.Product.Id, l.Product.BaseUnitOfMeasureId, l.Quantity, l.UnitCost))],
                        ExpectedAtUtc: expectedAtUtc,
                        CustomSupplierName: useCustomSupplier ? customSupplierName.Trim() : null),
                    CancellationToken.None);
            if (created is not { IsSuccess: true, Value: { } reference })
            {
                message = created.Error;
                return;
            }

            string query = string.Empty;
            if (submit)
            {
                ApiResult<PosReference> submitted = await Api.SubmitPurchaseOrderAsync(reference.Id, CancellationToken.None);
                query = submitted.IsSuccess ? "?done=submitted" : "?done=saved-not-submitted";

                // The owner's own signature is the only one this order needs,
                // so it is taken here and the buyer lands on the sending step
                // instead of an approval queue holding a single name.
                if (submitted.IsSuccess && SkipsApproval)
                {
                    ApiResult<PosReference> approved = await Api.ApprovePurchaseOrderAsync(
                        reference.Id, null, CancellationToken.None);
                    query = approved.IsSuccess ? "?done=ready-to-send" : "?done=submitted-not-approved";
                }
            }

            Navigation.NavigateTo(FormattableString.Invariant($"purchasing/{reference.Id:D}{query}"));
        }
        catch (HttpRequestException)
        {
            message = "The order could not be saved. Check the connection and try again.";
        }
        finally
        {
            saving = false;
        }
    }

    private sealed record PosCategory(Guid Id, string Code, string Name, int SortOrder, bool IsActive);

    private sealed class DraftLine(PosOrderableProduct product)
    {
        public PosOrderableProduct Product { get; } = product;

        public decimal Quantity { get; set; }

        public decimal UnitCost { get; set; }

        /// <summary>The supply request line this came from, when seeded from one.</summary>
        public int? RequestLineNo { get; init; }

        public decimal Total => Math.Round(Quantity * UnitCost, 2);

        public bool IsValid => Quantity > 0 && UnitCost >= 0;
    }
}
