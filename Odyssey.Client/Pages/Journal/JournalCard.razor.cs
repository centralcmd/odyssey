using Odyssey.ApiClient.Resources;
using Odyssey.ApiClient;
using Odyssey.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Pages.Weather;

namespace Odyssey.Client.Pages.Journal;

public partial class JournalCard
{
    private OdsConfirm _confirm = default!;

    // ── Data ────────────────────────────────────────────────────────────────
    private List<JournalEntrySummary> _entries = [];                       // server-filtered display list
    private readonly Dictionary<Guid, ExistingJournalEntry> _details = new();
    private readonly Dictionary<Guid, FileMetadataResponse?> _fileMeta = new(); // hydrated file names/sizes

    private List<ExistingJournalTag> _tags = [];
    private Dictionary<Guid, ExistingJournalTag> _tagById = new();
    private IReadOnlyList<OdsOption> _tagOptions = [];
    private IReadOnlyCollection<string> _archivedTagNames = [];

    private Dictionary<Guid, ExistingContact> _contactById = new();
    private IReadOnlyList<OdsOption> _contactOptions = [];

    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = "";

    // Hydrated attachment metadata per entry, filled when a card's footer count is opened. A missing
    // key means "not fetched yet" — the card shows its own loading row — and is what keeps the page
    // from hydrating every attachment on every card.
    private readonly Dictionary<Guid, IReadOnlyList<FileMetadataResponse>> _attachments = new();

    // The shared PhotoViewer over the open entry's photos (null index = closed), and the library state — heart,
    // Archived badge — of every shown entry's photos, which the link DTO does not carry.
    private PhotoViewer? _viewer;
    private IReadOnlyList<PhotoSummary> _viewerPhotos = [];
    private int? _viewerIndex;
    private readonly LinkedPhotoStates _photoStates = new();
    private bool _canReadPhotos;
    private bool _canUpdatePhotos;

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadFiles;
    private PhotoImageAccess _imageAccess = new(false);
    private bool _canReadContacts;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "journal-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _contactFilter = [];
    private IReadOnlyCollection<string> _mediaFilter = [];
    private DateTime? _from;
    private DateTime? _to;
    private IReadOnlyCollection<string> _statusFilter = [];

    // Attachment presence. The two are AND-ed — picking both wants entries carrying both — and both are
    // server-side predicates (hasPhotos / hasFiles), like every other filter on this page. That is what
    // lets the ICS export carry them too, so "Export filtered" and the list describe the same set.
    private const string MediaPhotos = "photos";
    private const string MediaFiles = "files";

    // The glyphs the entry card counts photos and files under.
    internal static readonly IReadOnlyList<OdsOption> _mediaOptions =
    [
        new(MediaPhotos, "Has photos") { Icon = "photo_library", IconColor = "var(--sea-400)" },
        new(MediaFiles, "Has files") { Icon = "attach_file", IconColor = "var(--mud-palette-text-secondary)" },
    ];

    // Status: single "Active / Archived" multiselect. Empty = hide archived (Active only); both = all.
    private static readonly IReadOnlyList<OdsOption> _statusOptions = ArchivalStatusOptions.Options;

    // ── Sort (server-side; keys map 1:1 to JournalEntrySortBy) ───────────────────
    private static readonly OdsTableSort DefaultSort = new("entryDate", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<JournalEntrySummary>> _sortFields =
    [
        new() { Key = "entryDate", Label = "Entry date", Type = OdsSortType.Date, DefaultDir = OdsSortDirection.Desc, SortValue = e => e.EntryDate },
        new() { Key = "title", Label = "Title", Type = OdsSortType.Text, SortValue = e => e.Title.ToLowerInvariant() },
        new() { Key = "createdAt", Label = "Created", Type = OdsSortType.Date, DefaultDir = OdsSortDirection.Desc, SortValue = e => e.CreatedAt },
    ];

    // ── Computed / overview ─────────────────────────────────────────────────────
    private int PhotoTotal => _entries.Sum(e => e.Photos.Count);
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _tagFilter.Count > 0
        || _statusFilter.Count > 0 || _contactFilter.Count > 0 || _mediaFilter.Count > 0
        || _from is not null || _to is not null;

    private IReadOnlyList<OdsBreakdownRow> TagRows => JournalOverviews.JournalTagRows(_tags, _entries);

    private IReadOnlyList<OdsTagChips.Tag> TagChips(IReadOnlyList<Guid> ids) =>
        OdsTagChips.Tag.For(ids, _tagById, t => t.Name, t => t.Icon, t => t.Color);

    // A contact link the caller cannot resolve — deleted, or no contacts.read — keeps its place and
    // renders as "Unavailable" rather than leaking the id into the chip row.
    private IReadOnlyList<JournalEntryCard.ContactLink> EntryContacts(JournalEntrySummary e) =>
        [.. e.ContactIds.Select(id =>
        {
            var c = _canReadContacts ? _contactById.GetValueOrDefault(id) : null;
            return new JournalEntryCard.ContactLink(
                id, c?.ResolvedDisplayName, c?.Type.ToString(), c?.Archived is not null);
        })];

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    // ── Weather (issue #363 §3.6) ─────────────────────────────────────────────
    // Weather follows the RENDERED window, not the loaded list: the list loads every entry, the infinite list
    // renders a batch, and only the rendered ids are asked about. Keyed by entry id, so a filter, sort or batch
    // change does not clear it — an entry already answered is not asked about again.
    private WeatherBatchLoader<JournalEntryWeatherItem>? _weatherLoader;
    private IReadOnlyList<Guid> _shownIds = [];
    private IJSObjectReference? _focusReturnJs;

    private WeatherBatchLoader<JournalEntryWeatherItem> WeatherLoader => _weatherLoader ??= new(
        WeatherBatchReads.JournalEntries(WeatherApi), item => item.JournalEntryId,
        () => InvokeAsync(StateHasChanged), Log, "journal", TimeSpan.FromMilliseconds(250));

    private void OnShownChanged(IReadOnlyList<JournalEntrySummary> shown)
    {
        _shownIds = [.. shown.Select(e => e.JournalEntryId)];
        _ = WeatherLoader.ShowAsync(_shownIds);
        _ = HydratePhotoStatesAsync(shown);
    }

    // The heart and the Archived badge need each shown photo's library state, which the link DTO does not carry:
    // read once per photo, for the entries on screen only, and redraw when any arrived (photos.read).
    private async Task HydratePhotoStatesAsync(IReadOnlyList<JournalEntrySummary> shown)
    {
        if (!_canReadPhotos)
        {
            return;
        }

        try
        {
            if (await _photoStates.EnsureAsync(Photos, shown.SelectMany(e => e.Photos).Select(p => p.PhotoId)))
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            // Supplementary: without the state the tiles simply show no heart and no badge.
            Log.LogDebug(ex, "Could not read the journal photos' library state.");
        }
    }

    // A sign-out, a different user or a changed claim set: start over from nothing.
    private async void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        try
        {
            var user = (await state).User;
            WeatherLoader.Reset(user.HasPermission(PermissionClaims.WeatherRead));
            _photoStates.Clear();
            await InvokeAsync(StateHasChanged);
            await WeatherLoader.ShowAsync(_shownIds);
        }
        catch (Exception)
        {
            // The page may already be gone; weather is supplementary and never worth an unhandled exception.
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        AuthenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;

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
        // The gallery's thumbnail needs photos.read, read once here rather than per tile (issue #365 B5).
        _imageAccess = PhotoImageAccess.From(user);
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canReadPhotos = user.HasPermission(PermissionClaims.PhotosRead);
        _canUpdatePhotos = user.HasPermission(PermissionClaims.PhotosUpdate);
        WeatherLoader.Reset(user.HasPermission(PermissionClaims.WeatherRead));
    }

    private async Task LoadTags()
    {
        _tags = (await JournalTags.ListAllAsync()).ItemsOrToast(Snackbar, "journal tags");
        _tagById = _tags.ToDictionary(t => t.JournalTagId);
        _archivedTagNames = [.. _tags.Where(t => t.Archived is not null).Select(t => t.Name)];
        _tagOptions =
        [
            .. _tags.Where(t => t.Archived is null)
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(JournalOverviews.JournalTagOption),
        ];
    }

    private async Task LoadContacts()
    {
        if (!_canReadContacts)
        {
            return;
        }

        var contacts = await ReferenceData.ContactsAsync();
        _contactById = contacts.ToDictionary(c => c.ContactId);
        _contactOptions = OdsContactOptions.Active(contacts);
    }

    // The server's ArchivalStatus filter is Active XOR Archived — there is no "all". So the toolbar's
    // status multiselect maps to the request(s): none → Active (hide archived by default); one selected →
    // that partition; both selected → two requests merged (the only way to show active + archived together).
    private IReadOnlyList<string> StatusRequests()
    {
        var hasActive = _statusFilter.Contains(nameof(ArchivalStatus.Active));
        var hasArchived = _statusFilter.Contains(nameof(ArchivalStatus.Archived));
        if (hasActive && hasArchived)
        {
            return [nameof(ArchivalStatus.Active), nameof(ArchivalStatus.Archived)];
        }

        if (hasArchived)
        {
            return [nameof(ArchivalStatus.Archived)];
        }

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
        // The Active + Archived pair is independent, so both legs are in flight at once rather than
        // paying two round-trips back to back.
        var response = await _listLoader.RunAsync(ct => Task.WhenAll(requests.Select(status =>
            Journal.ListAsync(_searchString, _tagFilter, _contactFilter, status, _sort.Key, dir, _from?.Date, _to?.Date, WantPhotos, WantFiles, ct))));
        if (response.IsSuperseded)
        {
            return;
        }

        var results = response.Value;
        if (results.Length == 1)
        {
            var result = results[0];
            _entries = result.ItemsOrToast(Snackbar, "journal entries");
            _loadError = !result.IsSuccess;
        }
        else
        {
            var (activeResult, archivedResult) = (results[0], results[1]);
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

    private bool? WantPhotos => JournalMediaFilter.Want(_mediaFilter, MediaPhotos);

    private bool? WantFiles => JournalMediaFilter.Want(_mediaFilter, MediaFiles);

    // The range is inclusive on both ends and both sides of the comparison are whole days: the picker
    // hands back a date, an entry's EntryDate is the picked day stored at midnight, and the server's
    // bounds are >= from / <= to. So the dates go over as-is — .Date only strips a time a future picker
    // might carry, which would otherwise exclude the "to" day's own entries.

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<JournalPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(JournalPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _tagFilter = state.TagFilter ?? [];
        _contactFilter = state.ContactFilter ?? [];
        _mediaFilter = _mediaOptions.KnownValues(state.MediaFilter);
        _from = state.From;
        _to = state.To;
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
        ContactFilter = [.. _contactFilter],
        MediaFilter = [.. _mediaFilter],
        From = _from,
        To = _to,
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
    private async Task OnContactFilterChanged(IReadOnlyCollection<string> values) { _contactFilter = values ?? []; PersistPageState(); await LoadEntries(); }
    private async Task OnMediaFilterChanged(IReadOnlyCollection<string> values) { _mediaFilter = values ?? []; PersistPageState(); await LoadEntries(); }
    private async Task OnFromChanged(DateTime? value) { _from = value; PersistPageState(); await LoadEntries(); }
    private async Task OnToChanged(DateTime? value) { _to = value; PersistPageState(); await LoadEntries(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadEntries(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadEntries(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _tagFilter = [];
        _contactFilter = [];
        _mediaFilter = [];
        _from = null;
        _to = null;
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
        public List<string> ContactFilter { get; set; } = [];
        public List<string> MediaFilter { get; set; } = [];
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Detail on demand + file hydration ────────────────────────────────────────
    // The card renders from the list projection, so a detail fetch is no longer part of DISPLAY: it is
    // fetched only for the writes that must re-project the whole entry (edit, archive, detach a file).
    private async Task<ExistingJournalEntry?> EnsureDetail(Guid id)
    {
        if (_details.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var entry = await Journal.GetAsync(id);
        if (entry is not null)
        {
            _details[id] = entry;
        }

        return entry;
    }

    // The attachment list behind the card's footer count. The summary carries only the count — the
    // file ids live on the detail — so opening the disclosure is what pays for both fetches, on the one
    // card the reader actually asked about.
    private async Task LoadAttachmentsAsync(JournalEntrySummary e)
    {
        if (_attachments.ContainsKey(e.JournalEntryId))
        {
            return;
        }

        var detail = await EnsureDetail(e.JournalEntryId);
        if (detail is null)
        {
            return;
        }

        var ids = detail.Attachments.Select(a => a.FileId).Distinct().ToList();
        var missing = _canReadFiles ? ids.Where(id => !_fileMeta.ContainsKey(id)).ToList() : [];
        if (missing.Count > 0)
        {
            var metas = await Task.WhenAll(missing.Select(id => Files.GetMetadataAsync(id)));
            for (var i = 0; i < missing.Count; i++)
            {
                _fileMeta[missing[i]] = metas[i];
            }
        }

        _attachments[e.JournalEntryId] = ResolvedAttachments(detail);
        StateHasChanged();
    }

    // Detaching a file edits the ENTRY, never the file: the row stays in the files store.
    private async Task RemoveAttachmentAsync(JournalEntrySummary e, Guid fileId)
    {
        if (!_canUpdate)
        {
            return;
        }

        var detail = await EnsureDetail(e.JournalEntryId);
        if (detail is null)
        {
            return;
        }

        var update = JournalWrite.WithoutAttachment(detail, fileId);
        if ((await Journal.UpdateAsync(e.JournalEntryId, update)).Toast(
                Snackbar, "Unable to remove the file", "File removed from the entry."))
        {
            _announce = "File removed from the entry.";
            _attachments.Remove(e.JournalEntryId);
            await ReloadEntry(e.JournalEntryId);
            await LoadAttachmentsAsync(e);
        }
    }

    private IReadOnlyList<JournalEntryCard.Photo> GalleryPhotos(JournalEntrySummary e) =>
        GalleryPhotos(e.Photos, _imageAccess, Photos, _photoStates);

    /// <summary>
    /// The tiles' sources (issue #365 B5, issue #428): both the Small thumbnail and the original are photo data
    /// under <c>photos.read</c>, and the label travels on the link. "No access" is the case with neither source. The
    /// heart and the "Archived" badge come from <paramref name="states"/> once a photo's library state is read.
    /// Static so the rule is tested without rendering the page.
    /// </summary>
    internal static IReadOnlyList<JournalEntryCard.Photo> GalleryPhotos(
        IEnumerable<JournalEntryPhotoDto> photos, PhotoImageAccess access, IPhotosApiClient photoClient,
        LinkedPhotoStates? states = null) =>
        [.. photos.OrderBy(p => p.Position).Select((p, i) =>
        {
            var image = PhotoImageSources.For(p.PhotoId, p.ThumbnailsReady, ThumbnailSize.Small, access, photoClient);
            var state = states?.Get(p.PhotoId);
            return new JournalEntryCard.Photo(
                p.PhotoId, PhotoMappers.Label(p.Title, p.FileName, $"Photo {i + 1}"), image, Unavailable: image.IsEmpty,
                Favourite: state is null ? null : state.Favourited is not null, Archived: state?.Archived is not null);
        })];

    private IReadOnlyList<FileMetadataResponse> ResolvedAttachments(ExistingJournalEntry e) =>
        [.. e.Attachments
            .Select(a => _canReadFiles ? _fileMeta.GetValueOrDefault(a.FileId) : null)
            .Where(m => m is not null)
            .Cast<FileMetadataResponse>()];

    // A tile opens the shared PhotoViewer over its entry's photos, in the entry's link order (Position), so ← / →
    // walk the card's tiles. A photo whose state could not be read opens as a minimal summary (image + navigation).
    // Focus returns to the tile on close: the viewer's dialog captures its opener.
    private async Task OpenPhoto(JournalEntrySummary e, Guid photoId)
    {
        var ordered = e.Photos.OrderBy(p => p.Position).Select(p => p.PhotoId).ToList();
        if (_canReadPhotos)
        {
            await _photoStates.EnsureAsync(Photos, ordered);
        }

        _viewerPhotos = _photoStates.Summaries(ordered);
        var index = ordered.IndexOf(photoId);
        _viewerIndex = index < 0 ? 0 : index;
    }

    // The tile heart favourites through the viewer, so a tile and the open photo do it the same way.
    private EventCallback<Guid> FavouriteCallback =>
        _canUpdatePhotos
            ? EventCallback.Factory.Create<Guid>(this, id => _viewer?.ToggleFavouriteAsync(id) ?? Task.CompletedTask)
            : default;

    // Favourite is patched in place; an archive re-reads the photo; an edit (a new title is on the link) or a
    // delete (the link is gone) reloads the list.
    internal async Task OnViewerChanged(PhotoViewerChange change)
    {
        switch (change.Kind)
        {
            case PhotoViewerChangeKind.Favourite:
                _photoStates.ToggleFavourite(change.PhotoId);
                break;
            case PhotoViewerChangeKind.Archive:
                await _photoStates.RefreshAsync(Photos, change.PhotoId);
                break;
            case PhotoViewerChangeKind.Edit:
                await _photoStates.RefreshAsync(Photos, change.PhotoId);
                _details.Clear();
                await LoadEntries();
                break;
            case PhotoViewerChangeKind.Delete:
                _photoStates.Forget(change.PhotoId);
                _details.Clear();
                await LoadEntries();
                break;
        }
    }

    // ── Contact chip helper (rendered inline in the markup) ─────────────────

    // ── Edit (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingJournalEntry? _editEntry;
    private Guid _editEntryKey;
    private bool _editEntryOpen;

    private async Task EditClicked(JournalEntrySummary e)
    {
        if (!_canUpdate)
        {
            return;
        }

        if (await EnsureDetail(e.JournalEntryId) is not { } detail)
        {
            return;
        }

        _editEntry = detail;
        _editEntryKey = Guid.NewGuid();
        _editEntryOpen = true;
    }

    private async Task OnEntryEdited()
    {
        if (_editEntry is not null)
        {
            _announce = "Entry updated.";
            await Task.WhenAll(LoadTags(), LoadContacts());
            var id = _editEntry.JournalEntryId;
            await ReloadEntry(id);
            // Its date or place may have moved: ask again for this entry alone.
            _ = WeatherLoader.RefreshAsync(id);
        }
    }

    // ── Archive / unarchive (PUT re-projecting the loaded entry) ───────────────────
    private async Task ToggleArchive(JournalEntrySummary e)
    {
        if (!_canUpdate)
        {
            return;
        }

        if (await EnsureDetail(e.JournalEntryId) is not { } detail)
        {
            return;
        }

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

    /// <summary>The delete confirmation's body: attached files and the entry's place both stay (issue #383 §3.9).</summary>
    internal static string DeleteMessage(string title) =>
        $"Permanently delete '{title}'? This cannot be undone. Attached files stay in your files. {LocationVisuals.RecordDeleteSentence}";

    private async Task ConfirmDelete(JournalEntrySummary e)
    {
        if (!_canDelete)
        {
            return;
        }

        var confirmed = await _confirm.AskAsync("Delete entry", DeleteMessage(e.Title), "Delete");

        if (confirmed && (await Journal.DeleteAsync(e.JournalEntryId)).Toast(Snackbar, "Delete failed", "Entry deleted."))
        {
            _announce = "Entry deleted.";
            _details.Remove(e.JournalEntryId);
            _attachments.Remove(e.JournalEntryId);
            WeatherLoader.Forget(e.JournalEntryId);
            await LoadEntries();
            await FocusPageHeadingAsync();
        }
    }

    private async Task ReloadEntry(Guid id)
    {
        _details.Remove(id);
        _attachments.Remove(id);
        await LoadEntries();
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), OdsCopied.Id("Entry"));

    // ── Create ──────────────────────────────────────────────────────────────────
    private Guid _createKey;
    private bool _createOpen;

    private void AddClicked()
    {
        if (!_canCreate)
        {
            return;
        }

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    // The dialog may have minted tags or contacts inline; reload them so the new entry's chips resolve.
    internal async Task OnEntryCreated()
    {
        await Task.WhenAll(LoadTags(), LoadContacts());
        await LoadEntries();
    }

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
        if (hasActive && hasArchived)
        {
            return null;
        }

        if (hasArchived)
        {
            return nameof(ArchivalStatus.Archived);
        }

        return nameof(ArchivalStatus.Active);
    }

    // "Export all" = every entry, all statuses (no filters). "Export filtered" = the current search/tag
    // set plus the collapsed status. A guard prevents a re-entrant double-click; a refused export toasts
    // the server's reason (forbidden / over-cap / generic) rather than downloading a fake file.
    private async Task ExportAsync(bool filtered)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        _announce = "Preparing journal export…";
        StateHasChanged();
        try
        {
            // Every filter is passed, so "Export filtered" and the list describe the same set. The
            // export endpoint binds the same query model as the list, which is what makes that hold
            // without a second definition of "filtered" living here.
            var summary = filtered
                ? (await JournalIcs.SummarizeExportAsync(_searchString, _tagFilter, ExportStatusParam(), _contactFilter, _from?.Date, _to?.Date, WantPhotos, WantFiles))
                    .OrToast(Snackbar, "Unable to export journal entries")
                : (await JournalIcs.SummarizeExportAsync()).OrToast(Snackbar, "Unable to export journal entries");
            // The snackbar container is a role="status" region and speaks the outcome itself, success or refusal, so the
            // page's announcer only covered the wait and is cleared rather than made to say it a second time.
            _announce = "";
            if (summary is null)
            {
                return;
            }

            await NativeTransfer.DownloadAsync(filtered
                ? JournalIcs.ExportUrl(_searchString, _tagFilter, ExportStatusParam(), _contactFilter, _from?.Date, _to?.Date, WantPhotos, WantFiles)
                : JournalIcs.ExportUrl());
            Snackbar.Add(
                $"Exporting {Plural(summary.EntryCount, "journal entry", "journal entries")}{CarriedSuffix(summary)} — your browser is downloading the file.",
                Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task ExportEntryAsync(JournalEntrySummary entry)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            var summary = (await JournalIcs.SummarizeExportOneAsync(entry.JournalEntryId)).OrToast(Snackbar, "Unable to export the journal entry");
            if (summary is null)
            {
                return;
            }

            await NativeTransfer.DownloadAsync(JournalIcs.ExportOneUrl(entry.JournalEntryId));
            var title = string.IsNullOrWhiteSpace(entry.Title) ? "the journal entry" : entry.Title;
            Snackbar.Add($"Exporting {title}{CarriedSuffix(summary)} — your browser is downloading the file.", Severity.Success);
        }
        finally
        {
            _exporting = false;
        }
    }

    // Every export carries its files and photos inline, so it can be far larger than the WASM heap: the summary runs
    // the server's checks first (caps, files.read / photos.read) and a refusal is a toast, then the browser downloads
    // the file straight to disk. A failure after that point shows in the browser's own download list.
    private static string Plural(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";

    private static string CarriedSuffix(JournalEntryIcsExportSummary summary)
    {
        var parts = new List<string>();
        if (summary.AttachmentCount > 0)
        {
            parts.Add(Plural(summary.AttachmentCount, "attached file", "attached files"));
        }

        if (summary.PhotoCount > 0)
        {
            parts.Add(Plural(summary.PhotoCount, "photo", "photos"));
        }

        return parts.Count == 0 ? "" : $" with {string.Join(" and ", parts)} ({ByteSize.Format(summary.AttachmentBytes)})";
    }

    // After an import that created/updated rows: drop everything cached per entry and reload. The card
    // renders from the list projection, so one reload refreshes every card — but the cached file
    // metadata has to go with it, or a re-imported photo keeps its old name.
    private async Task OnEntriesImported()
    {
        _details.Clear();
        _attachments.Clear();
        _fileMeta.Clear();
        await LoadEntries();
    }

    // ── Row action menu ────────────────────────────────────────────────────────
    // The card's own ⋯ menu, built from the caller's claims. It is an OdsMenu item list rather than
    // inline MudMenuItems because OdsRecordCard's Actions slot takes one control, and the shared menu
    // is what gives the Copy ID row its trailing affordance and a danger item its tint.
    private List<OdsMenuItem> BuildActions(JournalEntrySummary e)
    {
        var archived = e.Archived is not null;
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit entry",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(e)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "event_note",
            Label = "Export VJOURNAL",
            OnClick = EventCallback.Factory.Create(this, () => ExportEntryAsync(e)),
        });
        items.Add(OdsMenuItem.CopyId(EventCallback.Factory.Create(this, () => CopyId(e.JournalEntryId))));

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(e)),
            });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(e)),
            });
        }

        return items;
    }

    // Tag and contact links are no longer detached from the card: the card shows them as chips and the
    // edit dialog owns the picker that adds and removes them. The focus-return dance that used to land
    // a caller on a neighbouring tile went with them — a dialog returns focus to its own trigger.

    // The card is gone, and with it its ⋯ button and anything inside it that held focus: land on the page's h1
    // rather than letting focus fall to <body> (issue #363 §3.6 step 6).
    private async Task FocusPageHeadingAsync()
    {
        try
        {
            _focusReturnJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            await _focusReturnJs.InvokeVoidAsync("focusFirst", "h1");
        }
        catch (Exception)
        {
            // Best-effort: the deletion is already announced through the page's live region.
        }
    }

    public async ValueTask DisposeAsync()
    {
        AuthenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _weatherLoader?.Dispose();
        _listLoader.Dispose();
        if (_focusReturnJs is not null)
        {
            try
            { await _focusReturnJs.DisposeAsync(); }
            catch (Exception) { /* JS already gone on teardown */ }
        }
    }
}
