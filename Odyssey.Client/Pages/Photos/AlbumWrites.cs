using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Photos;

/// <summary>
/// The one place outside the album form that builds an album <c>PUT</c> body (issue #406 §3.4).
/// </summary>
/// <remarks>
/// <c>PUT /api/albums/{id}</c> is full replacement (#404 §5.2): a field left out is cleared. Every partial
/// edit (add photos, remove photos) starts from <see cref="From"/>, which copies every field — the icon
/// and colour included — so it changes only what the caller then sets. A source-lint fails on
/// <c>new UpdatePhotoAlbum</c> anywhere else but <c>AlbumFormDialog</c>, which owns the pickers.
/// </remarks>
public static class AlbumWrites
{
    /// <summary>An update that writes <paramref name="album"/> back unchanged.</summary>
    public static UpdatePhotoAlbum From(ExistingPhotoAlbum album) => new()
    {
        Name = album.Name,
        Description = album.Description,
        PhotoIds = [.. album.PhotoIds],
        CoverPhotoId = album.CoverPhotoId,
        Archived = album.Archived is not null,
        Icon = album.Icon,
        Color = album.Color,
    };
}
