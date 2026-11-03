using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherPointsPage
{
    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private ISystemSettingsApiClient SystemSettings { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private IPageStateService PageState { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IWeatherPointOptionsCache OptionsCache { get; set; } = default!;
    [Inject] private IWeatherClaimsRefresher ClaimsRefresher { get; set; } = default!;

    /// <summary><c>?open=&lt;weatherPointId&gt;</c>: expand, scroll to and focus that record (issue #388 §3).</summary>
    [SupplyParameterFromQuery(Name = "open")] public Guid? OpenId { get; set; }

    /// <summary>The interactivity check, as a seam for bUnit.</summary>
    internal static Func<bool> InteractiveCheck = OperatingSystem.IsBrowser;

    internal const string PageStateKey = "weather-points-page";

    // ── Data ─────────────────────────────────────────────────────────────────────────────────────
    private List<ExistingWeatherPoint> _points = [];
    private int? _total;
    private int _trackedCount;
    private int? _cap;
    private ExistingWeatherPoint? _pinned;
    private Guid? _expandedId;
    private Guid? _flashId;
    private Guid? _revealId;
    private readonly Dictionary<Guid, string> _moveNotes = [];
    private readonly HashSet<Guid> _refreshing = [];
    private readonly ListLoader _listLoader = new();

    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";

    // ── Claims (issue #388 §3 control-to-claim map) ──────────────────────────────────────────────────
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadLocations;
    private bool _canOpenSettings;

    // ── Persisted page state ─────────────────────────────────────────────────────────────────────────
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private int _batch = OdsPageSizes.Batch[0];

    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    private static readonly OdsSortField<ExistingWeatherPoint>[] AllSortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = p => p.Name.ToLowerInvariant() },
        new() { Key = "enabled", Label = "Created", Type = OdsSortType.Date, SortValue = p => p.EnabledAt },
        new() { Key = "linked", Label = "Linked locations", Type = OdsSortType.Number, SortValue = p => p.LinkedLocationCount ?? 0 },
    ];

    /// <summary>The <i>Linked locations</i> sort counts places, so it needs <c>locations.read</c> (the server 403s it).</summary>
    private OdsSortField<ExistingWeatherPoint>[] SortFields => SortFieldsFor(_canReadLocations);

    internal static OdsSortField<ExistingWeatherPoint>[] SortFieldsFor(bool canReadLocations) =>
        canReadLocations ? AllSortFields : [.. AllSortFields.Where(f => f.Key != "linked")];

    internal static string SortByFor(string key) => key switch
    {
        "enabled" => nameof(WeatherPointSortBy.EnabledAt),
        "linked" => nameof(WeatherPointSortBy.LinkedLocationCount),
        _ => nameof(WeatherPointSortBy.Name),
    };

    private int TrackedCount => _trackedCount;

    private string SubLineText => _total is { } total && !_loadError
        ? $"{total} {(total == 1 ? "point" : "points")}{(_cap is { } cap ? $" · {_trackedCount} of {cap} on a schedule" : "")}"
        : "Where weather is fetched; locations link to a point";

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await ReadClaimsAsync();
        await PageState.RestoreOrSeedAsync<WeatherPointsPageState>(PageStateKey, ApplyPageState, BuildPageState);
        await Task.WhenAll(LoadAsync(), LoadCapAsync());
        await OpenDeepLinkAsync();
    }

    private async Task ReadClaimsAsync()
    {
        var user = await Auth.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.WeatherPointsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.WeatherPointsDelete);
        _canReadLocations = user.HasPermission(PermissionClaims.LocationsRead);
        _canOpenSettings = user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);
        if (!_canReadLocations && _sort.Key == "linked")
            _sort = DefaultSort;
    }

    private bool _canReadSettings;

    private void ApplyPageState(WeatherPointsPageState state)
    {
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private WeatherPointsPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _searchString,
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadAsync(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearSearch()
    {
        _searchString = string.Empty;
        PersistPageState();
        await LoadAsync();
    }

    internal sealed class WeatherPointsPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }
        else
        {
            _announce = "Loading weather points…";
        }

        var search = string.IsNullOrWhiteSpace(_searchString) ? null : _searchString.Trim();
        var response = await _listLoader.RunAsync(ct => Weather.PointsAsync(
            1, PagedQuery.SizeAll, search, SortByFor(_sort.Key), _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc", ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;
        await OnStatusAsync(result.Status);
        _loadError = !result.IsSuccess;
        _points = result.PagedOrToast(Snackbar, "weather points").Items.ToList();
        _isLoading = false;
        _refetching = false;
        if (!_loadError)
        {
            // What the cap counts is every point, so a search reads the whole set once more.
            var all = search is null ? _points : (await Weather.PointsAsync(1, PagedQuery.SizeAll)).Value?.Items.ToList() ?? _points;
            _trackedCount = all.Count(IsScheduled);
            _total = all.Count;
            _announce = search is not null
                ? _points.Count == 0 ? "No weather points match your search." : $"{_points.Count} {(_points.Count == 1 ? "weather point" : "weather points")} match."
                : _points.Count == 0 ? "No weather points yet." : $"{_points.Count} {(_points.Count == 1 ? "weather point" : "weather points")}.";
        }
        else
        {
            _announce = "Weather points couldn't be loaded.";
        }

        StateHasChanged();
    }

    private static bool IsScheduled(ExistingWeatherPoint p) => p is { ForecastEnabled: true, IsTracked: true, PurgeInProgress: false };

    private async Task LoadCapAsync()
    {
        if (!_canReadSettings)
            return;
        if ((await SystemSettings.GetAsync()).Value is { } settings)
            _cap = settings.WeatherMaxTrackedPoints;
    }

    /// <summary>
    /// The deep link: the loaded batch first; otherwise fetch by id and pin it above the list, overriding the saved
    /// search for this visit without overwriting it. An unknown id focuses the h1 and says so.
    /// </summary>
    private async Task OpenDeepLinkAsync()
    {
        if (OpenId is not { } id)
            return;

        if (_points.Any(p => p.WeatherPointId == id))
        {
            _expandedId = id;
            _revealId = id;
            FocusAfterRender(CardId(id));
            return;
        }

        var result = await Weather.PointAsync(id);
        await OnStatusAsync(result.Status);
        if (result.Value is { } point)
        {
            _pinned = point;
            _expandedId = id;
            FocusAfterRender(CardId(id));
            return;
        }

        Announce("That weather point no longer exists.");
        FocusAfterRender(null);
    }

    private void ShowAll()
    {
        _pinned = null;
        FocusAfterRender(null);
    }

    // ── Expand ─────────────────────────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private void ToggleExpand(Guid id)
    {
        if (_expandedId is { } open && open != id)
            _moveNotes.Remove(open);
        if (_expandedId == id)
            _moveNotes.Remove(id);
        _expandedId = _expandedId == id ? null : id;
    }

    // ── Ids and focus ──────────────────────────────────────────────────────────────────────────────
    internal static string CardId(Guid id) => $"wp-{id}";

    internal static string MenuId(Guid id) => $"wp-menu-{id}";

    private string? _focusTarget;
    private bool _focusPending;

    /// <summary>Focus <paramref name="id"/>'s first control after the next render, else the page h1 — never body.</summary>
    private void FocusAfterRender(string? id)
    {
        _focusTarget = id;
        _focusPending = true;
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPending)
            return;
        _focusPending = false;
        try
        {
            await Js.InvokeVoidAsync("odsFocusChainOrHeading", (object)(_focusTarget is { } id ? new[] { id } : Array.Empty<string>()));
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>After a delete: the next row's menu, else the previous row's, else the page h1 (§6.4).</summary>
    internal static Guid? NeighbourOf(IReadOnlyList<ExistingWeatherPoint> rows, Guid id)
    {
        var index = rows.ToList().FindIndex(r => r.WeatherPointId == id);
        if (index < 0)
            return null;
        return index + 1 < rows.Count ? rows[index + 1].WeatherPointId
            : index > 0 ? rows[index - 1].WeatherPointId
            : null;
    }

    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
    }

    /// <summary>A 403 from a point endpoint asks the bounded claims refresher, then redraws from the new claims.</summary>
    private async Task OnStatusAsync(HttpStatusCode status)
    {
        if (await ClaimsRefresher.OnResponseAsync(status))
        {
            await ReadClaimsAsync();
            StateHasChanged();
        }
    }

    // ── Card header ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The position (mono), the time zone, the linked count — omitted, never <i>0</i>, when it is null — and
    /// <i>Household default</i>.
    /// </summary>
    internal static IReadOnlyList<RenderFragment?> MetaFor(ExistingWeatherPoint p) =>
    [
        OdsRecordMeta.Mono(WeatherVisuals.Pair3(p.Site.Latitude, p.Site.Longitude)),
        OdsRecordMeta.Text(p.Site.TimeZoneId),
        p.LinkedLocationCount is { } count
            ? OdsRecordMeta.Text(count == 0 ? "No linked locations" : $"Used by {count} {(count == 1 ? "location" : "locations")}")
            : null,
        p.IsHouseholdDefault ? OdsRecordMeta.Text("Household default") : null,
    ];

    // ── Row menu ─────────────────────────────────────────────────────────────────────────────────────
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingWeatherPoint p) => BuildRowActions(
        p, _canUpdate, _canDelete,
        settings: EventCallback.Factory.Create(this, () => OpenSettings(p)),
        refresh: EventCallback.Factory.Create(this, () => RefreshAsync(p)),
        purgeHistory: EventCallback.Factory.Create(this, () => OpenPurge(p, WeatherPurgeMode.HistoryOnly)),
        delete: EventCallback.Factory.Create(this, () => OpenPurge(p, WeatherPurgeMode.Full)));

    /// <summary>
    /// The row menu, by claim (issue #388 §3; design system · wxPointMenuItems): Weather settings… · Refresh forecast
    /// (forecast on) · divider · Delete weather history… (history off) · Delete weather point… (both off, not the
    /// household default). An interrupted purge offers only Continue deleting…. Absent, never dimmed; every item names
    /// the point.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildRowActions(
        ExistingWeatherPoint p, bool canUpdate, bool canDelete,
        EventCallback settings, EventCallback refresh, EventCallback purgeHistory, EventCallback delete)
    {
        var items = new List<OdsMenuItem>();
        if (p.PurgeInProgress)
        {
            if (canDelete)
                items.Add(new OdsMenuItem { Icon = "delete_sweep", Label = "Continue deleting…", AriaLabel = $"Continue deleting {p.Name}", Danger = true, OnClick = delete });
            return items;
        }

        if (canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "tune", Label = "Weather settings…", AriaLabel = $"Weather settings for {p.Name}", OnClick = settings });
            if (p.ForecastEnabled)
                items.Add(new OdsMenuItem { Icon = "refresh", Label = "Refresh forecast", AriaLabel = $"Refresh forecast for {p.Name}", OnClick = refresh });
        }

        var canDeletePoint = !p.ForecastEnabled && !p.HistoryEnabled && !p.IsHouseholdDefault;
        if (canDelete && (!p.HistoryEnabled || canDeletePoint))
        {
            if (items.Count > 0)
                items.Add(new OdsMenuItem { Divider = true });
            if (!p.HistoryEnabled)
                items.Add(new OdsMenuItem { Icon = "delete_sweep", Label = "Delete weather history…", AriaLabel = $"Delete weather history of {p.Name}", Danger = true, OnClick = purgeHistory });
            if (canDeletePoint)
                items.Add(new OdsMenuItem { Icon = "delete_forever", Label = "Delete weather point…", AriaLabel = $"Delete weather point {p.Name}", Danger = true, OnClick = delete });
        }

        return items;
    }

    // ── Dialogs ──────────────────────────────────────────────────────────────────────────────────────
    private enum Dialog { None, Create, Settings, Purge }

    private Dialog _dialog;
    private Guid _dialogKey;
    private bool _dialogOpen;
    private ExistingWeatherPoint? _target;
    private WeatherPurgeMode _purgeMode;
    private bool _purgeFromNotice;
    private Guid? _purgeNeighbour;

    private void Show(Dialog dialog, ExistingWeatherPoint? target)
    {
        _dialog = dialog;
        _target = target;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
        StateHasChanged();
    }

    private void NewClicked()
    {
        if (_canUpdate)
            Show(Dialog.Create, null);
    }

    private void OpenSettings(ExistingWeatherPoint p) => Show(Dialog.Settings, p);

    private void OpenPurge(ExistingWeatherPoint p, WeatherPurgeMode mode, bool fromNotice = false)
    {
        if (!_canDelete)
            return;
        _purgeMode = mode;
        _purgeFromNotice = fromNotice || _dialog == Dialog.Settings;
        _purgeNeighbour = NeighbourOf(VisibleRows, p.WeatherPointId);
        Show(Dialog.Purge, Current(p.WeatherPointId) ?? p);
    }

    /// <summary>Every close returns to the row's ⋯ trigger — also after Continue deleting from a notice or the settings alert.</summary>
    private string? PurgeReturnFocus => _target is { } t ? MenuId(t.WeatherPointId) : null;

    private IReadOnlyList<ExistingWeatherPoint> VisibleRows => _pinned is { } pinned ? [pinned] : _points;

    private ExistingWeatherPoint? Current(Guid id) =>
        _points.FirstOrDefault(p => p.WeatherPointId == id) ?? (_pinned?.WeatherPointId == id ? _pinned : null);

    private void Replace(ExistingWeatherPoint point)
    {
        var index = _points.FindIndex(p => p.WeatherPointId == point.WeatherPointId);
        if (index >= 0)
            _points[index] = point;
        if (_pinned?.WeatherPointId == point.WeatherPointId)
            _pinned = point;
    }

    private async Task OnCreatedAsync((ExistingWeatherPoint Point, bool FromLocation) created)
    {
        OptionsCache.Invalidate();
        await LoadAsync();
        _pinned = null;
        _expandedId = created.Point.WeatherPointId;
        _revealId = created.Point.WeatherPointId;
        _flashId = created.Point.WeatherPointId;
        // Announced only — there is no success toast (issue #388 §6.5).
        Announce($"Weather point {created.Point.Name} created.{(created.FromLocation ? " Link locations to it from the Locations page." : "")}");
        FocusAfterRender(CardId(created.Point.WeatherPointId));
        await OdsRecordReveal.EndFlashAsync(() => _flashId == created.Point.WeatherPointId, () => { _flashId = null; StateHasChanged(); });
    }

    private async Task OnUpdatedAsync((WeatherPointUpdateResult Result, bool Moved, bool WasOn) saved)
    {
        OptionsCache.Invalidate();
        var point = saved.Result.Point;
        Replace(point);
        await LoadAsync();
        var follow = saved.Result.Warnings.FirstOrDefault(w => w.Code == WeatherAdvisoryCode.LinkedLocationsFollowMove);
        var isOn = point.ForecastEnabled || point.HistoryEnabled;
        string message;
        if (saved.Moved)
        {
            var followText = follow is null ? null : WeatherVisuals.AdvisoryText(follow, point.Name);
            message = $"{point.Name} moved.{(followText is null ? "" : $" {followText}")}";
            if (followText is not null)
                _moveNotes[point.WeatherPointId] = message;
        }
        else
        {
            message = saved.WasOn && !isOn ? $"Weather is off for {point.Name}. Everything stored is kept."
                : !saved.WasOn && isOn ? $"Weather is back on for {point.Name}."
                : $"Weather settings for {point.Name} saved.";
        }

        Announce(message);
    }

    private async Task OnPurgedAsync()
    {
        OptionsCache.Invalidate();
        var target = _target!;
        if (_purgeMode == WeatherPurgeMode.Full || target.PurgeInProgress)
        {
            var linked = target.LinkedLocationCount;
            if (_pinned?.WeatherPointId == target.WeatherPointId)
                _pinned = null;
            await LoadAsync();
            Announce($"Weather point {target.Name} deleted.{(linked is > 0 ? linked == 1 ? " 1 location was unlinked." : $" {linked} locations were unlinked." : "")}");
            FocusAfterRender(_purgeNeighbour is { } next ? MenuId(next) : null);
            return;
        }

        await LoadAsync();
        Announce($"Weather history for {target.Name} deleted. The point is kept.");
        FocusAfterRender(MenuId(target.WeatherPointId));
    }

    private async Task OnInterruptedAsync(ExistingWeatherPoint? reread)
    {
        if (reread is not null)
        {
            Replace(reread);
            _target = reread;
        }

        await LoadAsync();
    }

    // ── Refresh forecast ─────────────────────────────────────────────────────────────────────────────
    private async Task RefreshAsync(ExistingWeatherPoint p)
    {
        if (!_refreshing.Add(p.WeatherPointId))
            return;
        try
        {
            var result = await Weather.RefreshPointAsync(p.WeatherPointId);
            await OnStatusAsync(result.Status);
            if (result.IsSuccess && result.Value is { } refreshed)
            {
                Announce(refreshed.Fetched
                    ? $"Forecast for {p.Name} refreshed — issued {WeatherVisuals.Clock(refreshed.IssuedAtUtc)}."
                    : WeatherVisuals.IsPause(refreshed.ForecastFailureKind)
                        ? $"MET Norway has paused fetching for {p.Name} for up to 6 hours. A refresh can’t shorten the pause."
                        : refreshed.ForecastFailureKind is { } kind
                            ? $"The forecast for {p.Name} couldn’t be refreshed: {WeatherVisuals.FailureOf(kind)!.Label.ToLowerInvariant()}."
                            : $"MET Norway has nothing newer for {p.Name} yet. It will be checked again on schedule.");
                await RefreshRowAsync(p.WeatherPointId);
                return;
            }

            if (result.Status is HttpStatusCode.GatewayTimeout or 0)
            {
                Announce($"The refresh for {p.Name} is taking longer than usual and may still finish. Its status updates here.");
                await RefreshRowAsync(p.WeatherPointId);
                return;
            }

            var message = result.Status switch
            {
                HttpStatusCode.ServiceUnavailable => "Weather isn’t fetched yet: add a contact address in System settings → Weather.",
                HttpStatusCode.TooManyRequests => "You’ve refreshed weather several times in the last few minutes. Wait a moment, then try again.",
                _ => result.Problem?.Message ?? $"The forecast for {p.Name} couldn’t be refreshed.",
            };
            Snackbar.Add(message, Severity.Warning);
            Announce(message);
        }
        finally
        {
            _refreshing.Remove(p.WeatherPointId);
        }
    }

    private async Task RefreshRowAsync(Guid id)
    {
        var result = await Weather.PointAsync(id);
        await OnStatusAsync(result.Status);
        if (result.Value is { } point)
        {
            Replace(point);
            StateHasChanged();
        }
    }

    public void Dispose() => _listLoader.Dispose();
}
