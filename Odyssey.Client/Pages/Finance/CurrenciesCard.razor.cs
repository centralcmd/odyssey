using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class CurrenciesCard
{
    private List<ExistingCurrency> _currencies = new();
    private List<ExistingCurrency> _allCurrencies = new();
    private bool _isLoading = true;
    private bool _refetching;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = "";

    // Server pagination (OdsPager): 1-based page + rows-per-page; TotalCount from the PagedResult.
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    private const string PageStateKey = "currencies-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyCollection<string> _syncFilter = [];

    // Sort (§6.5): Code + Name curated, plus Sync for a caller who can read it; one OdsTableSort synced
    // with the table headers.
    private static readonly OdsTableSort DefaultSort = new("code", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingCurrency>> BaseSortFields =
    [
        new() { Key = "code", Label = "Code", Type = OdsSortType.Text },
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text },
    ];
    private static readonly IReadOnlyList<OdsSortField<ExistingCurrency>> SyncSortFields =
        [.. BaseSortFields, new() { Key = "sync", Label = "Sync", Type = OdsSortType.Status }];

    private IReadOnlyList<OdsSortField<ExistingCurrency>> SortFields => _canReadSync ? SyncSortFields : BaseSortFields;

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("active", "Active"), new("archived", "Archived")];

    // Overview/breakdown reflect the whole dataset (issue #277 follow-up): derived from the unfiltered
    // _allCurrencies, not the server-filtered display list, so they stay accurate under an active filter.
    private IReadOnlyList<OdsBreakdownRow> StatusRows => OdsBreakdown.StatusRows(
        _allCurrencies, c => c.Archived is not null ? "archived" : "active",
        new OdsBreakdownDef<string>("active", "Active", "income", "task_alt"),
        new OdsBreakdownDef<string>("archived", "Archived", "outline", "inventory_2"));

    private int _activeCount => _allCurrencies.Count(c => c.Archived is null);
    private int _archivedCount => _allCurrencies.Count - _activeCount;
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _statusFilter.Count > 0 || (_canReadSync && _syncFilter.Count > 0);

    /// <summary>
    /// Whether the page runs interactively; a swappable seam for the reason <c>Settings.InteractiveCheck</c>
    /// is one. Process-wide, so a test that moves it restores it (<c>ExchangeRateSyncCollection</c>).
    /// </summary>
    internal static Func<bool> InteractiveCheck { get; set; } = static () => OperatingSystem.IsBrowser();

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await LoadPermissionsAsync();
        await RestorePageStateAsync();
        StateHasChanged();
        await Task.WhenAll(RefreshAsync(), LoadSyncStatusAsync());
    }

    // Full refresh: reload the unfiltered overview set + the server-filtered display list. Used on load
    // and after any create/edit/archive/delete that can change the totals.
    private async Task RefreshAsync()
    {
        _allCurrencies = (await Currencies.ListAllAsync()).ItemsOrToast(Snackbar, "currencies");
        await ReloadAsync();
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<CurrenciesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(CurrenciesPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        // A sync filter or sort saved under a role that could read sync is dropped under one that cannot,
        // since the server would refuse the request.
        _syncFilter = _canReadSync ? SyncOptions.KnownValues(state.SyncFilter) : [];
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private CurrenciesPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        StatusFilter = [.. _statusFilter],
        SyncFilter = [.. _syncFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSyncFilterChanged(IReadOnlyCollection<string> values) { _syncFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class CurrenciesPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public List<string> SyncFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();

        _canCreate = user.HasPermission(PermissionClaims.CurrenciesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.CurrenciesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.CurrenciesDelete);
        _canReadSync = user.HasPermission(PermissionClaims.SystemSettingsRead);
        _canWriteSync = _canReadSync && user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canPurge = _canWriteSync && user.HasPermission(PermissionClaims.ExchangeRatesDelete);
    }

    // Server-side fetch (issue #277): search/status/sort applied by the API. Called on first load and
    // on every search/filter/sort change (search debounced via the field).
    private async Task GetCurrencies()
    {
        // First load blanks the table for a spinner; every later fetch keeps the rows and shows the bar.
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // The two-value status filter maps to the server active/archived param only when one is selected.
        var response = await _listLoader.RunAsync(ct => Currencies.ListAsync(
            _page, _pageSize,
            search: _search,
            status: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            sync: _canReadSync ? _syncFilter : null,
            ct: ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "currencies");
        if (load.IsSuccess)
        {
            _currencies = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            _rowErrors.Clear();
            _announce = _totalCount == 0 ? "No currencies match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} currenc{(_totalCount == 1 ? "y" : "ies")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load currencies.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Reset to page 1, then fetch — for any search / filter / sort / size change. Page navigation
    // calls GetCurrencies directly so it keeps the requested page.
    private Task ReloadAsync()
    {
        _page = 1;
        return GetCurrencies();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetCurrencies();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetCurrencies();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _statusFilter = [];
        _syncFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    // ── The record list: one card open at a time (RecordCard rule) ─────────────────────────────────
    private string? _openCode;

    // One meta line: symbol · decimals · the example amount · and, for a sync caller, the coverage.
    private IReadOnlyList<RenderFragment?> MetaFor(ExistingCurrency c)
    {
        var meta = new List<RenderFragment?>
        {
            string.IsNullOrWhiteSpace(c.Symbol) ? null : Text(c.Symbol),
            Text($"{c.MinorUnits} {(c.MinorUnits == 1 ? "decimal" : "decimals")}"),
            Text(Example(c)),
        };
        if (_canReadSync && CoverageText(c.Sync) is { } coverage)
            meta.Add(Text(coverage));
        return meta;
    }

    private static RenderFragment Text(string value) => b => b.AddContent(0, value);

    // The fetched-rate count, beside the coverage: what a purge would delete (manual rates not counted).
    // Never for the base, which has no rates of its own, or a currency never synced that holds none.
    private IReadOnlyList<OdsRecordCount> CountsFor(ExistingCurrency c) =>
        _canReadSync && c.Sync is { Locked: false } s && (s.Enabled || s.FetchedRowCount > 0)
            ? [new OdsRecordCount("cloud_download", s.FetchedRowCount.ToString("N0"), "Fetched rates")]
            : [];

    // A sample amount written as the app writes money everywhere — the figure, then the ISO code — so it
    // shows the currency's decimal precision in the form a user reads.
    internal static string Example(ExistingCurrency c) => OdsMoney.Format(1234.5m, c.CurrencyCode, c.MinorUnits);

    internal IReadOnlyList<OdsMenuItem> BuildActions(ExistingCurrency c)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(c)) });

        items.AddRange(SyncActions(c));

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = c.Archived is not null ? "unarchive" : "archive",
                Label = c.Archived is not null ? "Restore" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(c)),
            });
        }

        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyCode(c.CurrencyCode)) });

        var purge = PurgeAction(c);
        if (_canDelete || purge is not null)
            items.Add(new OdsMenuItem { Divider = true });
        if (purge is not null)
            items.Add(purge);
        if (_canDelete)
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, () => DeleteClicked(c)) });

        return items;
    }

    private bool _createOpen;
    private Guid _createKey;

    private bool _editCurrencyOpen;
    private Guid _editCurrencyKey;
    private ExistingCurrency? _editCurrency;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    // Edit opens the shared currency dialog in edit mode (DS AddCurrencyModal), not an inline row
    // editor. A fresh key each time re-initialises the dialog from the chosen row.
    private void EditClicked(ExistingCurrency currency)
    {
        if (!_canUpdate)
            return;

        _editCurrency = currency;
        _editCurrencyKey = Guid.NewGuid();
        _editCurrencyOpen = true;
    }

    // Refresh in place: reload the overview set and the current page rather than jumping back to
    // page 1, so the edited row stays where the user was looking at it.
    private async Task OnCurrencyEdited()
    {
        _allCurrencies = (await Currencies.ListAllAsync()).ItemsOrToast(Snackbar, "currencies");
        await GetCurrencies();
    }

    private Task ToggleArchive(ExistingCurrency currency) =>
        PutCurrency(currency, currency.Name, currency.Symbol, currency.MinorUnits, currency.Archived is null);

    private async Task PutCurrency(ExistingCurrency currency, string name, string symbol, int minorUnits, bool archived)
    {
        if (!_canUpdate)
            return;

        if (string.IsNullOrWhiteSpace(name))
        {
            Snackbar.Add("Name is required.", Severity.Error);
            return;
        }

        var update = new NewCurrency
        {
            CurrencyCode = currency.CurrencyCode,
            Name = name.Trim(),
            Symbol = string.IsNullOrWhiteSpace(symbol) ? currency.Symbol : symbol.Trim(),
            MinorUnits = minorUnits,
            Archived = archived,
        };

        if ((await Currencies.UpdateAsync(currency.CurrencyCode, update)).Toast(Snackbar, "Update failed", "Currency updated."))
        {
            currency.Name = update.Name;
            currency.Symbol = update.Symbol;
            currency.MinorUnits = update.MinorUnits;
            currency.Archived = archived ? currency.Archived ?? DateTime.UtcNow : null;
            // Session-wide currency cache is now stale — every picker must re-fetch (issue #372).
            ReferenceData.InvalidateCurrencies();
            _allCurrencies = (await Currencies.ListAllAsync()).ItemsOrToast(Snackbar, "currencies");
            // Archiving takes a currency out of the sync and restoring brings it back, so a sync caller
            // re-reads the page for the row's projection.
            if (_canReadSync)
                await GetCurrencies();
        }

        StateHasChanged();
    }

    // ── Delete: a record list has no inline confirm, so it asks first ─────────────────────────────
    private ExistingCurrency? _deleting;
    private bool _deleteOpen;

    private void DeleteClicked(ExistingCurrency currency)
    {
        if (!_canDelete)
            return;
        _deleting = currency;
        _deleteOpen = true;
    }

    private void OnDeleteOpenChanged(bool open) => _deleteOpen = open;

    // Null closes the dialog; a refusal (a currency still in use) stays in it as the server's message.
    private async Task<string?> ConfirmDeleteAsync()
    {
        if (_deleting is not { } currency || !_canDelete)
            return null;

        var result = await Currencies.DeleteAsync(currency.CurrencyCode);
        if (!result.IsSuccess)
            return string.IsNullOrWhiteSpace(result.Error) ? "The currency couldn’t be deleted." : result.Error;

        Snackbar.Add("Currency deleted.", Severity.Success);
        ReferenceData.InvalidateCurrencies();
        if (_openCode == currency.CurrencyCode)
            _openCode = null;
        // Full refresh, not a local Remove: the delete changes the total, so the pager and the current
        // page have to be re-fetched or the page renders short against a stale count.
        await RefreshAsync();
        return null;
    }

    private Task CopyCode(string currencyCode) =>
        Clipboard.CopyAsync(currencyCode, "Currency code copied to clipboard.");

    public void Dispose() => _listLoader.Dispose();
}
