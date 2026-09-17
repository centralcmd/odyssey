namespace Odyssey.Dtos.Application;

/// <summary>
/// The numbers bounding a user's profile picture (issue #94 §4, §6), plus what the crop writes.
/// Mirrors <c>ContactAvatarLimits</c> in shape — the two surfaces run the same pipeline with their
/// own caps, which is exactly why the caps are a parameter rather than something the pipeline reads.
///
/// <para>
/// <b>Two caps, deliberately different.</b> The <i>source</i> cap is what the crop dialog will open —
/// browser-only, one image at a time, on the user's own action, so it is generous. The <i>stored</i>
/// cap is what the server accepts and is the one that matters: at the 1024² stored cap a picture
/// decodes to roughly 4 MB, and the client decodes one per row on a 50-row <c>/users</c> page.
/// </para>
///
/// <para>
/// These live in <c>Odyssey.Dtos</c> — zero project references, reachable from the WASM client — so
/// the crop dialog names the same symbols the server does. <b>Never write one of these numbers as a
/// literal at a call site</b>: <c>UploadCapSourceTests</c> lints for exactly that, and the effective
/// byte cap is <c>min(instance upload cap, <see cref="MaxImageBytes"/>)</c> — <c>min</c> is the only
/// correct direction, since a surface may tighten an instance-wide cap but must never override one an
/// administrator has lowered.
/// </para>
/// </summary>
public static class UserProfileImageLimits
{
    /// <summary>The stored picture's own byte cap, before it is tightened against the global upload cap.</summary>
    public const long MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The same cap in whole megabytes, for <c>TightenTo</c> and the dialog's hint. Not redundant:
    /// <c>TightenTo(int surfaceMegabytes)</c> takes megabytes, so without this the client-side
    /// <c>min()</c> could not be written without a literal — the very thing the lint forbids.
    /// </summary>
    public const int MaxImageMegabytes = (int)(MaxImageBytes / (1024 * 1024));

    /// <summary>The stored picture's pixel-dimension cap, on both axes. Authoritative, server-side.</summary>
    public const int MaxImageDimension = 1024;

    /// <summary>What the crop dialog will open, in the browser only.</summary>
    public const long MaxSourceBytes = 20 * 1024 * 1024;

    /// <summary>The source's pixel-dimension cap, in the browser only.</summary>
    public const int MaxSourceDimension = 8192;

    /// <summary>The square canvas the crop renders to, on both axes.</summary>
    public const int OutputDimension = 512;

    /// <summary>JPEG quality for a person's photograph.</summary>
    public const double JpegQuality = 0.85;

    /// <summary>
    /// The profile-picture MIME allow-list — <b>narrower</b> than <c>FileValidationService</c>'s global
    /// list and applied in addition to it, never instead of it. <c>image/gif</c> is absent (animation),
    /// and <c>image/svg+xml</c> is absent because it is active content and this path serves untrusted
    /// bytes <i>inline</i>.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>How the accepted types are named in user-visible text.</summary>
    public const string TypeLabel = "PNG, JPEG or WebP";

    /// <summary>
    /// Whether a stored <c>ContentType</c> is profile-picture-legal. The read path asks this, so a row
    /// that somehow held a disallowed type reads as absent rather than streaming (issue #94 §10.7).
    /// </summary>
    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType);

    /// <summary>
    /// The file extension for a <b>validated</b> content type. Derived at the one point it is needed,
    /// never stored: a stored filename would be a second, drift-prone answer to what the bytes are
    /// (issue #94 §6).
    /// </summary>
    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
