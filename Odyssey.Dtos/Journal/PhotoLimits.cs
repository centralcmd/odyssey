namespace Odyssey.Dtos.Journal;

/// <summary>
/// Shared validation bounds for the Photos module (issue #321). Referenced by the request-DTO data
/// annotations (so an over-cap link/filter array is rejected by <c>[ApiController]</c> model validation)
/// and by the services as defense-in-depth for direct (non-HTTP) callers.
/// </summary>
public static class PhotoLimits
{
    /// <summary>Max distinct tag / person / album links a single photo can carry, and the max length of a
    /// tag/person/album filter array on a list query.</summary>
    public const int MaxLinksPerKind = 50;

    /// <summary>Max number of photos a single album may contain.</summary>
    public const int MaxAlbumMembers = 1000;

    public const int MaxTitleLength = 200;
    public const int MaxCaptionLength = 2000;
    public const int MaxLocationNameLength = 256;
    public const int MaxTagNameLength = 64;
    public const int MaxTagDescriptionLength = 256;
    public const int MaxAlbumNameLength = 128;
    public const int MaxAlbumDescriptionLength = 1024;
    public const int MaxPixelDimension = 200000;
}
