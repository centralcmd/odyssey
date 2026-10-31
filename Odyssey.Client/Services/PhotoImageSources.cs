using System.Security.Claims;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Services;

/// <summary>The caller's two image claims, read once per page or dialog, never per tile (issue #365 §3.3).</summary>
public sealed record PhotoImageAccess(bool CanReadPhotos, bool CanReadFiles)
{
    public static PhotoImageAccess From(ClaimsPrincipal user) =>
        new(user.HasPermission(PermissionClaims.PhotosRead), user.HasPermission(PermissionClaims.FilesRead));
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
/// file id and <c>files.read</c>. A null or empty URL from either builder is absent, so the result never carries
/// <c>""</c>.
/// </summary>
public static class PhotoImageSources
{
    public static PhotoImageSource For(
        Guid? photoId, Guid? fileId, bool thumbnailsReady, ThumbnailSize size,
        PhotoImageAccess access, IPhotosApiClient photos, IFilesApiClient files)
    {
        var thumb = photoId is { } pid && thumbnailsReady && access.CanReadPhotos
            ? Usable(photos.ThumbnailUrl(pid, size))
            : null;
        var original = fileId is { } fid && access.CanReadFiles
            ? Usable(files.ContentUrl(fid))
            : null;
        return new PhotoImageSource(thumb, original);
    }

    private static string? Usable(string? url) => string.IsNullOrEmpty(url) ? null : url;
}
