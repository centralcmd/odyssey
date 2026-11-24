using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Photos;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>A photo change: the whole ordered id list (first = cover) and what to announce once it is saved.</summary>
public sealed record RecipePhotoChange(Guid[] PhotoIds, string Message);

public partial class RecipePhotoGrid
{
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;

    /// <summary>The recipe's photos in gallery order; the first is the cover.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<RecipePhotoDto> Photos { get; set; } = [];

    /// <summary>The caller's image claim, read once by the page.</summary>
    [Parameter, EditorRequired] public PhotoImageAccess Access { get; set; } = new(false);

    /// <summary><c>recipes.update</c>: the tiles' menus add Make cover · Move earlier · Move later · Detach.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public EventCallback<RecipePhotoChange> OnChange { get; set; }

    /// <summary>The viewer archived, deleted or edited a library photo: the host re-reads the recipe.</summary>
    [Parameter] public EventCallback OnLibraryChanged { get; set; }

    // The viewer's rows, built from the links themselves: each RecipePhotoDto carries the title and the library photo's
    // own favourite and archive state, so no photo is read per tile. The viewer's detail dialog reads the open photo.
    private IReadOnlyList<PhotoSummary> _summaries = [];
    private (Guid, string?, DateTime?, DateTime?, bool)[] _builtFrom = [];
    private PhotoViewer? _viewer;
    private bool _canUpdatePhotos;
    private int? _viewerIndex;
    private int _lastShown;

    protected override async Task OnInitializedAsync() =>
        _canUpdatePhotos = (await Auth.GetUserAsync()).HasPermission(PermissionClaims.PhotosUpdate);

    // The host hands a fresh list on every render; rebuild the rows only when what they show changed, so the viewer
    // keeps its own copy (and its in-place favourite patch) between real changes.
    protected override void OnParametersSet() => Rebuild();

    private void Rebuild()
    {
        var key = Photos.Select(p => (p.PhotoId, p.Title, p.Favourited, p.Archived, p.ThumbnailsReady)).ToArray();
        if (!key.SequenceEqual(_builtFrom))
        {
            _builtFrom = key;
            _summaries = [.. Photos.Select(p => PhotoMappers.LinkSummary(p.PhotoId, p.Title, p.Favourited, p.Archived, p.ThumbnailsReady))];
        }
    }

    /// <summary>A tile's image-button id; focus returns to it when the viewer closes.</summary>
    internal static string TileId(Guid photoId) => $"rc-photo-{photoId:N}";

    private string? ReturnFocusId => _lastShown >= 0 && _lastShown < Photos.Count ? TileId(Photos[_lastShown].PhotoId) : null;

    /// <summary>"Cover" on the first, "Archived" on an archived photo (the link's own state) — always visible, in text.</summary>
    internal static IReadOnlyList<string?> Badges(int index, RecipePhotoDto photo) =>
        [index == 0 ? "Cover" : null, photo.Archived is not null ? "Archived" : null];

    private void OpenAt(int index)
    {
        _lastShown = index;
        _viewerIndex = index;
    }

    private void OnViewerIndexChanged(int? index)
    {
        if (index is { } i)
        {
            _lastShown = i;
        }

        _viewerIndex = index;
    }

    // A favourite (from the tile heart or the open photo) is patched on the link in place; an archive, a delete or an
    // edit changes what the recipe read returns, so the host re-reads the recipe.
    private async Task OnViewerChangedAsync(PhotoViewerChange change)
    {
        if (change.Kind == PhotoViewerChangeKind.Favourite)
        {
            foreach (var link in Photos.Where(p => p.PhotoId == change.PhotoId))
            {
                link.Favourited = PhotoMappers.Toggled(link.Favourited);
            }

            Rebuild();
            return;
        }

        await OnLibraryChanged.InvokeAsync();
    }

    // The heart is the LIBRARY favourite: it changes the photo everywhere it is used. It goes through the viewer, so a
    // tile and the open photo favourite the same way.
    private Task ToggleFavouriteAsync(Guid id) => _viewer?.ToggleFavouriteAsync(id) ?? Task.CompletedTask;

    /// <summary>A tile's name: the photo's title, else its file name, else its position.</summary>
    internal static string NameOf(RecipePhotoDto photo, int index) =>
        PhotoMappers.Label(photo.Title, photo.FileName, $"Photo {index + 1}");

    /// <summary>
    /// One tile's menu: Open, then — with <paramref name="onChange"/> (recipes.update) — divider · Make cover (not on the
    /// cover) · Move earlier (not on the first) · Move later (not on the last) · divider · Detach. The omitted actions
    /// are absent; <see cref="OdsMenu"/> drops an orphaned divider.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildTileActions(
        IReadOnlyList<RecipePhotoDto> photos, int index, EventCallback<RecipePhotoChange> onChange, EventCallback onOpen, object receiver)
    {
        var ids = photos.Select(p => p.PhotoId).ToList();
        var name = NameOf(photos[index], index);
        var count = ids.Count;
        var items = new List<OdsMenuItem>
        {
            new() { Icon = "open_in_full", Label = "Open", AriaLabel = $"Open {name}", OnClick = onOpen },
        };
        if (!onChange.HasDelegate)
        {
            return items;
        }

        EventCallback Raise(Guid[] next, string message) =>
            EventCallback.Factory.Create(receiver, () => onChange.InvokeAsync(new RecipePhotoChange(next, message)));

        items.Add(new OdsMenuItem { Divider = true });
        if (index > 0)
        {
            items.Add(new OdsMenuItem { Icon = "star", Label = "Make cover", AriaLabel = $"Make cover — {name}", OnClick = Raise(Move(ids, index, 0), $"{name} is now the cover.") });
            items.Add(new OdsMenuItem
            {
                Icon = "arrow_back",
                Label = "Move earlier",
                AriaLabel = $"Move earlier — {name}",
                OnClick = Raise(Move(ids, index, index - 1), $"{name} moved to position {index} of {count}."),
            });
        }

        if (index < count - 1)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "arrow_forward",
                Label = "Move later",
                AriaLabel = $"Move later — {name}",
                OnClick = Raise(Move(ids, index, index + 1), $"{name} moved to position {index + 2} of {count}."),
            });
        }

        items.Add(new OdsMenuItem { Divider = true });
        var detached = ids.Where((_, i) => i != index).ToArray();
        var message = index == 0 && count > 1
            ? $"Photo detached. {NameOf(photos[1], 1)} is now the cover."
            : "Photo detached.";
        items.Add(new OdsMenuItem { Icon = "link_off", Label = "Detach", AriaLabel = $"Detach {name}", Danger = true, OnClick = Raise(detached, message) });
        return items;
    }

    private static Guid[] Move(List<Guid> ids, int from, int to)
    {
        var next = ids.ToList();
        var id = next[from];
        next.RemoveAt(from);
        next.Insert(to, id);
        return [.. next];
    }
}
