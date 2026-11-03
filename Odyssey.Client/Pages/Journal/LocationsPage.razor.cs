using Microsoft.JSInterop;
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
    [Inject] private Microsoft.JSInterop.IJSRuntime Js { get; set; } = default!;

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

    // Issue #383 §3.6: two separate two-option filters beside status — none or both sends no parameter.
    private IReadOnlyCollection<string> _coordinatesFilter = [];
    private IReadOnlyCollection<string> _useFilter = [];

    private bool? HasCoordinatesQuery =>
        LocationVisuals.FilterValue(_coordinatesFilter, LocationVisuals.HasCoordinatesKey, LocationVisuals.NeedsCoordinatesKey);

    private bool? ReferencedQuery =>
        LocationVisuals.FilterValue(_useFilter, LocationVisuals.UsedKey, LocationVisuals.NotUsedKey);

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
    private bool _canDelete;
    private bool _canReadWeather;
    private bool _canUpdateWeather;
    private bool _canReadWeatherData;
    private bool _canOpenSettings;

    // ── Dialogs ──────────────────────────────────────────────────────────────────
    private Guid _createKey = Guid.Empty;
    private bool _createOpen;
    private ExistingLocation? _editLocation;
    private Guid _editKey;
    private bool _editOpen;

    // ── Computed ────────────────────────────────────────────────────────────────
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _statusFilter.Count > 0
        || _coordinatesFilter.Count > 0 || _useFilter.Count > 0;

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
        _canDelete = user.HasPermission(PermissionClaims.LocationsDelete);
        // Weather on this page is Admin-only (issue #348): without weather.points.read it shows nothing.
        _canReadWeather = user.HasPermission(PermissionClaims.WeatherPointsRead);
        _canUpdateWeather = _canReadWeather && user.HasPermission(PermissionClaims.WeatherPointsUpdate);
        // Weather history is weather data, not setup: it follows weather.read, which every role holds.
        _canReadWeatherData = user.HasPermission(PermissionClaims.WeatherRead);
        _canOpenSettings = user.HasPermission(PermissionClaims.SystemSettingsUpdate);

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
        _coordinatesFilter = LocationVisuals.CoordinatesOptions.KnownValues(state.CoordinatesFilter);
        _useFilter = LocationVisuals.UseOptions.KnownValues(state.UseFilter);
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private LocationsPageState BuildPageState() => new()
    {
        SearchOpen = _searchOpen,
        Search = _searchString,
        StatusFilter = [.. _statusFilter],
        CoordinatesFilter = [.. _coordinatesFilter],
        UseFilter = [.. _useFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadLocations(); }
    private async Task OnCoordinatesFilterChanged(IReadOnlyCollection<string> values) { _coordinatesFilter = values ?? []; PersistPageState(); await LoadLocations(); }
    private async Task OnUseFilterChanged(IReadOnlyCollection<string> values) { _useFilter = values ?? []; PersistPageState(); await LoadLocations(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadLocations(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _statusFilter = [];
        _coordinatesFilter = [];
        _useFilter = [];
        PersistPageState();
        await LoadLocations();
    }

    internal sealed class LocationsPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [.. LocationVisuals.DefaultStatusFilter];
        public List<string> CoordinatesFilter { get; set; } = [];
        public List<string> UseFilter { get; set; } = [];
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
            HasCoordinatesQuery,
            ReferencedQuery,
            ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;
        _locations = result.ItemsOrToast(Snackbar, "locations");
        Credits = LocationVisuals.Credits(_locations);
        _loadError = !result.IsSuccess;
        _archivedMatch = !_loadError && _locations.Count == 0 && await ArchivedRowMatchesAsync();

        _announce = _loadError ? "Couldn't load locations."
            : _locations.Count == 0 ? LocationVisuals.NoFilterMatches
            : $"Showing {_locations.Count} {(_locations.Count == 1 ? "place" : "places")}";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();

        // Awaited, not fire-and-forget: the chips' names arrive after the list renders, and a newer load or a
        // dispose cancels this one so its answers never land on rows they no longer describe (N4).
        await ResolveMergeTargetsAsync(_locations);
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
        // The archived-match hint honours the two #383 filters too, so it never points at a row they hide.
        var archived = await Locations.ListAsync(1, 1, _searchString, [nameof(ArchivalStatus.Archived)],
            hasCoordinates: HasCoordinatesQuery, referenced: ReferencedQuery);
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
        new LocationRowClaims(_canUpdate, _canDelete),
        edit: EventCallback.Factory.Create(this, () => EditClicked(l)),
        copyCoordinates: EventCallback.Factory.Create(this, () => CopyAsync(LocationVisuals.FormatPair(l.Latitude, l.Longitude), "Coordinates copied.")),
        copyId: EventCallback.Factory.Create(this, () => CopyAsync(l.LocationId.ToString(), "ID copied.")),
        toggleArchive: EventCallback.Factory.Create(this, () => SetArchived(l, l.Archived is null)),
        merge: EventCallback.Factory.Create(this, () => MergeClicked(l)),
        delete: EventCallback.Factory.Create(this, () => DeleteClicked(l)),
        weather: ShowWeather ? WeatherActions(l) : null);

    /// <summary>The two claims beyond <c>locations.read</c> that decide the row menu (issue #383 §3.6).</summary>
    internal sealed record LocationRowClaims(bool CanUpdate, bool CanDelete);

    /// <summary>
    /// The row menu, by claim (issue #383 §3.6, SEC F7): Edit location · Copy coordinates (only with them) ·
    /// Copy ID · Merge into… · (weather) · divider · Archive / Restore · Delete. A merged-away row offers only
    /// Copy ID and Delete; Merge is absent on an archived row. Unavailable actions are ABSENT, never dimmed,
    /// and every item names the place in its accessible name.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildRowActions(
        ExistingLocation l, LocationRowClaims claims, EventCallback edit, EventCallback copyCoordinates, EventCallback copyId,
        EventCallback toggleArchive, EventCallback merge = default, EventCallback delete = default,
        IReadOnlyList<OdsMenuItem>? weather = null)
    {
        var merged = l.MergedIntoLocationId is not null;
        var items = new List<OdsMenuItem>();
        if (claims.CanUpdate && !merged)
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit location", AriaLabel = $"Edit location {l.Name}", OnClick = edit });

        if (l.HasCoordinates && !merged)
            items.Add(new OdsMenuItem
            {
                Icon = "my_location", Label = "Copy coordinates", AriaLabel = $"Copy coordinates of {l.Name}",
                TrailingIcon = "content_copy", OnClick = copyCoordinates,
            });
        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint", Label = "Copy ID", AriaLabel = $"Copy ID of {l.Name}", TrailingIcon = "content_copy", OnClick = copyId,
        });

        if (claims.CanUpdate && !merged && l.Archived is null)
            items.Add(new OdsMenuItem { Icon = "call_merge", Label = "Merge into…", AriaLabel = $"Merge into… — {l.Name}", OnClick = merge });

        // The weather items sit in their own group after Merge into… and before the Archive divider (issue #348).
        if (weather is { Count: > 0 } && !merged)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.AddRange(weather);
        }

        var archive = claims.CanUpdate && !merged;
        if (archive || claims.CanDelete)
            items.Add(new OdsMenuItem { Divider = true });

        if (archive)
        {
            var archived = l.Archived is not null;
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Restore" : "Archive",
                AriaLabel = $"{(archived ? "Restore" : "Archive")} {l.Name}",
                OnClick = toggleArchive,
            });
        }

        if (claims.CanDelete)
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", AriaLabel = $"Delete {l.Name}", Danger = true, OnClick = delete });

        return items;
    }

    /// <summary>The id of a row's menu wrapper — a focus target after merge, delete and Find coordinates.</summary>
    internal static string MenuId(Guid id) => $"loc-menu-{id}";

    /// <summary>Find coordinates: a place without them, under <c>locations.update</c>, never on a merged-away row.</summary>
    internal static bool ShowsFindCoordinates(ExistingLocation l, bool canUpdate) =>
        canUpdate && !l.HasCoordinates && l.MergedIntoLocationId is null;

    /// <summary>The id of a row's Find coordinates button.</summary>
    internal static string FindId(Guid id) => $"loc-find-{id}";

    /// <summary>
    /// Where focus goes after a delete (issue #383 §3.9, FE m6): the next row's menu button, else the
    /// previous one — recorded BEFORE the call, so list batching does not matter — else the page h1.
    /// </summary>
    internal static Guid? NeighbourOf(IReadOnlyList<ExistingLocation> rows, Guid id)
    {
        var index = rows.ToList().FindIndex(r => r.LocationId == id);
        if (index < 0)
            return null;
        return index + 1 < rows.Count ? rows[index + 1].LocationId
            : index > 0 ? rows[index - 1].LocationId
            : null;
    }

    // ── Merge / delete (issue #383 §3.8, §3.9) ───────────────────────────────────────
    private ExistingLocation? _mergeSource;
    private bool _mergeOpen;
    private Guid _mergeKey;
    private ExistingLocation? _deleteLocation;
    private bool _deleteOpen;
    private Guid _deleteKey;
    private Guid? _focusAfterRender;
    private bool _focusPending;

    private void MergeClicked(ExistingLocation l)
    {
        _mergeSource = l;
        _mergeKey = Guid.NewGuid();
        _mergeOpen = true;
    }

    private async Task OnMerged(ExistingLocation target)
    {
        var source = _mergeSource?.Name ?? "the place";
        Snackbar.Add($"Merged {source} into {target.Name}.", Severity.Success);
        await Task.WhenAll(LoadLocations(), LoadCounts());
        Announce($"Merged {source} into {target.Name}");
        FocusRowOrHeading(target.LocationId);
    }

    private void DeleteClicked(ExistingLocation l)
    {
        _deleteLocation = l;
        _deleteKey = Guid.NewGuid();
        _deleteOpen = true;
        // The neighbour is recorded before anything is sent.
        _deleteNeighbour = NeighbourOf(_locations, l.LocationId);
    }

    private Guid? _deleteNeighbour;

    /// <summary>After a merge: the target's row menu, else the page heading (§3.8, §6.5).</summary>
    internal static string MergeDestination(ExistingLocation target) => $"{MenuId(target.LocationId)} {PageHeader.HeadingId}";

    /// <summary>After a delete: the neighbouring row's menu, else the page heading (§3.9, §6.5).</summary>
    internal static string DeleteDestination(Guid? neighbour) =>
        neighbour is { } id ? $"{MenuId(id)} {PageHeader.HeadingId}" : PageHeader.HeadingId;

    private async Task OnDeleted(ExistingLocation deleted)
    {
        Snackbar.Add($"Deleted {deleted.Name}.", Severity.Success);
        _locations.RemoveAll(l => l.LocationId == deleted.LocationId);
        Credits = LocationVisuals.Credits(_locations);
        Announce($"Deleted {deleted.Name}");
        FocusRowOrHeading(_deleteNeighbour);
        await LoadCounts();
    }

    private void FocusRowOrHeading(Guid? id)
    {
        _focusAfterRender = id;
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
            await Js.InvokeVoidAsync("odsFocusChainOrHeading",
                (object)(_focusAfterRender is { } id ? new[] { MenuId(id) } : Array.Empty<string>()));
        }
        catch (Microsoft.JSInterop.JSException) { }
    }

    // ── Merged-into chips ──────────────────────────────────────────────────────────────
    private readonly Dictionary<Guid, string> _mergeTargets = [];
    private CancellationTokenSource? _mergeTargetsCts;

    /// <summary>At most this many target GETs are in flight at once.</summary>
    internal const int MergeTargetConcurrency = 4;

    /// <summary>
    /// The target's name — from the loaded list, else <c>GET /api/locations/{id}</c> (issue #383 §3.6). The GETs
    /// run in parallel under <see cref="MergeTargetConcurrency"/>; a failed one leaves its chip reading
    /// "another place"; and the run is cancelled by the next load or by dispose, so a superseded answer never
    /// reaches the page (frontend review N4).
    /// </summary>
    private async Task ResolveMergeTargetsAsync(IReadOnlyList<ExistingLocation> rows)
    {
        CancelMergeTargets();
        var cts = new CancellationTokenSource();
        _mergeTargetsCts = cts;
        var ct = cts.Token;

        var targets = rows.Select(l => l.MergedIntoLocationId).OfType<Guid>().Distinct().ToList();
        foreach (var id in targets)
        {
            if (rows.FirstOrDefault(l => l.LocationId == id) is { } loaded)
                _mergeTargets[id] = loaded.Name;
        }

        var missing = targets.Where(id => !_mergeTargets.ContainsKey(id)).ToList();
        if (missing.Count == 0)
            return;

        try
        {
            var names = await FetchNamesAsync(
                missing, async (id, token) =>
                {
                    var result = await Locations.GetAsync(id, token);
                    return result.IsSuccess ? result.Value?.Name : null;
                },
                MergeTargetConcurrency, ct);

            if (ct.IsCancellationRequested)
                return;
            foreach (var (id, name) in names)
                _mergeTargets[id] = name;

            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the page went away.
        }
    }

    /// <summary>
    /// The names of <paramref name="ids"/>, fetched in parallel with at most <paramref name="concurrency"/> in
    /// flight. An id whose fetch fails or answers nothing is left out, so its chip falls back to "another
    /// place"; cancellation propagates.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, string>> FetchNamesAsync(
        IReadOnlyCollection<Guid> ids, Func<Guid, CancellationToken, Task<string?>> fetch, int concurrency, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(concurrency);
        var fetched = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (Id: id, Name: await fetch(id, ct));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return (Id: id, Name: (string?)null);
            }
            finally
            {
                gate.Release();
            }
        }));

        ct.ThrowIfCancellationRequested();
        return fetched.Where(f => f.Name is not null).ToDictionary(f => f.Id, f => f.Name!);
    }

    private void CancelMergeTargets()
    {
        var previous = _mergeTargetsCts;
        _mergeTargetsCts = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private string? MergedInto(ExistingLocation l) =>
        l.MergedIntoLocationId is { } id ? LocationVisuals.MergedInto(_mergeTargets.GetValueOrDefault(id) ?? "another place") : null;

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

    private void EditClicked(ExistingLocation l) => OpenEdit(l, findCoordinates: false);

    // Find coordinates (issue #383 §3.6): the edit dialog at the search step, prefilled; focus returns to the
    // row's Find coordinates button, or — once the row has coordinates and the button is gone — its menu.
    private void FindCoordinatesClicked(ExistingLocation l) => OpenEdit(l, findCoordinates: true);

    private bool _editFindCoordinates;

    private void OpenEdit(ExistingLocation l, bool findCoordinates)
    {
        _editLocation = l;
        _editFindCoordinates = findCoordinates;
        _editKey = Guid.NewGuid();
        _editOpen = true;
    }

    private string EditReturnFocus(ExistingLocation l) =>
        _editFindCoordinates ? $"{FindId(l.LocationId)} {MenuId(l.LocationId)}" : MenuId(l.LocationId);

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
        var index = _locations.FindIndex(l => l.LocationId == saved.LocationId);
        if (index >= 0)
            _locations[index] = saved;
        Credits = LocationVisuals.Credits(_locations);
        Announce($"{saved.Name} saved.");
        await LoadCounts();
    }

    /// <summary>
    /// Builds the full-replacement body that changes only <see cref="UpdateLocation.Archived"/> — the
    /// structured address included, or the PUT would clear it (issue #380).
    /// </summary>
    internal static UpdateLocation WithArchived(ExistingLocation l, bool archived) => new()
    {
        Name = l.Name,
        Description = l.Description,
        AddressText = l.AddressText,
        Line1 = l.Line1,
        Line2 = l.Line2,
        PostalCode = l.PostalCode,
        City = l.City,
        Region = l.Region,
        CountryCode = l.CountryCode,
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
    /// <summary>
    /// The collapsed row's meta line (issue #383 §3.6): the address (structured, else as written, else a muted
    /// <i>No address</i>), coordinates in mono and the time zone when set, then the usage sub-line.
    /// </summary>
    private static IReadOnlyList<RenderFragment?> MetaFor(ExistingLocation l) =>
    [
        LocationVisuals.ComposeAddress(l) is { } composed ? OdsRecordMeta.Text(composed)
            : string.IsNullOrWhiteSpace(l.AddressText) ? NoAddress : OdsRecordMeta.Text(l.AddressText),
        l.HasCoordinates ? OdsRecordMeta.Mono(LocationVisuals.FormatPair(l.Latitude, l.Longitude)) : null,
        string.IsNullOrEmpty(l.TimeZoneId) ? null : OdsRecordMeta.Text(l.TimeZoneId),
        OdsRecordMeta.Text(LocationVisuals.UsageSubLine(l.Usage)),
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

    // ── Weather (issue #348; weather points, issue #387) ────────────────────────────────────────────
    // Interim until the weather-points UI (#388): the Admin sees the linked point's settings in the record and
    // can refresh its forecast from the row menu; creating, editing, moving, purging and linking points are not
    // offered here yet.

    private Dictionary<Guid, ExistingWeatherPoint> _weatherRows = [];
    private bool _weatherLoaded;
    private readonly HashSet<Guid> _refreshing = [];

    /// <summary>Weather shows only once its rows are known — never a guess from an empty list.</summary>
    private bool ShowWeather => _canReadWeather && _weatherLoaded;

    /// <summary>The Admin row of the point the place links to, or null.</summary>
    private ExistingWeatherPoint? WeatherRow(ExistingLocation l) =>
        l.WeatherPoint is { } link ? _weatherRows.GetValueOrDefault(link.WeatherPointId) : null;

    /// <summary>
    /// Weather history (every role): on a place whose linked point has history on or any measured day stored, and
    /// is not being purged — decided by the link's flags alone, with no second read (#387 §5 endpoint 11).
    /// </summary>
    private bool ShowHistory(ExistingLocation l) =>
        _canReadWeatherData && l.WeatherPoint is { PurgeInProgress: false } p && (p.HistoryEnabled || p.HasStoredHistory);

    private async Task LoadWeatherAsync()
    {
        if (!_canReadWeather)
            return;

        var result = await Weather.PointsAsync(1, PagedQuery.SizeAll);
        if (result.IsSuccess && result.Value is { } page)
        {
            _weatherRows = page.Items.ToDictionary(r => r.WeatherPointId);
            _weatherLoaded = true;
        }
        else
        {
            Snackbar.Add("Weather points couldn’t be loaded.", Severity.Warning);
        }
        StateHasChanged();
    }

    /// <summary>The Admin's weather items. Unavailable actions are absent, not dimmed.</summary>
    private IReadOnlyList<OdsMenuItem> WeatherActions(ExistingLocation l) => BuildWeatherActions(
        WeatherRow(l), _canUpdateWeather,
        refresh: EventCallback.Factory.Create(this, () => RefreshWeatherAsync(l)));

    internal static IReadOnlyList<OdsMenuItem> BuildWeatherActions(ExistingWeatherPoint? row, bool canUpdate, EventCallback refresh)
    {
        var items = new List<OdsMenuItem>();
        if (row is { PurgeInProgress: false, ForecastEnabled: true } && canUpdate)
            items.Add(new OdsMenuItem { Icon = "refresh", Label = "Refresh forecast", OnClick = refresh });
        return items;
    }

    private async Task RefreshWeatherAsync(ExistingLocation l)
    {
        if (!_refreshing.Add(l.LocationId))
            return;
        try
        {
            await RefreshWeatherCoreAsync(l);
        }
        finally
        {
            _refreshing.Remove(l.LocationId);
        }
    }

    private async Task RefreshWeatherCoreAsync(ExistingLocation l)
    {
        if (WeatherRow(l) is not { } row)
            return;

        var result = await Weather.RefreshPointAsync(row.WeatherPointId);
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

        if (result.Status is System.Net.HttpStatusCode.GatewayTimeout or 0)
        {
            // The fetch may still be running server-side: say so and re-read rather than report a failure.
            Announce($"The refresh for {l.Name} is taking longer than usual and may still finish. Its status updates here.");
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

    public void Dispose()
    {
        CancelMergeTargets();
        _listLoader.Dispose();
    }
}
