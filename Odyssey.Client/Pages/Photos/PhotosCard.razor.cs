using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Photos;

public partial class PhotosCard
{
    private readonly List<PhotoSummary> _photos = [];
    private int _total;
    private bool _loading = true;
    private bool _loadError;

    // Filters.
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _albums = [];
    private IReadOnlyCollection<string> _tags = [];
    private IReadOnlyCollection<string> _people = [];
    private DateTime? _from;
    private DateTime? _to;
    private bool _favouritesOnly;
    private bool _archivedView;
    private OdsTableSort _sort = new("date", OdsSortDirection.Desc);
    private int _pageSize = 24;
    private int _visible = 24;

    // Header region open-state (persisted).
    private bool _overviewOpen;
    private bool _searchOpen = true;

    // Options + name maps.
    private IReadOnlyList<OdsOption> _tagOptions = [];
    private IReadOnlyList<OdsOption> _albumOptions = [];
    private IReadOnlyList<OdsOption> _peopleOptions = [];
    private Dictionary<Guid, string> _tagNames = [];
    private Dictionary<Guid, string> _albumNames = [];
    private Dictionary<Guid, string> _personNames = [];
    private IReadOnlyList<PhotoAlbumSummary> _albumSummaries = [];
    private int _libraryTotal;
    private PhotoLibraryOverview? _overview;

    // Selection + dialogs.
    private bool _selecting;
    private readonly HashSet<Guid> _selected = [];
    private int? _detailIndex;
    private Guid? _editId;
    private IReadOnlyCollection<Guid>? _addToAlbumIds;
    private bool _uploadOpen;

    // Permissions.
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canManageAlbums;
    private bool _canCreateTags;
    private bool _canRenameFile;

    private const string PageStateKey = "photos-page";

    private static readonly IReadOnlyList<int> PageSizes = [24, 48, 96];
    private static readonly IReadOnlyList<OdsSortField<PhotoSummary>> SortFields =
    [
        new() { Key = "date", Label = "Date taken", Type = OdsSortType.Date },
        new() { Key = "title", Label = "Title", Type = OdsSortType.Text },
        new() { Key = "added", Label = "Recently added", Type = OdsSortType.Date },
    ];

    private bool HasFilters => !string.IsNullOrWhiteSpace(_search) || _albums.Count > 0 || _tags.Count > 0
        || _people.Count > 0 || _from is not null || _to is not null || _favouritesOnly;

    // Empty-state copy distinguishes the archived view (a distinct place, not "no photos yet") from a
    // genuinely empty library; the over-filtered case is OdsListStatus's shared "no matches" state.
    private string EmptyTitle => _archivedView ? "No archived photos" : "No photos yet";

    private string EmptySubtitle => _archivedView
        ? "Photos you archive are kept here."
        : "Photos you upload — or attach to journal entries — show up here.";

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.PhotosCreate)
                     && user.HasPermission(PermissionClaims.FilesRead);
        _canUpdate = user.HasPermission(PermissionClaims.PhotosUpdate);
        _canDelete = user.HasPermission(PermissionClaims.PhotosDelete);
        _canManageAlbums = user.HasPermission(PermissionClaims.PhotoAlbumsUpdate);
        _canCreateTags = user.HasPermission(PermissionClaims.PhotoTagsCreate);
        _canRenameFile = user.HasPermission(PermissionClaims.FilesUpdate);

        await LoadOptionsAsync();
        await PageState.RestoreOrSeedAsync<PhotosPageState>(PageStateKey, ApplyPageState, BuildPageState);
        _visible = _pageSize;
        await LoadLibraryCountAsync();
        await ReloadAsync();
    }

    // The subline shows the whole-library count (unfiltered); the Overview panel's own stats/breakdowns
    // live in the shared PhotoLibraryOverview component.
    private async Task LoadLibraryCountAsync() =>
        _libraryTotal = (await Photos.ListAsync(1, 1, status: null))
            .PagedOrToast(Snackbar, "the library count").TotalCount;

    private async Task LoadOptionsAsync()
    {
        var tags = (await PhotoTags.ListAllAsync()).ItemsOrToast(Snackbar, "photo tags");
        _tagOptions = [.. tags.Select(t => new OdsOption(t.PhotoTagId.ToString(), t.Name))];
        _tagNames = tags.ToDictionary(t => t.PhotoTagId, t => t.Name);

        _albumSummaries = (await Albums.ListAllAsync()).ItemsOrToast(Snackbar, "albums");
        _albumOptions = [.. _albumSummaries.Select(a => new OdsOption(a.PhotoAlbumId.ToString(), a.Name))];
        _albumNames = _albumSummaries.ToDictionary(a => a.PhotoAlbumId, a => a.Name);

        // People come from the caller's Person contacts (requires contacts.read; degrades to empty).
        var people = (await Contacts.ListAllAsync(types: ["Person"])).ItemsOrToast(Snackbar, "people");
        _peopleOptions = [.. people.Select(c => new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName))];
        _personNames = people.ToDictionary(c => c.ContactId, c => c.ResolvedDisplayName);
    }

    private async Task<PagedLoad<PhotoSummary>> FetchAsync(int page, int pageSize)
    {
        var result = await Photos.ListAsync(
            page: page, pageSize: pageSize, search: _search,
            tagIds: _tags, personIds: _people, albumIds: _albums,
            from: _from, to: _to, favouritesOnly: _favouritesOnly,
            status: _archivedView ? "Archived" : null,
            sortBy: ServerSort(_sort.Key), sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        return result.PagedOrToast(Snackbar, "photos");
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        StateHasChanged();

        var load = await FetchAsync(page: 1, pageSize: _visible);

        // A failed fetch yields no items; without this the grid would render "No photos yet".
        _loadError = !load.IsSuccess;
        _photos.Clear();
        _photos.AddRange(load.Items);
        _total = load.TotalCount;

        _loading = false;
        StateHasChanged();
    }

    private static string ServerSort(string key) => key switch
    {
        "title" => "Title",
        "added" => "CreatedAt",
        _ => "TakenAt",
    };

    private async Task ApplyFilter(Action change)
    {
        change();
        _visible = _pageSize;
        _selected.Clear();
        PersistPageState();
        await ReloadAsync();
    }

    private void OnSearchChanged(string value) => _search = value ?? string.Empty;

    private Task OnSearchSubmitted() => ApplyFilter(() => { });

    // Offered from the over-filtered empty state (issue #368) — the page told the user to "clear a
    // filter" without giving them anything to click. The archived view is a place, not a filter, so
    // it stays put.
    private Task ClearFiltersAsync() => ApplyFilter(() =>
    {
        _search = string.Empty;
        _albums = [];
        _tags = [];
        _people = [];
        _from = null;
        _to = null;
        _favouritesOnly = false;
    });

    // ── Persisted page state (IPageStateService, key photos-page) ──
    private sealed class PhotosPageState
    {
        public bool OverviewOpen { get; set; }
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> Albums { get; set; } = [];
        public List<string> Tags { get; set; } = [];
        public List<string> People { get; set; } = [];
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public bool FavouritesOnly { get; set; }
        public bool ArchivedView { get; set; }
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = 24;
    }

    private void ApplyPageState(PhotosPageState s)
    {
        _overviewOpen = s.OverviewOpen;
        _searchOpen = s.SearchOpen;
        _search = s.Search;
        _albums = _albumOptions.KnownValues(s.Albums);
        _tags = _tagOptions.KnownValues(s.Tags);
        _people = _peopleOptions.KnownValues(s.People);
        _from = s.From;
        _to = s.To;
        _favouritesOnly = s.FavouritesOnly;
        _archivedView = s.ArchivedView;
        _pageSize = PageSizes.Contains(s.PageSize) ? s.PageSize : 24;
        if (!string.IsNullOrEmpty(s.SortField) && s.SortDirection is { } dir)
        {
            _sort = new OdsTableSort(s.SortField, dir);
        }
    }

    private PhotosPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        Albums = [.. _albums],
        Tags = [.. _tags],
        People = [.. _people],
        From = _from,
        To = _to,
        FavouritesOnly = _favouritesOnly,
        ArchivedView = _archivedView,
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }

    // Append the next page instead of refetching the whole (growing) window.
    private async Task LoadMore()
    {
        var load = await FetchAsync(page: _photos.Count / _pageSize + 1, pageSize: _pageSize);
        _photos.AddRange(load.Items);
        _total = load.TotalCount;
        _visible = _photos.Count; // keep the window size in sync for a later mutation refresh
        StateHasChanged();
    }

    // After a mutation the whole library changed, so refresh the subline count, the grid window, and
    // the Overview panel if it's currently open (it reloads itself on remount otherwise).
    private async Task RefreshAsync()
    {
        await LoadLibraryCountAsync();
        await ReloadAsync();
        if (_overview is not null)
        {
            await _overview.RefreshAsync();
        }
    }

    private OdsPhotoTile.Vm TileVm(PhotoSummary p) =>
        new(p.PhotoId, Files.ContentUrl(p.FileId), string.IsNullOrWhiteSpace(p.Title) ? "Photo" : p.Title!,
            p.Archived is not null, p.Favourited is not null);

    private string TagName(Guid id) => _tagNames.GetValueOrDefault(id, "—");
    private string AlbumName(Guid id) => _albumNames.GetValueOrDefault(id, "—");
    private string PersonName(Guid id) => _personNames.GetValueOrDefault(id, "—");

    // ── Selection ──
    private void ToggleSelect(Guid id) { if (!_selected.Add(id)) _selected.Remove(id); }
    private void StartSelectWith(Guid id) { _selecting = true; _selected.Clear(); _selected.Add(id); }
    private void ExitSelect() { _selecting = false; _selected.Clear(); }
    private void ToggleSelectAll()
    {
        if (_selected.Count >= _photos.Count)
            _selected.Clear();
        else
        { _selected.Clear(); foreach (var p in _photos) _selected.Add(p.PhotoId); }
    }

    private void OpenDetail(Guid id) => _detailIndex = _photos.FindIndex(p => p.PhotoId == id);
    private void OpenEditFromDetail(Guid id) { _detailIndex = null; _editId = id; }
    private void OpenAddToAlbum() => _addToAlbumIds = [.. _selected];

    // ── Single-photo mutations (full PUT, preserving other fields) ──
    // Each mutation guards on the permission claim as well as hiding its control, matching the
    // Finance and Journal pages. The server authorizes independently, so this is defense in depth
    // rather than the boundary: it stops a handler still reachable through a stale render, a
    // keyboard path or a bulk loop from firing a request the user cannot make.
    private async Task ToggleFavouriteAsync(Guid id) => await MutateAsync(id, u => u.Favourite = !u.Favourite);
    private async Task ToggleArchiveAsync(Guid id)
    {
        if (!_canUpdate)
            return;

        await MutateAsync(id, u => u.Archived = !u.Archived);
        _detailIndex = null;
        await RefreshAsync();
    }

    private async Task<bool> MutateAsync(Guid id, Action<UpdatePhoto> mutate)
    {
        if (!_canUpdate)
            return false;

        var p = await Photos.GetAsync(id);
        if (p is null)
        {
            return false;
        }

        var body = PhotoMappers.ToUpdate(p);
        mutate(body);
        var ok = (await Photos.UpdateAsync(id, body)).Toast(Snackbar, "Update failed");
        if (ok)
        {
            await RefreshAsync();
        }

        return ok;
    }

    private async Task DeleteAsync(Guid id)
    {
        if (!_canDelete)
            return;

        if ((await Photos.DeleteAsync(id)).Toast(Snackbar, "Delete failed", "Photo deleted."))
        {
            _detailIndex = null;
            await RefreshAsync();
        }
    }

    // ── Bulk actions ──
    private async Task BulkFavouriteAsync()
    {
        if (!_canUpdate)
            return;

        foreach (var id in _selected.ToList())
            await MutateSilentAsync(id, u => u.Favourite = true);
        ExitSelect();
        await RefreshAsync();
    }

    private async Task BulkArchiveAsync(bool archive)
    {
        if (!_canUpdate)
            return;

        foreach (var id in _selected.ToList())
            await MutateSilentAsync(id, u => u.Archived = archive);
        ExitSelect();
        await RefreshAsync();
    }

    private async Task BulkDeleteAsync()
    {
        if (!_canDelete)
            return;

        foreach (var id in _selected.ToList())
            (await Photos.DeleteAsync(id)).Toast(Snackbar, "Delete failed");
        Snackbar.Add($"{_selected.Count} photo(s) deleted.", Severity.Success);
        ExitSelect();
        await RefreshAsync();
    }

    private async Task MutateSilentAsync(Guid id, Action<UpdatePhoto> mutate)
    {
        if (!_canUpdate)
            return;

        var p = await Photos.GetAsync(id);
        if (p is null)
        {
            return;
        }

        var body = PhotoMappers.ToUpdate(p);
        mutate(body);
        (await Photos.UpdateAsync(id, body)).Toast(Snackbar, "Update failed");
    }

    private async Task OnPhotoSaved() { _editId = null; await RefreshAsync(); }
    private async Task OnUploaded() => await RefreshAsync();
    private async Task OnAlbumsChanged()
    {
        _addToAlbumIds = null;
        ExitSelect();
        await LoadOptionsAsync();
        await RefreshAsync();
    }

    // ── Header fragments ──
    private RenderFragment SubFragment => builder =>
        builder.AddContent(0, $"{_libraryTotal} photo{(_libraryTotal == 1 ? "" : "s")} · {_albumSummaries.Count} albums");
}
