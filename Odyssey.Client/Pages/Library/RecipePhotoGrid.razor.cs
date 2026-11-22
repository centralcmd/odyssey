using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>A photo change: the whole ordered id list (first = cover) and what to announce once it is saved.</summary>
public sealed record RecipePhotoChange(Guid[] PhotoIds, string Message);

public partial class RecipePhotoGrid
{
    /// <summary>The recipe's photos in gallery order; the first is the cover.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<RecipePhotoDto> Photos { get; set; } = [];

    /// <summary>The caller's image claim, read once by the page.</summary>
    [Parameter, EditorRequired] public PhotoImageAccess Access { get; set; } = new(false);

    /// <summary><c>recipes.update</c>: the tiles carry their ⋯ menus.</summary>
    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public EventCallback<RecipePhotoChange> OnChange { get; set; }

    /// <summary>A tile's name: the photo's title, else its file name, else its position.</summary>
    internal static string NameOf(RecipePhotoDto photo, int index) =>
        !string.IsNullOrWhiteSpace(photo.Title) ? photo.Title!
        : !string.IsNullOrWhiteSpace(photo.FileName) ? photo.FileName!
        : $"Photo {index + 1}";

    /// <summary>
    /// One tile's menu: Make cover (not on the cover) · Move earlier (not on the first) · Move later (not on the last) ·
    /// divider · Detach. The omitted actions are absent; <see cref="OdsMenu"/> drops the orphaned divider.
    /// </summary>
    internal static IReadOnlyList<OdsMenuItem> BuildTileActions(
        IReadOnlyList<RecipePhotoDto> photos, int index, EventCallback<RecipePhotoChange> onChange, object receiver)
    {
        var ids = photos.Select(p => p.PhotoId).ToList();
        var name = NameOf(photos[index], index);
        var count = ids.Count;
        EventCallback Raise(Guid[] next, string message) =>
            EventCallback.Factory.Create(receiver, () => onChange.InvokeAsync(new RecipePhotoChange(next, message)));

        var items = new List<OdsMenuItem>();
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
