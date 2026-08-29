using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// Shared mapping between the Photos DTOs, used by both the Photos library (<c>PhotosCard</c>) and the
/// Journal photo integration (<c>JournalCard</c>). Kept in one place so the full-body PUT payload can't
/// drift between call sites — a divergence there silently wipes fields on a favourite/archive toggle
/// (PR #326 review).
/// </summary>
public static class PhotoMappers
{
    /// <summary>
    /// Full-body update payload that preserves every editable field of the current photo; callers flip
    /// only the field they intend to change (e.g. <see cref="UpdatePhoto.Favourite"/> or
    /// <see cref="UpdatePhoto.Archived"/>). <c>FileName</c> is deliberately left unset so a metadata
    /// toggle never touches the underlying file name (rename is a separate, files.update-gated action).
    /// </summary>
    public static UpdatePhoto ToUpdate(ExistingPhoto p) => new()
    {
        Title = p.Title,
        Caption = p.Caption,
        TakenAt = p.TakenAt,
        CapturedLatitude = p.CapturedLatitude,
        CapturedLongitude = p.CapturedLongitude,
        LocationName = p.LocationName,
        PixelWidth = p.PixelWidth,
        PixelHeight = p.PixelHeight,
        Archived = p.Archived is not null,
        Favourite = p.Favourited is not null,
        TagIds = [.. p.TagIds],
        PersonContactIds = [.. p.PersonContactIds],
        AlbumIds = [.. p.AlbumIds],
    };

    /// <summary>Project the full photo down to the list <see cref="PhotoSummary"/> the detail dialog binds.</summary>
    public static PhotoSummary ToSummary(ExistingPhoto p) => new()
    {
        PhotoId = p.PhotoId,
        FileId = p.FileId,
        Title = p.Title,
        TakenAt = p.TakenAt,
        LocationName = p.LocationName,
        PixelWidth = p.PixelWidth,
        PixelHeight = p.PixelHeight,
        Archived = p.Archived,
        Favourited = p.Favourited,
        CreatedByUserId = p.CreatedByUserId,
        CreatedByName = p.CreatedByName,
        TagIds = [.. p.TagIds],
        PersonContactIds = [.. p.PersonContactIds],
        PersonCount = p.PersonContactIds.Count,
        AlbumCount = p.AlbumIds.Count,
    };

    /// <summary>
    /// A minimal summary from just the ids — enough for the detail dialog to render the image and
    /// navigate when the full photo can't be fetched (no photos.read, or the photo was since deleted).
    /// </summary>
    public static PhotoSummary MinimalSummary(Guid photoId, Guid fileId) => new()
    {
        PhotoId = photoId,
        FileId = fileId,
        PersonCount = 0,
        AlbumCount = 0,
    };
}
