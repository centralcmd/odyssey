using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Photos;

public partial class AlbumsPage
{
    private List<PhotoAlbumSummary> _albumList = [];
    private bool _loading = true;
    private bool _loadError;
    private ExistingPhotoAlbum? _openAlbum;
    private List<PhotoSummary> _albumPhotos = [];
    private int? _detailIndex;
    private Guid? _formAlbumId;   // null = new album, id = edit
    private bool _formOpen;

    // Selection within an open album (mirrors the /photos grid; album-scoped actions only).
    private bool _selecting;
    private readonly HashSet<Guid> _selected = [];
    private Guid[]? _addToAlbumIds;

    private Dictionary<Guid, string> _tagNames = [];
    private Dictionary<Guid, string> _albumNames = [];
    private Dictionary<Guid, string> _personNames = [];

    // Header regions (the DS shares the Overview panel + a "Search albums…" field across both views).
    private bool _overviewOpen;
    private bool _searchOpen = true;
    private string _albumSearch = string.Empty;

    private IReadOnlyList<PhotoAlbumSummary> FilteredAlbums =>
        string.IsNullOrWhiteSpace(_albumSearch)
            ? _albumList
            : [.. _albumList.Where(a => a.Name.Contains(_albumSearch, StringComparison.OrdinalIgnoreCase))];

    // Offered from the over-filtered empty state (issue #368).
    private void ClearAlbumSearch()
    {
        _albumSearch = string.Empty;
        PersistPageState();
    }

    private bool _canCreate;
    private bool _canUpdate;

    private const string PageStateKey = "albums-page";

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.PhotoAlbumsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.PhotoAlbumsUpdate);
        await LoadAsync();
        await PageState.RestoreOrSeedAsync<AlbumsPageState>(PageStateKey, ApplyPageState, BuildPageState);
    }

    // ── Persisted page state (IPageStateService, key albums-page) ──
    private sealed class AlbumsPageState
    {
        public bool OverviewOpen { get; set; }
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
    }

    private void ApplyPageState(AlbumsPageState s)
    {
        _overviewOpen = s.OverviewOpen;
        _searchOpen = s.SearchOpen;
        _albumSearch = s.Search;
    }

    private AlbumsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _albumSearch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _albumSearch = value; PersistPageState(); }

    private async Task LoadAsync()
    {
        _loading = true;

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // library that genuinely has no albums.
        var albums = await Albums.ListAllAsync();
        _albumList = albums.ItemsOrToast(Snackbar, "albums");
        _loadError = !albums.IsSuccess;
        _albumNames = _albumList.ToDictionary(a => a.PhotoAlbumId, a => a.Name);

        var tags = (await PhotoTags.ListAllAsync()).ItemsOrToast(Snackbar, "photo tags");
        _tagNames = tags.ToDictionary(t => t.PhotoTagId, t => t.Name);

        // People come from Person contacts (requires contacts.read; degrades to empty) so
        // the album lightbox's People chips show names, not raw ids (spec §10.5).
        var people = (await Contacts.ListAllAsync(types: ["Person"])).ItemsOrToast(Snackbar, "people");
        _personNames = people.ToDictionary(c => c.ContactId, c => c.ResolvedDisplayName);
        _loading = false;
    }

    private async Task OpenAlbum(Guid id)
    {
        ExitSelect();
        _openAlbum = await Albums.GetAsync(id);
        if (_openAlbum is not { } album)
        {
            _albumPhotos = [];
            return;
        }

        // Fetch just this album's photos (server-side albumIds filter) — both active and archived, since
        // an album can contain archived members and the list defaults to active-only — then order them by
        // the album's own Position order (the list endpoint sorts by its own keys, not album position).
        var byId = new Dictionary<Guid, PhotoSummary>();
        foreach (var status in new[] { (string?)null, "Archived" })
        {
            var load = await Photos.ListAsync(1, PagedQuery.LimitAll, albumIds: [id.ToString()], status: status);
            foreach (var p in load.PagedItemsOrToast(Snackbar, "album photos"))
                byId[p.PhotoId] = p;
        }

        _albumPhotos = [.. album.PhotoIds.Select(pid => byId.GetValueOrDefault(pid)).Where(p => p is not null).Select(p => p!)];
    }

    private void CloseAlbum() { _openAlbum = null; ExitSelect(); }

    private void OpenDetail(Guid id) => _detailIndex = _albumPhotos.FindIndex(p => p.PhotoId == id);

    // ── Selection (only meaningful inside an open album) ──
    private void ToggleSelecting() { _selecting = !_selecting; _selected.Clear(); }
    private void ToggleSelect(Guid id) { if (!_selected.Add(id)) _selected.Remove(id); }
    private void StartSelectWith(Guid id) { _selecting = true; _selected.Clear(); _selected.Add(id); }
    private void ExitSelect() { _selecting = false; _selected.Clear(); }

    private void ToggleSelectAll()
    {
        if (_selected.Count >= _albumPhotos.Count)
            _selected.Clear();
        else
        { _selected.Clear(); foreach (var p in _albumPhotos) _selected.Add(p.PhotoId); }
    }

    private void OpenAddToAlbum() => _addToAlbumIds = [.. _selected];

    private async Task OnAddedToAlbum()
    {
        var reopen = _openAlbum?.PhotoAlbumId;
        _addToAlbumIds = null;
        ExitSelect();
        await LoadAsync();
        if (reopen is { } id && _albumList.Any(a => a.PhotoAlbumId == id))
        {
            await OpenAlbum(id);
        }
    }

    // Removing from an album is an album PUT with the reduced ordered PhotoIds; a removed cover is
    // nulled (matching the server's album-PUT eval order in spec §7).
    private async Task RemoveSelectedFromAlbumAsync()
    {
        // Guards on the claim as well as hiding the control, matching the Finance and Journal
        // pages. The server authorizes independently; this stops a handler still reachable
        // through a stale render or the keyboard from firing a request the user cannot make.
        if (!_canUpdate)
            return;

        if (_openAlbum is not { } album || _selected.Count == 0)
        {
            return;
        }

        var removed = _selected.Count;
        var ok = (await Albums.UpdateAsync(album.PhotoAlbumId, new UpdatePhotoAlbum
        {
            Name = album.Name,
            Description = album.Description,
            PhotoIds = [.. album.PhotoIds.Where(id => !_selected.Contains(id))],
            CoverPhotoId = album.CoverPhotoId is { } c && _selected.Contains(c) ? null : album.CoverPhotoId,
            Archived = album.Archived is not null,
        })).Toast(Snackbar, "Remove failed", $"{removed} photo{(removed == 1 ? "" : "s")} removed from the album.");

        if (ok)
        {
            ExitSelect();
            await LoadAsync();
            await OpenAlbum(album.PhotoAlbumId);
        }
    }

    private void OpenForm(Guid? albumId)
    {
        _formAlbumId = albumId;
        _formOpen = true;
    }

    private async Task OnAlbumSaved()
    {
        // Reopen the edited album (or stay on the current drill-in for a newly-created one).
        var reopen = _formAlbumId ?? _openAlbum?.PhotoAlbumId;
        _formOpen = false;
        await LoadAsync();
        if (reopen is { } id && _albumList.Any(a => a.PhotoAlbumId == id))
        {
            await OpenAlbum(id);
        }
        else
        {
            _openAlbum = null;
        }
    }

    private OdsPhotoTile.Vm TileVm(PhotoSummary p) =>
        new(p.PhotoId, Files.ContentUrl(p.FileId), string.IsNullOrWhiteSpace(p.Title) ? "Photo" : p.Title!,
            p.Archived is not null, p.Favourited is not null);

    private string CoverStyle(PhotoAlbumSummary a) =>
        a.CoverFileId is { } f
            ? $"background: center/cover url('{Files.ContentUrl(f)}');"
            : "background: var(--mud-palette-surface); display:flex; align-items:center; justify-content:center;";

    private string TagName(Guid id) => _tagNames.GetValueOrDefault(id, "—");
    private string AlbumName(Guid id) => _albumNames.GetValueOrDefault(id, "—");
    private string PersonName(Guid id) => _personNames.GetValueOrDefault(id, "—");

    private RenderFragment SubFragment => builder =>
        builder.AddContent(0, _openAlbum is null
            ? $"{_albumList.Count} album{(_albumList.Count == 1 ? "" : "s")}"
            : $"{_albumPhotos.Count} photo{(_albumPhotos.Count == 1 ? "" : "s")}");
}
