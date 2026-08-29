using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class ExchangeRatesCard
{
    private List<ExistingExchangeRate> _rates = new();
    private List<ExistingExchangeRate> _allRates = new();
    private HashSet<Guid> _currentIds = new();
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";

    // Server pagination (OdsPager): 1-based page + rows-per-page; TotalCount from the PagedResult.
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    private const string PageStateKey = "exchange-rates-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _toFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyList<string> _toCodes = [];

    // Sort (§6.6): As-of date (default) · Currency pair · Rate; one OdsTableSort synced with headers.
    private static readonly OdsTableSort DefaultSort = new("asOf", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingExchangeRate>> _sortFields =
    [
        new() { Key = "asOf", Label = "As-of date", Type = OdsSortType.Date },
        new() { Key = "pair", Label = "Currency pair", Type = OdsSortType.Text },
        new() { Key = "rate", Label = "Rate", Type = OdsSortType.Number },
    ];

    private IReadOnlyList<OdsBreakdownRow> StatusRows => OdsBreakdown.StatusRows(
        _rates, r => _currentIds.Contains(r.ExchangeRateId) ? "current" : "historical",
        new OdsBreakdownDef<string>("current", "Current", "income", "bolt"),
        new OdsBreakdownDef<string>("historical", "Historical", "outline", "history"));

    private IReadOnlyList<OdsOption> _toOptions => [.. _toCodes.Select(OdsOption.From)];
    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("current", "Current"), new("historical", "Historical")];

    private int _pairCount => _allRates.Select(r => $"{r.FromCurrencyCode}>{r.ToCurrencyCode}").Distinct().Count();
    private DateTime? _latestAsOf => _allRates.Count == 0 ? null : _allRates.Max(r => r.AsOf);
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _toFilter.Count > 0 || _statusFilter.Count > 0;

    private static string Fmt(decimal n) => n.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        await LoadPermissionsAsync();
        await RefreshAsync();
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    // The Target options are data-driven (currencies load later), so its saved
    // values are restored as-is; only the static Status filter is sanitised.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<RatesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(RatesPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _toFilter = state.TargetFilter ?? [];
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private RatesPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        TargetFilter = [.. _toFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnToFilterChanged(IReadOnlyCollection<string> values) { _toFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class RatesPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TargetFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();

        _canCreate = user.HasPermission(PermissionClaims.ExchangeRatesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.ExchangeRatesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.ExchangeRatesDelete);
    }

    // Full refresh (issue #277): the current/historical badge and the Target-filter options are
    // derived from the whole rate set, so we fetch it once (unfiltered) for that, then fetch the
    // server-filtered/sorted display slice. Used on load and after create/delete.
    private async Task RefreshAsync()
    {
        _allRates = (await ExchangeRates.ListAllAsync()).ItemsOrToast(Snackbar, "exchange rates");
        RecomputeDerived();
        await ReloadAsync();
    }

    // Server-side display fetch: search + target/status filters + sort applied by the API.
    private async Task GetRates()
    {
        // First load blanks the table for a spinner; every later fetch keeps the rows and shows the bar.
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // current/historical is a two-value toggle: filter only when exactly one is selected.
        var result = await ExchangeRates.ListAsync(
            _page, _pageSize,
            search: _search,
            toCurrencies: _toFilter,
            status: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "exchange rates");
        if (load.IsSuccess)
        {
            _rates = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            _announce = _totalCount == 0 ? "No rates match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} rate{(_totalCount == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load exchange rates.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // The current rate for a (From, To) pair is the one with the latest AsOf (CreatedAt breaks ties);
    // everything else is historical. Derived from the full set so the badge stays correct under filters.
    private void RecomputeDerived()
    {
        _currentIds = _allRates
            .GroupBy(r => (r.FromCurrencyCode, r.ToCurrencyCode))
            .Select(g => g.OrderByDescending(r => r.AsOf).ThenByDescending(r => r.CreatedAt).First().ExchangeRateId)
            .ToHashSet();

        _toCodes = _allRates
            .Select(r => r.ToCurrencyCode)
            .Distinct()
            .OrderBy(code => code)
            .ToList();
    }

    // Reset to page 1, then fetch — for any search / filter / sort / size change. Page navigation
    // calls GetRates directly so it keeps the requested page.
    private Task ReloadAsync()
    {
        _page = 1;
        return GetRates();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetRates();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetRates();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _toFilter = [];
        _statusFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingExchangeRate r, OdsRecordActionContext ctx)
    {
        var items = new List<OdsMenuItem>
        {
            new()
            {
                Icon = ctx.Expanded ? "close" : "expand_more",
                Label = ctx.Expanded ? "Collapse" : "View details",
                OnClick = EventCallback.Factory.Create(this, ctx.Toggle),
            },
        };

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(r)) });
        }

        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyId(r.ExchangeRateId)) });

        if (_canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, ctx.Remove) });
        }

        return items;
    }

    private bool _createOpen;
    private Guid _createKey;

    private bool _editRateOpen;
    private Guid _editRateKey;
    private ExistingExchangeRate? _editRate;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    // Edit reuses the record-rate dialog in edit mode (DS RecordRateModal) with the currency pair
    // locked. A fresh key each time re-initialises the dialog from the chosen row.
    private void EditClicked(ExistingExchangeRate rate)
    {
        if (!_canUpdate)
            return;

        _editRate = rate;
        _editRateKey = Guid.NewGuid();
        _editRateOpen = true;
    }

    // A corrected AsOf can change which entry is Current for the pair, so refresh the full set.
    private Task OnRateEdited() => RefreshAsync();

    private async Task HandleDelete(object key)
    {
        if (!_canDelete)
            return;

        var rate = _rates.FirstOrDefault(r => r.ExchangeRateId.Equals(key));
        if (rate is null)
            return;

        if ((await ExchangeRates.DeleteAsync(rate.ExchangeRateId)).Toast(Snackbar, "Delete failed", "Exchange rate deleted."))
        {
            await RefreshAsync();
        }

        StateHasChanged();
    }

    private Task CopyId(Guid exchangeRateId) =>
        Clipboard.CopyAsync(exchangeRateId.ToString(), "Exchange rate ID copied to clipboard.");
}
