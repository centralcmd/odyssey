using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Weather;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

public partial class LocationsPage
{
    [Inject] private ILocationsApiClient Locations { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private IPageStateService PageState { get; set; } = default!;
    [Inject] private IWeatherApiClient Weather { get; set; } = default!;
    [Inject] private ISystemSettingsApiClient SystemSettings { get; set; } = default!;

    // ── Data ────────────────────────────────────────────────────────────────
    private List<ExistingLocation> _locations = [];

    /// <summary>Whole-file counts for the sub-line and the first-run decision; null until known.</summary>
    private int? _activeCount;
    private int? _archivedCount;

    /// <summary>The list is empty, the filter hides archived rows, and an archived row matches the search.</summary>
    private bool _archivedMatch;

    private Guid? _flashId;
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "locations-page";
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = LocationVisuals.DefaultStatusFilter;

    // ── Sort — LocationSortBy: Name (default, ascending), CreatedAt, UpdatedAt; the server applies it. ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    private static readonly OdsSortField<ExistingLocation>[] SortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = l => l.Name.ToLowerInvariant() },
        new() { Key = "created", Label = "Added", Type = OdsSortType.Date, SortValue = l => l.CreatedAt },
        new() { Key = "updated", Label = "Last edited", Type = OdsSortType.Date, SortValue = l => l.UpdatedAt },
    ];

    /// <summary>The sort keys the toolbar uses, mapped onto <see cref="LocationSortBy"/>.</summary>
    internal static string SortByFor(string key) => key switch
    {
        "created" => nameof(LocationSortBy.CreatedAt),
        "updated" => nameof(LocationSortBy.UpdatedAt),
        _ => nameof(LocationSortBy.Name),
    };

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canReadWeather;
    private bool _canUpdateWeather;
    private bool _canPurgeWeather;

    // ── Dialogs ──────────────────────────────────────────────────────────────────
    private Guid _createKey = Guid.Empty;
    private bool _createOpen;
    private ExistingLocation? _editLocation;
    private Guid _editKey;
    private bool _editOpen;

    // ── Computed ────────────────────────────────────────────────────────────────
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _statusFilter.Count > 0;

    private int? FileTotal => _activeCount + _archivedCount;

    /// <summary>
    /// "No matches, clear a filter" — unless the file itself is empty (the first-run state, which a Guest
    /// sees without a button) or the archived-match line has something more useful to say.
    /// </summary>
    private bool ShowFilteredEmpty => _hasFilters && FileTotal is not 0 && !_archivedMatch;

    private string SubLineText => _activeCount is { } live
        ? $"{live} {(live == 1 ? "place" : "places")}{(_archivedCount is > 0 and var archived ? $" · {archived} archived" : "")}"
        : "";

    private string EmptyTitle => "No locations yet";

    private string EmptyDescription => EmptyDescriptionFor(_canCreate);

    /// <summary>A reader who cannot create is told someone else can start the list — it offers no button they cannot use.</summary>
    internal static string EmptyDescriptionFor(bool canCreate) => canCreate
        ? "Keep one shared list of the places that matter to the household — home, the cabin, school. Search an address or place name and the coordinates come with it."
        : "The household hasn’t saved any places yet. Someone with permission to add locations can start the list.";

    /// <summary>The credits line's sources — recomputed when the rows change, not on every render.</summary>
    private IReadOnlyList<LocationAttribution> Credits { get; set; } = [];

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.LocationsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.LocationsUpdate);
        // Weather on this page is Admin-only (issue #348): without weather.locations.read it shows nothing.
        _canReadWeather = user.HasPermission(PermissionClaims.WeatherLocationsRead);
        _canUpdateWeather = _canReadWeather && user.HasPermission(PermissionClaims.WeatherLocationsUpdate);
        _canPurgeWeather = _canReadWeather && user.HasPermission(PermissionClaims.WeatherHistoryDelete);
        _canReadSettings = user.HasPermission(PermissionClaims.SystemSettingsRead);

        await RestorePageStateAsync();
        await Task.WhenAll(LoadLocations(), LoadCounts(), LoadWeatherAsync());
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<LocationsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(LocationsPageState state)
    {
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _statusFilter = LocationVisuals.StatusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private LocationsPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _searchString,
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadLocations(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadLocations(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _statusFilter = [];
        PersistPageState();
        await LoadLocations();
    }

    private sealed class LocationsPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [.. LocationVisuals.DefaultStatusFilter];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Loading ──────────────────────────────────────────────────────────────────
    private async Task LoadLocations()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // ItemsOrToast falls back to [], which is indistinguishable from a genuinely empty file and
        // would render the first-run empty state after a 500 — so the failure is tracked explicitly.
        var response = await _listLoader.RunAsync(ct => Locations.ListAllAsync(
            _searchString,
            _statusFilter,
            SortByFor(_sort.Key),
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;
        _locations = result.ItemsOrToast(Snackbar, "locations");
        Credits = LocationVisuals.Credits(_locations);
        _loadError = !result.IsSuccess;
        _archivedMatch = !_loadError && _locations.Count == 0 && await ArchivedRowMatchesAsync();

        _announce = _loadError ? "Couldn't load locations."
            : _locations.Count == 0 ? "No locations match your filters."
            : $"Showing {_locations.Count} location{(_locations.Count == 1 ? "" : "s")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    /// <summary>
    /// Whether the empty list is hiding an archived match: the filter excludes Archived, there is a search,
    /// and the same search over archived rows finds one.
    /// </summary>
    private async Task<bool> ArchivedRowMatchesAsync()
    {
        var hidesArchived = _statusFilter.Count == 1 && _statusFilter.Contains(nameof(ArchivalStatus.Active));
        if (!hidesArchived || string.IsNullOrWhiteSpace(_searchString) || _archivedCount is 0)
            return false;
        var archived = await Locations.ListAsync(1, 1, _searchString, [nameof(ArchivalStatus.Archived)]);
        return archived.IsSuccess && archived.Value is { TotalCount: > 0 };
    }

    /// <summary>The sub-line's whole-file tally, independent of the filters.</summary>
    private async Task LoadCounts()
    {
        var active = Locations.ListAsync(1, 1, statuses: [nameof(ArchivalStatus.Active)]);
        var archived = Locations.ListAsync(1, 1, statuses: [nameof(ArchivalStatus.Archived)]);
        await Task.WhenAll(active, archived);
        _activeCount = (await active).IsSuccess ? (await active).Value?.TotalCount : null;
        _archivedCount = (await archived).IsSuccess ? (await archived).Value?.TotalCount : null;
        StateHasChanged();
    }

    // ── Expand ──────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private void ToggleExpand(Guid id) => _expandedId = _expandedId == id ? null : id;

    // ── Row menu ──────────────────────────────────────────────────────────────────
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingLocation l) => BuildRowActions(
        l,
        _canUpdate,
        edit: EventCallback.Factory.Create(this, () => EditClicked(l)),
        copyCoordinates: EventCallback.Factory.Create(this, () => CopyAsync(LocationVisuals.FormatPair(l.Latitude, l.Longitude), "Coordinates copied.")),
        copyId: EventCallback.Factory.Create(this, () => CopyAsync(l.LocationId.ToString(), "ID copied.")),
        toggleArchive: EventCallback.Factory.Create(this, () => SetArchived(l, l.Archived is null)),
        weather: _canReadWeather ? WeatherActions(l) : null);

    /// <summary>
    /// The row menu: Edit and Archive / Restore need <c>locations.update</c> and are ABSENT without it;
    /// the two copies are read-only and always there. There is no Delete — a location is archived.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildRowActions(
        ExistingLocation l, bool canUpdate, EventCallback edit, EventCallback copyCoordinates, EventCallback copyId,
        EventCallback toggleArchive, IReadOnlyList<OdsMenuItem>? weather = null)
    {
        var items = new List<OdsMenuItem>();
        if (canUpdate)
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit location", OnClick = edit });

        items.Add(new OdsMenuItem { Icon = "my_location", Label = "Copy coordinates", TrailingIcon = "content_copy", OnClick = copyCoordinates });
        items.Add(new OdsMenuItem { Icon = "fingerprint", Label = "Copy ID", TrailingIcon = "content_copy", OnClick = copyId });

        // The weather items sit after Copy ID and before Archive (issue #348).
        if (weather is { Count: > 0 })
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.AddRange(weather);
        }

        if (canUpdate)
        {
            var archived = l.Archived is not null;
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Restore" : "Archive",
                OnClick = toggleArchive,
            });
        }

        return items;
    }

    private async Task CopyAsync(string text, string message)
    {
        if (await Clipboard.CopyAsync(text, message))
            Announce(message);
    }

    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
    }

    // ── Create / edit ──────────────────────────────────────────────────────────────
    private void AddClicked()
    {
        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private void EditClicked(ExistingLocation l)
    {
        _editLocation = l;
        _editKey = Guid.NewGuid();
        _editOpen = true;
    }

    private async Task OnLocationCreated(ExistingLocation created)
    {
        Snackbar.Add($"{created.Name} created.", Severity.Success);
        await Task.WhenAll(LoadLocations(), LoadCounts());
        _expandedId = created.LocationId;
        Announce($"{created.Name} created.");
        await Flash(created.LocationId);
    }

    private async Task OnLocationSaved(ExistingLocation saved)
    {
        Snackbar.Add($"{saved.Name} saved.", Severity.Success);
        // The weather chip and notice derive "moved" from the live location, so they follow the edit as-is;
        // the rows are re-read for the server's own view.
        _ = LoadWeatherAsync();
        var index = _locations.FindIndex(l => l.LocationId == saved.LocationId);
        if (index >= 0)
            _locations[index] = saved;
        Credits = LocationVisuals.Credits(_locations);
        Announce($"{saved.Name} saved.");
        await LoadCounts();
    }

    /// <summary>Builds the full-replacement body that changes only <see cref="UpdateLocation.Archived"/>.</summary>
    internal static UpdateLocation WithArchived(ExistingLocation l, bool archived) => new()
    {
        Name = l.Name,
        Description = l.Description,
        AddressText = l.AddressText,
        Latitude = l.Latitude,
        Longitude = l.Longitude,
        AltitudeMeters = l.AltitudeMeters,
        TimeZoneId = l.TimeZoneId,
        Source = l.Source,
        SourceReference = l.SourceReference,
        Archived = archived,
    };

    private async Task SetArchived(ExistingLocation l, bool archived)
    {
        if (!_canUpdate)
            return;

        var result = await Locations.UpdateAsync(l.LocationId, WithArchived(l, archived));
        var message = archived
            ? $"{l.Name} archived. Add Archived to the status filter to see it."
            : $"{l.Name} restored.";
        var outcome = result.IsSuccess ? ApiResult.Success(result.Status) : ApiResult.Failure(result.Status, result.Problem!);
        if (!outcome.Toast(Snackbar, archived ? "Unable to archive location" : "Unable to restore location", message))
            return;

        Announce(message);
        await Task.WhenAll(LoadLocations(), LoadCounts());
    }

    private async Task Flash(Guid id)
    {
        _flashId = id;
        StateHasChanged();
        await OdsRecordReveal.EndFlashAsync(() => _flashId == id, EndFlash);
    }

    private void EndFlash()
    {
        _flashId = null;
        StateHasChanged();
    }

    // ── Card header ────────────────────────────────────────────────────────────────
    private static IReadOnlyList<RenderFragment?> MetaFor(ExistingLocation l) =>
    [
        string.IsNullOrWhiteSpace(l.AddressText) ? NoAddress : OdsRecordMeta.Text(l.AddressText),
        OdsRecordMeta.Mono(LocationVisuals.FormatPair(l.Latitude, l.Longitude)),
        OdsRecordMeta.Text(l.TimeZoneId),
    ];

    private static readonly RenderFragment NoAddress = builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "loc-meta-muted");
        builder.AddContent(2, "No address");
        builder.CloseElement();
    };

    private static RenderFragment SourceFoot(ExistingLocation l) => l.Attribution is { } attribution
        ? builder =>
        {
            builder.OpenComponent<LocationAttributionLink>(0);
            builder.AddComponentParameter(1, nameof(LocationAttributionLink.Attribution), attribution);
            builder.CloseComponent();
        }
        : OdsInfoTile.Caption("no third-party data")!;

    // ── Weather (issue #348, Admin only) ────────────────────────────────────────────────────────────

    private bool _canReadSettings;
    private Dictionary<Guid, ExistingWeatherLocation> _weatherRows = [];
    private int? _weatherCap;
    private Guid _wxKey;
    private ExistingLocation? _wxSettingsLocation;
    private bool _wxSettingsOpen;
    private ExistingLocation? _wxApplyLocation;
    private bool _wxApplyOpen;
    private ExistingLocation? _wxPurgeLocation;
    private bool _wxPurgeOpen;

    private ExistingWeatherLocation? WeatherRow(ExistingLocation l) => _weatherRows.GetValueOrDefault(l.LocationId);

    /// <summary>Places on a schedule — what the cap counts.</summary>
    private int TrackedCount => _weatherRows.Values.Count(r => r is { Enabled: true, IsTracked: true, LocationArchived: false, PurgeInProgress: false });

    private async Task LoadWeatherAsync()
    {
        if (!_canReadWeather)
            return;

        var rows = Weather.ListLocationsAsync();
        if (_canReadSettings && (await SystemSettings.GetAsync()).Value is { } settings)
            _weatherCap = settings.WeatherMaxTrackedLocations;
        var result = await rows;
        if (result.IsSuccess && result.Value is { } list)
            _weatherRows = list.ToDictionary(r => r.LocationId);
        StateHasChanged();
    }

    /// <summary>The Admin's weather items. Unavailable actions are absent, not dimmed.</summary>
    private IReadOnlyList<OdsMenuItem> WeatherActions(ExistingLocation l) => BuildWeatherActions(
        l, WeatherRow(l), _canUpdateWeather, _canPurgeWeather,
        enable: EventCallback.Factory.Create(this, () => OpenWeatherSettings(l)),
        apply: EventCallback.Factory.Create(this, () => OpenWeatherApply(l)),
        refresh: EventCallback.Factory.Create(this, () => RefreshWeatherAsync(l)),
        purge: EventCallback.Factory.Create(this, () => OpenWeatherPurge(l)));

    internal static IReadOnlyList<OdsMenuItem> BuildWeatherActions(
        ExistingLocation l, ExistingWeatherLocation? row, bool canUpdate, bool canPurge,
        EventCallback enable, EventCallback apply, EventCallback refresh, EventCallback purge)
    {
        var items = new List<OdsMenuItem>();
        if (row is null)
        {
            if (canUpdate && l.Archived is null)
                items.Add(new OdsMenuItem { Icon = "wb_sunny", Label = "Turn on weather…", OnClick = enable });
            return items;
        }

        if (row.PurgeInProgress)
        {
            if (canPurge)
                items.Add(new OdsMenuItem { Icon = "delete_sweep", Label = "Continue deleting weather history…", Danger = true, OnClick = purge });
            return items;
        }

        if (canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "tune", Label = "Weather settings…", OnClick = enable });
            if (row.Enabled && l.Archived is null)
            {
                items.Add(new OdsMenuItem { Icon = "refresh", Label = "Refresh forecast", OnClick = refresh });
                if (WeatherVisuals.Moved(row, l))
                    items.Add(new OdsMenuItem { Icon = "edit_location_alt", Label = "Apply new position…", OnClick = apply });
            }
        }

        if (canPurge && !row.Enabled && !row.IsHouseholdDefault)
        {
            if (items.Count > 0)
                items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete_forever", Label = "Delete weather history…", Danger = true, OnClick = purge });
        }

        return items;
    }

    private void OpenWeatherSettings(ExistingLocation l)
    {
        _wxKey = Guid.NewGuid();
        _wxSettingsLocation = l;
        _wxSettingsOpen = true;
    }

    private void OpenWeatherApply(ExistingLocation l)
    {
        _wxKey = Guid.NewGuid();
        _wxApplyLocation = l;
        _wxApplyOpen = true;
    }

    private void OpenWeatherPurge(ExistingLocation l)
    {
        _wxKey = Guid.NewGuid();
        _wxPurgeLocation = l;
        _wxPurgeOpen = true;
    }

    private void StoreWeatherRow(ExistingWeatherLocation row)
    {
        _weatherRows[row.LocationId] = row;
        StateHasChanged();
    }

    private async Task OnWeatherSaved(ExistingWeatherLocation row)
    {
        var wasOn = _weatherRows.TryGetValue(row.LocationId, out var before) && before.Enabled;
        Announce(!wasOn && row.Enabled ? $"Weather is on for {row.Name}." : !row.Enabled && wasOn ? $"Weather is off for {row.Name}. Its history is kept." : $"Weather settings for {row.Name} saved.");
        // Re-read every row: the cap's suspension flags are computed across them.
        await LoadWeatherAsync();
    }

    private void OnWeatherApplied(ExistingWeatherLocation row)
    {
        StoreWeatherRow(row);
        Announce($"Weather for {row.Name} now uses the new position.");
    }

    private void OnWeatherPurged()
    {
        if (_wxPurgeLocation is { } l)
        {
            _weatherRows.Remove(l.LocationId);
            Announce($"Weather history for {l.Name} deleted.");
        }
        StateHasChanged();
    }

    private void OnWeatherPurgeInterrupted(ExistingWeatherLocation? row)
    {
        if (row is not null)
            StoreWeatherRow(row);
    }

    private async Task RefreshWeatherAsync(ExistingLocation l)
    {
        var result = await Weather.RefreshAsync(l.LocationId);
        if (result.IsSuccess && result.Value is { } refreshed)
        {
            Announce(refreshed.Fetched
                ? $"Forecast for {l.Name} refreshed — issued {WeatherVisuals.Clock(refreshed.IssuedAtUtc)}."
                : WeatherVisuals.IsPause(refreshed.ForecastFailureKind)
                    ? $"MET Norway has paused fetching for {l.Name} for up to 6 hours. A refresh can’t shorten the pause."
                    : refreshed.ForecastFailureKind is { } kind
                        ? $"The forecast for {l.Name} couldn’t be refreshed: {WeatherVisuals.FailureOf(kind)!.Label.ToLowerInvariant()}."
                        : $"MET Norway has nothing newer for {l.Name} yet.");
            await LoadWeatherAsync();
            return;
        }

        var message = result.Status switch
        {
            System.Net.HttpStatusCode.ServiceUnavailable => "Weather isn’t set up: add a contact address for MET Norway in System settings → Weather.",
            System.Net.HttpStatusCode.TooManyRequests => "Weather actions have been used many times in the last few minutes. Wait a moment, then try again.",
            _ => result.Problem?.Message ?? $"The forecast for {l.Name} couldn’t be refreshed.",
        };
        Snackbar.Add(message, Severity.Warning);
        Announce(message);
    }

    public void Dispose() => _listLoader.Dispose();
}
