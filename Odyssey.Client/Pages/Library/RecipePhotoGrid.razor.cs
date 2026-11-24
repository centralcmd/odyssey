using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
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
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>The recipe's photos in gallery order; the first is the cover.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<RecipePhotoDto> Photos { get; set; } = [];

    /// <summary>The caller's image claim, read once by the page.</summary>
    [Parameter, EditorRequired] public PhotoImageAccess Access { get; set; } = new(false);

    /// <summary><c>recipes.update</c>: the tiles' menus add Make cover · Move earlier · Move later · Detach.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public EventCallback<RecipePhotoChange> OnChange { get; set; }

    /// <summary>The viewer archived, deleted or edited a library photo: the host re-reads the recipe.</summary>
    [Parameter] public EventCallback OnLibraryChanged { get; set; }

    // The library photos behind the links — favourite, archived and the viewer's details. A link the caller cannot
    // fetch falls back to a minimal summary so the image and the viewer's navigation still work.
    private List<PhotoSummary> _summaries = [];
    private Guid[] _loadedIds = [];
    private bool _canUpdatePhotos;
    private int? _viewerIndex;
    private int _lastShown;

    protected override async Task OnInitializedAsync() =>
        _canUpdatePhotos = (await Auth.GetUserAsync()).HasPermission(PermissionClaims.PhotosUpdate);

    protected override async Task OnParametersSetAsync()
    {
        var ids = Photos.Select(p => p.PhotoId).ToArray();
        if (!ids.SequenceEqual(_loadedIds))
        {
            _loadedIds = ids;
            await HydrateAsync();
        }
    }

    private async Task HydrateAsync()
    {
        var ids = _loadedIds;
        var fulls = await Task.WhenAll(ids.Select(id => PhotosApi.GetAsync(id)));
        if (!ReferenceEquals(ids, _loadedIds))
        {
            return;
        }

        _summaries = [.. ids.Select((id, i) => fulls[i] is { } full ? PhotoMappers.ToSummary(full) : PhotoMappers.MinimalSummary(id))];
    }

    private PhotoSummary? SummaryOf(Guid id) => _summaries.FirstOrDefault(s => s.PhotoId == id);

    /// <summary>A tile's id — the li that holds it; focus returns to its open button when the viewer closes.</summary>
    internal static string TileId(Guid photoId) => $"rc-photo-{photoId:N}";

    private string? ReturnFocusId => _lastShown >= 0 && _lastShown < Photos.Count ? TileId(Photos[_lastShown].PhotoId) : null;

    /// <summary>"Cover" on the first, "Archived" on an archived photo — always visible, in text.</summary>
    internal static IReadOnlyList<string?> Badges(int index, PhotoSummary? summary) =>
        [index == 0 ? "Cover" : null, summary?.Archived is not null ? "Archived" : null];

    private void OpenAt(int index)
    {
        if (_summaries.Count != Photos.Count)
        {
            return;
        }

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

    private async Task OnViewerChangedAsync(PhotoViewerChange change)
    {
        await HydrateAsync();
        if (change.Kind != PhotoViewerChangeKind.Favourite)
        {
            await OnLibraryChanged.InvokeAsync();
        }
    }

    // The heart is the LIBRARY favourite: it changes the photo everywhere it is used.
    private async Task ToggleFavouriteAsync(Guid id)
    {
        var full = await PhotosApi.GetAsync(id);
        if (full is null)
        {
            return;
        }

        var body = PhotoMappers.ToUpdate(full);
        body.Favourite = full.Favourited is null;
        if ((await PhotosApi.UpdateAsync(id, body)).Toast(Snackbar, "Could not update favourite"))
        {
            var i = _summaries.FindIndex(p => p.PhotoId == id);
            if (i >= 0)
            {
                _summaries[i] = _summaries[i] with { Favourited = body.Favourite ? DateTime.UtcNow : null };
            }
        }
    }

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
