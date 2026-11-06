using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>
/// The locations desk: the warehouses and stores the business runs from, and the
/// stock rules that hold at each of them.
/// </summary>
/// <remarks>
/// One location is edited at a time. Its details and its stock rules go to two
/// different endpoints under two different permissions, but to the person at the
/// screen they are one page with one pending state, so the footer saves whatever
/// has actually changed and says plainly whether anything is outstanding.
/// </remarks>
public partial class Locations
{
    private readonly List<PosLocationAdmin> locations = [];
    private PosLocationAdmin? selected;
    private bool loading = true;
    private bool working;
    private bool showCreate;
    private bool showCloseConfirm;
    private string? message;
    private string search = string.Empty;
    private string scope = "live";

    private string newCode = string.Empty;
    private string newName = string.Empty;
    private int newKind = 1;
    private string newTimeZone = "Asia/Manila";

    private string editCode = string.Empty;
    private string editName = string.Empty;
    private int editKind = 1;
    private string editTimeZone = "Asia/Manila";
    private int editNegativePolicy;
    private bool editDirectDelivery;
    private int editExpiryDays;

    private string closeDate = Today();

    private ElementReference createRef;
    private ElementReference closeRef;
    private bool focusCreate;
    private bool focusClose;

    private bool CanViewLocations => Session.HasPermission("location.manage");
    private bool CanManageLocations => Session.Current?.User.Permissions.Contains("location.manage", StringComparer.Ordinal) == true;
    private bool CanManageSettings => Session.Current?.User.Permissions.Contains("settings.manage", StringComparer.Ordinal) == true;

    private bool ModalOpen => showCreate || showCloseConfirm;
    private int ActiveCount => locations.Count(location => location.IsActive);
    private int ClosedCount => locations.Count(location => !location.IsActive);
    private int StrictCount => locations.Count(location => location.Settings.NegativeStockPolicy == 0);
    private int DirectDeliveryCount => locations.Count(location => location.Settings.AllowsDirectSupplierDelivery);

    /// <summary>A closed location is a record, so nothing on it is editable.</summary>
    private bool DetailsReadOnly => !CanManageLocations || selected?.IsActive != true;

    private bool SettingsReadOnly => !CanManageSettings || selected?.IsActive != true;

    /// <summary>
    /// The locations on show: the live ones by default, narrowed by the search
    /// box. A long-retired store should not sit between two shops somebody is
    /// actually running.
    /// </summary>
    private List<PosLocationAdmin> Visible
    {
        get
        {
            IEnumerable<PosLocationAdmin> rows = scope switch
            {
                "live" => locations.Where(location => location.IsActive),
                "closed" => locations.Where(location => !location.IsActive),
                _ => locations,
            };

            if (!string.IsNullOrWhiteSpace(search))
            {
                string term = search.Trim();
                rows = rows.Where(location =>
                    location.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || location.Code.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            return [.. rows];
        }
    }

    private bool DetailsDirty => selected is { } location
        && (!string.Equals(editCode.Trim(), location.Code, StringComparison.Ordinal)
            || !string.Equals(editName.Trim(), location.Name, StringComparison.Ordinal)
            || editKind != location.Kind
            || !string.Equals(editTimeZone.Trim(), location.TimeZoneId, StringComparison.Ordinal));

    private bool SettingsDirty => selected is { } location
        && (editNegativePolicy != location.Settings.NegativeStockPolicy
            || editDirectDelivery != location.Settings.AllowsDirectSupplierDelivery
            || editExpiryDays != location.Settings.ExpiryWarningDays);

    private bool Dirty => DetailsDirty || SettingsDirty;

    private bool CanCreate => !string.IsNullOrWhiteSpace(newCode) && !string.IsNullOrWhiteSpace(newName) && !string.IsNullOrWhiteSpace(newTimeZone);

    private bool CanSaveDetails => !string.IsNullOrWhiteSpace(editCode) && !string.IsNullOrWhiteSpace(editName) && !string.IsNullOrWhiteSpace(editTimeZone);

    private bool CanSave => Dirty && !working && CanSaveDetails && selected?.IsActive == true;

    private bool CanClose => DateOnly.TryParse(closeDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>What the footer says about the state of the form.</summary>
    private string Caption => selected switch
    {
        null => string.Empty,
        { IsActive: false } => "A closed location is read-only. Its record stays available.",
        _ when Dirty && !CanSaveDetails => "A code, name and IANA timezone are needed before this can be saved.",
        _ when Dirty => "You have changes that are not yet saved.",
        _ when DetailsReadOnly && SettingsReadOnly => "You can review this location's rules but not change them.",
        _ => "All changes saved.",
    };

    private string SideFootText => Dirty && selected is { } location
        ? $"Unsaved changes on {location.Name}."
        : "Select a location to update its settings.";

    private string EmptyListText => !string.IsNullOrWhiteSpace(search)
        ? "No locations match your search."
        : scope switch
        {
            "closed" => "No locations have been closed.",
            "live" => "No live locations.",
            _ => "No locations yet.",
        };

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (!CanViewLocations)
        {
            loading = false;
            return;
        }

        await LoadLocationsAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusCreate)
        {
            focusCreate = false;
            await FocusAsync(createRef);
        }

        if (focusClose)
        {
            focusClose = false;
            await FocusAsync(closeRef);
        }
    }

    private static async Task FocusAsync(ElementReference target)
    {
        try
        {
            await target.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // The dialog closed before it could take focus.
        }
    }

    private async Task LoadLocationsAsync(Guid? keepSelected = null)
    {
        loading = true;
        try
        {
            ApiResult<List<PosLocationAdmin>> result = await Api.GetLocationAdministrationAsync(CancellationToken.None);
            if (!result.IsSuccess || result.Value is null)
            {
                message = result.Error;
                return;
            }

            locations.Clear();
            locations.AddRange(result.Value.OrderBy(location => location.Kind).ThenBy(location => location.Code, StringComparer.Ordinal));
            Guid? targetId = keepSelected ?? selected?.Id;
            PosLocationAdmin? target = targetId is { } id ? locations.FirstOrDefault(location => location.Id == id) : locations.FirstOrDefault();
            if (target is not null)
            {
                SelectLocation(target);
            }
            else
            {
                // Never leave the editor pointed at a row the list no longer returns.
                selected = null;
            }
        }
        catch (HttpRequestException)
        {
            message = "The location service is unavailable. Try again shortly.";
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>
    /// Saves whatever has actually changed. Details and stock rules are separate
    /// endpoints under separate permissions; the person editing sees one Save.
    /// </summary>
    private async Task SaveAsync()
    {
        if (selected is not { } location || !CanSave)
        {
            return;
        }

        if (SettingsDirty && editExpiryDays is < 0 or > 3650)
        {
            message = "Expiry warning must be between 0 and 3650 days.";
            return;
        }

        working = true;
        message = null;
        try
        {
            if (DetailsDirty)
            {
                ApiResult<string> details = await Api.UpdateLocationAsync(
                    location.Id,
                    new PosUpdateLocationRequest(
                        editCode.Trim().ToUpperInvariant(),
                        editName.Trim(),
                        editKind,
                        editTimeZone.Trim()),
                    CancellationToken.None);

                if (!details.IsSuccess)
                {
                    message = details.Error;
                    return;
                }
            }

            if (SettingsDirty)
            {
                PosLocationSettings settings = location.Settings with
                {
                    NegativeStockPolicy = editNegativePolicy,
                    AllowsDirectSupplierDelivery = editDirectDelivery,
                    ExpiryWarningDays = editExpiryDays,
                };

                ApiResult<string> stock = await Api.UpdateLocationSettingsAsync(location.Id, settings, CancellationToken.None);
                if (!stock.IsSuccess)
                {
                    message = stock.Error;
                    return;
                }
            }

            string saved = editName.Trim();
            // LoadLocationsAsync re-runs SelectLocation, which clears message,
            // so set the success text AFTER the reload.
            await LoadLocationsAsync(location.Id);
            message ??= $"Changes saved for {saved}.";
        }
        catch (HttpRequestException)
        {
            message = "The location service is unavailable. Try again shortly.";
        }
        finally
        {
            working = false;
        }
    }

    private async Task CreateLocationAsync()
    {
        if (!CanManageLocations || !CanCreate)
        {
            message = "Enter a code, name and IANA timezone.";
            return;
        }

        working = true;
        message = null;
        try
        {
            ApiResult<PosReference> result = await Api.CreateLocationAsync(
                new PosCreateLocationRequest(newCode.Trim().ToUpperInvariant(), newName.Trim(), newKind, newTimeZone.Trim()),
                CancellationToken.None);
            if (!result.IsSuccess || result.Value is null)
            {
                message = result.Error;
                return;
            }

            newCode = string.Empty;
            newName = string.Empty;
            newKind = 1;
            newTimeZone = "Asia/Manila";
            showCreate = false;
            // A location created while the list is filtered should still be the
            // one on screen, so widen the view to where it will be.
            scope = "live";
            search = string.Empty;
            // LoadLocationsAsync re-runs SelectLocation, which clears message,
            // so set the success text AFTER the reload.
            await LoadLocationsAsync(result.Value.Id);
            message ??= "The location was created with conservative operating defaults.";
        }
        catch (HttpRequestException)
        {
            message = "The location service is unavailable. Try again shortly.";
        }
        finally
        {
            working = false;
        }
    }

    private async Task CloseLocationAsync()
    {
        if (!CanManageLocations || selected is not { } location)
        {
            return;
        }

        if (!DateOnly.TryParse(closeDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly effective))
        {
            message = "Choose the effective closing date.";
            return;
        }

        working = true;
        message = null;
        try
        {
            ApiResult<string> result = await Api.CloseLocationAsync(location.Id, effective, CancellationToken.None);
            if (!result.IsSuccess)
            {
                message = result.Error;
                return;
            }

            showCloseConfirm = false;
            // The administration list is where a closed location is reviewed, so
            // it comes back from the reload and the editor stays on it, now
            // showing the retired panel rather than an empty page.
            await LoadLocationsAsync(location.Id);
            message ??= $"{location.Name} was closed. It no longer accepts new operations.";
        }
        catch (HttpRequestException)
        {
            message = "The location service is unavailable. Try again shortly.";
        }
        finally
        {
            working = false;
        }
    }

    private void SelectLocation(PosLocationAdmin location)
    {
        selected = location;
        editCode = location.Code;
        editName = location.Name;
        editKind = location.Kind;
        editTimeZone = location.TimeZoneId;
        editNegativePolicy = location.Settings.NegativeStockPolicy;
        editDirectDelivery = location.Settings.AllowsDirectSupplierDelivery;
        editExpiryDays = location.Settings.ExpiryWarningDays;
        showCloseConfirm = false;
        closeDate = Today();
        message = null;
    }

    /// <summary>Puts the form back to what the server holds.</summary>
    private void DiscardChanges()
    {
        if (selected is { } location)
        {
            SelectLocation(location);
        }
    }

    private void ToggleDirectDelivery()
    {
        if (!SettingsReadOnly)
        {
            editDirectDelivery = !editDirectDelivery;
        }
    }

    private void SetScope(string value)
    {
        scope = value;
        message = null;
    }

    private void OnSearchChanged(ChangeEventArgs e) => search = e.Value?.ToString() ?? string.Empty;

    private void OnCloseDateChanged(ChangeEventArgs e) => closeDate = e.Value?.ToString() ?? string.Empty;

    private void OpenCreate()
    {
        showCreate = true;
        message = null;
        focusCreate = true;
    }

    private void CloseCreate()
    {
        showCreate = false;
        message = null;
    }

    /// <summary>
    /// Esc and a click on the backdrop close the dialog, but only while nothing
    /// has been typed: a stray click should not discard a half-filled location.
    /// </summary>
    private void TryDismissCreate()
    {
        if (!CanCreate && string.IsNullOrWhiteSpace(newCode) && string.IsNullOrWhiteSpace(newName))
        {
            CloseCreate();
        }
    }

    private void OnCreateKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            TryDismissCreate();
        }
    }

    private void OpenCloseConfirm()
    {
        showCloseConfirm = true;
        message = null;
        focusClose = true;
    }

    private void ToggleCloseConfirm()
    {
        showCloseConfirm = !showCloseConfirm;
        message = null;
    }

    private void TryDismissClose()
    {
        if (!working)
        {
            showCloseConfirm = false;
            message = null;
        }
    }

    private void OnCloseKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            TryDismissClose();
        }
    }

    private static string Today()
        => DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string KindName(int kind) => kind == 0 ? "Main warehouse" : "Retail store";

    /// <summary>The one line under the title: how long this location has run.</summary>
    private static string TimelineLine(PosLocationAdmin location)
    {
        string opened = location.OpenedOn.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        string line = $"Open since {opened} · {location.TimeZoneId}";
        return location is { IsActive: false, ClosedOn: { } closedOn }
            ? $"{line} · closed {closedOn.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}"
            : line;
    }
}
