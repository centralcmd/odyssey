using System.Net;

namespace Odyssey.Core.Journal.Thumbnails;

/// <summary>
/// One <c>404</c> for every thumbnail miss (issue #362 §5): an unknown photo, a thumbnail not ready and a released
/// object alike, so the response never says which.
/// </summary>
public sealed class PhotoThumbnailNotFoundException() : DomainException("No thumbnail is available for this photo.")
{
    public override int StatusCode => (int)HttpStatusCode.NotFound;

    public override string? Code => "photo-thumbnail-not-found";
}
