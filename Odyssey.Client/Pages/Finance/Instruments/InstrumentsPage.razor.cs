using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>
/// The <c>/instruments</c> page (issue #452 §3): state, claims, the list fetch, deep links, the row menu and its
/// writes. The markup and the record fragment are in <c>InstrumentsPage.razor</c>.
/// </summary>
public partial class InstrumentsPage : IDisposable
{
    internal const string PageStateKey = "instruments-page";
    internal const string NoPriceYet = "No price yet";
    internal const string IdentityLockedLine =
        "Symbol, exchange, ISIN, type and currency are locked because this instrument has prices or sources.";
    internal const string DeletePricesFirstLine = "To delete this instrument, delete its prices first.";
    internal const string UnknownInstrument = "This instrument no longer exists";

    /// <summary>The deep link (§3.3): <c>/instruments?instrument={id}</c>. Parsed with <see cref="Guid.TryParse(string?, out Guid)"/>.</summary>
    [SupplyParameterFromQuery(Name = "instrument")] public string? InstrumentQuery { get; set; }

    /// <summary>
    /// Whether the page runs interactively; a swappable seam, as on Currencies. Process-wide, so a test that moves
    /// it restores it (<c>InstrumentsCollection</c>).
    /// </summary>
    internal static Func<bool> InteractiveCheck { get; set; } = static () => OperatingSystem.IsBrowser();

    private List<ExistingInstrument> _instruments = [];
    private InstrumentSummary? _summary;
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private readonly ListLoader _listLoader = new();
    private OdsLiveAnnouncer? _polite;
    private OdsLiveAnnouncer? _assertive;

    // ── Claims (§3.1): every surface is absent without its claim, never greyed ──────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadPrices;
    private bool _canCreatePrices;
    private bool _canDeletePrices;
    private bool _canPurgeFetched;
    private bool _canReadSources;
    private bool _canEditSources;

    // ── Toolbar state, persisted per user (PageStateService) ───────────────────────────────────────
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _typeFilter = [];
    private IReadOnlyCollection<string> _currencyFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private int _batch = OdsPageSizes.Batch[0];
    private static readonly OdsTableSort DefaultSort = new("symbol", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    // Currency decimals for the close figures, and the Currency filter's options.
    private IReadOnlyList<ExistingCurrency> _currencies = [];

    private Guid? _openId;
    private readonly HashSet<Guid> _busy = [];

    // Deep links (§3.3).
    private string? _handledQuery;
    private bool _initialized;
    private ExistingInstrument? _pinned;
    private Guid? _revealId;
    private Guid? _focusRecordId;
    private bool _focusHeading;

    // ── Price sources: one status read per visit (§3.4), shared by every expanded record ───────────
    private InstrumentPriceSourcesStatus? _sourcesStatus;
    private bool _sourcesFailed;
    private bool _sourcesDirty;
    private readonly Dictionary<Guid, int> _bindingCounts = [];
    private bool _discardOpen;
    private Guid? _pendingOpen;
    private string? _pendingNavigation;

    private static readonly IReadOnlyList<OdsSortField<ExistingInstrument>> AllSortFields =
    [
        new() { Key = "symbol", Label = "Symbol", Type = OdsSortType.Text },
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status },
        new() { Key = "latest", Label = "Latest price date", Type = OdsSortType.Date },
    ];

    /// <summary>"Latest price date" only for <c>instrument-prices.read</c> — the server <c>403</c>s it otherwise.</summary>
    internal static IReadOnlyList<OdsSortField<ExistingInstrument>> SortFieldsFor(bool canReadPrices) =>
        canReadPrices ? AllSortFields : [.. AllSortFields.Where(f => f.Key != "latest")];

    private IReadOnlyList<OdsSortField<ExistingInstrument>> SortFields => SortFieldsFor(_canReadPrices);

    private static InstrumentSortBy SortByOf(string key) => key switch
    {
        "name" => InstrumentSortBy.Name,
        "type" => InstrumentSortBy.Type,
        "latest" => InstrumentSortBy.LatestDate,
        _ => InstrumentSortBy.Symbol,
    };

    private bool HasFilters =>
        !string.IsNullOrWhiteSpace(_search) || _typeFilter.Count > 0 || _currencyFilter.Count > 0 || _statusFilter.Count > 0;

    /// <summary>The Status filter's two archival states — the Budgets and Accounts shape; none or both means all.</summary>
    internal static readonly IReadOnlyList<OdsBreakdownDef<ArchivalStatus>> StatusDefs =
    [
        new(ArchivalStatus.Active, "Active", "income", "task_alt"),
        new(ArchivalStatus.Archived, "Archived", "outline", "inventory_2"),
    ];

    internal static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs, s => s.ToString());

    /// <summary>
    /// The server's archival filter from the Status selection: exactly one state narrows to it; none or both
    /// ask for every instrument, archived included.
    /// </summary>
    internal static ArchivalStatus? StatusOf(IReadOnlyCollection<string> statuses) =>
        statuses.Count == 1 && Enum.TryParse<ArchivalStatus>(statuses.First(), out var status) ? status : null;

    /// <summary>
    /// The list request's archival half: <see cref="StatusOf"/>, and <c>includeArchived</c> always on, so that
    /// "none or both" really lists archived instruments too rather than falling back to the server's active-only
    /// default.
    /// </summary>
    internal static InstrumentsFilter ArchivalFilterFor(IReadOnlyCollection<string> statuses) =>
        new() { Status = StatusOf(statuses), IncludeArchived = true };

    /// <summary>The list minus a pinned record, which renders once, above it.</summary>
    private IReadOnlyList<ExistingInstrument> ListedInstruments =>
        _pinned is { } pin ? [.. _instruments.Where(i => i.InstrumentId != pin.InstrumentId)] : _instruments;

    private string SubLineText => _summary is { } s ? $"{s.TotalCount:N0} followed · {s.ArchivedCount:N0} archived" : string.Empty;

    private IReadOnlyList<OdsOption> CurrencyFilterOptions =>
    [
        .. _currencies.Select(c => c.CurrencyCode)
            .Concat(_currencyFilter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(OdsOption.From),
    ];

    /// <summary>"By type" from the summary's unfiltered counts: every type the kit names, zero counts included.</summary>
    private IReadOnlyList<OdsBreakdownRow> TypeRows => TypeRowsFor(_summary);

    internal static IReadOnlyList<OdsBreakdownRow> TypeRowsFor(InstrumentSummary? summary)
    {
        var counts = (summary?.ByType ?? []).GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        return
        [
            .. Enum.GetValues<InstrumentType>()
                .Where(t => t != InstrumentType.Other || counts.GetValueOrDefault(t) > 0)
                .Select(t =>
                {
                    var type = InstrumentVisuals.TypeOf(t);
                    return new OdsBreakdownRow
                    {
                        Key = t, Icon = type.Icon, IconColor = type.Color, Label = type.Label,
                        Count = counts.GetValueOrDefault(t),
                    };
                }),
        ];
    }

    /// <summary>The summary tile's foot: the attribution(s), as plain text, for a price reader (§3.1).</summary>
    internal static string? AttributionText(InstrumentSummary summary) =>
        summary.Attributions is { Count: > 0 } names
            ? "Prices from " + string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
            : null;

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await LoadPermissionsAsync();
        await PageState.RestoreOrSeedAsync<InstrumentsPageState>(PageStateKey, ApplyPageState, BuildPageState);
        StateHasChanged();
        _currencies = await ReferenceData.CurrenciesAsync();
        await Task.WhenAll(ReloadAsync(), LoadSummaryAsync(), LoadSourcesStatusAsync());
        _initialized = true;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized || _handledQuery == InstrumentQuery)
            return;

        _handledQuery = InstrumentQuery;
        if (InstrumentQuery is not null)
            await HandleDeepLinkAsync(InstrumentQuery);
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.InstrumentsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.InstrumentsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.InstrumentsDelete);
        _canReadPrices = user.HasPermission(PermissionClaims.InstrumentPricesRead);
        _canCreatePrices = user.HasPermission(PermissionClaims.InstrumentPricesCreate);
        _canDeletePrices = user.HasPermission(PermissionClaims.InstrumentPricesDelete);
        _canPurgeFetched = _canDeletePrices && user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canReadSources = user.HasPermission(PermissionClaims.SystemSettingsRead);
        _canEditSources = _canReadSources && user.HasPermission(PermissionClaims.SystemSettingsUpdate);
    }

    // ── Page state ──────────────────────────────────────────────────────────────────────────────────

    private void ApplyPageState(InstrumentsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _typeFilter = OdsTypeRegistries.InstrumentOptions.KnownValues(state.TypeFilter);
        _currencyFilter = [.. (state.CurrencyFilter ?? []).Where(c => !string.IsNullOrWhiteSpace(c))];
        _statusFilter = StatusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private InstrumentsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        TypeFilter = [.. _typeFilter],
        CurrencyFilter = [.. _currencyFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnCurrencyFilterChanged(IReadOnlyCollection<string> values) { _currencyFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _typeFilter = [];
        _currencyFilter = [];
        _statusFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    internal sealed class InstrumentsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TypeFilter { get; set; } = [];
        public List<string> CurrencyFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Loads ───────────────────────────────────────────────────────────────────────────────────────

    private InstrumentsFilter CurrentFilter => new()
    {
        Search = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(),
        Types = [.. _typeFilter.Select(t => Enum.TryParse<InstrumentType>(t, out var type) ? type : (InstrumentType?)null)
            .OfType<InstrumentType>()],
        CurrencyCodes = [.. _currencyFilter],
        Status = ArchivalFilterFor(_statusFilter).Status,
        IncludeArchived = ArchivalFilterFor(_statusFilter).IncludeArchived,
        SortBy = SortByOf(_sort.Key),
        Ascending = _sort.Dir == OdsSortDirection.Asc,
    };

    private async Task ReloadAsync()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var filter = CurrentFilter;
        var response = await _listLoader.RunAsync(ct => Instruments.ListAsync(1, OdsPageSizes.All, filter, ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "instruments");
        if (load.IsSuccess)
        {
            _instruments = [.. load.Items];
            _loadError = false;
            Announce(_instruments.Count == 0
                ? HasFilters ? "No instruments match these filters." : "No instruments yet."
                : $"Showing {_instruments.Count} instrument{(_instruments.Count == 1 ? "" : "s")}.");
            if (_pinned is { } pin && _instruments.FirstOrDefault(i => i.InstrumentId == pin.InstrumentId) is { } listed)
            {
                // The pinned record is in the list now — show it there, with the server's current state.
                _pinned = null;
                _revealId = listed.InstrumentId;
            }
        }
        else
        {
            _loadError = true;
            AnnounceAssertive("Couldn't load instruments.");
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    private async Task LoadSummaryAsync()
    {
        var result = await Instruments.GetSummaryAsync();
        if (result.IsSuccess)
            _summary = result.Value;
    }

    private async Task RefreshAsync()
    {
        await Task.WhenAll(ReloadAsync(), LoadSummaryAsync());
        StateHasChanged();
    }

    /// <summary>One sources read per page visit (§3.4), for <c>system-settings.read</c> holders only.</summary>
    private async Task LoadSourcesStatusAsync()
    {
        if (!_canReadSources)
            return;

        var result = await PriceSources.GetStatusAsync();
        _sourcesFailed = !result.IsSuccess;
        _sourcesStatus = result.IsSuccess ? result.Value : null;
        StateHasChanged();
    }

    // ── Record presentation ────────────────────────────────────────────────────────────────────────

    internal static string RecordIdFor(Guid id) => $"ins-{id:N}";

    private static string MenuIdFor(Guid id) => $"ins-{id:N}-menu";

    private int MinorUnitsOf(string currencyCode) =>
        OdsMoney.MinorUnitsOf(_currencies.FirstOrDefault(c => string.Equals(c.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase)));

    private string CloseText(ExistingInstrument i) =>
        InstrumentVisuals.FormatClose(i.LatestClose, i.CurrencyCode, MinorUnitsOf(i.CurrencyCode));

    private static IReadOnlyList<RenderFragment?> MetaFor(ExistingInstrument i) =>
    [
        Text(i.Name),
        !string.IsNullOrEmpty(i.MicCode) ? Text(i.MicCode) : !string.IsNullOrEmpty(i.Isin) ? Text($"ISIN {i.Isin}") : null,
    ];

    private static RenderFragment Text(string value) => b => b.AddContent(0, value);

    // The headline figure is the latest close, or "No price yet" — for a price reader only (§3.1).
    private RenderFragment? FigureFor(ExistingInstrument i) =>
        !_canReadPrices ? null : Text(i.LatestClose is null ? NoPriceYet : CloseText(i));

    private string? FigureCaptionFor(ExistingInstrument i) =>
        _canReadPrices && i.LatestClose is not null && i.LatestTradingDate is { } date ? $"Close {RunTime.Date(date)}" : null;

    /// <summary>
    /// Whether the instrument has prices. A caller without <c>instrument-prices.read</c> gets no count; Delete is then
    /// offered and the server's <c>409</c> is the guard.
    /// </summary>
    internal static bool HasPrices(ExistingInstrument i) => i.PriceCount is > 0;

    private string SourcesMeta(ExistingInstrument i) =>
        _sourcesStatus is { } status && _bindingCounts.TryGetValue(i.InstrumentId, out var count)
            ? $"{count} of {status.MaxBindingsPerInstrument}"
            : string.Empty;

    // ── The row menu (§3.3): only what the caller can do NOW; a missing action's reason is text in the record ──

    internal sealed record RowClaims(
        bool CanUpdate, bool CanDelete, bool CanReadPrices, bool CanCreatePrices, bool CanDeletePrices);

    private RowClaims Claims => new(_canUpdate, _canDelete, _canReadPrices, _canCreatePrices, _canDeletePrices);

    /// <summary>The menu's items for one instrument; the actions are the page's, the selection is the claims'.</summary>
    internal static IReadOnlyList<OdsMenuItem> BuildActions(
        ExistingInstrument i, RowClaims claims, Func<ExistingInstrument, string, EventCallback> act)
    {
        var archived = i.Archived is not null;
        var items = new List<OdsMenuItem>();
        if (claims.CanUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = act(i, "edit") });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "archive",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = act(i, "archive"),
            });
            if (!archived)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = i.ShowOnDashboard ? "visibility_off" : "space_dashboard",
                    Label = i.ShowOnDashboard ? "Hide from dashboard" : "Show on dashboard",
                    OnClick = act(i, "dashboard"),
                });
            }
        }

        if (claims.CanReadPrices)
            items.Add(new OdsMenuItem { Icon = "query_stats", Label = "View prices", OnClick = act(i, "prices") });
        if (claims.CanCreatePrices && !archived)
            items.Add(new OdsMenuItem { Icon = "add_chart", Label = "Add price", OnClick = act(i, "add-price") });

        items.Add(new OdsMenuItem { Divider = true });
        if (claims.CanDeletePrices && HasPrices(i))
            items.Add(new OdsMenuItem { Icon = "delete_sweep", Label = "Delete all prices…", Danger = true, OnClick = act(i, "purge") });
        if (claims.CanDelete && !HasPrices(i))
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = act(i, "delete") });

        // A menu of nothing but a divider has no trigger at all.
        return items.Any(item => !item.Divider) ? items : [];
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingInstrument i) =>
        BuildActions(i, Claims, (row, action) => EventCallback.Factory.Create(this, () => RunActionAsync(row, action)));

    private Task RunActionAsync(ExistingInstrument i, string action) => action switch
    {
        "edit" => Edit(i),
        "archive" => ToggleArchiveAsync(i),
        "dashboard" => ToggleDashboardAsync(i),
        "prices" => Go($"/instrument-prices?instrument={i.InstrumentId}"),
        "add-price" => Go($"/instrument-prices?instrument={i.InstrumentId}&add=1"),
        "purge" => Purge(i),
        "delete" => Delete(i),
        _ => Task.CompletedTask,
    };

    private Task Go(string uri)
    {
        Navigation.NavigateTo(uri);
        return Task.CompletedTask;
    }

    // ── New / Edit ────────────────────────────────────────────────────────────────────────────────

    private Guid _dialogKey;
    private bool _dialogOpen;
    private ExistingInstrument? _editing;
    private string? _dialogReturnFocusId;

    private void NewClicked()
    {
        if (!_canCreate)
            return;
        _editing = null;
        _dialogReturnFocusId = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private Task Edit(ExistingInstrument i)
    {
        if (!_canUpdate)
            return Task.CompletedTask;
        _editing = i;
        _dialogReturnFocusId = MenuIdFor(i.InstrumentId);
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
        return Task.CompletedTask;
    }

    private async Task OnCreatedAsync(ExistingInstrument created)
    {
        Snackbar.Add($"{created.Symbol} added", Severity.Success);
        Announce($"{created.Symbol} added");
        await RefreshAsync();
        Navigation.NavigateTo($"/instruments?instrument={created.InstrumentId}");
    }

    private async Task OnUpdatedAsync(ExistingInstrument updated)
    {
        Snackbar.Add($"{updated.Symbol} saved", Severity.Success);
        Announce($"{updated.Symbol} saved");
        if (_pinned?.InstrumentId == updated.InstrumentId)
            _pinned = updated;
        await RefreshAsync();
    }

    private async Task GoToAsync(Guid id)
    {
        // The same link twice changes no query parameter, so it is handled here rather than by the router.
        if (Guid.TryParse(InstrumentQuery, out var current) && current == id)
        {
            await HandleDeepLinkAsync(id.ToString());
            return;
        }

        Navigation.NavigateTo($"/instruments?instrument={id}");
    }

    // ── Archive and the dashboard flag: optimistic, rolled back on failure (§5) ─────────────────────

    private async Task ToggleArchiveAsync(ExistingInstrument i)
    {
        if (!_canUpdate || !_busy.Add(i.InstrumentId))
            return;

        var before = i.Archived;
        var archiving = before is null;
        i.Archived = archiving ? DateTime.UtcNow : null;
        StateHasChanged();

        var result = archiving ? await Instruments.ArchiveAsync(i.InstrumentId) : await Instruments.UnarchiveAsync(i.InstrumentId);
        _busy.Remove(i.InstrumentId);
        if (result.IsSuccess)
        {
            Announce($"{i.Symbol} {(archiving ? "archived" : "unarchived")}");
            await LoadSummaryAsync();
        }
        else
        {
            i.Archived = before;
            Fail(result.Status, archiving ? $"archive {i.Symbol}" : $"unarchive {i.Symbol}", result.Error);
        }

        StateHasChanged();
    }

    private async Task ToggleDashboardAsync(ExistingInstrument i)
    {
        if (!_canUpdate || !_busy.Add(i.InstrumentId))
            return;

        var show = !i.ShowOnDashboard;
        i.ShowOnDashboard = show;
        StateHasChanged();

        var result = await Instruments.UpdateAsync(i.InstrumentId, InstrumentDialog.ToRequest(i));
        _busy.Remove(i.InstrumentId);
        if (result.IsSuccess)
        {
            Announce(show ? $"{i.Symbol} is on the dashboard" : $"{i.Symbol} is hidden from the dashboard");
            await LoadSummaryAsync();
        }
        else
        {
            i.ShowOnDashboard = !show;
            Fail(result.Status, show ? $"show {i.Symbol} on the dashboard" : $"hide {i.Symbol} from the dashboard", result.Error);
        }

        StateHasChanged();
    }

    /// <summary>A failure, said once as a toast and once assertively; a 403 names the action (§7).</summary>
    private void Fail(HttpStatusCode status, string action, string? error)
    {
        var message = status == HttpStatusCode.Forbidden
            ? $"You don't have permission to {action}."
            : $"Couldn't {action}{(string.IsNullOrWhiteSpace(error) ? "." : $": {error}")}";
        Snackbar.Add(message, Severity.Error);
        AnnounceAssertive(message);
    }

    // ── Delete all prices ─────────────────────────────────────────────────────────────────────────

    private ExistingInstrument? _purging;
    private bool _purgeOpen;

    private Task Purge(ExistingInstrument i)
    {
        if (!_canDeletePrices)
            return Task.CompletedTask;
        _purging = i;
        _purgeOpen = true;
        return Task.CompletedTask;
    }

    private async Task OnPurgedAsync(int deleted)
    {
        var message = $"{deleted:N0} price{(deleted == 1 ? "" : "s")} deleted";
        Snackbar.Add(message, Severity.Success);
        Announce(message);
        await RefreshAsync();
        if (_pinned is { } pin && _purging?.InstrumentId == pin.InstrumentId)
            await RefreshPinnedAsync(pin.InstrumentId);
    }

    // ── Delete ────────────────────────────────────────────────────────────────────────────────────

    private ExistingInstrument? _deleting;
    private bool _deleteOpen;
    private string? _deleteReturnFocusIds;

    private Task Delete(ExistingInstrument i)
    {
        if (!_canDelete)
            return Task.CompletedTask;
        _deleting = i;
        _deleteReturnFocusIds = RemovalFocusChain(ListedInstruments, i.InstrumentId);
        _deleteOpen = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Where focus goes once a record is removed (§3.3 "Row removed"): the next record's header, else the previous
    /// one's, else the page <c>h1</c> — the modal's restore chain falls back to the heading on its own.
    /// </summary>
    internal static string RemovalFocusChain(IReadOnlyList<ExistingInstrument> rows, Guid removed)
    {
        var index = rows.ToList().FindIndex(r => r.InstrumentId == removed);
        if (index < 0)
            return string.Empty;
        var chain = new List<string>(2);
        if (index + 1 < rows.Count)
            chain.Add(RecordIdFor(rows[index + 1].InstrumentId));
        if (index > 0)
            chain.Add(RecordIdFor(rows[index - 1].InstrumentId));
        return string.Join(' ', chain);
    }

    private async Task<string?> ConfirmDeleteAsync()
    {
        if (_deleting is not { } doomed || !_canDelete)
            return null;

        var result = await Instruments.DeleteAsync(doomed.InstrumentId);
        if (!result.IsSuccess)
        {
            if (result.Status == HttpStatusCode.Forbidden)
                return $"You don't have permission to delete {doomed.Symbol}.";
            return string.IsNullOrWhiteSpace(result.Error) ? "The instrument couldn't be deleted." : result.Error;
        }

        Snackbar.Add($"{doomed.Symbol} deleted", Severity.Success);
        Announce($"{doomed.Symbol} deleted");
        if (_openId == doomed.InstrumentId)
        {
            _openId = null;
            _sourcesDirty = false;
        }
        if (_pinned?.InstrumentId == doomed.InstrumentId)
            _pinned = null;
        await RefreshAsync();
        return null;
    }

    // ── Deep links (§3.3) ─────────────────────────────────────────────────────────────────────────

    private async Task HandleDeepLinkAsync(string query)
    {
        if (!Guid.TryParse(query, out var id))
        {
            UnknownLink();
            return;
        }

        var result = await Instruments.GetAsync(id);
        if (!result.IsSuccess || result.Value is not { } instrument)
        {
            if (result.Status == HttpStatusCode.NotFound || result.IsSuccess)
                UnknownLink();
            else
                Fail(result.Status, "open that instrument", result.Error);
            return;
        }

        _pinned = _instruments.Any(i => i.InstrumentId == id) ? null : instrument;
        _revealId = id;
        _openId = id;
        _focusRecordId = id;
        StateHasChanged();
    }

    private void UnknownLink()
    {
        Snackbar.Add(UnknownInstrument, Severity.Warning);
        Announce(UnknownInstrument);
    }

    private async Task RefreshPinnedAsync(Guid id)
    {
        var result = await Instruments.GetAsync(id);
        if (result.IsSuccess && result.Value is { } fresh)
            _pinned = fresh;
        StateHasChanged();
    }

    private Task ClearPinAsync()
    {
        _pinned = null;
        _revealId = null;
        _handledQuery = null;
        _focusHeading = true;
        Announce("Showing all instruments");
        Navigation.NavigateTo("/instruments");
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusRecordId is { } id && !_isLoading)
        {
            _focusRecordId = null;
            // The record's header is the first button in its card. focus() scrolls it into view instantly, never
            // smoothly, so reduced motion holds; records carry scroll-margin so sticky chrome never covers them.
            await InvokeJsAsync("odsFocusChainOrHeading", new object[] { new[] { RecordIdFor(id) } });
        }
        else if (_focusHeading)
        {
            _focusHeading = false;
            await InvokeJsAsync("odsFocusChainOrHeading", new object[] { Array.Empty<string>() });
        }
    }

    private async Task InvokeJsAsync(string identifier, object[] args)
    {
        try { await Js.InvokeVoidAsync(identifier, args); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime (prerender, bUnit) */ }
    }

    // ── One record open at a time; unsaved price-source edits ask before they are lost (§3.4) ───────

    private void RequestToggle(Guid id, bool open)
    {
        Guid? next = open ? id : null;
        if (_sourcesDirty && _openId is not null && next != _openId)
        {
            _pendingOpen = next;
            _pendingNavigation = null;
            _discardOpen = true;
            return;
        }

        _openId = next;
    }

    private void OnSourcesDirtyChanged(bool dirty) => _sourcesDirty = dirty;

    private void OnBindingCountChanged(Guid id, int count)
    {
        if (_bindingCounts.TryGetValue(id, out var known) && known == count)
            return;
        _bindingCounts[id] = count;
        StateHasChanged();
    }

    private async Task OnSourcesSavedAsync(Guid id)
    {
        // HasEnabledSource and IdentityLocked follow the bindings, so the record re-reads.
        await ReloadAsync();
        if (_pinned?.InstrumentId == id)
            await RefreshPinnedAsync(id);
    }

    private void OnBeforeInternalNavigation(LocationChangingContext context)
    {
        if (!_sourcesDirty)
            return;

        context.PreventNavigation();
        _pendingNavigation = context.TargetLocation;
        _pendingOpen = _openId;
        _discardOpen = true;
        StateHasChanged();
    }

    private void OnDiscardOpenChanged(bool open)
    {
        _discardOpen = open;
        if (!open)
            _pendingNavigation = null;
    }

    private Task<bool> DiscardSourcesAsync()
    {
        _sourcesDirty = false;
        if (_pendingNavigation is { } target)
        {
            _pendingNavigation = null;
            Navigation.NavigateTo(target);
        }
        else
        {
            _openId = _pendingOpen;
        }

        return Task.FromResult(true);
    }

    private void Announce(string message) => _ = _polite?.Announce(message);

    private void AnnounceAssertive(string message) => _ = _assertive?.Announce(message);

    public void Dispose() => _listLoader.Dispose();
}
