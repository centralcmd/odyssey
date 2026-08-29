using Odyssey.ApiClient;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using ArchivalStatus = Odyssey.Dtos.Journal.ArchivalStatus;

namespace Odyssey.Client.Pages.Journal;

public partial class JournalCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<JournalEntrySummary> _entries = [];                       // server-filtered display list
    private readonly Dictionary<Guid, ExistingJournalEntry> _details = new();
    private readonly Dictionary<Guid, FileMetadataResponse?> _fileMeta = new(); // hydrated file names/sizes

    private List<ExistingJournalTag> _tags = [];
    private Dictionary<Guid, ExistingJournalTag> _tagById = new();
    private IReadOnlyList<OdsOption> _tagOptions = [];

    private Dictionary<Guid, ExistingContact> _contactById = new();
    private IReadOnlyList<OdsOption> _contactOptions = [];

    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // Photo detail dialog — the shared library lightbox (Odyssey Design System · Photos · detail).
    // null index = closed.
    private List<PhotoSummary> _detailPhotos = [];
    private int? _detailIndex;
    private Guid? _detailEntryId;
    private Guid? _editPhotoId;
    private bool _canUpdatePhotos;
    private bool _canDeletePhotos;
    private bool _canCreatePhotoTags;
    private bool _canRenameFile;
    private bool _photoRefsLoaded;
    private Dictionary<Guid, string> _photoTagNames = [];
    private Dictionary<Guid, string> _photoAlbumNames = [];
    private IReadOnlyList<OdsOption> _photoTagOptions = [];
    private IReadOnlyList<OdsOption> _photoAlbumOptions = [];
    private IReadOnlyList<OdsOption> _photoPeopleOptions = [];
    private IJSObjectReference? _focusJs;

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadFiles;
    private bool _canReadContacts;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "journal-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    // Status: single "Active / Archived" multiselect. Empty = hide archived (Active only); both = all.
    private static readonly IReadOnlyList<OdsOption> _statusOptions =
    [
        new(nameof(ArchivalStatus.Active), "Active"),
        new(nameof(ArchivalStatus.Archived), "Archived"),
    ];

    // ── Sort (server-side; keys map 1:1 to JournalEntrySortBy) ───────────────────
    private static readonly OdsTableSort DefaultSort = new("entryDate", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<JournalEntrySummary>> _sortFields =
    [
        new() { Key = "entryDate", Label = "Entry date", Type = OdsSortType.Date, DefaultDir = OdsSortDirection.Desc, SortValue = e => e.EntryDate },
        new() { Key = "title", Label = "Title", Type = OdsSortType.Text, SortValue = e => e.Title.ToLowerInvariant() },
        // Server-side sort (the API maps "createdAt" → CreatedAt); the summary carries no CreatedAt, so
        // no client SortValue — the list is displayed in the order the server returns.
        new() { Key = "createdAt", Label = "Created", Type = OdsSortType.Date, DefaultDir = OdsSortDirection.Desc },
    ];

    // ── Computed / overview ─────────────────────────────────────────────────────
    private int PhotoTotal => _entries.Sum(e => e.PhotoCount);
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _tagFilter.Count > 0 || _statusFilter.Count > 0;

    private IReadOnlyList<OdsBreakdownRow> TagRows =>
        [.. _tags.Where(t => t.Archived is null)
            .Select(t => new OdsBreakdownRow
            {
                Key = t.JournalTagId,
                Icon = "label",
                IconColor = "var(--tag-text)",
                Label = t.Name,
                Count = _entries.Count(e => e.TagIds.Contains(t.JournalTagId)),
            })
            .Where(r => (int)r.Count > 0)];

    private List<ExistingJournalTag> EntryTags(IReadOnlyList<Guid> ids) =>
        [.. ids.Select(id => _tagById.GetValueOrDefault(id)).Where(t => t is not null).Cast<ExistingJournalTag>()];

    // Compare on the stable ids; display the resolver's resolved name (always non-null — issue #316).
    private static string LastEdited(ExistingJournalEntry e) =>
        !string.IsNullOrWhiteSpace(e.UpdatedByUserId) && e.UpdatedByUserId != e.CreatedByUserId
            ? $"{e.UpdatedByName} · {e.UpdatedAt.ToLocalTime():MMM d, yyyy}"
            : e.UpdatedAt.ToLocalTime().ToString("MMM d, yyyy");

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await Task.WhenAll(LoadTags(), LoadContacts());
        await LoadEntries();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.JournalCreate);
        _canUpdate = user.HasPermission(PermissionClaims.JournalUpdate);
        _canDelete = user.HasPermission(PermissionClaims.JournalDelete);
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canUpdatePhotos = user.HasPermission(PermissionClaims.PhotosUpdate);
        _canDeletePhotos = user.HasPermission(PermissionClaims.PhotosDelete);
        _canCreatePhotoTags = user.HasPermission(PermissionClaims.PhotoTagsCreate);
        _canRenameFile = user.HasPermission(PermissionClaims.FilesUpdate);
    }

    private async Task LoadTags()
    {
        _tags = (await JournalTags.ListAllAsync()).ItemsOrToast(Snackbar, "journal tags");
        _tagById = _tags.ToDictionary(t => t.JournalTagId);
        _tagOptions =
        [
            .. _tags.Where(t => t.Archived is null)
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new OdsOption(t.JournalTagId.ToString(), t.Name)),
        ];
    }

    private async Task LoadContacts()
    {
        if (!_canReadContacts)
            return;
        var contacts = await ReferenceData.ContactsAsync();
        _contactById = contacts.ToDictionary(c => c.ContactId);
        _contactOptions =
        [
            .. contacts.Where(c => c.Archived is null)
                .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c =>
                {
                    var meta = OdsTypeRegistries.ContactTypeOf(c.Type.ToString());
                    return new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName) { Icon = meta.Icon, IconColor = meta.Color };
                }),
        ];
    }

    // The server's ArchivalStatus filter is Active XOR Archived — there is no "all". So the toolbar's
    // status multiselect maps to the request(s): none → Active (hide archived by default); one selected →
    // that partition; both selected → two requests merged (the only way to show active + archived together).
    private IReadOnlyList<string> StatusRequests()
    {
        var hasActive = _statusFilter.Contains(nameof(ArchivalStatus.Active));
        var hasArchived = _statusFilter.Contains(nameof(ArchivalStatus.Archived));
        if (hasActive && hasArchived) return [nameof(ArchivalStatus.Active), nameof(ArchivalStatus.Archived)];
        if (hasArchived) return [nameof(ArchivalStatus.Archived)];
        return [nameof(ArchivalStatus.Active)];
    }

    private async Task LoadEntries()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // genuinely empty set and would render the onboarding empty state after a 500. When the
        // Active + Archived pair is fetched, either leg failing makes the combined list untrustworthy.
        var dir = _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc";
        var requests = StatusRequests();
        if (requests.Count == 1)
        {
            var result = await Journal.ListAsync(_searchString, _tagFilter, null, requests[0], _sort.Key, dir);
            _entries = result.ItemsOrToast(Snackbar, "journal entries");
            _loadError = !result.IsSuccess;
        }
        else
        {
            var activeResult = await Journal.ListAsync(_searchString, _tagFilter, null, requests[0], _sort.Key, dir);
            var archivedResult = await Journal.ListAsync(_searchString, _tagFilter, null, requests[1], _sort.Key, dir);
            var active = activeResult.ItemsOrToast(Snackbar, "journal entries");
            var archived = archivedResult.ItemsOrToast(Snackbar, "archived journal entries");
            _entries = [.. active, .. archived];
            _loadError = !activeResult.IsSuccess || !archivedResult.IsSuccess;
        }

        _announce = _loadError ? "Couldn't load journal entries."
            : _entries.Count == 0 ? "No entries match your filters."
            : $"Showing {_entries.Count} {(_entries.Count == 1 ? "entry" : "entries")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<JournalPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(JournalPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _tagFilter = state.TagFilter ?? [];
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private JournalPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _searchString,
        TagFilter = [.. _tagFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnTagFilterChanged(IReadOnlyCollection<string> values) { _tagFilter = values ?? []; PersistPageState(); await LoadEntries(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadEntries(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadEntries(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _tagFilter = [];
        _statusFilter = [];
        PersistPageState();
        await LoadEntries();
    }

    private sealed class JournalPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TagFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Expand / detail load + hydration ─────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private async Task ToggleExpand(Guid id)
    {
        if (_expandedId == id)
        {
            _expandedId = null;
            return;
        }
        _expandedId = id;
        await EnsureDetail(id);
    }

    private async Task EnsureDetail(Guid id)
    {
        if (_details.ContainsKey(id))
            return;

        _announce = "Loading entry…";
        StateHasChanged();

        var entry = await Journal.GetAsync(id);
        if (entry is null)
            return;
        _details[id] = entry;
        await HydrateFilesAsync(entry);
        StateHasChanged();
    }

    // Fetch file metadata (name/size/type) for an entry's photos + attachments, so the gallery/table
    // can name them. A file the caller can't read (no files.read) or that was deleted resolves to null
    // and renders as an "Unavailable" placeholder (spec §11 / FE #7).
    private async Task HydrateFilesAsync(ExistingJournalEntry entry)
    {
        if (!_canReadFiles)
            return;
        var ids = entry.Photos.Select(p => p.FileId).Concat(entry.Attachments.Select(a => a.FileId)).Distinct();
        foreach (var fileId in ids)
        {
            if (_fileMeta.ContainsKey(fileId))
                continue;
            _fileMeta[fileId] = await Files.GetMetadataAsync(fileId);
        }
    }

    private IReadOnlyList<JournalPhotoGallery.Photo> GalleryPhotos(ExistingJournalEntry e) =>
        [.. e.Photos.OrderBy(p => p.Position).Select(p =>
        {
            var meta = _canReadFiles ? _fileMeta.GetValueOrDefault(p.FileId) : null;
            var name = meta?.FileName ?? "Photo";
            var src = meta is not null ? Files.ContentUrl(p.FileId) : null;
            return new JournalPhotoGallery.Photo(p.FileId.ToString(), name, src, meta is null);
        })];

    private IReadOnlyList<FileMetadataResponse> ResolvedAttachments(ExistingJournalEntry e) =>
        [.. e.Attachments
            .Select(a => _canReadFiles ? _fileMeta.GetValueOrDefault(a.FileId) : null)
            .Where(m => m is not null)
            .Cast<FileMetadataResponse>()];

    // Remember the activating gallery tile so focus returns to it when the lightbox closes (WCAG 2.4.3),
    // then open. The lightbox itself traps focus (MudFocusTrap) while open.
    // Clicking a gallery tile opens the shared PhotoDetailDialog over the entry's photos. The gallery
    // keys tiles by FileId; the entry's link order (Position) drives the dialog's prev/next, so the
    // summary list is built in that same order and the clicked tile's ordinal is the opening index.
    private async Task OpenPhotoDetail(ExistingJournalEntry e, JournalPhotoGallery.Photo photo)
    {
        _focusJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/overlay-focus.js");
        await _focusJs.InvokeVoidAsync("remember");

        await EnsurePhotoRefsAsync();

        var ordered = e.Photos.OrderBy(p => p.Position).ToList();
        // Hydrate all links in parallel — full detail gives favourite/archived/metadata; a link that
        // can't be fetched (no photos.read / since-deleted) falls back to a minimal summary so the
        // image + navigation still work.
        var fulls = await Task.WhenAll(ordered.Select(jp => Photos.GetAsync(jp.PhotoId)));
        _detailPhotos =
        [
            .. ordered.Select((jp, i) => fulls[i] is { } full
                ? PhotoMappers.ToSummary(full)
                : PhotoMappers.MinimalSummary(jp.PhotoId, jp.FileId)),
        ];
        _detailEntryId = e.JournalEntryId;
        var idx = ordered.FindIndex(p => p.FileId.ToString() == photo.Id);
        _detailIndex = idx < 0 ? 0 : idx;
    }

    private async Task ClosePhotoDetail()
    {
        _detailIndex = null;
        if (_focusJs is not null)
        {
            try { await _focusJs.InvokeVoidAsync("restore"); } catch (Exception) { /* best-effort focus return */ }
        }
    }

    // Favourite is the one action the dialog always exposes (its header heart); wire it through the
    // Photos API and reflect the new state in the open summary list. No-op without photos.update.
    private async Task ToggleFavouritePhotoAsync(Guid id)
    {
        if (!_canUpdatePhotos)
        {
            return;
        }

        var full = await Photos.GetAsync(id);
        if (full is null)
        {
            return;
        }

        var body = PhotoMappers.ToUpdate(full);
        body.Favourite = full.Favourited is null;
        if ((await Photos.UpdateAsync(id, body)).Toast(Snackbar, "Could not update favourite"))
        {
            var i = _detailPhotos.FindIndex(p => p.PhotoId == id);
            if (i >= 0)
            {
                _detailPhotos[i] = _detailPhotos[i] with { Favourited = body.Favourite ? DateTime.UtcNow : null };
            }
        }
    }

    // Edit opens the shared EditPhotoDialog (closing the detail dialog first, mirroring the library).
    private void OpenEditPhoto(Guid id)
    {
        _detailIndex = null;
        _editPhotoId = id;
    }

    private async Task OnPhotoSaved()
    {
        _editPhotoId = null;
        await RefreshOpenEntryAsync();
    }

    private async Task ArchivePhotoAsync(Guid id)
    {
        if (!_canUpdatePhotos)
        {
            return;
        }

        var full = await Photos.GetAsync(id);
        if (full is null)
        {
            return;
        }

        var body = PhotoMappers.ToUpdate(full);
        body.Archived = full.Archived is null;
        if ((await Photos.UpdateAsync(id, body)).Toast(Snackbar, "Could not archive photo",
                body.Archived ? "Photo archived." : "Photo unarchived."))
        {
            _detailIndex = null;
            await RefreshOpenEntryAsync();
        }
    }

    private async Task DeletePhotoAsync(Guid id)
    {
        if (!_canDeletePhotos)
        {
            return;
        }

        if ((await Photos.DeleteAsync(id)).Toast(Snackbar, "Delete failed", "Photo deleted."))
        {
            _detailIndex = null;
            await RefreshOpenEntryAsync();
        }
    }

    // Re-fetch the open entry so the gallery + counts reflect an edit/archive/delete. Clears the entry's
    // cached file metadata first so a rename shows through (HydrateFilesAsync skips ids already cached).
    private async Task RefreshOpenEntryAsync()
    {
        if (_detailEntryId is not { } eid)
        {
            return;
        }

        if (_details.TryGetValue(eid, out var current))
        {
            foreach (var fid in current.Photos.Select(p => p.FileId))
            {
                _fileMeta.Remove(fid);
            }
        }

        var entry = await Journal.GetAsync(eid);
        if (entry is null)
        {
            _details.Remove(eid);
            return;
        }

        _details[eid] = entry;
        await HydrateFilesAsync(entry);
        StateHasChanged();
    }

    private string PhotoTagName(Guid id) => _photoTagNames.GetValueOrDefault(id, "—");
    private string PhotoAlbumName(Guid id) => _photoAlbumNames.GetValueOrDefault(id, "—");
    private string PhotoPersonName(Guid id) => _contactById.GetValueOrDefault(id)?.ResolvedDisplayName ?? "—";

    // Tag/album names + edit-dialog option lists for the detail rail — loaded once, lazily, on first
    // open. People resolve from the contacts already loaded for the entry chips.
    private async Task EnsurePhotoRefsAsync()
    {
        if (_photoRefsLoaded)
        {
            return;
        }

        var tags = (await PhotoTags.ListAllAsync()).ItemsOrToast(Snackbar, "photo tags");
        _photoTagNames = tags.ToDictionary(t => t.PhotoTagId, t => t.Name);
        _photoTagOptions = [.. tags.Select(t => new OdsOption(t.PhotoTagId.ToString(), t.Name))];

        var albums = (await Albums.ListAllAsync()).ItemsOrToast(Snackbar, "albums");
        _photoAlbumNames = albums.ToDictionary(a => a.PhotoAlbumId, a => a.Name);
        _photoAlbumOptions = [.. albums.Select(a => new OdsOption(a.PhotoAlbumId.ToString(), a.Name))];

        // Active Person contacts for the people picker — reuse the entry's already-loaded set.
        _photoPeopleOptions =
        [
            .. _contactById.Values
                .Where(c => c.Type == ContactType.Person && c.Archived is null)
                .Select(c => new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName)),
        ];

        // Set only after the fetches succeed, so a transient failure retries on the next open.
        _photoRefsLoaded = true;
    }

    // ── Contact chip helper (rendered inline in the markup) ─────────────────

    // ── Edit (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingJournalEntry? _editEntry;
    private Guid _editEntryKey;
    private bool _editEntryOpen;

    private async Task EditClicked(JournalEntrySummary e)
    {
        if (!_canUpdate) return;
        await EnsureDetail(e.JournalEntryId);
        if (!_details.TryGetValue(e.JournalEntryId, out var detail))
            return;

        _editEntry = detail;
        _editEntryKey = Guid.NewGuid();
        _editEntryOpen = true;
    }

    private async Task OnEntryEdited()
    {
        if (_editEntry is not null)
        {
            _announce = "Entry updated.";
            await ReloadEntry(_editEntry.JournalEntryId);
        }
    }

    // ── Archive / unarchive (PUT re-projecting the loaded entry) ───────────────────
    private async Task ToggleArchive(JournalEntrySummary e)
    {
        if (!_canUpdate) return;
        await EnsureDetail(e.JournalEntryId);
        if (!_details.TryGetValue(e.JournalEntryId, out var detail)) return;

        var archiving = detail.Archived is null;
        var update = JournalWrite.FromDetail(detail, archiving);
        if ((await Journal.UpdateAsync(e.JournalEntryId, update)).Toast(Snackbar,
                archiving ? "Unable to archive entry" : "Unable to unarchive entry",
                archiving ? "Entry archived." : "Entry unarchived."))
        {
            _announce = archiving ? "Entry archived." : "Entry unarchived.";
            await ReloadEntry(e.JournalEntryId);
        }
    }

    private async Task ConfirmDelete(JournalEntrySummary e)
    {
        if (!_canDelete) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete entry",
            $"Permanently delete '{e.Title}'? This cannot be undone. Attached files stay in your files.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed == true && (await Journal.DeleteAsync(e.JournalEntryId)).Toast(Snackbar, "Delete failed", "Entry deleted."))
        {
            _announce = "Entry deleted.";
            _details.Remove(e.JournalEntryId);
            if (_expandedId == e.JournalEntryId) _expandedId = null;
            await LoadEntries();
        }
    }

    private async Task ReloadEntry(Guid id)
    {
        _details.Remove(id);
        await EnsureDetail(id);
        await LoadEntries();
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Entry ID copied.");

    // ── Create ──────────────────────────────────────────────────────────────────
    private Guid _createKey;
    private bool _createOpen;

    private void AddClicked()
    {
        if (!_canCreate) return;
        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private Task OnEntryCreated() => LoadEntries();

    // ── VJOURNAL export / import (issue #339) ────────────────────────────────────
    private bool _canImport => _canCreate && _canUpdate;
    private bool _importOpen;
    private bool _exporting;

    private void OpenImport() => _importOpen = true;

    // The export endpoint issues one GET, so the Active/Archived toggles collapse to a single status
    // param (§3 / §30): both selected → all statuses (omit); exactly one → that value; neither → Active
    // (the page's default view — "neither selected" is not "show everything").
    private string? ExportStatusParam()
    {
        var hasActive = _statusFilter.Contains(nameof(ArchivalStatus.Active));
        var hasArchived = _statusFilter.Contains(nameof(ArchivalStatus.Archived));
        if (hasActive && hasArchived) return null;
        if (hasArchived) return nameof(ArchivalStatus.Archived);
        return nameof(ArchivalStatus.Active);
    }

    // "Export all" = every entry, all statuses (no filters). "Export filtered" = the current search/tag
    // set plus the collapsed status. A guard prevents a re-entrant double-click; a failed export toasts
    // the server's reason (forbidden / over-cap / generic) rather than downloading a fake file.
    private async Task ExportAsync(bool filtered)
    {
        if (_exporting) return;
        _exporting = true;
        _announce = "Exporting journal entries…";
        StateHasChanged();
        try
        {
            var result = filtered
                ? await JournalIcs.ExportAsync(_searchString, _tagFilter, ExportStatusParam())
                : await JournalIcs.ExportAsync();
            if (result.OrToast(Snackbar, "Unable to export journal entries") is { } file)
            {
                await DownloadIcsAsync(file);
            }
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task ExportEntryAsync(JournalEntrySummary entry)
    {
        if (_exporting) return;
        _exporting = true;
        try
        {
            var result = await JournalIcs.ExportOneAsync(entry.JournalEntryId);
            if (result.OrToast(Snackbar, "Unable to export the journal entry") is { } file)
            {
                await DownloadIcsAsync(file);
            }
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task DownloadIcsAsync(ApiFile file)
    {
        await JS.InvokeVoidAsync("downloadFileFromBytes", file.Bytes, file.FileName, "text/calendar");
        Snackbar.Add($"Exported {file.FileName}", Severity.Success);
    }

    // After an import that created/updated rows: refresh the list + overview counts, then actively
    // re-fetch a still-expanded row's detail and file metadata — clearing it alone would leave the open
    // row spinning forever, since expansion only re-fetches on toggle (§3 #6).
    private async Task OnEntriesImported()
    {
        if (_expandedId is { } eid && _details.TryGetValue(eid, out var current))
        {
            foreach (var fileId in current.Photos.Select(p => p.FileId))
            {
                _fileMeta.Remove(fileId);
            }

            _details.Remove(eid);
        }

        await LoadEntries();

        if (_expandedId is { } openId)
        {
            await EnsureDetail(openId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusJs is not null)
        {
            try { await _focusJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
