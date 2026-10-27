using System.Net;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages;

public partial class Preferences
{
    // Seeded synchronously from the service's last-known preferences in OnInitialized
    // (after injection, before render) so the page opens on the right values, no flash.
    private bool _isDarkMode;
    private bool _savedDarkMode;
    private IReadOnlyList<OdsOption> _currencyOptions = [];
    private string _currencyCode = string.Empty;
    private string _mainCurrencyCode = string.Empty;
    private string _savedCurrencyCode = string.Empty;
    private string _savedMainCurrencyCode = string.Empty;
    private bool _isLoading = true;
    private bool _isSaving;
    private bool _justSaved;
    // The page's *settings* (dark mode + currencies) save under the DarkMode
    // service's "preferences-page" key; this is the page's *view* state (search
    // section + box), kept under a separate key so the two never collide.
    private const string PageStateKey = "preferences-ui-page";
    private bool _searchOpen = true;
    private string _searchText = string.Empty;
    private List<ExistingCurrency> _currencies = [];

    // The caller's own weather place. Not part of the page's preferences blob: it is its own
    // server-owned user-preferences row, written only through PUT /api/weather/preferences, which
    // checks the place is selectable. "" follows the household default, as on the dashboard card.
    private const string FollowHouseholdDefault = "";
    private bool _canChooseWeather;
    private WeatherLocationOptions _weatherOptions = new();
    private string _weatherLocation = FollowHouseholdDefault;
    private string _savedWeatherLocation = FollowHouseholdDefault;
    private string? _weatherError;

    /// <summary>The interactivity check, as a seam for bUnit (the <c>WeatherWidget.InteractiveCheck</c> pattern).</summary>
    internal static Func<bool> InteractiveCheck = OperatingSystem.IsBrowser;

    private bool DarkModeSwitchValue
    {
        get => _isDarkMode;
        set
        {
            if (_isDarkMode == value)
                return;
            _isDarkMode = value;
            UserPreferences.PreviewDarkMode(_isDarkMode);
        }
    }

    // Per-row dirty flags drive the inline "unsaved" dot on each changed row;
    // their union enables the Save button. Saving commits the baselines, so the
    // dots clear and the button returns to disabled.
    private bool DarkModeDirty => _isDarkMode != _savedDarkMode;
    private bool DefaultCurrencyDirty => !string.Equals(_currencyCode, _savedCurrencyCode, StringComparison.Ordinal);
    private bool MainCurrencyDirty => !string.Equals(_mainCurrencyCode, _savedMainCurrencyCode, StringComparison.Ordinal);
    private bool WeatherLocationDirty => !string.Equals(_weatherLocation, _savedWeatherLocation, StringComparison.Ordinal);
    private bool PagePreferencesDirty => DarkModeDirty || DefaultCurrencyDirty || MainCurrencyDirty;

    private bool HasUnsavedChanges => PagePreferencesDirty || WeatherLocationDirty;

    // ── Page view-state persistence (search section + box) ────────────────────
    private async Task RestoreViewStateAsync()
    {
        var state = await PageState.LoadAsync<PreferencesPageState>(PageStateKey);
        if (state is null)
        { PersistPageState(); return; }
        _searchOpen = state.SearchOpen;
        _searchText = state.Search ?? string.Empty;
    }

    private void PersistPageState() => PageState.QueueSave(PageStateKey, new PreferencesPageState
    {
        SearchOpen = _searchOpen,
        Search = _searchText,
    });

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchText = value ?? string.Empty; PersistPageState(); }

    private sealed class PreferencesPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
    }

    private bool ShowDarkModeCard => Matches("Dark mode", "Toggle between dark and light theme");
    private bool ShowCurrencyCard => Matches("Default currency", "Currency used by default for new transactions");
    private bool ShowMainCurrencyCard => Matches("Main currency", "Currency used to display total net worth, assets and liabilities");

    /// <summary>As the dashboard card's picker: the claim, and two or more places to choose between.</summary>
    private bool ShowWeatherLocationCard =>
        _canChooseWeather && _weatherOptions.Locations.Count > 1
        && Matches("Weather location", "Place the weather card on your dashboard shows");

    private IReadOnlyList<OdsOption> WeatherLocationOptions
    {
        get
        {
            var household = _weatherOptions.Locations.FirstOrDefault(l => l.LocationId == _weatherOptions.HouseholdDefaultLocationId);
            var options = new List<OdsOption>
            {
                new(FollowHouseholdDefault, household is not null ? $"{household.Name} (household default)" : "Household default"),
            };
            options.AddRange(_weatherOptions.Locations
                .Where(l => household is null || l.LocationId != household.LocationId)
                .Select(l => new OdsOption(l.LocationId.ToString("D"), l.Name)));
            return options;
        }
    }

    private bool Matches(string title, string description) =>
        string.IsNullOrWhiteSpace(_searchText) ||
        title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
        description.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

    protected override void OnInitialized()
    {
        var current = UserPreferences.Current;
        _isDarkMode = _savedDarkMode = current.DarkModeEnabled;
        _currencyCode = _savedCurrencyCode = current.DefaultCurrency ?? string.Empty;
        _mainCurrencyCode = _savedMainCurrencyCode = current.MainCurrency ?? string.Empty;
    }

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await RestoreViewStateAsync();
        StateHasChanged();

        _isLoading = true;
        _savedDarkMode = await UserPreferences.GetDarkModePreferencesAsync();
        _isDarkMode = _savedDarkMode;
        _currencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
        _mainCurrencyCode = UserPreferences.MainCurrency ?? string.Empty;
        await Task.WhenAll(LoadCurrencies(), LoadWeatherLocationAsync());
        // Baseline after LoadCurrencies, which may substitute an archived/unknown
        // saved code for an active one — so the page opens clean, not pre-dirtied.
        _savedCurrencyCode = _currencyCode;
        _savedMainCurrencyCode = _mainCurrencyCode;
        _isLoading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            StateHasChanged();
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();

        if (_currencies.Count > 0 && _currencies.All(c => !string.Equals(c.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase)))
            _currencyCode = _currencies[0].CurrencyCode;

        if (_currencies.Count > 0 && _currencies.All(c => !string.Equals(c.CurrencyCode, _mainCurrencyCode, StringComparison.OrdinalIgnoreCase)))
            _mainCurrencyCode = _currencies[0].CurrencyCode;
    }

    /// <summary>
    /// A stored place that is no longer selectable reads as the household default, as on the dashboard card.
    /// A failed read leaves the row hidden rather than offering a choice it cannot show the current value of.
    /// </summary>
    private async Task LoadWeatherLocationAsync()
    {
        var user = await Auth.GetUserAsync();
        if (!user.HasPermission(PermissionClaims.WeatherPreferencesUpdate))
            return;

        var options = Weather.OptionsAsync();
        var preferences = Weather.PreferencesAsync();
        await Task.WhenAll(options, preferences);
        if ((await options).Value is not { } loaded || (await preferences).Value is not { } own)
            return;

        _weatherOptions = loaded;
        _weatherLocation = _savedWeatherLocation =
            own.DefaultLocation is { } place && loaded.Locations.Any(l => l.LocationId == place.LocationId)
                ? place.LocationId.ToString("D")
                : FollowHouseholdDefault;
        _canChooseWeather = true;
    }

    private void OnWeatherLocationChanged(string value)
    {
        _weatherLocation = value;
        _weatherError = null;
    }

    private Task OnCurrencyChanged(string value)
    {
        _currencyCode = value;
        return Task.CompletedTask;
    }

    private Task OnMainCurrencyChanged(string value)
    {
        _mainCurrencyCode = value;
        return Task.CompletedTask;
    }

    private async Task Save(MouseEventArgs arg)
    {
        if (_isSaving || !HasUnsavedChanges)
            return;

        _isSaving = true;
        _justSaved = false;
        // The two writes are independent: each half that succeeds commits its own baselines, so a refused
        // weather place leaves only that row dirty and never costs the user their currency change.
        var saved = !PagePreferencesDirty
            || await UserPreferences.SaveUserPreferencesAsync(new UserPreferencesPage(_isDarkMode, _currencyCode, _mainCurrencyCode));
        if (saved && PagePreferencesDirty)
        {
            _savedDarkMode = _isDarkMode;
            _savedCurrencyCode = _currencyCode;
            _savedMainCurrencyCode = _mainCurrencyCode;
        }

        if (WeatherLocationDirty)
            saved &= await SaveWeatherLocationAsync();

        _isSaving = false;

        if (saved)
        {
            // Brief "Saved" confirmation on the button (design: save → check, then revert).
            _justSaved = true;
            StateHasChanged();
            await Task.Delay(OdsTiming.ConfirmFlashMs);
            _justSaved = false;
            StateHasChanged();
        }
    }

    private async Task<bool> SaveWeatherLocationAsync()
    {
        Guid? next = Guid.TryParse(_weatherLocation, out var id) ? id : null;
        var result = await Weather.SetPreferenceAsync(next);
        if (!result.IsSuccess)
        {
            _weatherError = result.Status == HttpStatusCode.BadRequest
                ? "That location no longer has weather. Choose another one."
                : "Your weather location couldn't be saved. Try again.";
            return false;
        }

        _savedWeatherLocation = _weatherLocation;
        _weatherError = null;
        return true;
    }

    public void Dispose()
    {
        if (_isDarkMode != _savedDarkMode)
            UserPreferences.PreviewDarkMode(_savedDarkMode);
    }
}
