using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

/// <summary>What a <see cref="PhotoViewer"/> changed, raised through <see cref="PhotoViewer.OnChanged"/>.</summary>
public enum PhotoViewerChangeKind
{
    Favourite,
    Archive,
    Delete,
    Edit,
}

/// <summary>One saved change: which photo, and what happened to it.</summary>
public sealed record PhotoViewerChange(Guid PhotoId, PhotoViewerChangeKind Kind);

/// <summary>
/// THE way a library photo is opened, on every surface that lists library photos (Odyssey Design System ·
/// <c>ui_kits/web/Photos.jsx</c> <c>PhotoViewer</c>, version 1795477868-c9a2): the detail dialog
/// (<see cref="PhotoDetailDialog"/> — the photo large, ← / → and Previous / Next, the details rail, the favourite
/// heart, and Edit · Download · Archive · Delete) and, from Edit, the metadata dialog (<see cref="EditPhotoDialog"/>).
/// So a photo opens the same way, with the same options, wherever it is shown.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a page uses it.</b> Keep it mounted and drive it by index — <c>null</c> means closed:
/// <code>
/// &lt;OdsPhotoTile Name="…" Image="…" OnOpen="() =&gt; _viewerIndex = i" /&gt;
/// &lt;PhotoViewer Photos="_photos" Index="_viewerIndex" IndexChanged="i =&gt; _viewerIndex = i"
///              OnChanged="_ =&gt; ReloadAsync()" /&gt;
/// </code>
/// <c>Photos</c> is the list the surface shows, as <see cref="PhotoSummary"/> rows (a link the caller cannot read
/// becomes <see cref="PhotoMappers.MinimalSummary"/>). The viewer does the writes itself (favourite, archive,
/// delete, edit — each guarded by its <c>photos.*</c> claim as well as hidden without it) and raises
/// <see cref="OnChanged"/> after each one that succeeded, so the host only reloads. Archive and Delete close the
/// viewer; Favourite updates the open photo in place; Edit closes the detail dialog and opens the editor.
/// </para>
/// <para>
/// <b>Archive and Delete are offered on every surface</b> (owner decision, design-system 1795477868-c9a2), gated
/// only by <c>photos.update</c> / <c>photos.delete</c>.
/// </para>
/// <para>
/// <b>Focus</b> returns to whatever opened it — the tile — on close (the detail dialog's <see cref="Components.OdsModal"/>
/// captures it on each opening), or to <see cref="ReturnFocusToId"/> when the opener unmounts with a popover. Esc
/// closes only the viewer, even when it is opened over another dialog: MudBlazor's key interceptor is bound to each
/// dialog's own container, so the dialog beneath never sees the key (<c>ModalStackTests</c>).
/// </para>
/// <para>
/// <b>Names.</b> A host that already holds the photo tags, albums and people passes them as <see cref="Lookups"/>;
/// otherwise the viewer loads them once on first open (only what the caller may read) and again after an edit.
/// </para>
/// </remarks>
public partial class PhotoViewer
{
    [Inject] private IPhotosApiClient PhotosApi { get; set; } = default!;
    [Inject] private IPhotoTagsApiClient PhotoTagsApi { get; set; } = default!;
    [Inject] private IAlbumsApiClient AlbumsApi { get; set; } = default!;
    [Inject] private IContactsApiClient ContactsApi { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>The photos the surface lists, in its order; ← / → walk this list.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<PhotoSummary> Photos { get; set; } = [];

    /// <summary>The open photo's index into <see cref="Photos"/>, or <c>null</c> when the viewer is closed.</summary>
    [Parameter] public int? Index { get; set; }

    /// <summary>Raised on ← / →, and with <c>null</c> when the viewer closes (Esc, close, Archive, Delete, Edit).</summary>
    [Parameter] public EventCallback<int?> IndexChanged { get; set; }

    /// <summary>Raised after each change the viewer saved, so the host can reload its list.</summary>
    [Parameter] public EventCallback<PhotoViewerChange> OnChanged { get; set; }

    /// <summary>The host's own tag, album and people lookups; omit to let the viewer load them.</summary>
    [Parameter] public PhotoViewerLookups? Lookups { get; set; }

    /// <summary>Id (or space-separated chain) to focus on close when the opener is gone — a row-menu item.</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    private List<PhotoSummary> _photos = [];
    private List<PhotoSummary> _source = [];
    private PhotoViewerLookups? _ownLookups;
    private bool _loadingLookups;
    private Guid? _editId;
    private Guid? _editPending;

    private bool _canUpdate;
    private bool _canDelete;
    private bool _canCreateTags;
    private bool _canCreateContacts;
    private bool _canReadTags;
    private bool _canReadAlbums;
    private bool _canReadContacts;

    private bool IsOpen => Index is { } i && i >= 0 && i < _photos.Count;

    private PhotoViewerLookups Effective => Lookups ?? _ownLookups ?? PhotoViewerLookups.Empty;

    protected override async Task OnInitializedAsync()
    {
        var user = await Auth.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.PhotosUpdate);
        _canDelete = user.HasPermission(PermissionClaims.PhotosDelete);
        _canCreateTags = user.HasPermission(PermissionClaims.PhotoTagsCreate);
        _canCreateContacts = user.HasPermission(PermissionClaims.ContactsCreate);
        _canReadTags = user.HasPermission(PermissionClaims.PhotoTagsRead);
        _canReadAlbums = user.HasPermission(PermissionClaims.PhotoAlbumsRead);
        _canReadContacts = user.HasPermission(PermissionClaims.ContactsRead);
    }

    protected override async Task OnParametersSetAsync()
    {
        // A host reload hands over new rows (often in the same List instance, so compare the rows themselves);
        // keep the viewer's own copy, which Favourite patches in place until the host's next change arrives.
        if (!Photos.SequenceEqual(_source))
        {
            _source = [.. Photos];
            _photos = [.. Photos];
        }

        if (Index is not null && Lookups is null && _ownLookups is null && !_loadingLookups)
        {
            await LoadLookupsAsync();
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Edit opens only once the detail dialog has closed and handed focus back to the tile, so the editor
        // captures the tile as its opener and returns focus there in turn.
        if (_editPending is { } id && Index is null)
        {
            _editPending = null;
            _editId = id;
            StateHasChanged();
        }
    }

    private async Task LoadLookupsAsync()
    {
        _loadingLookups = true;
        try
        {
            _ownLookups = await PhotoViewerLookups.LoadAsync(PhotoTagsApi, AlbumsApi, ContactsApi, Snackbar,
                _canReadTags, _canReadAlbums, _canReadContacts);
        }
        finally
        {
            _loadingLookups = false;
        }
    }

    private Task OnIndexChanged(int index) => IndexChanged.InvokeAsync(index);

    private Task CloseAsync() => IndexChanged.InvokeAsync(null);

    private async Task BeginEdit(Guid id)
    {
        if (!_canUpdate)
        {
            return;
        }

        _editPending = id;
        await CloseAsync();
    }

    /// <summary>Toggle a photo's favourite — the entry point a host's tile heart uses, so a tile and the open
    /// photo favourite the same way. A no-op without <c>photos.update</c>; raises <see cref="OnChanged"/>.</summary>
    public async Task ToggleFavouriteAsync(Guid id)
    {
        if (!_canUpdate || await PutAsync(id, (full, body) => body.Favourite = full.Favourited is null, "Could not update favourite") is not { } body)
        {
            return;
        }

        var i = _photos.FindIndex(p => p.PhotoId == id);
        if (i >= 0)
        {
            _photos[i] = _photos[i] with { Favourited = body.Favourite ? DateTime.UtcNow : null };
        }

        await OnChanged.InvokeAsync(new PhotoViewerChange(id, PhotoViewerChangeKind.Favourite));
    }

    internal async Task ToggleArchiveAsync(Guid id)
    {
        if (!_canUpdate)
        {
            return;
        }

        var body = await PutAsync(id, (full, b) => b.Archived = full.Archived is null, "Could not archive photo",
            b => b.Archived ? "Photo archived." : "Photo unarchived.");
        if (body is null)
        {
            return;
        }

        await CloseAsync();
        await OnChanged.InvokeAsync(new PhotoViewerChange(id, PhotoViewerChangeKind.Archive));
    }

    internal async Task DeleteAsync(Guid id)
    {
        if (!_canDelete)
        {
            return;
        }

        if ((await PhotosApi.DeleteAsync(id)).Toast(Snackbar, "Delete failed", "Photo deleted."))
        {
            _photos.RemoveAll(p => p.PhotoId == id);
            await CloseAsync();
            await OnChanged.InvokeAsync(new PhotoViewerChange(id, PhotoViewerChangeKind.Delete));
        }
    }

    private async Task OnEditSavedAsync()
    {
        var id = _editId;
        _editId = null;
        if (Lookups is null)
        {
            // The editor may have created tags or people inline; reload so the rail names them.
            await LoadLookupsAsync();
        }

        if (id is { } saved)
        {
            await OnChanged.InvokeAsync(new PhotoViewerChange(saved, PhotoViewerChangeKind.Edit));
        }
    }

    // A full PUT that preserves every other field (the photo is re-read first).
    private async Task<UpdatePhoto?> PutAsync(Guid id, Action<ExistingPhoto, UpdatePhoto> mutate, string failure,
        Func<UpdatePhoto, string>? success = null)
    {
        var full = await PhotosApi.GetAsync(id);
        if (full is null)
        {
            return null;
        }

        var body = PhotoMappers.ToUpdate(full);
        mutate(full, body);
        return (await PhotosApi.UpdateAsync(id, body)).Toast(Snackbar, failure, success?.Invoke(body)) ? body : null;
    }
}
