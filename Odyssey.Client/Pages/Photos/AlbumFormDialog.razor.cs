using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Dtos.Application;
using Odyssey.Client.Authorization;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

using Odyssey.Client.Pages.Attachments;

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

    // Icon and colour (issue #406). The pickers own them in both modes; a server field error lands on its
    // field, never in a toast.
    private string? _icon;
    private string? _color;
    private string? _iconError;
    private string? _colorError;
    private bool _colorValid = true;
    private bool _colorShowErrors;
    private OdsTagIconPicker? _iconPicker;
    private OdsTagColorPicker? _colorPicker;

    // Edit's load gate (§3.5): set only after GET /api/albums/{id} succeeds. Until then nothing is
    // editable and Save is disabled, so a full-replacement PUT is never built from empty state.
    private bool _seeded;
    private bool _loadFailed;
    private Dictionary<Guid, PhotoSummary> _summaries = [];

    // Create mode's picks, in pick order; edit mode appends straight onto _members.
    private List<Guid> _newIds = [];

    // Thumbnail + label for a photo picked in this session, whose summary the member load never saw.
    private readonly Dictionary<Guid, AttachPhotoItem> _pickedInfo = [];
    private PhotoImageAccess _imageAccess = new(false);
    private bool _pickerOpen;

    private int PhotoCount => IsEdit ? _members.Count : _newIds.Count;

    // Per-instance DOM ids for focus return, and the dialog's live region (WCAG 4.1.3).
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private string _announce = string.Empty;
    private string?[]? _focusAfter;
    private IJSObjectReference? _focusReturnJs;

    private string NewRemoveId(Guid id) => $"pl-newrm-{_uid}-{id}";

    // Removing a staged pick destroys the button that had focus (WCAG 2.4.3): land on the next pick's
    // remove button, else the previous one's, else "Add photos".
    private void RemoveNew(Guid id)
    {
        var index = _newIds.IndexOf(id);
        var neighbour = index + 1 < _newIds.Count ? _newIds[index + 1]
            : index > 0 ? _newIds[index - 1]
            : (Guid?)null;
        _newIds.Remove(id);
        _announce = $"{LabelFor(id)} removed from the album.";
        _focusAfter = [neighbour is { } n ? $"#{NewRemoveId(n)}" : null, $"#pl-addrow-{_uid} button"];
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusAfter is not { } candidates)
            return;
        _focusAfter = null;
        try
        {
            _focusReturnJs ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/focus-return.js");
            await _focusReturnJs.InvokeVoidAsync("focusFirstLater", candidates);
        }
        catch (Exception)
        {
            // Best-effort: the removal is already announced through the live region.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_focusReturnJs is not null)
        {
            try { await _focusReturnJs.DisposeAsync(); } catch (Exception) { /* JS already gone on teardown */ }
        }
    }

    private RenderFragment TitleFragment => builder => builder.AddContent(0, IsEdit ? "Edit album" : "New album");
    private RenderFragment? SubtitleFragment => IsEdit && _loaded
        ? builder => builder.AddContent(0, _name)
        : null;

    /// <summary>
    /// This surface's own, tighter product limit — a cover image is not a general file upload. Kept as
    /// a named constant: a deliberate product decision, not drift. The photo picker applies it as the
    /// smaller of it and the instance-wide cap, so an administrator lowering the global cap still reaches here.
    /// </summary>
    private const int SurfaceMaxMegabytes = 25;

    protected override async Task OnParametersSetAsync()
    {
        var key = AlbumId ?? Guid.Empty;
        if (Open && (!_loaded || _loadedFor != key))
        {
            _loaded = true;
            _loadedFor = key;
            _imageAccess = PhotoImageAccess.From(await Auth.GetUserAsync());
            _name = string.Empty;
            _desc = string.Empty;
            _members = [];
            _cover = null;
            _archived = null;
            _newIds = [];
            _pickedInfo.Clear();
            _icon = null;
            _color = null;
            _iconError = null;
            _colorError = null;
            _colorValid = true;
            _colorShowErrors = false;
            _seeded = false;
            _loadFailed = false;

            if (AlbumId is not null)
            {
                await LoadAlbumAsync();
            }
        }
        else if (!Open)
        {
            _loaded = false;
        }
    }

    private async Task LoadAlbumAsync()
    {
        if (AlbumId is not { } id)
        {
            return;
        }

        var album = await Albums.GetAsync(id);
        if (album is not { } a)
        {
            _loadFailed = true;
            return;
        }

        _name = a.Name;
        _desc = a.Description ?? string.Empty;
        _members = [.. a.PhotoIds];
        _cover = a.CoverPhotoId;
        _archived = a.Archived;
        _icon = a.Icon;
        _color = a.Color;
        _loadFailed = false;
        _seeded = true;

        await LoadSummariesAsync();
    }

    // Retry removes the alert, and with it the focused button, so focus is placed again once the load
    // settles: on the album name when it loaded, back on Retry when it failed again (WCAG 2.4.3, 4.1.3).
    private async Task RetryLoadAsync()
    {
        _loadFailed = false;
        _announce = "Loading the album.";
        StateHasChanged();
        await LoadAlbumAsync();
        _announce = _seeded ? "Album loaded." : "Couldn't load this album.";
        _focusAfter = _seeded ? [$"#pl-albform-{_uid} input"] : [$"#pl-albform-{_uid} button"];
    }

    private void OnIconChanged(string? icon)
    {
        _icon = icon;
        _iconError = null;
    }

    private void OnColorChanged(string? color)
    {
        _color = color;
        _colorError = null;
    }

    private void OnColorValidChanged(bool valid)
    {
        _colorValid = valid;
        if (valid)
        {
            _colorShowErrors = false;
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
    private PhotoImageSource Thumb(PhotoSummary? s) => s is null
        ? PhotoImageSource.None
        : PhotoImageSources.For(s.PhotoId, s.ThumbnailsReady, ThumbnailSize.Small, _imageAccess, Photos);

    // A member picked in this session has no summary yet; its pick carries the file id and a label.
    private string LabelFor(Guid id) =>
        Summary(id) is { } s ? Label(s) : _pickedInfo.GetValueOrDefault(id)?.Name ?? "Photo";

    private PhotoImageSource ThumbFor(Guid id) =>
        Summary(id) is { } s ? Thumb(s)
        : _pickedInfo.TryGetValue(id, out var p)
            ? PhotoImageSources.For(p.PhotoId, p.ThumbnailsReady, ThumbnailSize.Small, _imageAccess, Photos)
        : PhotoImageSource.None;

    private void OpenPicker() => _pickerOpen = true;

    private void AddPicked(IReadOnlyList<AttachPhotoItem> items)
    {
        var target = IsEdit ? _members : _newIds;
        var added = 0;
        foreach (var item in items)
        {
            var id = item.PhotoId;
            if (target.Contains(id))
                continue;
            _pickedInfo[id] = item;
            target.Add(id);
            added++;
        }
        if (added > 0)
            _announce = added == 1 ? "1 photo added to the album." : $"{added} photos added to the album.";
    }

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
        if (string.IsNullOrWhiteSpace(_name) || (IsEdit && !_seeded))
        {
            return;
        }

        // An empty or invalid Custom colour blocks the save; the picker says why and takes focus (§8).
        if (!_colorValid)
        {
            _colorShowErrors = true;
            StateHasChanged();
            if (_colorPicker is not null)
            {
                await _colorPicker.FocusErrorAsync();
            }

            return;
        }

        _busy = true;
        _iconError = null;
        _colorError = null;

        ApiResult result;
        if (AlbumId is { } id)
        {
            // The form owns every field, so it builds the full-replacement body itself (§3.4).
            result = await Albums.UpdateAsync(id, new UpdatePhotoAlbum
            {
                Name = _name.Trim(),
                Description = string.IsNullOrWhiteSpace(_desc) ? null : _desc.Trim(),
                PhotoIds = [.. _members],
                CoverPhotoId = _cover,
                Archived = _archived is not null,
                Icon = _icon,
                Color = _color,
            });
        }
        else
        {
            result = await Albums.CreateAsync(new NewPhotoAlbum
            {
                Name = _name.Trim(),
                Description = string.IsNullOrWhiteSpace(_desc) ? null : _desc.Trim(),
                PhotoIds = [.. _newIds],
                Icon = _icon,
                Color = _color,
            });
        }

        _busy = false;
        var ok = await ShowFieldErrorsAsync(result)
            ? false
            : result.Toast(Snackbar, IsEdit ? "Save failed" : "Create failed", IsEdit ? "Album updated." : "Album created.");
        if (ok)
        {
            await OnSaved.InvokeAsync();
            await Close();
        }
    }

    /// <summary>
    /// Puts a server <c>400</c> on icon or colour onto its field and moves focus there (§7). Returns true
    /// when it did, so the caller shows no toast. Icon wins focus when both fail (first in DOM order).
    /// </summary>
    private async Task<bool> ShowFieldErrorsAsync(ApiResult result)
    {
        _iconError = result.Problem?.ErrorFor("icon");
        _colorError = result.Problem?.ErrorFor("color");
        if (_iconError is null && _colorError is null)
        {
            return false;
        }

        StateHasChanged();
        if (_iconError is not null && _iconPicker is not null)
        {
            await _iconPicker.FocusSelectedAsync();
        }
        else if (_colorPicker is not null)
        {
            await _colorPicker.FocusErrorAsync();
        }

        return true;
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
