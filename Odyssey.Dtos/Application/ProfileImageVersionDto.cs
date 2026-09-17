namespace Odyssey.Dtos.Application;

/// <summary>
/// What <c>POST /api/profile/image</c> returns (issue #94 §7): the new image-version token.
///
/// <para>
/// A <c>200</c> carrying this, rather than a <c>204</c>, because the client <b>needs</b> the token —
/// it is the URL cache key, and without re-keying the <c>src</c> the string would be unchanged,
/// Blazor would emit no DOM update, the browser would never re-request, and the read path's
/// <c>no-cache</c>/<c>ETag</c> machinery would never come into play at all. The replaced picture
/// would simply stay on screen.
/// </para>
/// </summary>
public sealed record ProfileImageVersionDto
{
    /// <summary>The token to re-key the image URL with. Fresh on every write, byte-identical re-uploads included.</summary>
    public Guid ImageVersion { get; set; }
}
