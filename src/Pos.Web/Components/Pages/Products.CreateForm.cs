using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Shared.Scanning;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The "Add a product" dialog: its state, what it refuses to submit, and the
/// checks it runs against a barcode as one is typed or scanned.
/// </summary>
public sealed partial class Products
{
    // Create form.
    private string newSku = string.Empty;
    private string newName = string.Empty;
    private string newCategoryId = string.Empty;
    private string newUnitId = string.Empty;
    private string newBarcode = string.Empty;
    private string newDescription = string.Empty;
    private string newBrandId = string.Empty;
    private string newSupplierId = string.Empty;
    private decimal newCost;
    private bool newTracksBatches;
    private bool newTracksExpiry;
    private int? newShelfLifeDays;
    private bool newVatExempt;
    private bool focusModal;

    /// <summary>Why the create button is disabled, or null when it is ready.</summary>
    private string? CreateBlockedReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(newSku))
            {
                return "A SKU is needed.";
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                return "A name is needed.";
            }

            if (!Guid.TryParse(newCategoryId, out _))
            {
                return "Choose a category.";
            }

            if (!Guid.TryParse(newUnitId, out _))
            {
                return "Choose a base unit.";
            }

            // The server enforces this too; stopping here saves filling the
            // form in only to be refused on submit.
            return barcodeTaken is { } owner ? $"That barcode already belongs to {owner}." : null;
        }
    }

    /// <summary>
    /// A SKU proposed from the name, offered rather than filled in: house SKU
    /// conventions vary and only the operator knows theirs.
    /// </summary>
    private string? SuggestedSku
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(newSku) || string.IsNullOrWhiteSpace(newName))
            {
                return null;
            }

            string[] words = newName.ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => new string([.. word.Where(char.IsAsciiLetterOrDigit)]))
                .Where(word => word.Length > 0)
                .Take(2)
                .ToArray();

            return words.Length == 0 ? null : string.Join('-', words);
        }
    }

    /// <summary>
    /// Checks a typed or scanned code against the catalogue as it is entered.
    /// </summary>
    /// <remarks>
    /// Barcodes are unique across the catalogue, so creating a product with
    /// one that is taken fails at the server after the operator has filled in
    /// the whole form. Asking first turns that into a sentence under the field.
    /// </remarks>
    private async Task OnNewBarcodeTypedAsync()
    {
        string code = BarcodeText.Clean(newBarcode);
        int generation = ++barcodeCheckGeneration;
        barcodeTaken = null;

        if (code.Length < 4)
        {
            return;
        }

        await Task.Delay(350);
        if (generation != barcodeCheckGeneration)
        {
            return;
        }

        ApiResult<PosScannedProduct> found = await Api.FindProductByScanAsync(code, CancellationToken.None);
        if (generation == barcodeCheckGeneration && found is { IsSuccess: true, Value: { } owner })
        {
            barcodeTaken = owner.Name;
        }
    }

    private ElementReference modalRef;

    /// <summary>Whether anything has been entered that closing would throw away.</summary>
    private bool IsFormDirty =>
        !string.IsNullOrWhiteSpace(newName) ||
        !string.IsNullOrWhiteSpace(newSku) ||
        !string.IsNullOrWhiteSpace(newBarcode) ||
        !string.IsNullOrWhiteSpace(newDescription) ||
        !string.IsNullOrWhiteSpace(newCategoryId) ||
        !string.IsNullOrWhiteSpace(newUnitId) ||
        !string.IsNullOrWhiteSpace(newBrandId) ||
        !string.IsNullOrWhiteSpace(newSupplierId) ||
        newCost != 0m || newTracksBatches || newTracksExpiry || newVatExempt;

    /// <summary>
    /// Closes the dialog on Esc or a click outside, but only while nothing has
    /// been typed.
    /// </summary>
    /// <remarks>
    /// A stray click on the backdrop should not discard a half-filled product.
    /// Once there is something to lose, Cancel is the way out — a deliberate
    /// click on a button that says what it does.
    /// </remarks>
    private void TryDismiss()
    {
        if (!IsFormDirty)
        {
            ToggleCreate();
        }
    }
    private void OnModalKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            TryDismiss();
        }
    }

    private void ToggleCreate()
    {
        showCreate = !showCreate;
        attaching = null;
        attachTarget = null;
        barcodeTaken = null;
        barcodeCheckGeneration++;
        focusModal = showCreate;

        if (!showCreate)
        {
            ResetCreateForm();
        }
    }

    /// <summary>Empties the form so the next open starts clean.</summary>
    private void ResetCreateForm()
    {
        newSku = string.Empty;
        newName = string.Empty;
        newCategoryId = string.Empty;
        newUnitId = string.Empty;
        newBarcode = string.Empty;
        newDescription = string.Empty;
        newBrandId = string.Empty;
        newSupplierId = string.Empty;
        newCost = 0m;
        newTracksBatches = false;
        newTracksExpiry = false;
        newShelfLifeDays = null;
        newVatExempt = false;
        barcodeTaken = null;
        barcodeCheckGeneration++;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusModal)
        {
            return;
        }

        focusModal = false;
        try
        {
            await modalRef.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // The dialog closed before it could take focus.
        }
    }

    /// <summary>Opens the create form with the unidentified code already in it.</summary>
    private void BeginCreateWithCode()
    {
        newBarcode = lookupMissing ?? string.Empty;
        showCreate = true;
        focusModal = true;
        attaching = null;
        attachTarget = null;
        // It came from a lookup that found nothing, so it is known to be free.
        barcodeTaken = null;
        barcodeCheckGeneration++;
    }

    private async Task CreateProductAsync()
    {
        if (CreateBlockedReason is not null
            || !Guid.TryParse(newCategoryId, out Guid categoryId)
            || !Guid.TryParse(newUnitId, out Guid unitId))
        {
            return;
        }

        working = true;
        message = null;
        try
        {
            PosCreateProduct body = new(
                newSku.Trim(),
                newName.Trim(),
                categoryId,
                unitId,
                Description: string.IsNullOrWhiteSpace(newDescription) ? null : newDescription.Trim(),
                BrandId: Guid.TryParse(newBrandId, out Guid brandId) ? brandId : null,
                PrimarySupplierId: Guid.TryParse(newSupplierId, out Guid supplierId) ? supplierId : null,
                TaxCode: null,
                IsVatExempt: newVatExempt,
                DefaultPurchaseCost: newCost,
                TracksBatches: newTracksBatches,
                TracksExpiry: newTracksExpiry,
                ShelfLifeDays: newTracksExpiry ? newShelfLifeDays : null,
                InitialBarcode: string.IsNullOrWhiteSpace(newBarcode) ? null : BarcodeText.Clean(newBarcode));

            ApiResult<PosReference> result = await Api.CreateProductAsync(body, CancellationToken.None);
            if (result is not { IsSuccess: true, Value: { } reference })
            {
                message = result.Error;
                return;
            }

            Navigation.NavigateTo($"/catalog/products/{reference.Id:D}");
        }
        finally
        {
            working = false;
        }
    }
}
