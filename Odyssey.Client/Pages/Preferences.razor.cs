using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages;

public partial class Preferences
{
    // Seeded synchronously from the service's last-known preferences in OnInitialized
    // (after injection, before render) so the page opens on the right values, no flash.
    private bool _isDarkMode;
    private bool _savedDarkMode;
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

    private bool HasUnsavedChanges => DarkModeDirty || DefaultCurrencyDirty || MainCurrencyDirty;

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
        if (!OperatingSystem.IsBrowser())
            return;

        await RestoreViewStateAsync();
        StateHasChanged();

        _isLoading = true;
        _savedDarkMode = await UserPreferences.GetDarkModePreferencesAsync();
        _isDarkMode = _savedDarkMode;
        _currencyCode = UserPreferences.DefaultCurrency ?? string.Empty;
        _mainCurrencyCode = UserPreferences.MainCurrency ?? string.Empty;
        await LoadCurrencies();
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

        if (_currencies.Count > 0 && _currencies.All(c => !string.Equals(c.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase)))
            _currencyCode = _currencies[0].CurrencyCode;

        if (_currencies.Count > 0 && _currencies.All(c => !string.Equals(c.CurrencyCode, _mainCurrencyCode, StringComparison.OrdinalIgnoreCase)))
            _mainCurrencyCode = _currencies[0].CurrencyCode;
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
        var saved = await UserPreferences.SaveUserPreferencesAsync(new UserPreferencesPage(_isDarkMode, _currencyCode, _mainCurrencyCode));
        _isSaving = false;

        if (saved)
        {
            // Commit every field's baseline together so the button returns to its
            // clean (disabled) state and the "Unsaved changes" hint clears.
            _savedDarkMode = _isDarkMode;
            _savedCurrencyCode = _currencyCode;
            _savedMainCurrencyCode = _mainCurrencyCode;
            // Brief "Saved" confirmation on the button (design: save → check, then revert).
            _justSaved = true;
            StateHasChanged();
            await Task.Delay(OdsTiming.ConfirmFlashMs);
            _justSaved = false;
            StateHasChanged();
        }
    }

    public void Dispose()
    {
        if (_isDarkMode != _savedDarkMode)
            UserPreferences.PreviewDarkMode(_savedDarkMode);
    }
}
