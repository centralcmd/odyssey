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
/// One photo the dialog hands back — the DS <c>onSubmit</c> item. Every upload is a library photo since issue #428,
/// so <see cref="PhotoId"/> is always known. <see cref="ThumbnailsReady"/> travels with the pick so the host's own
/// strip can show the thumbnail (issue #365 B2); a fresh upload's is always false.
/// </summary>
public sealed record AttachPhotoItem(AttachDocumentSource Source, Guid PhotoId, string Name, bool ThumbnailsReady = false);

public partial class AttachPhotosDialog
{
    internal const string UploadTab = "upload";
    internal const string LibraryTab = "library";

    /// <summary>The page the library tab reads: the newest photos, narrowed by the search.</summary>
    internal const int LibraryPageSize = 96;

    private PhotoImageAccess _imageAccess = new(false);

    private PhotoImageSource ImageOf(PhotoSummary photo) =>
        PhotoImageSources.For(photo.PhotoId, photo.ThumbnailsReady, ThumbnailSize.Small, _imageAccess, Photos);

    private static readonly IReadOnlyList<OdsSegmentedOption> Tabs =
    [
        new() { Value = UploadTab, Label = "Upload new", Icon = "upload" },
        new() { Value = LibraryTab, Label = "From Photos", Icon = "photo_library" },
    ];

    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

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

    /// <summary>
    /// A surface's own, tighter per-file cap in megabytes. The effective cap is the smaller of it and
    /// the instance-wide one — a surface may tighten, never loosen.
    /// </summary>
    [Parameter] public int? SurfaceMaxMegabytes { get; set; }

    /// <summary>
    /// Raised with the photos picked or uploaded. Usually once, after which the dialog closes; after a
    /// partly failed upload it is raised with what made it and the dialog stays open for the retry, which
    /// raises it again with only the newly added photos — so a host appends, never replaces.
    /// </summary>
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
        // Every upload is a library photo (issue #428), so photos.create alone gates it; files.create plays no part.
        _canUpload = user.HasPermission(PermissionClaims.PhotosCreate);
        _canBrowse = !UploadOnly && user.HasPermission(PermissionClaims.PhotosRead);
        _imageAccess = PhotoImageAccess.From(user);
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
        Notice(null);
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
        AttachedIds.Contains(photo.PhotoId);

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
        var rejected = new List<string>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (f.Source is not null && !AllowedExtensions.Contains(ext))
            {
                rejected.Add($"“{UploadFailureText.Clean(f.Name)}” isn’t a JPEG, PNG, GIF or WebP image.");
                continue;
            }
            if (f.SizeBytes > _uploadLimits.MaxUploadBytes)
            {
                rejected.Add($"“{UploadFailureText.Clean(f.Name)}” exceeds the {_uploadLimits.MaxUploadMegabytes} MB limit.");
                continue;
            }
            kept.Add(f);
        }

        _uploads = kept;
        if (_error is not null && _uploads.Count > 0)
            _error = null;

        // Every rejection of one pick in one alert (WCAG 3.3.1); a pick or removal with none clears a stale one.
        Notice(rejected.Count > 0 ? UploadFailureText.Several(rejected, itemised: true, "can’t be added") : null);
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
                new AttachPhotoItem(AttachDocumentSource.Library, p.PhotoId, LabelOf(p), p.ThumbnailsReady))]);
            return;
        }

        if (_uploads.Count == 0)
        {
            _error = "Add at least one photo to upload.";
            return;
        }

        _busy = true;
        _error = null;
        Notice(null);
        var added = new List<AttachPhotoItem>();
        var failed = new List<OdsUploadFile>();
        var perFile = new List<string>();
        string? stoppedText = null;
        try
        {
            var queue = _uploads.Where(f => f.Source is not null).ToList();
            for (var i = 0; i < queue.Count; i++)
            {
                var file = queue[i];
                if (_uploaded.GetValueOrDefault(file.Uid) is { } done)
                {
                    added.Add(done);
                    continue;
                }

                var created = await Photos.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes));
                if (created.IsSuccess && created.Value is { } photo)
                {
                    var item = new AttachPhotoItem(AttachDocumentSource.Upload, photo.PhotoId, file.Name);
                    _uploaded[file.Uid] = item;
                    added.Add(item);
                    continue;
                }

                // 507 / 503 / 429 stop the batch (issue #449 §3.2): the failed photo and every untried one stay listed.
                var stop = UploadFailureText.StopOf(created.Status);
                if (stop != UploadStop.None)
                {
                    failed.AddRange(queue.Skip(i));
                    stoppedText = UploadFailureText.Stopped(stop, queue.Count - i, created.RetryAfter);
                    break;
                }

                perFile.Add(created.Status == HttpStatusCode.Conflict
                    ? $"“{UploadFailureText.Clean(file.Name)}” is already in your library — pick it from From Photos instead."
                    : UploadFailureText.PerFile(created.Status, created.Problem?.Detail, file.Name, _uploadLimits.MaxUploadMegabytes));
                failed.Add(file);
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
            // The alert line carries the reasons (issue #449 §6); no toast while the dialog is open.
            Notice(stoppedText is not null
                ? (perFile.Count == 0 ? stoppedText : $"{stoppedText} {UploadFailureText.Several(perFile, itemised: true)}")
                : perFile.Count == 0
                ? (failed.Count == 1
                    ? $"“{UploadFailureText.Clean(failed[0].Name)}” wasn’t added. Try again, or remove it."
                    : $"{failed.Count} photos weren’t added. Try again, or remove them.")
                : UploadFailureText.Several(perFile, itemised: true));
        }
        finally
        {
            _busy = false;
        }
    }

    // The dialog's one announcement for a failure or warning while it is open (issue #449 §6), re-keyed per batch.
    private string? _notice;
    private int _noticeKey;

    private void Notice(string? text)
    {
        _notice = text;
        if (text is not null)
            _noticeKey++;
    }

    private async Task FinishAsync(IReadOnlyList<AttachPhotoItem> items)
    {
        await OnSubmit.InvokeAsync(items);
        await OpenChanged.InvokeAsync(false);
    }

    private Task CloseAsync() => OpenChanged.InvokeAsync(false);
}
