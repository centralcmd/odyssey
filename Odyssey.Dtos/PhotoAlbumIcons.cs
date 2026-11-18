namespace Odyssey.Dtos;

/// <summary>
/// The album twin of <see cref="TagIcons"/> (issues #404, #406): an album's icon is a key from the same
/// catalogue, and only its never-stored default glyph differs.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is <b>not</b> a member of <see cref="TagIcons.All"/>, exactly as
/// <see cref="TagIcons.Default"/> is not: an album selecting the default sends <c>null</c>, so each state
/// has one representation, and <c>[TagIcon]</c> refuses the default key on the write path. It lives in
/// <c>Odyssey.Dtos</c> so the server and the WASM client name one symbol, and it is a <c>const</c> so the
/// client can use it as a dictionary key and in attributes.
/// </remarks>
public static class PhotoAlbumIcons
{
    /// <summary>The album glyph, drawn for an album whose icon is <c>null</c> or no longer in the catalogue.</summary>
    public const string Default = "photo_album";

    /// <summary>The glyph to draw for an album's stored icon: a known catalogue key as-is, anything else <see cref="Default"/>.</summary>
    public static string Glyph(string? key) => TagIcons.IsKnown(key) ? key! : Default;
}
