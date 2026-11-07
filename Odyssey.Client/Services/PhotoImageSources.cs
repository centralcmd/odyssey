using System.Security.Claims;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>
/// The caller's image claim, read once per page or dialog, never per tile (issue #365 §3.3). Since issue #428 both the
/// thumbnail and the original are photo data under <c>photos.read</c>; <c>files.read</c> reaches no photo.
/// </summary>
public sealed record PhotoImageAccess(bool CanReadPhotos)
{
    public static PhotoImageAccess From(ClaimsPrincipal user) => new(user.HasPermission(PermissionClaims.PhotosRead));
}

/// <summary>The two candidate URLs for one photo image, tried thumbnail → original → placeholder.</summary>
public sealed record PhotoImageSource(string? Thumb, string? Original)
{
    public static readonly PhotoImageSource None = new(null, null);

    public bool IsEmpty => Thumb is null && Original is null;
}

/// <summary>
/// The one client rule that decides a photo image's sources (issue #365 §3.3; the design system's
/// <c>PhotoImage</c>). The thumbnail needs the photo id, a ready flag and <c>photos.read</c>; the original needs the
/// photo id and <c>photos.read</c> (issue #428 §5.2). A null or empty URL from either builder is absent, so the result never carries
/// <c>""</c>.
/// </summary>
public static class PhotoImageSources
{
    public static PhotoImageSource For(
        Guid? photoId, bool thumbnailsReady, ThumbnailSize size, PhotoImageAccess access, IPhotosApiClient photos)
    {
        var thumb = photoId is { } pid && thumbnailsReady && access.CanReadPhotos
            ? Usable(photos.ThumbnailUrl(pid, size))
            : null;
        var original = photoId is { } id && access.CanReadPhotos
            ? Usable(photos.ContentUrl(id))
            : null;
        return new PhotoImageSource(thumb, original);
    }

    private static string? Usable(string? url) => string.IsNullOrEmpty(url) ? null : url;
}
