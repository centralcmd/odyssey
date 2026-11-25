using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Library;

public partial class BooksPage
{
    [Inject] private IBooksApiClient Books { get; set; } = default!;
    [Inject] private ITagsApiClient<ExistingBookTag> BookTags { get; set; } = default!;
    [Inject] private IPhotosApiClient PhotosApi { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private IPageStateService PageState { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;

    // ── Data ────────────────────────────────────────────────────────────────────────────────
    private List<BookSummary> _books = [];

    /// <summary>The whole catalogue, unfiltered — what the overview and the sub-line describe.</summary>
    private List<BookSummary> _all = [];

    private IReadOnlyList<ExistingBookTag> _tags = [];
    private Dictionary<Guid, ExistingBookTag> _tagById = [];
    private IReadOnlyList<OdsOption> _tagOptions = [];
    private IReadOnlyList<OdsOption> _authorOptions = [];

    /// <summary>Saved books that no longer match the filters: they keep their place until the list reloads.</summary>
    private readonly HashSet<Guid> _offFilter = [];

    private Guid? _flashId;
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ────────────────────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = string.Empty;
    private Guid? _expandedId;

    private readonly Dictionary<Guid, Guid> _contributorTokens = new();
    private readonly Dictionary<Guid, Guid> _photosTokens = new();
    private readonly Dictionary<Guid, Guid> _filesTokens = new();

    // ── Persisted page state ────────────────────────────────────────────────────────────────
    private const string PageStateKey = "books-page";
    private const string Any = "any";
    private const string Yes = "yes";
    private const string No = "no";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyCollection<string> _formatFilter = [];
    private IReadOnlyCollection<string> _tagFilter = [];
    private string? _authorFilter;
    private string _coverFilter = Any;
    private string _filesFilter = Any;

    private static readonly OdsTableSort DefaultSort = new("title", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    /// <summary>Sort keys → <see cref="BookSortBy"/>. Author is offered only with <c>contacts.read</c> (the server refuses it without).</summary>
    private static readonly OdsSortField<BookSummary> TitleSort = new() { Key = "title", Label = "Title", Type = OdsSortType.Text };
    private static readonly OdsSortField<BookSummary> AuthorSort = new() { Key = "author", Label = "Author", Type = OdsSortType.Text };
    private static readonly OdsSortField<BookSummary> YearSort = new() { Key = "year", Label = "Year", Type = OdsSortType.Number };
    private static readonly OdsSortField<BookSummary> CreatedSort = new() { Key = "created", Label = "Recently added", Type = OdsSortType.Date };
    private static readonly OdsSortField<BookSummary> UpdatedSort = new() { Key = "updated", Label = "Recently updated", Type = OdsSortType.Date };

    private IReadOnlyList<OdsSortField<BookSummary>> SortFields =>
        _canReadContacts ? [TitleSort, AuthorSort, YearSort, CreatedSort, UpdatedSort] : [TitleSort, YearSort, CreatedSort, UpdatedSort];

    internal static string SortByOf(string key) => key switch
    {
        "author" => nameof(BookSortBy.Author),
        "year" => nameof(BookSortBy.Year),
        "created" => nameof(BookSortBy.CreatedAt),
        "updated" => nameof(BookSortBy.UpdatedAt),
        _ => nameof(BookSortBy.Title),
    };

    // ── Permissions ─────────────────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadContacts;
    private bool _canReadPhotos;
    private bool _canReadFiles;
    private bool _canReadTags;

    // ── Dialogs ─────────────────────────────────────────────────────────────────────────────
    private Guid _dialogKey = Guid.Empty;
    private bool _dialogOpen;
    private ExistingBook? _editBook;
    private BookSummary? _deleteBook;
    private Guid _deleteKey;
    private bool _deleteOpen;

    // ── Computed ────────────────────────────────────────────────────────────────────────────
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _statusFilter.Count > 0 || _formatFilter.Count > 0 || _tagFilter.Count > 0
        || !string.IsNullOrEmpty(_authorFilter) || _coverFilter != Any || _filesFilter != Any;

    private string SubLineText
    {
        get
        {
            var shelf = _all.Count(b => BookVisuals.OnTheShelf(b.Status));
            return $"{shelf.ToString(CultureInfo.CurrentCulture)} on the shelf · {BookVisuals.Plural(_all.Count, "book", "books")} on file";
        }
    }

    private static IReadOnlyList<OdsOption> HasOptions(string yes, string no) =>
        [new(Any, "Any"), new(Yes, yes), new(No, no)];

    private static readonly IReadOnlyList<OdsOption> CoverOptions = HasOptions("Has cover", "No cover");

    private static readonly IReadOnlyList<OdsOption> FilesOptions = HasOptions("Has files", "No files");

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        await LoadPermissionsAsync();
        await RestorePageStateAsync();
        await Task.WhenAll(LoadTagsAsync(), LoadAuthorsAsync());
        await LoadBooks();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.BooksCreate);
        _canUpdate = user.HasPermission(PermissionClaims.BooksUpdate);
        _canDelete = user.HasPermission(PermissionClaims.BooksDelete);
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canReadPhotos = user.HasPermission(PermissionClaims.PhotosRead);
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canReadTags = user.HasPermission(PermissionClaims.BookTagsRead);
    }

    private async Task LoadTagsAsync()
    {
        if (!_canReadTags)
        {
            return;
        }

        var result = await BookTags.ListAllAsync();
        var tags = result.ItemsOrToast(Snackbar, "book tags");
        if (!result.IsSuccess && _tags.Count > 0)
        {
            // A failed re-read after an inline create keeps the list the page has rather than blanking every chip.
            return;
        }

        _tags = tags;
        _tagById = _tags.ToDictionary(t => t.BookTagId);
        _tagOptions = BookVisuals.TagOptions(_tags);
        _tagFilter = [.. _tagFilter.Where(id => _tagOptions.Any(o => o.Value == id))];
    }

    private async Task LoadAuthorsAsync()
    {
        if (!_canReadContacts)
        {
            return;
        }

        var contacts = await ReferenceData.ContactsAsync();
        _authorOptions =
        [
            .. contacts.OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase).Select(OdsContactOptions.From),
        ];
    }

    // ── Page-state persistence ──────────────────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<BooksPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(BooksPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _statusFilter = BookVisuals.StatusOptions.KnownValues(state.StatusFilter);
        _formatFilter = OdsTypeRegistries.BookFormatOptions.KnownValues(state.FormatFilter);
        // Tags and authors are checked against what this reader may see; a stale id would filter on nothing visible.
        _tagFilter = _canReadTags ? [.. state.TagFilter ?? []] : [];
        _authorFilter = _canReadContacts && Guid.TryParse(state.AuthorFilter, out _) ? state.AuthorFilter : null;
        _coverFilter = _canReadPhotos && state.CoverFilter is Yes or No ? state.CoverFilter : Any;
        _filesFilter = _canReadFiles && state.FilesFilter is Yes or No ? state.FilesFilter : Any;
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private BooksPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _searchString,
        StatusFilter = [.. _statusFilter],
        FormatFilter = [.. _formatFilter],
        TagFilter = [.. _tagFilter],
        AuthorFilter = _authorFilter,
        CoverFilter = _coverFilter,
        FilesFilter = _filesFilter,
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; return FiltersChanged(); }
    private Task OnFormatFilterChanged(IReadOnlyCollection<string> values) { _formatFilter = values ?? []; return FiltersChanged(); }
    private Task OnTagFilterChanged(IReadOnlyCollection<string> values) { _tagFilter = values ?? []; return FiltersChanged(); }
    private Task OnAuthorFilterChanged(string? value) { _authorFilter = string.IsNullOrEmpty(value) ? null : value; return FiltersChanged(); }
    private Task OnCoverFilterChanged(string value) { _coverFilter = value ?? Any; return FiltersChanged(); }
    private Task OnFilesFilterChanged(string value) { _filesFilter = value ?? Any; return FiltersChanged(); }
    private Task OnSortChanged(OdsTableSort sort) { _sort = sort; return FiltersChanged(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private Task FiltersChanged()
    {
        PersistPageState();
        return LoadBooks();
    }

    private Task ClearFilters()
    {
        _searchString = string.Empty;
        _statusFilter = [];
        _formatFilter = [];
        _tagFilter = [];
        _authorFilter = null;
        _coverFilter = Any;
        _filesFilter = Any;
        return FiltersChanged();
    }

    private sealed class BooksPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public List<string> FormatFilter { get; set; } = [];
        public List<string> TagFilter { get; set; } = [];
        public string? AuthorFilter { get; set; }
        public string CoverFilter { get; set; } = Any;
        public string FilesFilter { get; set; } = Any;
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The list query for the current filters — every key the server reads, nothing the reader may not use.</summary>
    internal BooksListFilter Filter() => new()
    {
        Search = string.IsNullOrWhiteSpace(_searchString) ? null : _searchString.Trim(),
        Statuses = _statusFilter.Count > 0 ? _statusFilter : null,
        Formats = _formatFilter.Count > 0 ? _formatFilter : null,
        TagIds = _tagFilter.Count > 0 ? [.. _tagFilter.Select(Guid.Parse)] : null,
        ContactId = Guid.TryParse(_authorFilter, out var author) ? author : null,
        HasCover = _coverFilter switch { Yes => true, No => false, _ => null },
        HasAttachments = _filesFilter switch { Yes => true, No => false, _ => null },
        SortBy = SortByOf(_sort.Key),
        SortDir = _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
    };

    private async Task LoadBooks()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var filtered = _hasFilters;
        var response = await _listLoader.RunAsync(ct => Books.ListAllAsync(Filter(), ct));
        if (response.IsSuperseded)
        {
            return;
        }

        var result = response.Value;
        _books = result.ItemsOrToast(Snackbar, "books");
        _loadError = !result.IsSuccess;
        // A reload is when an off-filter book leaves: the list now says what matches.
        _offFilter.Clear();

        if (!filtered && result.IsSuccess)
        {
            _all = [.. _books];
        }
        else if (filtered && _all.Count == 0)
        {
            await LoadAllAsync();
        }

        _announce = _loadError ? "Couldn't load books."
            : _books.Count == 0 ? (filtered ? "No books match these filters." : "No books yet.")
            : $"Showing {BookVisuals.Plural(_books.Count, "book", "books")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    /// <summary>The unfiltered catalogue for the overview, when the list itself is filtered.</summary>
    private async Task LoadAllAsync()
    {
        var all = await Books.ListAllAsync(new BooksListFilter());
        if (all.IsSuccess && all.Value is { } rows)
        {
            _all = rows;
        }
    }

    /// <summary>
    /// The open record read its book (or saved a change): the list row follows it in place, and the overview with it.
    /// A book that no longer matches the filters keeps its place and says so (<see cref="_offFilter"/>).
    /// </summary>
    private async Task OnBookLoaded(ExistingBook book)
    {
        var summary = ToSummary(book, _canReadPhotos, _canReadFiles);
        var index = _books.FindIndex(b => b.BookId == book.BookId);
        // Opening a card reads the same version the row already shows; only a change since then is patched in.
        if (index >= 0 && _books[index].UpdatedAt != summary.UpdatedAt)
        {
            _books[index] = summary;
            var allIndex = _all.FindIndex(b => b.BookId == book.BookId);
            if (allIndex >= 0)
            {
                _all[allIndex] = summary;
            }

            await CheckFiltersAsync(book.BookId);
            StateHasChanged();
        }
    }

    /// <summary>Asks the server whether a saved book still matches the filters; one that does not stays, marked.</summary>
    private async Task CheckFiltersAsync(Guid bookId)
    {
        if (!_hasFilters)
        {
            return;
        }

        var matching = await Books.ListAllAsync(Filter());
        if (matching.IsSuccess && matching.Value is { } rows && rows.All(r => r.BookId != bookId))
        {
            _offFilter.Add(bookId);
        }
        else
        {
            _offFilter.Remove(bookId);
        }
    }

    /// <summary>The list row for a book read whole — the counts follow the same claims the list endpoint applies.</summary>
    internal static BookSummary ToSummary(ExistingBook b, bool canReadPhotos, bool canReadFiles) => new()
    {
        BookId = b.BookId,
        Title = b.Title,
        Year = b.Year,
        Isbn = b.Isbn,
        Publisher = b.Publisher,
        Format = b.Format,
        Status = b.Status,
        MainAuthor = b.MainAuthor,
        ContributorCount = b.Contributors.Count,
        AttachmentCount = canReadFiles ? b.Attachments?.Count ?? 0 : null,
        ImageCount = canReadPhotos ? b.Images?.Count ?? 0 : null,
        CoverPhotoId = canReadPhotos ? b.CoverPhotoId : null,
        CoverThumbnailsReady = canReadPhotos && b.Images?.FirstOrDefault(i => i.IsCover)?.ThumbnailsReady == true,
        TagIds = [.. b.TagIds],
        DisplayIcon = b.DisplayIcon,
        DisplayColor = b.DisplayColor,
        UpdatedAt = b.UpdatedAt,
    };

    // ── Expand ──────────────────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    // Any change of the open card consumes the pending row-menu requests (a record cannot remember a handled token).
    private void ToggleExpand(Guid id)
    {
        _expandedId = _expandedId == id ? null : id;
        _contributorTokens.Clear();
        _photosTokens.Clear();
        _filesTokens.Clear();
    }

    // Null, not Guid.Empty, for a book whose menu never asked.
    private static Guid? TokenFor(Dictionary<Guid, Guid> tokens, Guid id) => tokens.TryGetValue(id, out var token) ? token : null;

    private void Request(Dictionary<Guid, Guid> tokens, Guid id)
    {
        // Open the card first: the dialog writes into a section that has to be in view.
        if (_expandedId != id)
        {
            ToggleExpand(id);
        }

        tokens[id] = Guid.NewGuid();
    }

    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
    }

    // ── Card header ─────────────────────────────────────────────────────────────────────────
    private OdsRecordImage? CoverOf(BookSummary b)
    {
        if (!_canReadPhotos || b.CoverPhotoId is not { } cover)
        {
            return null;
        }

        var source = PhotoImageSources.For(cover, b.CoverThumbnailsReady, ThumbnailSize.Small, new PhotoImageAccess(true), PhotosApi);
        return (source.Thumb ?? source.Original) is { } src ? new OdsRecordImage(src) : null;
    }

    private IReadOnlyList<RenderFragment?> MetaFor(BookSummary b)
    {
        var author = BookVisuals.ContactName(b.MainAuthor, _canReadContacts);
        if (_canReadContacts && b.MainAuthor.Archived == true)
        {
            author += " (archived)";
        }

        return
        [
            OdsRecordMeta.Text(author),
            OdsRecordMeta.Text(b.Year is { } y ? BookVisuals.Year(y) : null),
            OdsRecordMeta.Text(b.Publisher),
        ];
    }

    /// <summary>Images and Files, each omitted at 0 — and absent, not 0, without its read claim (the server sends null).</summary>
    internal static IReadOnlyList<OdsRecordCount> CountsFor(BookSummary b)
    {
        var counts = new List<OdsRecordCount>(2);
        if (b.ImageCount is > 0 and var images)
        {
            counts.Add(new OdsRecordCount("photo_library", images.ToString(CultureInfo.CurrentCulture), "Images"));
        }

        if (b.AttachmentCount is > 0 and var files)
        {
            counts.Add(new OdsRecordCount("attach_file", files.ToString(CultureInfo.CurrentCulture), "Files"));
        }

        return counts;
    }

    private IReadOnlyList<OdsTagChips.Tag> TagChips(BookSummary b) =>
        _canReadTags ? OdsTagChips.Tag.For(b.TagIds, _tagById, TagLabel, t => t.Icon, t => t.Color) : [];

    private static string TagLabel(ExistingBookTag t) => t.Archived is null ? t.Name : $"{t.Name} (archived)";

    // ── Row menu ────────────────────────────────────────────────────────────────────────────
    private IReadOnlyList<OdsMenuItem> RowActions(BookSummary b)
    {
        var items = new List<OdsMenuItem>();
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(b)) });
        }

        // The contract "New party" pattern: contributors are added from the record, never the dialog.
        if (_canUpdate && _canReadContacts && b.ContributorCount < BookLimits.MaxContributors)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "person_add",
                Label = "Add contributor",
                OnClick = EventCallback.Factory.Create(this, () => Request(_contributorTokens, b.BookId)),
            });
        }

        if (_canUpdate && _canReadPhotos)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "add_photo_alternate",
                Label = "Add images",
                OnClick = EventCallback.Factory.Create(this, () => Request(_photosTokens, b.BookId)),
            });
        }

        if (_canUpdate && _canReadFiles)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "attach_file",
                Label = "Attach files",
                OnClick = EventCallback.Factory.Create(this, () => Request(_filesTokens, b.BookId)),
            });
        }

        items.Add(OdsMenuItem.CopyId(EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(b.BookId.ToString(), OdsCopied.Id("Book")))));

        if (_canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => DeleteClicked(b)),
            });
        }

        return items;
    }

    // ── Create / edit ───────────────────────────────────────────────────────────────────────
    private void AddClicked()
    {
        _editBook = null;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
    }

    private async Task EditClicked(BookSummary b)
    {
        // The row carries no description, notes or version: the dialog edits the book as stored now.
        var result = await Books.GetAsync(b.BookId);
        if (result.OrToast(Snackbar, "Couldn’t open the book") is not { } book)
        {
            return;
        }

        _editBook = book;
        _dialogKey = Guid.NewGuid();
        _dialogOpen = true;
        StateHasChanged();
    }

    private async Task OnBookSaved(ExistingBook saved)
    {
        // A tag created inline in the dialog is not in the page's list yet: reload so the filter, the By tag
        // tile and the next dialog see it.
        if (_canReadTags && TagQuickCreateHost.NeedsTagReload(saved.TagIds, _tagById))
        {
            await LoadTagsAsync();
        }

        var summary = ToSummary(saved, _canReadPhotos, _canReadFiles);
        var index = _books.FindIndex(b => b.BookId == saved.BookId);
        if (index >= 0)
        {
            _books[index] = summary;
        }
        else
        {
            // A new book goes to the top of the loaded items, not re-sorted; it takes its place on the next load.
            _books.Insert(0, summary);
        }

        var allIndex = _all.FindIndex(b => b.BookId == saved.BookId);
        if (allIndex >= 0)
        {
            _all[allIndex] = summary;
        }
        else
        {
            _all.Add(summary);
        }

        await CheckFiltersAsync(saved.BookId);
        if (index < 0)
        {
            _expandedId = saved.BookId;
            _contributorTokens.Clear();
            _photosTokens.Clear();
            _filesTokens.Clear();
        }

        _flashId = saved.BookId;
        StateHasChanged();
        await OdsRecordReveal.EndFlashAsync(() => _flashId == saved.BookId, EndFlash);
    }

    private void EndFlash()
    {
        _flashId = null;
        StateHasChanged();
    }

    // ── Delete ──────────────────────────────────────────────────────────────────────────────
    private void DeleteClicked(BookSummary b)
    {
        _deleteBook = b;
        _deleteKey = Guid.NewGuid();
        _deleteOpen = true;
    }

    private async Task<bool> DeleteAsync(BookSummary b)
    {
        if (!_canDelete)
        {
            return false;
        }

        if (!(await Books.DeleteAsync(b.BookId)).Toast(Snackbar, "Unable to delete the book", "Book deleted"))
        {
            return false;
        }

        _books.RemoveAll(x => x.BookId == b.BookId);
        _all.RemoveAll(x => x.BookId == b.BookId);
        _offFilter.Remove(b.BookId);
        _contributorTokens.Remove(b.BookId);
        _photosTokens.Remove(b.BookId);
        _filesTokens.Remove(b.BookId);
        if (_expandedId == b.BookId)
        {
            _expandedId = null;
        }

        Announce($"{b.Title} deleted.");
        return true;
    }

    public void Dispose() => _listLoader.Dispose();
}
