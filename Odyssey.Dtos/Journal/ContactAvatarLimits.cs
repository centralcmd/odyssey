namespace Odyssey.Dtos.Journal;

/// <summary>
/// The four numbers bounding a contact image (issue #86 §4), plus what the crop writes.
///
/// <para>
/// <b>Two caps, deliberately different.</b> The <i>source</i> cap is what the crop dialog will open —
/// browser-only, one image at a time, on the user's own action, so it is generous. The <i>stored</i>
/// cap is what the server accepts and is the one that matters: a 4096² PNG under 2 MB is ~67 MB
/// decoded, and the client decodes every avatar in a 50-row list. At 1024² a direct API upload at the
/// cap decodes to ~4 MB.
/// </para>
///
/// <para>
/// These live in <c>Odyssey.Dtos</c> — zero project references, reachable from the WASM client — so the
/// crop dialog names the same symbols the server does. <b>Never write one of these numbers as a literal
/// at a call site</b>: <c>UploadCapSourceTests</c> lints for exactly that, and the effective byte cap is
/// <c>min(global upload cap, <see cref="MaxAvatarBytes"/>)</c> — <c>min</c> is the only correct
/// direction, since a surface may tighten the instance-wide cap but must never override a lowered one.
/// </para>
/// </summary>
public static class ContactAvatarLimits
{
    /// <summary>The stored avatar's own byte cap, before it is tightened against the global upload cap.</summary>
    public const long MaxAvatarBytes = 2 * 1024 * 1024;

    /// <summary>The same cap in whole megabytes, for <c>TightenTo</c> and the dialog's hint.</summary>
    public const int MaxAvatarMegabytes = (int)(MaxAvatarBytes / (1024 * 1024));

    /// <summary>The stored avatar's pixel-dimension cap, on both axes. Authoritative, server-side.</summary>
    public const int MaxAvatarDimension = 1024;

    /// <summary>What the crop dialog will open, in the browser only.</summary>
    public const long MaxSourceBytes = 20 * 1024 * 1024;

    /// <summary>The source's pixel-dimension cap, in the browser only.</summary>
    public const int MaxSourceDimension = 8192;

    /// <summary>The square canvas the crop renders to, on both axes.</summary>
    public const int OutputDimension = 512;

    /// <summary>JPEG quality for a person's photograph; an organization's logo is encoded as PNG.</summary>
    public const double JpegQuality = 0.85;

    /// <summary>
    /// The avatar MIME allow-list — <b>narrower</b> than <c>FileValidationService</c>'s global list and
    /// applied in addition to it, never instead of it. <c>image/gif</c> is absent (animation), and
    /// <c>image/svg+xml</c> is absent because it is active content and this is the product's first
    /// surface that serves untrusted bytes <i>inline</i>.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>How the accepted types are named in user-visible text.</summary>
    public const string TypeLabel = "PNG, JPEG or WebP";

    /// <summary>Whether a stored <c>ContentType</c> is avatar-legal. The read path and all three release
    /// sites ask this, so a mis-pointed <c>AvatarFileId</c> can never stream a tax statement.</summary>
    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType);

    /// <summary>
    /// The file extension for a <b>validated</b> content type. Derived, never hardcoded: the stored
    /// <c>FileName</c> is generated, so the extension has to follow what the bytes actually are.
    /// </summary>
    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
