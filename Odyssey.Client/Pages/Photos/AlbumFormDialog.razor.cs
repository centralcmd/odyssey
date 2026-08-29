using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

public partial class AlbumFormDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Null = create a new album; otherwise edit this album.</summary>
    [Parameter] public Guid? AlbumId { get; set; }

    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => AlbumId is not null;
    private Guid _loadedFor = Guid.Empty;
    private bool _loaded;
    private bool _busy;

    private string _name = string.Empty;
    private string _desc = string.Empty;
    private List<Guid> _members = [];
    private Guid? _cover;
    private DateTime? _archived;
    private Dictionary<Guid, PhotoSummary> _summaries = [];

    private List<OdsUploadFile> _files = [];
    private string? _uploadError;

    private RenderFragment TitleFragment => builder => builder.AddContent(0, IsEdit ? "Edit album" : "New album");
    private RenderFragment? SubtitleFragment => IsEdit && _loaded
        ? builder => builder.AddContent(0, _name)
        : null;

    /// <summary>
    /// This surface's own, tighter product limit — a cover image is not a general file upload. Kept as
    /// a named constant: a deliberate product decision, not drift. The effective cap is the smaller of
    /// it and the instance-wide cap, so an administrator lowering the global cap still reaches here.
    /// </summary>
    private const int SurfaceMaxMegabytes = 25;

    private UploadLimitsDto _uploadLimits = UploadLimitsCache.Fallback.TightenTo(SurfaceMaxMegabytes);

    protected override async Task OnParametersSetAsync()
    {
        _uploadLimits = (await UploadLimits.GetAsync()).TightenTo(SurfaceMaxMegabytes);
        var key = AlbumId ?? Guid.Empty;
        if (Open && (!_loaded || _loadedFor != key))
        {
            _loaded = true;
            _loadedFor = key;
            _name = string.Empty;
            _desc = string.Empty;
            _members = [];
            _cover = null;
            _archived = null;
            _files = [];
            _uploadError = null;

            if (AlbumId is { } id)
            {
                var album = await Albums.GetAsync(id);
                if (album is { } a)
                {
                    _name = a.Name;
                    _desc = a.Description ?? string.Empty;
                    _members = [.. a.PhotoIds];
                    _cover = a.CoverPhotoId;
                    _archived = a.Archived;
                }

                await LoadSummariesAsync();
            }
        }
        else if (!Open)
        {
            _loaded = false;
        }
    }

    // Load only this album's photos (server-side albumIds filter), across both archival states, so the
    // member list resolves thumbnails/titles without pulling the whole library.
    private async Task LoadSummariesAsync()
    {
        _summaries = [];
        if (AlbumId is not { } id)
        {
            return;
        }

        foreach (var status in new[] { (string?)null, "Archived" })
        {
            var load = await Photos.ListAsync(1, PagedQuery.LimitAll, albumIds: [id.ToString()], status: status);
            foreach (var p in load.PagedItemsOrToast(Snackbar, "album photos"))
            {
                _summaries[p.PhotoId] = p;
            }
        }
    }

    private PhotoSummary? Summary(Guid id) => _summaries.GetValueOrDefault(id);
    private static string Label(PhotoSummary? s) => s is null ? "Photo" : (string.IsNullOrWhiteSpace(s.Title) ? "Photo" : s.Title!);
    private string ThumbStyle(PhotoSummary? s) =>
        s is null ? string.Empty : $"background: center/cover url('{Files.ContentUrl(s.FileId)}');";

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _members.Count)
        {
            return;
        }

        (_members[index], _members[target]) = (_members[target], _members[index]);
    }

    private void Remove(Guid id)
    {
        _members.Remove(id);
        if (_cover == id)
        {
            _cover = null;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            return;
        }

        _busy = true;
        _uploadError = null;

        // Upload any staged files first, creating one library Photo per file. Newly-created photos seed
        // the new album's membership (create) or append to the ordered member list (edit).
        var uploaded = await UploadStagedPhotosAsync();
        if (uploaded is null)
        {
            _busy = false;
            return;
        }

        bool ok;
        if (AlbumId is { } id)
        {
            ok = (await Albums.UpdateAsync(id, new UpdatePhotoAlbum
            {
                Name = _name.Trim(),
                Description = string.IsNullOrWhiteSpace(_desc) ? null : _desc.Trim(),
                PhotoIds = [.. _members, .. uploaded],
                CoverPhotoId = _cover,
                Archived = _archived is not null,
            })).Toast(Snackbar, "Save failed", "Album updated.");
        }
        else
        {
            ok = (await Albums.CreateAsync(new NewPhotoAlbum
            {
                Name = _name.Trim(),
                Description = string.IsNullOrWhiteSpace(_desc) ? null : _desc.Trim(),
                PhotoIds = [.. uploaded],
            })).Toast(Snackbar, "Create failed", "Album created.");
        }

        _busy = false;
        if (ok)
        {
            _files = [];
            await OnSaved.InvokeAsync();
            await Close();
        }
    }

    // Uploads each staged file and creates a library Photo over it, returning the new photo ids in
    // pick order. Returns null (aborting the save) if any upload/create fails, so the album write is
    // never committed against a partial set.
    private async Task<List<Guid>?> UploadStagedPhotosAsync()
    {
        var ids = new List<Guid>();
        foreach (var file in _files.Where(f => f.Source is not null))
        {
            try
            {
                var stored = await Files.UploadAsync(file.Source!.ToApiUpload(_uploadLimits.MaxUploadBytes), file.Name);
                var result = await Photos.CreateAsync(new NewPhoto { FileId = stored.Id });
                if (!result.IsSuccess)
                {
                    _uploadError = result.Error;
                    Snackbar.Add($"Couldn’t add “{file.Name}”: {_uploadError}", Severity.Error);
                    return null;
                }

                if (result.Value is { } created)
                {
                    ids.Add(created.PhotoId);
                }
            }
            catch (Exception ex)
            {
                _uploadError = ex.Message;
                Snackbar.Add($"Upload failed: {ex.Message}", Severity.Error);
                return null;
            }
        }

        return ids;
    }

    private async Task DeleteAsync()
    {
        if (AlbumId is not { } id)
        {
            return;
        }

        _busy = true;
        var ok = (await Albums.DeleteAsync(id)).Toast(Snackbar, "Delete failed", "Album deleted.");
        _busy = false;
        if (ok)
        {
            await OnSaved.InvokeAsync();
            await Close();
        }
    }

    private Task Close() => OpenChanged.InvokeAsync(false);
}
