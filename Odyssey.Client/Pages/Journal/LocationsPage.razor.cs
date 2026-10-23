using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
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

    private string EmptyDescription => _canCreate
        ? "Keep one shared list of the places that matter to the household — home, the cabin, school. Search an address or place name and the coordinates come with it."
        : "The household hasn’t saved any places yet. Someone with permission to add locations can start the list.";

    private IReadOnlyList<LocationAttribution> Credits => LocationVisuals.Credits(_locations);

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.LocationsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.LocationsUpdate);

        await RestorePageStateAsync();
        await Task.WhenAll(LoadLocations(), LoadCounts());
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

    private async Task ReloadLocation(Guid id)
    {
        var result = await Locations.GetAsync(id);
        if (result.IsSuccess && result.Value is { } fresh)
        {
            var index = _locations.FindIndex(l => l.LocationId == id);
            if (index >= 0)
                _locations[index] = fresh;
        }

        await LoadCounts();
    }

    // ── Expand ──────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private void ToggleExpand(Guid id) => _expandedId = _expandedId == id ? null : id;

    // ── Row menu ──────────────────────────────────────────────────────────────────
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingLocation l)
    {
        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit location",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(l)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "my_location",
            Label = "Copy coordinates",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyAsync(LocationVisuals.FormatPair(l.Latitude, l.Longitude), "Coordinates copied.")),
        });
        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyAsync(l.LocationId.ToString(), "ID copied.")),
        });

        if (_canUpdate)
        {
            var archived = l.Archived is not null;
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Restore" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => SetArchived(l, !archived)),
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
        var index = _locations.FindIndex(l => l.LocationId == saved.LocationId);
        if (index >= 0)
            _locations[index] = saved;
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

    public void Dispose() => _listLoader.Dispose();
}
