using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Client.Pages.Library;

/// <summary>
/// The Recipes filter bar's vocabularies and their mapping onto <see cref="RecipeListFilter"/> (design system
/// components-recipes.md · The list). Pure, so the mapping is pinned by tests.
/// </summary>
public static class RecipeFilters
{
    public const string AnyTime = "any";
    public const string HasKey = "has";
    public const string NoneKey = "none";

    /// <summary>Total time: Any time · Up to 15 min · Up to 30 min · Up to 1 h · Up to 2 h.</summary>
    public static readonly IReadOnlyList<OdsOption> TimeOptions =
    [
        new(AnyTime, "Any time"),
        new("15", "Up to 15 min"),
        new("30", "Up to 30 min"),
        new("60", "Up to 1 h"),
        new("120", "Up to 2 h"),
    ];

    public static readonly IReadOnlyList<OdsOption> PhotoOptions =
    [
        new(HasKey, "Has photos") { Icon = "photo_library", IconColor = "var(--mud-palette-text-secondary)" },
        new(NoneKey, "No photos") { Icon = "hide_image", IconColor = "var(--mud-palette-text-secondary)" },
    ];

    public static readonly IReadOnlyList<OdsOption> FileOptions =
    [
        new(HasKey, "Has files") { Icon = "attach_file", IconColor = "var(--mud-palette-text-secondary)" },
        new(NoneKey, "No files") { Icon = "link_off", IconColor = "var(--mud-palette-text-secondary)" },
    ];

    /// <summary>The newest pick only: the two-option filters (and Status, which the API takes as one value) are one at a time.</summary>
    public static IReadOnlyCollection<string> OneAtATime(IReadOnlyCollection<string>? previous, IReadOnlyCollection<string>? next)
    {
        var now = (next ?? []).ToList();
        if (now.Count <= 1)
        {
            return now;
        }

        var added = now.FirstOrDefault(v => !(previous ?? []).Contains(v)) ?? now[^1];
        return [added];
    }

    /// <summary>"has" → true, "none" → false, nothing → no filter.</summary>
    public static bool? Flag(IReadOnlyCollection<string> values) =>
        values.Count == 1 ? values.Contains(HasKey) : null;

    /// <summary>The Total time bound in minutes, or <c>null</c> for Any time.</summary>
    public static int? MaxMinutes(string? time) =>
        int.TryParse(time, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m)
            ? Math.Clamp(m, 1, RecipeLimits.MaxTotalMinutes)
            : null;

    /// <summary>The sort keys the toolbar uses, mapped onto <see cref="RecipeSortBy"/>.</summary>
    public static RecipeSortBy SortByFor(string key) => key switch
    {
        "updated" => RecipeSortBy.UpdatedAt,
        "created" => RecipeSortBy.CreatedAt,
        "total" => RecipeSortBy.TotalMinutes,
        _ => RecipeSortBy.Title,
    };

    /// <summary>The list query for the filter bar's state. A filter whose claim is missing is never sent (the API refuses it).</summary>
    public static RecipeListFilter Build(
        string? search, IReadOnlyCollection<string> tags, string? time, IReadOnlyCollection<string> photos,
        IReadOnlyCollection<string> files, IReadOnlyCollection<string> status, OdsTableSort sort,
        bool canTags, bool canPhotos, bool canFiles) => new()
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            TagIds = canTags && tags.Count > 0
                ? [.. tags.Select(t => Guid.TryParse(t, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)]
                : null,
            MaxTotalMinutes = MaxMinutes(time),
            HasPhotos = canPhotos ? Flag(photos) : null,
            HasAttachments = canFiles ? Flag(files) : null,
            Status = status.Count == 1 && Enum.TryParse<ArchivalStatus>(status.First(), out var s) ? s : null,
            SortBy = SortByFor(sort.Key),
            SortDir = sort.Dir == OdsSortDirection.Asc ? SortDirection.Asc : SortDirection.Desc,
        };
}
