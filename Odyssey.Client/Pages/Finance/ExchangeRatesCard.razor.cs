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
    // The whole table's counts, currencies and attribution (issue #351 §3.C) — one summary call, so the
    // page cost no longer grows with the fetched history the way loading every row did.
    private ExchangeRateSummary? _summary;
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

    private const string PageStateKey = "exchange-rates-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _toFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyCollection<string> _sourceFilter = [];

    // Sort (§6.6): As-of date (default) · Currency pair · Rate; one OdsTableSort synced with headers.
    private static readonly OdsTableSort DefaultSort = new("asOf", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingExchangeRate>> _sortFields =
    [
        new() { Key = "asOf", Label = "As-of date", Type = OdsSortType.Date },
        new() { Key = "pair", Label = "Currency pair", Type = OdsSortType.Text },
        new() { Key = "rate", Label = "Rate", Type = OdsSortType.Number },
    ];

    // The "By status" tile counts the WHOLE table from the summary, never the page in view.
    private IReadOnlyList<OdsBreakdownRow> StatusRows => _summary is not { } summary ? [] :
        OdsBreakdown.CountedStatusRows<ExchangeRateStatus>(
            status => status switch
            {
                ExchangeRateStatus.Current => summary.ByStatus.Current,
                ExchangeRateStatus.Upcoming => summary.ByStatus.Upcoming,
                _ => summary.ByStatus.Historical,
            },
            StatusDef(ExchangeRateStatus.Current, "income", "bolt"),
            StatusDef(ExchangeRateStatus.Upcoming, "info", "schedule"),
            StatusDef(ExchangeRateStatus.Historical, "outline", "history"));

    // The row's mark takes its status chip's colour pair (Odyssey Design System · reference-data.md, rate
    // status): Current green, Upcoming blue, Historical the secondary-text grey Archived and Draft wear.
    // The chip still carries the label.
    internal static (string Bg, string Fg) MarkColours(ExchangeRateStatus status) => status switch
    {
        ExchangeRateStatus.Current => ("var(--finance-income-soft)", "var(--finance-income)"),
        ExchangeRateStatus.Upcoming => ("var(--info-soft)", "var(--info-text)"),
        _ => ("color-mix(in srgb, var(--mud-palette-text-secondary) var(--tint-soft), transparent)", "var(--mud-palette-text-secondary)"),
    };

    private static OdsBreakdownDef<ExchangeRateStatus> StatusDef(ExchangeRateStatus status, string tone, string icon) =>
        new(status, OdsTypeRegistries.ExchangeRateStatusOf(status).Label, tone, icon);

    private IReadOnlyList<OdsOption> _toOptions => [.. (_summary?.ToCurrencyCodes ?? []).Select(OdsOption.From)];

    // Values are the enum names the API binds; labels come from the shared vocabularies.
    private static readonly IReadOnlyList<OdsOption> _statusOptions =
    [
        .. new[] { ExchangeRateStatus.Current, ExchangeRateStatus.Upcoming, ExchangeRateStatus.Historical }
            .Select(s => new OdsOption(s.ToString(), OdsTypeRegistries.ExchangeRateStatusOf(s).Label)),
    ];

    private static readonly IReadOnlyList<OdsOption> _sourceOptions =
    [
        new(nameof(ExchangeRateSource.Manual), OdsTypeRegistries.ExchangeRateSources["Manual"].Label),
        new(nameof(ExchangeRateSource.Fetched), OdsTypeRegistries.ExchangeRateSources[nameof(ExchangeRateProvider.NorgesBank)].Label),
    ];

    private string SubLineText
    {
        get
        {
            if (_summary is not { } summary)
                return string.Empty;
            var text = $"{summary.TotalCount.ToString("N0", CultureInfo.CurrentCulture)} rate{(summary.TotalCount == 1 ? "" : "s")}";
            if (summary.LatestAsOfUtc is { } latest)
                text += $" · latest {latest.ToLocalTime():MMM dd, yyyy, HH:mm}";
            if (!string.IsNullOrEmpty(summary.Attribution))
                text += $" · Source: {summary.Attribution}";
            return text;
        }
    }

    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search) || _toFilter.Count > 0 || _statusFilter.Count > 0 || _sourceFilter.Count > 0;

    private static RenderFragment ChipFor(OdsChipValue chip) => builder =>
    {
        builder.OpenComponent<OdsChip>(0);
        builder.AddComponentParameter(1, nameof(OdsChip.Tone), chip.Tone);
        builder.AddComponentParameter(2, nameof(OdsChip.Dot), chip.Dot);
        builder.AddComponentParameter(3, nameof(OdsChip.ChildContent), (RenderFragment)(b => b.AddContent(0, chip.Label)));
        builder.CloseComponent();
    };

    private static bool IsFetched(ExistingExchangeRate rate) => rate.Source == ExchangeRateSource.Fetched;


    // Both figures live on ExchangeRateFigures so the zero guard and the format are testable.
    private static string Fmt(decimal n) => ExchangeRateFigures.Format(n);

    private static decimal Inverse(decimal rate) => ExchangeRateFigures.Inverse(rate);

    /// <summary>
    /// Whether the page runs interactively; a swappable seam for the reason <c>Settings.InteractiveCheck</c>
    /// is one. Process-wide, so a test that moves it restores it (<c>ExchangeRateSyncCollection</c>).
    /// </summary>
    internal static Func<bool> InteractiveCheck { get; set; } = static () => OperatingSystem.IsBrowser();

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
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
        _sourceFilter = _sourceOptions.KnownValues(state.SourceFilter);
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
        SourceFilter = [.. _sourceFilter],
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
    private async Task OnSourceFilterChanged(IReadOnlyCollection<string> values) { _sourceFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class RatesPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TargetFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public List<string> SourceFilter { get; set; } = [];
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

    // Full refresh: the summary (counts, target options, attribution) and then the display slice.
    // Used on load and after create, edit or delete — any of which can change a pair's Current row.
    private async Task RefreshAsync()
    {
        if ((await ExchangeRates.GetSummaryAsync()).OrToast(Snackbar, "Unable to load the exchange-rate summary") is { } summary)
            _summary = summary;
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

        // Every option selected means no filter: sending them all would also drop the Upcoming rows the
        // server lists beside them.
        var status = _statusFilter.Count == _statusOptions.Count ? null : _statusFilter;
        var source = _sourceFilter.Count == _sourceOptions.Count ? null : _sourceFilter;
        var response = await _listLoader.RunAsync(ct => ExchangeRates.ListAsync(
            _page, _pageSize,
            search: _search,
            toCurrencies: _toFilter,
            status: status,
            source: source,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            ct: ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "exchange rates");
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
        _sourceFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    internal IReadOnlyList<OdsMenuItem> BuildActions(ExistingExchangeRate r, OdsRecordActionContext ctx)
    {
        var items = new List<OdsMenuItem>();

        // A fetched rate is never edited in place (a 422 keyed source): the Edit item is absent.
        if (_canUpdate && !IsFetched(r))
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(r)) });
        }

        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyId(r.ExchangeRateId)) });

        // Nor is one deleted on its own (a 422 keyed source): the sync would never refill the gap, so fetched
        // rates leave only through the per-currency purge on Currencies.
        if (_canDelete && !IsFetched(r))
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

    public void Dispose() => _listLoader.Dispose();
}
