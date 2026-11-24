using MudBlazor;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

/// <summary>
/// The names <see cref="PhotoViewer"/> hydrates a photo's ids with — tags, albums and people — and the option lists
/// its Edit dialog offers. A host that already holds them (the Photos page) passes its own through
/// <see cref="PhotoViewer.Lookups"/>; otherwise the viewer loads them once with <see cref="LoadAsync"/>, asking only
/// for what the caller's claims allow so a missing claim never surfaces as a toast.
/// </summary>
public sealed record PhotoViewerLookups(
    IReadOnlyList<OdsOption> TagOptions,
    Func<string, string?>? TagNameBlocked,
    IReadOnlyList<OdsOption> PeopleOptions,
    IReadOnlyList<OdsOption> AlbumOptions,
    IReadOnlyDictionary<Guid, ExistingPhotoTag> Tags,
    IReadOnlyDictionary<Guid, PhotoAlbumSummary> Albums,
    IReadOnlyDictionary<Guid, string> People)
{
    public static readonly PhotoViewerLookups Empty = new([], null, [], [],
        new Dictionary<Guid, ExistingPhotoTag>(), new Dictionary<Guid, PhotoAlbumSummary>(), new Dictionary<Guid, string>());

    public string TagName(Guid id) => Tags.TryGetValue(id, out var t) ? t.Name : "—";

    public string? TagIcon(Guid id) => Tags.GetValueOrDefault(id)?.Icon;

    public string? TagColor(Guid id) => Tags.GetValueOrDefault(id)?.Color;

    public string PersonName(Guid id) => People.GetValueOrDefault(id, "—");

    public OdsTagChips.Tag? AlbumChip(Guid id) => AlbumVisuals.Chip(Albums.GetValueOrDefault(id));

    /// <summary>The lookups from their three lists — the same option shapes the Photos page builds.</summary>
    public static PhotoViewerLookups From(
        IReadOnlyList<ExistingPhotoTag> tags, IReadOnlyList<PhotoAlbumSummary> albums, IReadOnlyList<ExistingContact> people) =>
        new(
            [.. tags.Select(JournalOverviews.PhotoTagOption)],
            TagNameRule.For(tags.Select(t => new TagNameEntry(t.Name, t.Archived is not null)), TagNameRule.Pages.Photo),
            [.. OdsContactOptions.Active(people).Select(o => o with { Icon = PhotoLibraryOverview.PersonGlyph, IconColor = PhotoLibraryOverview.PersonInk })],
            [.. albums.Select(AlbumVisuals.FilterOption)],
            tags.ToDictionary(t => t.PhotoTagId),
            albums.ToDictionary(a => a.PhotoAlbumId),
            people.ToDictionary(c => c.ContactId, c => c.ResolvedDisplayName));

    /// <summary>Loads the lookups the caller may read; a list it may not read stays empty.</summary>
    public static async Task<PhotoViewerLookups> LoadAsync(
        IPhotoTagsApiClient tagsApi, IAlbumsApiClient albumsApi, IContactsApiClient contactsApi, ISnackbar snackbar,
        bool canReadTags, bool canReadAlbums, bool canReadContacts)
    {
        var tags = canReadTags ? (await tagsApi.ListAllAsync()).ItemsOrToast(snackbar, "photo tags") : [];
        var albums = canReadAlbums ? (await albumsApi.ListAllAsync()).ItemsOrToast(snackbar, "albums") : [];
        var people = canReadContacts ? (await contactsApi.ListAllAsync(types: ["Person"])).ItemsOrToast(snackbar, "people") : [];
        return From(tags, albums, people);
    }
}
