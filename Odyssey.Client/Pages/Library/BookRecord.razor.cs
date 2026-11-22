using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Services;
using Odyssey.Dtos.Books;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Library;

public partial class BookRecord
{
    /// <summary>The problem code a stale <c>version</c> answers with (issue #527 §5).</summary>
    public const string VersionConflictCode = "book-version-conflict";

    private OdsConfirm _confirm = default!;

    [Inject] private IBooksApiClient Books { get; set; } = default!;
    [Inject] private IFilesApiClient FilesApi { get; set; } = default!;
    [Inject] private IPhotosApiClient PhotosApi { get; set; } = default!;
    [Inject] private IPhotoTagsApiClient PhotoTags { get; set; } = default!;
    [Inject] private IAlbumsApiClient Albums { get; set; } = default!;
    [Inject] private IReferenceDataCache ReferenceData { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IClipboardService Clipboard { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    [Parameter, EditorRequired] public Guid BookId { get; set; }

    /// <summary><c>books.update</c> — the contributor, image and file menus.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public bool CanReadContacts { get; set; }

    [Parameter] public bool CanReadPhotos { get; set; }

    [Parameter] public bool CanReadFiles { get; set; }

    /// <summary>The saved book no longer matches the page's filters; it stays until the list reloads.</summary>
    [Parameter] public bool OffFilter { get; set; }

    /// <summary>A fresh token opens New contributor — how the record ⋯ menu reaches a section it does not own.</summary>
    [Parameter] public Guid? ContributorRequestToken { get; set; }

    /// <summary>A fresh token opens Add images.</summary>
    [Parameter] public Guid? PhotosRequestToken { get; set; }

    /// <summary>A fresh token opens Attach files.</summary>
    [Parameter] public Guid? FilesRequestToken { get; set; }

    /// <summary>Raised with the book after every (re)read, so the list row can follow it.</summary>
    [Parameter] public EventCallback<ExistingBook> OnLoaded { get; set; }

    /// <summary>The page's one live region (two would race each other).</summary>
    [Parameter] public EventCallback<string> OnAnnounce { get; set; }

    private ExistingBook? _book;
    private bool _loadFailed;

    private Guid? _handledContributorToken;
    private Guid? _handledPhotosToken;
    private Guid? _handledFilesToken;

    private Guid _contributorKey = Guid.Empty;
    private bool _contributorOpen;
    private int? _contributorIndex;

    private Guid _photosKey = Guid.Empty;
    private bool _photosOpen;

    private Guid _filesKey = Guid.Empty;
    private bool _filesOpen;
    private readonly List<Guid> _collectedFiles = [];

    private IReadOnlyList<PhotoSummary> _photoSummaries = [];
    private int? _photoIndex;

    /// <summary>The book being shown; a render test sets it through <see cref="UseBook"/>.</summary>
    internal ExistingBook? Book => _book;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        await LoadAsync();
    }

    protected override void OnParametersSet()
    {
        if (_book is null)
        {
            return;
        }

        HandleRequests();
    }

    /// <summary>Test seam: show <paramref name="book"/> without a read, as the estimates section does.</summary>
    internal void UseBook(ExistingBook book)
    {
        _book = book;
        _loadFailed = false;
        HandleRequests();
    }

    /// <summary>Re-reads the book. Public because <c>OnInitializedAsync</c> early-returns outside the browser.</summary>
    public async Task LoadAsync()
    {
        var result = await Books.GetAsync(BookId);
        _loadFailed = !result.IsSuccess;
        if (result.IsSuccess && result.Value is { } book)
        {
            _book = book;
            await OnLoaded.InvokeAsync(book);
            HandleRequests();
        }

        StateHasChanged();
    }

    // A request that arrived while the book was still loading is honoured once it is in.
    private void HandleRequests()
    {
        if (Fresh(ContributorRequestToken, ref _handledContributorToken) && CanAddContributor)
        {
            OpenContributor(null);
        }

        if (Fresh(PhotosRequestToken, ref _handledPhotosToken) && CanUpdate && CanReadPhotos)
        {
            _photosKey = Guid.NewGuid();
            _photosOpen = true;
        }

        if (Fresh(FilesRequestToken, ref _handledFilesToken) && CanUpdate && CanReadFiles)
        {
            _collectedFiles.Clear();
            _filesKey = Guid.NewGuid();
            _filesOpen = true;
        }
    }

    private static bool Fresh(Guid? token, ref Guid? handled)
    {
        if (token is not { } t || t == Guid.Empty || t == handled)
        {
            return false;
        }

        handled = t;
        return true;
    }

    private bool CanAddContributor =>
        CanUpdate && CanReadContacts && _book is { } b && b.Contributors.Count < BookLimits.MaxContributors;

    // ── The PUT that every link change goes through ──────────────────────────────────────────

    /// <summary>
    /// The book as an <see cref="UpdateBook"/> that changes nothing: every scalar as stored, every array <c>null</c>
    /// (unchanged), the current version. A link change sets exactly one array on it.
    /// </summary>
    internal static UpdateBook Unchanged(ExistingBook b) => new()
    {
        Version = b.Version,
        Title = b.Title,
        Publisher = b.Publisher,
        Year = b.Year,
        Isbn = b.Isbn,
        Format = b.Format,
        Status = b.Status,
        Description = b.Description,
        Notes = b.Notes,
        MainAuthorContactId = b.MainAuthor.ContactId,
        Contributors = null,
        PhotoIds = null,
        CoverPhotoId = null,
        AttachmentFileIds = null,
        TagIds = null,
    };

    private async Task<bool> PutAsync(UpdateBook request, string failureLead, string said)
    {
        if (_book is not { } b)
        {
            return false;
        }

        var result = await Books.UpdateAsync(b.BookId, request);
        if (result.IsSuccess && result.Value is { } saved)
        {
            _book = saved;
            await OnLoaded.InvokeAsync(saved);
            await OnAnnounce.InvokeAsync(said);
            StateHasChanged();
            return true;
        }

        if (result.Problem?.Code == VersionConflictCode)
        {
            // Never an overwrite: the latest version is read back and the change is the user's to make again.
            Snackbar.Add("Someone else changed this book. It has been reloaded — make the change again.", Severity.Warning);
            await LoadAsync();
            return false;
        }

        result.OrToast(Snackbar, failureLead);
        return false;
    }

    // ── Contributors ─────────────────────────────────────────────────────────────────────────

    private List<ExistingBookContributor> OrderedContributors =>
        _book is null ? [] : [.. _book.Contributors.OrderBy(c => c.Position)];

    private static List<BookContributorRequest> Requests(IEnumerable<ExistingBookContributor> rows) =>
        [.. rows.Select(c => new BookContributorRequest { ContactId = c.Contact.ContactId, Role = c.Role })];

    private IReadOnlyList<OdsMenuItem>? ContributorMenu(int index)
    {
        if (!CanUpdate || !CanReadContacts || _book is null)
        {
            return null;
        }

        var rows = OrderedContributors;
        var row = rows[index];
        var name = row.Contact.Name ?? "contact";
        var items = new List<OdsMenuItem>
        {
            new() { Icon = "edit", Label = "Edit contributor", OnClick = EventCallback.Factory.Create(this, () => OpenContributor(index)) },
        };
        if (index > 0)
        {
            items.Add(new() { Icon = "arrow_upward", Label = "Move up", OnClick = EventCallback.Factory.Create(this, () => MoveContributorAsync(index, -1)) });
        }

        if (index < rows.Count - 1)
        {
            items.Add(new() { Icon = "arrow_downward", Label = "Move down", OnClick = EventCallback.Factory.Create(this, () => MoveContributorAsync(index, 1)) });
        }

        items.Add(new() { Icon = "content_copy", Label = "Copy name", OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(name, "Name copied")) });
        items.Add(new() { Divider = true });
        items.Add(new()
        {
            Icon = "person_remove",
            Label = "Remove contributor",
            Danger = true,
            OnClick = EventCallback.Factory.Create(this, () => RemoveContributorAsync(index)),
        });
        return items;
    }

    private void OpenContributor(int? index)
    {
        _contributorIndex = index;
        _contributorKey = Guid.NewGuid();
        _contributorOpen = true;
    }

    /// <summary>The dialog's save: the whole ordered list with this row added (last) or replaced.</summary>
    private Task<bool> SaveContributorAsync(BookContributorRequest row)
    {
        var rows = Requests(OrderedContributors);
        var editing = _contributorIndex is { } i && i < rows.Count;
        if (editing)
        {
            rows[_contributorIndex!.Value] = row;
        }
        else
        {
            rows.Add(row);
        }

        return PutAsync(Unchanged(_book!) with { Contributors = rows },
            editing ? "Unable to save the contributor" : "Unable to add the contributor",
            editing ? "Contributor saved." : "Contributor added.");
    }

    private Task MoveContributorAsync(int index, int by)
    {
        var rows = Requests(OrderedContributors);
        var row = rows[index];
        rows.RemoveAt(index);
        rows.Insert(index + by, row);
        return PutAsync(Unchanged(_book!) with { Contributors = rows }, "Unable to move the contributor",
            $"Contributor moved to position {index + by + 1} of {rows.Count}.");
    }

    private async Task RemoveContributorAsync(int index)
    {
        var rows = OrderedContributors;
        var name = rows[index].Contact.Name ?? "this contributor";
        if (!await _confirm.AskAsync("Remove contributor", $"Remove {name} from “{_book!.Title}”? The contact itself is kept.", "Remove", "person_remove"))
        {
            return;
        }

        var next = Requests(rows);
        next.RemoveAt(index);
        await PutAsync(Unchanged(_book!) with { Contributors = next }, "Unable to remove the contributor", "Contributor removed.");
    }

    // ── Images ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The images, cover first, then in the stored order.</summary>
    internal IReadOnlyList<BookImageDto> OrderedImages =>
        _book?.Images is not { } images ? []
        : [.. images.Where(i => i.PhotoId == _book.CoverPhotoId), .. images.Where(i => i.PhotoId != _book.CoverPhotoId)];

    private List<Guid> PhotoIds => [.. (_book?.Images ?? []).Select(i => i.PhotoId)];

    private async Task AddImagesAsync(IReadOnlyList<AttachPhotoItem> items)
    {
        _photosOpen = false;
        if (_book is not { } b)
        {
            return;
        }

        var current = PhotoIds;
        var added = items.Select(x => x.PhotoId).Where(id => !current.Contains(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return;
        }

        var ids = current.Concat(added).ToList();
        // The first image added to a cover-less book becomes its cover.
        var cover = b.CoverPhotoId ?? ids[0];
        var coverName = items.FirstOrDefault(x => x.PhotoId == cover)?.Name;
        var said = b.CoverPhotoId is null
            ? $"{BookVisuals.Plural(added.Count, "image", "images")} added. {(coverName is { Length: > 0 } ? coverName : "The first image")} is now the cover."
            : $"{BookVisuals.Plural(added.Count, "image", "images")} added.";
        await PutAsync(Unchanged(b) with { PhotoIds = ids, CoverPhotoId = cover }, "Unable to add images", said);
    }

    private Task SetCoverAsync(Guid? photoId)
    {
        var name = photoId is { } id ? ImageName(id) : null;
        return PutAsync(Unchanged(_book!) with { PhotoIds = PhotoIds, CoverPhotoId = photoId }, "Unable to change the cover",
            photoId is null ? "No cover selected." : $"{name} is now the cover.");
    }

    private async Task DetachImageAsync(Guid photoId)
    {
        var name = ImageName(photoId);
        if (!await _confirm.AskAsync("Detach image", $"Detach {name} from “{_book!.Title}”? The photo stays in your library.", "Detach", "link_off"))
        {
            return;
        }

        var wasCover = _book!.CoverPhotoId == photoId;
        await PutAsync(Unchanged(_book) with
        {
            PhotoIds = [.. PhotoIds.Where(id => id != photoId)],
            CoverPhotoId = wasCover ? null : _book.CoverPhotoId,
        }, "Unable to detach the image", wasCover ? "Image detached. No cover selected." : "Image detached.");
    }

    private string ImageName(Guid photoId)
    {
        var images = OrderedImages;
        var index = images.ToList().FindIndex(i => i.PhotoId == photoId);
        var image = index < 0 ? null : images[index];
        return PhotoMappers.Label(image?.Title, image?.FileName, $"Image {index + 1}");
    }

    private async Task OpenImage(Guid photoId)
    {
        await EnsurePhotoRefsAsync();
        var images = OrderedImages;
        _photoSummaries =
        [
            .. images.Select(i => new PhotoSummary
            {
                PhotoId = i.PhotoId,
                Title = i.Title,
                ThumbnailsReady = i.ThumbnailsReady,
                PersonCount = 0,
                AlbumCount = 0,
            }),
        ];
        var index = images.ToList().FindIndex(i => i.PhotoId == photoId);
        _photoIndex = index < 0 ? 0 : index;
    }

    // The viewer's detail rail names a photo's tags, people and albums; without these it would print raw ids.
    // Loaded once, on the first Open; each read is the caller's own, so one it may not make leaves "—".
    private bool _photoRefsLoaded;
    private Dictionary<Guid, ExistingPhotoTag> _photoTagById = [];
    private Dictionary<Guid, ExistingContact> _contactById = [];
    private Dictionary<Guid, PhotoAlbumSummary> _albumById = [];

    private async Task EnsurePhotoRefsAsync()
    {
        if (_photoRefsLoaded)
        {
            return;
        }

        var tags = await PhotoTags.ListAllAsync();
        if (tags.IsSuccess && tags.Value is { } tagRows)
        {
            _photoTagById = tagRows.ToDictionary(t => t.PhotoTagId);
        }

        var albums = await Albums.ListAllAsync();
        if (albums.IsSuccess && albums.Value is { } albumRows)
        {
            _albumById = albumRows.ToDictionary(a => a.PhotoAlbumId);
        }

        if (CanReadContacts)
        {
            _contactById = (await ReferenceData.ContactsAsync()).ToDictionary(c => c.ContactId);
        }

        _photoRefsLoaded = true;
    }

    private string PhotoTagName(Guid id) => _photoTagById.GetValueOrDefault(id)?.Name ?? "—";

    private string? PhotoTagIcon(Guid id) => _photoTagById.GetValueOrDefault(id)?.Icon;

    private string? PhotoTagColor(Guid id) => _photoTagById.GetValueOrDefault(id)?.Color;

    private string PhotoPersonName(Guid id) => _contactById.GetValueOrDefault(id)?.ResolvedDisplayName ?? "—";

    private OdsTagChips.Tag? PhotoAlbumChip(Guid id) => Odyssey.Client.Pages.Photos.AlbumVisuals.Chip(_albumById.GetValueOrDefault(id));

    // ── Files ────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<OdsFilesRow> FileRows => (_book?.Attachments ?? []).Select(a => new OdsFilesRow
    {
        Id = a.FileId.ToString(),
        Name = a.FileName,
        Kind = OdsTypeRegistries.FileKindOf(a.ContentType).Label,
        SizeBytes = a.SizeBytes,
        UploadedAtUtc = a.CreatedAt,
    });

    private OdsFileKindMeta KindMeta(OdsFilesRow row)
    {
        var kind = OdsTypeRegistries.FileKindOf(AttachmentOf(row)?.ContentType);
        return new OdsFileKindMeta(kind.Icon, kind.Color, kind.Soft);
    }

    private BookAttachmentDto? AttachmentOf(OdsFilesRow row) =>
        (_book?.Attachments ?? []).FirstOrDefault(a => a.FileId.ToString() == row.Id);

    /// <summary>Download · Copy ID. There is no preview and no Edit: a book file carries no type, and EPUB is never rendered.</summary>
    private IReadOnlyList<OdsMenuItem> FileMenu(OdsFilesRow row) =>
    [
        new OdsMenuItem
        {
            Icon = "download",
            Label = "Download",
            OnClick = EventCallback.Factory.Create(this, () => DownloadAsync(row)),
        },
        OdsMenuItem.CopyId(EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(row.Id, OdsCopied.Id("File")))),
    ];

    private async Task DownloadAsync(OdsFilesRow row)
    {
        if (AttachmentOf(row) is not { } file)
        {
            return;
        }

        var content = await FilesApi.GetContentAsync(file.FileId);
        if (content is null)
        {
            Snackbar.Add("Download failed.", Severity.Error);
            return;
        }

        await JsRuntime.SaveFileAsync(content, file.FileName, file.ContentType);
    }

    private EventCallback<OdsFilesRow> DetachFileAction =>
        CanUpdate ? EventCallback.Factory.Create<OdsFilesRow>(this, DetachFileAsync) : default;

    private async Task DetachFileAsync(OdsFilesRow row)
    {
        if (AttachmentOf(row) is not { } file || _book is not { } b)
        {
            return;
        }

        if (!await _confirm.AskAsync("Detach file", $"Detach “{file.FileName}” from “{b.Title}”? The file itself is kept in Files.", "Detach", "link_off"))
        {
            return;
        }

        await PutAsync(Unchanged(b) with
        {
            AttachmentFileIds = [.. (b.Attachments ?? []).Select(a => a.FileId).Where(id => id != file.FileId)],
        }, "Unable to detach the file", "File detached.");
    }

    /// <summary>
    /// The dialog hands over each uploaded or picked file; a book links them in ONE PUT once all are in, since every
    /// link change replaces the whole array.
    /// </summary>
    private Task<bool> CollectFile(AttachDocumentItem item)
    {
        _collectedFiles.Add(item.FileId);
        return Task.FromResult(true);
    }

    private async Task AddFilesAsync(IReadOnlyList<AttachDocumentItem> items)
    {
        if (_book is not { } b)
        {
            return;
        }

        var current = (b.Attachments ?? []).Select(a => a.FileId).ToList();
        var added = _collectedFiles.Concat(items.Select(i => i.FileId)).Where(id => !current.Contains(id)).Distinct().ToList();
        _collectedFiles.Clear();
        if (added.Count == 0)
        {
            return;
        }

        await PutAsync(Unchanged(b) with { AttachmentFileIds = [.. current, .. added] }, "Unable to attach the files",
            $"{BookVisuals.Plural(added.Count, "file", "files")} attached.");
    }
}
