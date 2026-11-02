using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// The Address field of a contact or property (issue #383 v5 §3.3a). Parameters and transitions; the markup is in
/// <c>LocationAddressField.razor</c>.
/// </summary>
public partial class LocationAddressField
{
    [Inject] private ILocationsApiClient Locations { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    /// <summary>The field's state — the dialog owns it and reads the request from it on save.</summary>
    [Parameter, EditorRequired] public AddressBlockModel Model { get; set; } = default!;

    /// <summary>Errors keyed <c>address</c> (the picker) or by hand-entry field.</summary>
    [Parameter] public IReadOnlyDictionary<string, string>? Errors { get; set; }

    /// <summary>The ids: the picker is <c>‹IdBase›-picker</c>, a hand-entry input <c>‹IdBase›-‹field›</c>.</summary>
    [Parameter, EditorRequired] public string IdBase { get; set; } = default!;

    /// <summary>Raised after every change, so the dialog can clear stale errors and re-render.</summary>
    [Parameter] public EventCallback OnChange { get; set; }

    /// <summary>The dialog's one polite announcer.</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    /// <summary>Raised after the place itself was edited through Edit address, so the host can reload the record.</summary>
    [Parameter] public EventCallback OnPlaceEdited { get; set; }

    /// <summary>The picker's debounce; a test passes zero.</summary>
    [Parameter] public int DebounceMs { get; set; } = 250;

    /// <summary>The address search's whole pause; a test passes zero.</summary>
    [Parameter] public int AddressDebounceMs { get; set; } = 400;

    /// <summary>The picker's input id — where the error summary's <c>address</c> entry and <i>Search instead</i> go.</summary>
    public string PickerId => PickerIdFor(IdBase);

    public static string PickerIdFor(string idBase) => $"{idBase}-picker";

    /// <summary>The summary's links: the picker, and each hand-entry input.</summary>
    public static IReadOnlyDictionary<string, string> ErrorLinks(string idBase) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [AddressBlockModel.AddressKey] = PickerIdFor(idBase),
        [AddressBlockModel.Line1Key] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.Line1Key),
        [AddressBlockModel.Line2Key] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.Line2Key),
        [AddressBlockModel.PostalCodeKey] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.PostalCodeKey),
        [AddressBlockModel.CityKey] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.CityKey),
        [AddressBlockModel.RegionKey] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.RegionKey),
        [AddressBlockModel.CountryCodeKey] = OdsAddressBlock.FieldId(idBase, AddressBlockModel.CountryCodeKey),
    };

    private string EditButtonId => $"{IdBase}-edit";

    private OdsLocationSelect? _picker;
    private ExistingLocation? _editing;

    // Where focus goes after the next render: a step swaps the control that had it out of the DOM.
    private string? _focusNext;

    private string? Err(string key) => Errors is not null && Errors.TryGetValue(key, out var message) ? message : null;

    private async Task OnPickedAsync(LocationPick? pick)
    {
        string? announcement = null;
        if (pick is null)
        {
            Model.ClearPick();
        }
        else if (pick.Candidate is { } candidate)
        {
            announcement = Model.Stage(candidate);
        }
        else if (pick.Id is { } id)
        {
            // The place's usage feeds the usage line; the stored one's is already known.
            ExistingLocation? place = null;
            if (id != Model.Stored?.LocationId && (await Locations.GetAsync(id)) is { IsSuccess: true, Value: { } found })
                place = found;
            Model.PickPlace(id, place);
        }

        if (announcement is not null)
            await OnAnnounce.InvokeAsync(announcement);
        await OnChange.InvokeAsync();
    }

    private async Task EnterByHandAsync(string typed)
    {
        Model.EnterByHand(typed);
        _focusNext = OdsAddressBlock.FieldId(IdBase, AddressBlockModel.Line1Key);
        await OnChange.InvokeAsync();
        StateHasChanged();
    }

    private async Task SearchInsteadAsync()
    {
        Model.SearchInstead();
        _focusNext = PickerId;
        await OnChange.InvokeAsync();
        StateHasChanged();
    }

    /// <summary>Edit address: the location dialog for the linked place, read fresh.</summary>
    private async Task EditAddressAsync()
    {
        if (Model.LinkedId is not { } id)
            return;
        var result = await Locations.GetAsync(id);
        if (result is { IsSuccess: true, Value: { } place })
            _editing = place;
        else
            await OnAnnounce.InvokeAsync("Couldn’t load that place.");
    }

    private void OnEditOpenChanged(bool open)
    {
        if (!open)
            _editing = null;
    }

    /// <summary>The place was saved: the field names its new address, and the change reaches every record using it.</summary>
    private async Task OnPlaceSavedAsync(ExistingLocation saved)
    {
        _editing = null;
        Model.PlaceEdited(saved);
        _picker?.Refresh(saved);
        await OnAnnounce.InvokeAsync("Address updated for every record that uses it");
        await OnPlaceEdited.InvokeAsync();
        await OnChange.InvokeAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusNext is not { } id)
            return;
        _focusNext = null;
        try { await Js.InvokeVoidAsync("odsFocusById", id); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime (prerender, bUnit) */ }
    }
}
