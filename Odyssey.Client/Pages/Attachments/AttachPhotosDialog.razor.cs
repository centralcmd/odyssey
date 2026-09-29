using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Attachments;

/// <summary>
/// One photo the dialog hands back — the DS <c>onSubmit</c> item. <see cref="PhotoId"/> is null for an
/// upload made without <see cref="AttachPhotosDialog.CreateLibraryPhoto"/>: only the stored file exists
/// until the host's own write links it.
/// </summary>
public sealed record AttachPhotoItem(AttachDocumentSource Source, Guid? PhotoId, Guid FileId, string Name);

public partial class AttachPhotosDialog
{
    internal const string UploadTab = "upload";
    internal const string LibraryTab = "library";

    /// <summary>The page the library tab reads: the newest photos, narrowed by the search.</summary>
    internal const int LibraryPageSize = 96;

    private static readonly IReadOnlyList<OdsSegmentedOption> Tabs =
    [
        new() { Value = UploadTab, Label = "Upload new", Icon = "upload" },
        new() { Value = LibraryTab, Label = "From Photos", Icon = "photo_library" },
    ];

    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    [Inject] private IFilesApiClient Files { get; set; } = default!;
    [Inject] private IPhotosApiClient Photos { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IUploadLimitsCache UploadLimits { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    [Parameter, EditorRequired] public string Subtitle { get; set; } = string.Empty;

    /// <summary>
    /// Photos already on the entry or album — shown disabled as "Already added". Matched against both a
    /// photo's id and its file id, since an album holds the one and a journal entry the other.
    /// </summary>
    [Parameter] public IReadOnlyCollection<Guid> AttachedIds { get; set; } = [];

    /// <summary>Upload only — no tabs, no library (the Photos page's own upload).</summary>
    [Parameter] public bool UploadOnly { get; set; }

    /// <summary>Creates a library Photo over each upload (needs <c>photos.create</c>).</summary>
    [Parameter] public bool CreateLibraryPhoto { get; set; } = true;

    /// <summary>
    /// A surface's own, tighter per-file cap in megabytes. The effective cap is the smaller of it and
    /// the instance-wide one — a surface may tighten, never loosen.
    /// </summary>
    [Parameter] public int? SurfaceMaxMegabytes { get; set; }

    /// <summary>Raised with the photos picked or uploaded; the dialog then closes.</summary>
    [Parameter] public EventCallback<IReadOnlyList<AttachPhotoItem>> OnSubmit { get; set; }

    /// <summary>A line under the upload field — the Photos page's location-privacy notice.</summary>
    [Parameter] public RenderFragment? Footnote { get; set; }

    /// <summary>Announces uploads in a snackbar — for the host whose upload IS the write (the Photos page).</summary>
    [Parameter] public bool Announce { get; set; }

    private string _tab = UploadTab;
    private List<OdsUploadFile> _uploads = [];
    private string? _error;
    private bool _busy;

    private List<PhotoSummary> _library = [];
    // Picks keep their summary, not just the id: the library is one server-searched page, so a photo
    // picked before the search changed is no longer in _library, and resolving picks through it would
    // drop them at submit while the button still counted them.
    private readonly List<PhotoSummary> _picked = [];

    // Latest-wins: a slow earlier search must not overwrite the result of a newer one.
    private int _searchVersion;

    // An upload that already produced its item (stored, and its library photo created) is not redone
    // when a partly failed batch is retried.
    private readonly Dictionary<string, AttachPhotoItem> _uploaded = [];

    // Stored files by row, so a retry whose library-photo step failed does not store the bytes again.
    private readonly Dictionary<string, Guid> _stored = [];

    private string _announce = string.Empty;
    private bool _libraryLoaded;
    private bool _libraryLoading;
    private bool _libraryFailed;
    private string _query = string.Empty;

    private bool _canUpload;
    private bool _canBrowse;

    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback;

    private bool ShowTabs => !UploadOnly && _canUpload && _canBrowse;

    private string SubmitText
    {
        get
        {
            var n = _tab == UploadTab ? _uploads.Count : _picked.Count;
            if (UploadOnly)
                return n > 1 ? $"Upload {n} photos" : "Upload";
            return _tab == UploadTab
                ? (n > 1 ? $"Upload and add {n}" : "Upload and add")
                : (n > 1 ? $"Add {n} photos" : "Add photo");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        var user = await AuthenticationState.GetUserAsync();
        _canUpload = user.HasPermission(PermissionClaims.FilesCreate)
                     && (!CreateLibraryPhoto || user.HasPermission(PermissionClaims.PhotosCreate));
        _canBrowse = !UploadOnly && user.HasPermission(PermissionClaims.PhotosRead);
        if (!_canUpload && _canBrowse)
            _tab = LibraryTab;

        _uploadLimits = await UploadLimits.GetAsync();
        if (SurfaceMaxMegabytes is { } surface)
            _uploadLimits = _uploadLimits.TightenTo(surface);

        if (_tab == LibraryTab)
            await LoadLibraryAsync();
    }

    private async Task OnTabChanged(string? tab)
    {
        _tab = tab == LibraryTab ? LibraryTab : UploadTab;
        _error = null;
        if (_tab == LibraryTab && !_libraryLoaded)
            await LoadLibraryAsync();
    }

    private async Task OnQueryChanged(string? query)
    {
        _query = query ?? string.Empty;
        await LoadLibraryAsync();
    }

    /// <summary>Reads one page of active library photos, newest first, narrowed server-side by the search.</summary>
    public async Task LoadLibraryAsync()
    {
        if (!_canBrowse)
            return;

        var version = ++_searchVersion;
        _libraryLoading = true;
        _libraryFailed = false;
        var result = await Photos.ListAsync(1, LibraryPageSize,
            search: string.IsNullOrWhiteSpace(_query) ? null : _query.Trim());
        if (version != _searchVersion)
            return;

        _libraryFailed = !result.IsSuccess;
        _library = result.IsSuccess && result.Value is { } page ? [.. page.Items] : [];
        _libraryLoaded = result.IsSuccess;
        _libraryLoading = false;
        _announce = _libraryFailed ? "Couldn’t load your photos."
            : _library.Count == 0 ? (string.IsNullOrWhiteSpace(_query) ? "Your photo library is empty." : $"No photos match “{_query}”.")
            : $"{_library.Count} photo{(_library.Count == 1 ? "" : "s")} shown.";
        StateHasChanged();
    }

    private bool IsPicked(PhotoSummary photo) => _picked.Any(p => p.PhotoId == photo.PhotoId);

    private bool IsAttached(PhotoSummary photo) =>
        AttachedIds.Contains(photo.PhotoId) || AttachedIds.Contains(photo.FileId);

    private static string LabelOf(PhotoSummary photo) =>
        string.IsNullOrWhiteSpace(photo.Title) ? "Photo" : photo.Title!;

    private static string TileLabel(PhotoSummary photo, bool added) =>
        added ? $"{LabelOf(photo)} — already added" : LabelOf(photo);

    private void TogglePick(PhotoSummary photo)
    {
        if (IsAttached(photo))
            return;
        var existing = _picked.FindIndex(p => p.PhotoId == photo.PhotoId);
        if (existing >= 0)
            _picked.RemoveAt(existing);
        else
            _picked.Add(photo);
        _error = null;
    }

    // Controlled list — filter out anything outside the image allow-list or over the cap before it lands.
    private void OnUploadsChanged(IReadOnlyList<OdsUploadFile> files)
    {
        var kept = new List<OdsUploadFile>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                Snackbar.Add($"“{f.Name}” isn’t a JPEG, PNG, GIF or WebP image.", Severity.Warning);
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                Snackbar.Add($"“{f.Name}” exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.", Severity.Warning);
                continue;
            }
            kept.Add(f);
        }

        _uploads = kept;
        if (_error is not null && _uploads.Count > 0)
            _error = null;
    }

    private async Task SubmitAsync()
    {
        if (_busy)
            return;

        if (_tab == LibraryTab)
        {
            if (_picked.Count == 0)
            {
                _error = "Pick at least one photo.";
                return;
            }
            await FinishAsync([.. _picked.Select(p =>
                new AttachPhotoItem(AttachDocumentSource.Library, p.PhotoId, p.FileId, LabelOf(p)))]);
            return;
        }

        if (_uploads.Count == 0)
        {
            _error = "Add at least one photo to upload.";
            return;
        }

        _busy = true;
        _error = null;
        var added = new List<AttachPhotoItem>();
        var failed = new List<OdsUploadFile>();
        try
        {
            foreach (var file in _uploads.Where(f => f.Source is not null))
            {
                var item = _uploaded.GetValueOrDefault(file.Uid) ?? await UploadOneAsync(file);
                if (item is null)
                {
                    failed.Add(file);
                    continue;
                }
                _uploaded[file.Uid] = item;
                added.Add(item);
            }

            if (Announce && added.Count > 0)
                Snackbar.Add(added.Count == 1 ? "Photo added." : $"{added.Count} photos added.", Severity.Success);

            if (failed.Count == 0)
            {
                await FinishAsync(added);
                return;
            }

            // Partly failed: hand over what worked, keep only the failures for a retry, and say so —
            // closing would discard the failed rows, and staying silent would read as a dead button.
            if (added.Count > 0)
                await OnSubmit.InvokeAsync(added);
            _uploads = failed;
            _error = failed.Count == 1
                ? $"“{failed[0].Name}” wasn’t added. Try again, or remove it."
                : $"{failed.Count} photos weren’t added. Try again, or remove them.";
            _announce = _error;
        }
        finally
        {
            _busy = false;
        }
    }

    // One file: store it, then (for a library host) create its library photo. Null when it failed; the
    // reason is already on screen.
    private async Task<AttachPhotoItem?> UploadOneAsync(OdsUploadFile file)
    {
        if (!_stored.TryGetValue(file.Uid, out var storedId))
        {
            try
            {
                storedId = (await Files.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes), file.Name)).Id;
            }
            catch (Exception)
            {
                Snackbar.Add($"Couldn’t upload “{file.Name}”.", Severity.Error);
                return null;
            }
            _stored[file.Uid] = storedId;
        }

        if (!CreateLibraryPhoto)
            return new AttachPhotoItem(AttachDocumentSource.Upload, null, storedId, file.Name);

        var created = await Photos.CreateAsync(new NewPhoto { FileId = storedId });
        if (created.IsSuccess && created.Value is { } photo)
            return new AttachPhotoItem(AttachDocumentSource.Upload, photo.PhotoId, storedId, file.Name);

        // A 409 means this file is already a library photo, so no new one was made and there is no id
        // to link. Name the way out rather than dropping the file silently.
        Snackbar.Add(created.Status == HttpStatusCode.Conflict
            ? $"“{file.Name}” is already in your library — pick it from From Photos instead."
            : $"Couldn’t add “{file.Name}”: {created.Error}", Severity.Error);
        return null;
    }

    private async Task FinishAsync(IReadOnlyList<AttachPhotoItem> items)
    {
        await OnSubmit.InvokeAsync(items);
        await OpenChanged.InvokeAsync(false);
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
