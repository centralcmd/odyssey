using Microsoft.JSInterop;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

public partial class InstrumentPricesPage
{
    internal const string PageStateKey = "instrument-prices-page";
    internal const string MissingInstrumentMessage = "This instrument no longer exists";

    /// <summary>A deep link's instrument filter (<c>?instrument={id}</c>), for this visit only (§3.3 Deep links).</summary>
    [SupplyParameterFromQuery(Name = "instrument")] public string? InstrumentQuery { get; set; }

    /// <summary><c>&amp;add=1</c> opens Add price locked to the linked instrument (the Instruments page's "Add price").</summary>
    [SupplyParameterFromQuery(Name = "add")] public string? AddQuery { get; set; }

    private List<ExistingInstrumentPrice> _prices = [];
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    // Announced through Announce() so a repeated message ("Couldn't delete the price." twice) is spoken
    // again — a bound Message is silent when the text does not change (PR #457 accessibility review).
    private OdsLiveAnnouncer? _polite;
    private OdsLiveAnnouncer? _assertive;

    // A write announces its own outcome; the refetch after it must not replace that with the list count.
    private bool _suppressListAnnouncement;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();

    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;

    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    // Whole-table counts for the overview tiles and the sub-line — never the page in view.
    private int? _allCount;
    private int? _currentCount;
    private int? _manualCount;
    private List<string> _attributions = [];
    private Dictionary<string, int> _minorUnits = new(StringComparer.OrdinalIgnoreCase);

    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private string? _instrumentFilter;
    private OdsOption? _instrumentFilterOption;
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyCollection<string> _sourceFilter = [];
    private DateTime? _from;
    private DateTime? _to;
    private DateOnly? _today;

    // A deep link overrides the persisted filters for this visit only: what was saved is written back unchanged.
    private PricesPageState? _savedFilters;

    private static readonly OdsTableSort DefaultSort = new("date", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;

    internal static readonly IReadOnlyList<OdsSortField<ExistingInstrumentPrice>> SortFields =
    [
        new() { Key = "date", Label = "Trading date", Type = OdsSortType.Date },
        new() { Key = "instrument", Label = "Instrument", Type = OdsSortType.Text },
        new() { Key = "close", Label = "Close", Type = OdsSortType.Number },
    ];

    private static readonly OdsBreakdownDef<InstrumentPriceStatus>[] StatusDefs =
    [
        StatusDef(InstrumentPriceStatus.Current, "income"),
        StatusDef(InstrumentPriceStatus.Historical, "outline"),
    ];

    private static readonly OdsBreakdownDef<InstrumentPriceSource>[] SourceDefs =
    [
        SourceDef(InstrumentPriceSource.Manual),
        SourceDef(InstrumentPriceSource.Fetched),
    ];

    internal static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs);
    internal static readonly IReadOnlyList<OdsOption> SourceOptions = OdsBreakdown.Options(SourceDefs);

    private static OdsBreakdownDef<InstrumentPriceStatus> StatusDef(InstrumentPriceStatus status, string tone)
    {
        var info = InstrumentVisuals.StatusOf(status);
        return new(status, info.Label, tone, info.Icon);
    }

    private static OdsBreakdownDef<InstrumentPriceSource> SourceDef(InstrumentPriceSource source)
    {
        var info = InstrumentVisuals.SourceOf(source);
        return new(source, info.Label, "outline", info.Icon);
    }

    private IReadOnlyList<OdsBreakdownRow> StatusRows => _allCount is not { } all || _currentCount is not { } current ? [] :
        OdsBreakdown.CountedStatusRows(s => s == InstrumentPriceStatus.Current ? current : all - current, StatusDefs);

    private IReadOnlyList<OdsBreakdownRow> SourceRows => _allCount is not { } all || _manualCount is not { } manual ? [] :
        OdsBreakdown.CountedStatusRows(s => s == InstrumentPriceSource.Manual ? manual : all - manual, SourceDefs);

    private int FetchedCount => _allCount is { } all && _manualCount is { } manual ? all - manual : 0;

    /// <summary>"{n} prices", then the attribution as plain text while fetched prices exist (never auto-linked).</summary>
    internal string SubLineText
    {
        get
        {
            if (_allCount is not { } all)
                return string.Empty;
            var text = $"{all.ToString("N0", CultureInfo.CurrentCulture)} price{(all == 1 ? "" : "s")}";
            if (FetchedCount > 0 && _attributions.Count > 0)
                text += $" · Prices from {string.Join(", ", _attributions)}";
            return text;
        }
    }

    private IReadOnlyList<OdsOption> InstrumentFilterOptions =>
        _instrumentFilterOption is { } option ? [option] : [];

    private bool _hasFilters =>
        !string.IsNullOrWhiteSpace(_search) || _instrumentFilter is not null || _statusFilter.Count > 0
        || _sourceFilter.Count > 0 || _from is not null || _to is not null;

    private DateTime? MaxDate => _today?.ToDateTime(TimeOnly.MinValue);

    protected override async Task OnInitializedAsync()
    {
        _today = InstrumentVisuals.TryOsloToday(Time);
        await LoadPermissionsAsync();
        await RestorePageStateAsync();

        var linked = await ApplyDeepLinkAsync();
        await LoadMinorUnitsAsync();
        await RefreshAsync();

        if (linked is { Archived: null } instrument && _canCreate && AddQuery == "1")
            OpenDialog(price: null, lockedId: instrument.InstrumentId, focusClose: false, returnFocusId: null);
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.InstrumentPricesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.InstrumentPricesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.InstrumentPricesDelete);
    }

    private async Task LoadMinorUnitsAsync()
    {
        try
        {
            _minorUnits = ((await ReferenceData.CurrenciesAsync()) ?? [])
                .GroupBy(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => DashboardFigures.MinorUnits(g.First()), StringComparer.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            // The close then shows the default two decimals; the code is never at risk.
        }
    }

    /// <summary>
    /// Resolves <c>?instrument=</c>. An unparseable or unknown id is the same case (§3.3 step 1, 5): a toast and a
    /// polite announcement, and the list stays unfiltered.
    /// </summary>
    private async Task<ExistingInstrument?> ApplyDeepLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(InstrumentQuery))
            return null;

        if (Guid.TryParse(InstrumentQuery, out var id)
            && await Instruments.GetAsync(id) is { IsSuccess: true, Value: { } instrument })
        {
            _savedFilters = BuildPageState();
            _search = string.Empty;
            _statusFilter = [];
            _sourceFilter = [];
            _from = null;
            _to = null;
            _instrumentFilter = instrument.InstrumentId.ToString();
            _instrumentFilterOption = InstrumentVisuals.OptionFor(instrument);
            return instrument;
        }

        Snackbar.Add(MissingInstrumentMessage, Severity.Warning);
        Announce(MissingInstrumentMessage);
        return null;
    }

    // ── Page state ───────────────────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<PricesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(PricesPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _statusFilter = StatusOptions.KnownValues(state.StatusFilter);
        _sourceFilter = SourceOptions.KnownValues(state.SourceFilter);
        _from = state.From;
        _to = state.To;
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private PricesPageState BuildPageState()
    {
        var filters = _savedFilters;
        return new()
        {
            OverviewOpen = _overviewOpen,
            SearchOpen = _searchOpen,
            Search = filters is null ? _search : filters.Search,
            StatusFilter = filters is null ? [.. _statusFilter] : filters.StatusFilter,
            SourceFilter = filters is null ? [.. _sourceFilter] : filters.SourceFilter,
            From = filters is null ? _from : filters.From,
            To = filters is null ? _to : filters.To,
            SortField = _sort.Key,
            SortDirection = _sort.Dir,
            PageSize = _pageSize,
        };
    }

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    internal sealed class PricesPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public List<string> SourceFilter { get; set; } = [];
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSourceFilterChanged(IReadOnlyCollection<string> values) { _sourceFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnFromChanged(DateTime? value) { _from = value; PersistPageState(); await ReloadAsync(); }
    private async Task OnToChanged(DateTime? value) { _to = value; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private async Task OnInstrumentFilterChanged(string? value)
    {
        _instrumentFilter = string.IsNullOrEmpty(value) ? null : value;
        if (_instrumentFilter is null)
            _instrumentFilterOption = null;
        await ReloadAsync();
    }

    // The toolbar picker filters by any instrument, archived included — an archived instrument keeps its history.
    private async Task<IReadOnlyList<OdsOption>> SearchFilterInstrumentsAsync(string query, CancellationToken ct)
    {
        var result = await Instruments.ListAsync(1, 20, new InstrumentsFilter { Search = query, IncludeArchived = true }, ct);
        if (!result.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.Error);
        }

        var options = (result.Value?.Items ?? []).Select(InstrumentVisuals.OptionFor).ToList();
        // Keep the picked one's label once the result list moves on.
        _instrumentFilterOption = options.FirstOrDefault(o => o.Value == _instrumentFilter) ?? _instrumentFilterOption;
        return options;
    }

    // ── Loading ──────────────────────────────────────────────────────────────────
    private InstrumentPricesFilter CurrentFilter() => new()
    {
        Search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(),
        InstrumentIds = Guid.TryParse(_instrumentFilter, out var id) ? [id] : null,
        // Every option selected means no filter.
        Statuses = _statusFilter.Count is 0 || _statusFilter.Count == StatusOptions.Count ? null
            : [.. _statusFilter.Select(Enum.Parse<InstrumentPriceStatus>)],
        Sources = _sourceFilter.Count is 0 || _sourceFilter.Count == SourceOptions.Count ? null
            : [.. _sourceFilter.Select(Enum.Parse<InstrumentPriceSource>)],
        From = _from is { } from ? DateOnly.FromDateTime(from) : null,
        To = _to is { } to ? DateOnly.FromDateTime(to) : null,
        SortBy = _sort.Key switch
        {
            "instrument" => InstrumentPriceSortBy.Instrument,
            "close" => InstrumentPriceSortBy.Close,
            _ => InstrumentPriceSortBy.TradingDate,
        },
        Ascending = _sort.Dir == OdsSortDirection.Asc,
    };

    // Full refresh: the whole-table counts and attribution, then the display slice. Used on load and after any write.
    // A write passes announceList: false — it announces its own outcome, which the list count must not replace.
    private async Task RefreshAsync(bool announceList = true)
    {
        _suppressListAnnouncement = !announceList;
        try
        { await Task.WhenAll(LoadCountsAsync(), GetPrices()); }
        finally
        { _suppressListAnnouncement = false; }
    }

    private async Task LoadCountsAsync()
    {
        var all = Prices.ListAsync(1, 1);
        var current = Prices.ListAsync(1, 1, new InstrumentPricesFilter { Statuses = [InstrumentPriceStatus.Current] });
        var manual = Prices.ListAsync(1, 1, new InstrumentPricesFilter { Sources = [InstrumentPriceSource.Manual] });
        var summary = Instruments.GetSummaryAsync();
        await Task.WhenAll(all, current, manual, summary);

        _allCount = (await all) is { IsSuccess: true, Value: { } a } ? a.TotalCount : null;
        _currentCount = (await current) is { IsSuccess: true, Value: { } c } ? c.TotalCount : null;
        _manualCount = (await manual) is { IsSuccess: true, Value: { } m } ? m.TotalCount : null;
        _attributions = (await summary) is { IsSuccess: true, Value.Attributions: { } attributions } ? attributions : [];
    }

    private async Task GetPrices()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var filter = CurrentFilter();
        var response = await _listLoader.RunAsync(ct => Prices.ListAsync(_page, _pageSize, filter, ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "instrument prices");
        if (load.IsSuccess)
        {
            _prices = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            if (!_suppressListAnnouncement)
                Announce(_totalCount == 0 ? "No prices match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} price{(_totalCount == 1 ? "" : "s")}.");
        }
        else
        {
            _loadError = true;
            AnnounceAssertive("Couldn't load instrument prices.");
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    private Task ReloadAsync()
    {
        _page = 1;
        return GetPrices();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetPrices();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetPrices();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _instrumentFilter = null;
        _instrumentFilterOption = null;
        _statusFilter = [];
        _sourceFilter = [];
        _from = null;
        _to = null;
        _savedFilters = null;
        PersistPageState();
        await ReloadAsync();
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────
    private readonly HashSet<Guid> _busy = [];

    internal bool IsBusy(ExistingInstrumentPrice price) => _busy.Contains(price.InstrumentPriceId);

    /// <summary>"{symbol} {date}" — the expand toggle's and the busy spinner's name ("Saving EQNR 6 Nov 2026").</summary>
    internal static string RowLabelFor(ExistingInstrumentPrice price) => $"{price.Symbol} {FormatDate(price.TradingDate)}";

    internal static string FormatDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private string FormatClose(ExistingInstrumentPrice price) =>
        InstrumentVisuals.FormatClose(price.Close, price.CurrencyCode,
            _minorUnits.TryGetValue(price.CurrencyCode, out var minor) ? minor : OdsMoney.DefaultMinorUnits);

    /// <summary>
    /// A fetched row returns NO items — so it renders no menu trigger at all (§3.3), never an empty or disabled
    /// menu. A manual row lists only what the caller's claims allow.
    /// </summary>
    internal IReadOnlyList<OdsMenuItem> BuildActions(ExistingInstrumentPrice price, OdsRecordActionContext ctx)
    {
        if (price.Source == InstrumentPriceSource.Fetched)
            return [];

        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit", Label = "Edit",
                OnClick = EventCallback.Factory.Create(this, () => OpenDialog(price, null, false, ctx.MenuTriggerId)),
            });
        }

        if (_canDelete)
        {
            if (items.Count > 0)
                items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, ctx.Remove) });
        }

        return items;
    }

    private async Task HandleDelete(object key)
    {
        if (!_canDelete || key is not Guid id || _prices.FirstOrDefault(p => p.InstrumentPriceId == id) is not { } price)
            return;

        // Where focus lands if the row goes: the next row's header, else the previous one's, else the h1.
        var index = _prices.IndexOf(price);
        var candidates = _prices.Skip(index + 1).Concat(_prices.Take(index).Reverse())
            .Select(p => p.InstrumentPriceId).ToList();

        _busy.Add(id);
        Announce($"Deleting price {RowLabelFor(price)}");
        StateHasChanged();
        var result = await Prices.DeleteAsync(id);
        _busy.Remove(id);
        // The row count and the pager's total come from one fetch: refetch, never just drop the row.
        await RefreshAsync(announceList: false);
        ReportDelete(result, price);
        if (result.IsSuccess || result.Status == HttpStatusCode.NotFound)
            await FocusAfterRemovalAsync(candidates);
    }

    private OdsRecordTable<ExistingInstrumentPrice>? _table;

    private async Task FocusAfterRemovalAsync(IReadOnlyList<Guid> candidates)
    {
        await InvokeAsync(StateHasChanged);
        var present = _prices.Select(p => p.InstrumentPriceId).ToHashSet();
        if (_table is not null && candidates.FirstOrDefault(present.Contains) is var next && next != Guid.Empty
            && await _table.FocusRowHeaderAsync(next))
            return;
        try
        { await Js.InvokeVoidAsync("odsFocusChainOrHeading", Array.Empty<string>()); }
        catch (JSException) { }
    }

    private void ReportDelete(ApiResult result, ExistingInstrumentPrice price)
    {
        if (result.IsSuccess)
        {
            Snackbar.Add("Price deleted.", Severity.Success);
            Announce($"Price {RowLabelFor(price)} deleted");
        }
        else if (result.Status == HttpStatusCode.NotFound)
        {
            Snackbar.Add("This price no longer exists.", Severity.Warning);
            Announce("This price no longer exists");
        }
        else
        {
            result.Toast(Snackbar, "Delete failed");
            AnnounceAssertive("Couldn't delete the price.");
        }
    }

    // ── Dialog ───────────────────────────────────────────────────────────────────
    private Guid _dialogKey;
    private bool _dialogOpen;
    private ExistingInstrumentPrice? _dialogPrice;
    private Guid? _dialogLockedId;
    private bool _dialogFocusClose;
    private string? _dialogReturnFocusId;

    private void AddClicked()
    {
        if (!_canCreate)
            return;
        OpenDialog(null, null, false, null);
    }

    private void OpenDialog(ExistingInstrumentPrice? price, Guid? lockedId, bool focusClose, string? returnFocusId)
    {
        _dialogPrice = price;
        _dialogLockedId = lockedId;
        _dialogFocusClose = focusClose;
        _dialogReturnFocusId = returnFocusId;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private void OnDialogBusyChanged(bool busy)
    {
        if (_dialogPrice is not { } price)
            return;
        if (busy)
        {
            _busy.Add(price.InstrumentPriceId);
            Announce($"Saving price {RowLabelFor(price)}");
        }
        else
            _busy.Remove(price.InstrumentPriceId);
        StateHasChanged();
    }

    private async Task OnPriceSaved(ExistingInstrumentPrice saved)
    {
        await RefreshAsync(announceList: false);
        Announce(_dialogPrice is null ? $"Price {RowLabelFor(saved)} created" : $"Price {RowLabelFor(saved)} saved");
    }

    /// <summary>"Edit that price" after a duplicate: the existing manual price opens for editing, focus on Close.</summary>
    private async Task EditExistingAsync(Guid priceId)
    {
        if (!_canUpdate)
            return;

        _dialogOpen = false;
        var result = await Prices.GetAsync(priceId);
        if (result is { IsSuccess: true, Value: { } price })
        {
            OpenDialog(price, null, true, null);
            return;
        }

        Snackbar.Add("That price no longer exists.", Severity.Warning);
        Announce("That price no longer exists");
    }

    private void Announce(string message) => _ = _polite?.Announce(message);

    private void AnnounceAssertive(string message) => _ = _assertive?.Announce(message);

    public void Dispose() => _listLoader.Dispose();
}
