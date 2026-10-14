using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Shared.Scanning;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// One product, and the barcodes that resolve to it.
/// </summary>
/// <remarks>
/// A barcode carries a unit and a pack quantity, not just a value: the same
/// product may be scanned as a single tin and as a case of twenty-four, and the
/// ledger has to receive the right number of base units either way.
/// </remarks>
public sealed partial class ProductDetail : IDisposable
{
    [Inject] private VaultFlowApiClient Api { get; set; } = default!;

    [Inject] private UserSession Session { get; set; } = default!;

    /// <summary>The product on show.</summary>
    [Parameter] public Guid ProductId { get; set; }

    private static readonly CultureInfo Peso = CultureInfo.GetCultureInfo("en-PH");

    private readonly List<PosProductBarcode> barcodes = [];
    private readonly List<PosUnitOfMeasure> units = [];
    private readonly List<PosCategory> categories = [];

    private PosCatalogProduct? product;
    private CancellationTokenSource? inFlight;
    private bool loading = true;
    private bool working;
    private bool adding;
    private string? message;

    private string newCode = string.Empty;
    private string newUnitId = string.Empty;
    private decimal newPackQuantity = 1m;
    private bool newIsPrimary;

    private string? retiring;
    private string retireReason = string.Empty;

    private bool CanManageBarcodes => Session.HasPermission("product.barcode.manage");

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync() => await LoadAsync();

    /// <summary>Stops any load still running once the page is gone.</summary>
    public void Dispose()
    {
        inFlight?.Cancel();
        inFlight?.Dispose();
        inFlight = null;
    }

    private async Task LoadAsync()
    {
        inFlight?.Cancel();
        inFlight?.Dispose();
        CancellationTokenSource source = new();
        inFlight = source;

        loading = true;
        try
        {
            CancellationToken token = source.Token;

            if (units.Count == 0)
            {
                ApiResult<List<PosUnitOfMeasure>> unitResult = await Api.GetUnitsAsync(token);
                if (unitResult is { IsSuccess: true, Value: { } loadedUnits })
                {
                    units.AddRange(loadedUnits.OrderBy(u => u.Code));
                }

                ApiResult<List<PosCategory>> categoryResult = await Api.GetCategoriesAsync(token);
                if (categoryResult is { IsSuccess: true, Value: { } loadedCategories })
                {
                    categories.AddRange(loadedCategories);
                }
            }

            ApiResult<PosCatalogProduct> productResult = await Api.GetCatalogProductAsync(ProductId, token);
            if (productResult is not { IsSuccess: true, Value: { } loaded })
            {
                message = productResult.Error ?? "This product could not be found.";
                return;
            }

            product = loaded;
            await LoadBarcodesAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded, or the page is gone.
        }
        finally
        {
            loading = false;
            if (ReferenceEquals(inFlight, source))
            {
                inFlight = null;
            }

            source.Dispose();
        }
    }

    private async Task LoadBarcodesAsync(CancellationToken token)
    {
        ApiResult<List<PosProductBarcode>> result = await Api.GetProductBarcodesAsync(ProductId, token);
        if (result is { IsSuccess: true, Value: { } loaded })
        {
            barcodes.Clear();
            barcodes.AddRange(loaded);
        }
    }

    private void ToggleAdd()
    {
        adding = !adding;
        retiring = null;
        newCode = string.Empty;
        newUnitId = string.Empty;
        newPackQuantity = 1m;
        newIsPrimary = barcodes.Count == 0;
    }

    private void OnScannedAsync(BarcodeScan scan)
    {
        newCode = scan.Code;
        adding = true;
    }

    private Task OnAddKeyAsync(KeyboardEventArgs e)
        => e.Key == "Enter" && !string.IsNullOrWhiteSpace(newCode) ? AddBarcodeAsync() : Task.CompletedTask;

    private async Task AddBarcodeAsync()
    {
        string code = BarcodeText.Clean(newCode);
        if (code.Length == 0 || newPackQuantity <= 0m)
        {
            return;
        }

        await RunAsync(
            token => Api.AddProductBarcodeAsync(
                ProductId,
                new PosAddProductBarcode(
                    code,
                    Guid.TryParse(newUnitId, out Guid unitId) ? unitId : null,
                    newPackQuantity,
                    newIsPrimary),
                token),
            $"{code} now scans as this product.");

        if (message?.StartsWith(code, StringComparison.Ordinal) == true)
        {
            adding = false;
            newCode = string.Empty;
        }
    }

    private void BeginRetire(string barcode)
    {
        retiring = barcode;
        retireReason = string.Empty;
        adding = false;
    }

    private void CancelRetire()
    {
        retiring = null;
        retireReason = string.Empty;
    }

    private async Task RetireAsync()
    {
        if (retiring is not { } code)
        {
            return;
        }

        string? reason = string.IsNullOrWhiteSpace(retireReason) ? null : retireReason.Trim();
        retiring = null;
        await RunTextAsync(
            token => Api.RetireProductBarcodeAsync(ProductId, code, reason, token),
            $"{code} is retired. Scanning it will no longer find this product.");
    }

    private Task SetPrimaryAsync(string barcode)
        => RunTextAsync(
            token => Api.SetPrimaryProductBarcodeAsync(ProductId, barcode, token),
            $"{barcode} is now the primary code.");

    private async Task RunAsync(
        Func<CancellationToken, Task<ApiResult<PosReference>>> command, string success)
    {
        working = true;
        message = null;
        try
        {
            ApiResult<PosReference> result = await command(CancellationToken.None);
            message = result.IsSuccess ? success : result.Error;
            if (result.IsSuccess)
            {
                await ReloadAfterChangeAsync();
            }
        }
        finally
        {
            working = false;
        }
    }

    private async Task RunTextAsync(
        Func<CancellationToken, Task<ApiResult<string>>> command, string success)
    {
        working = true;
        message = null;
        try
        {
            ApiResult<string> result = await command(CancellationToken.None);
            message = result.IsSuccess ? success : result.Error;
            if (result.IsSuccess)
            {
                await ReloadAfterChangeAsync();
            }
        }
        finally
        {
            working = false;
        }
    }

    private async Task ReloadAfterChangeAsync()
    {
        try
        {
            await LoadBarcodesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The page is gone.
        }
    }

    private string UnitName(Guid unitId)
        => units.FirstOrDefault(unit => unit.Id == unitId)?.Code ?? "base unit";

    private string CategoryName(Guid categoryId)
        => categories.FirstOrDefault(category => category.Id == categoryId)?.Name ?? "Uncategorised";

    private static string FormatQuantity(decimal quantity)
        => quantity % 1m == 0m
            ? quantity.ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);
}
