using Odyssey.Core.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Avatar;

/// <summary>What an accepted avatar upload produced — the bytes that actually get stored.</summary>
/// <param name="Bytes">The stripped container. These are what get hashed, sized and stored.</param>
/// <param name="ContentType">The <b>validated</b> content type, from which the filename extension is derived.</param>
/// <param name="Width">Pixel width, from the container's own header.</param>
/// <param name="Height">Pixel height, from the container's own header.</param>
public sealed record ValidatedAvatar(byte[] Bytes, string ContentType, int Width, int Height);

/// <summary>
/// The policy layer over <see cref="FileValidationService"/> for a contact image (issue #86 §4, §5.2).
/// It adds the narrow MIME allow-list, the effective byte cap, the dimension cap and the animation
/// rejection — <b>in addition to</b> the global validation, never instead of it.
///
/// <para>
/// It is deliberately transport-agnostic: it takes bytes and a declared content type rather than an
/// <c>IFormFile</c>, because the vCard import path has no <c>IFormFile</c> at all and must run the
/// <i>identical</i> pipeline — allow-list, magic bytes, byte cap, dimension cap, animation check,
/// metadata strip, output re-validation.
/// </para>
/// </summary>
public static class ContactAvatarValidator
{
    /// <summary>
    /// Validates, strips and re-validates. Throws <see cref="DomainValidationException"/> — which
    /// <c>GlobalExceptionHandler</c> maps to a <c>400</c> with an RFC 7807 body — naming the actual
    /// limit or the accepted types, never echoing a filename or any part of the image.
    /// </summary>
    /// <param name="bytes">The uploaded or decoded container.</param>
    /// <param name="declaredContentType">
    /// The client-declared media type. Attacker-controlled, which is the whole reason the magic-byte
    /// check below exists; it is never what gets logged or stored.
    /// </param>
    /// <param name="effectiveMaxBytes">
    /// <c>min(global upload cap, <see cref="ContactAvatarLimits.MaxAvatarBytes"/>)</c>. <c>min</c> is the
    /// only correct direction: lowering the instance-wide cap must still bind here.
    /// </param>
    public static ValidatedAvatar Validate(ReadOnlySpan<byte> bytes, string? declaredContentType, long effectiveMaxBytes)
    {
        if (bytes.Length == 0)
        {
            throw new DomainValidationException("The image is empty.");
        }

        if (bytes.Length > effectiveMaxBytes)
        {
            throw new DomainValidationException(
                $"The image must be {Megabytes(effectiveMaxBytes)} or smaller. "
                + "Crop a smaller area, or choose a different file.");
        }

        var contentType = NormalizeContentType(declaredContentType);
        if (contentType is null || !ContactAvatarLimits.IsAllowedContentType(contentType))
        {
            throw new DomainValidationException(
                $"'{Describe(declaredContentType)}' is not an accepted image type. "
                + $"Choose a {ContactAvatarLimits.TypeLabel} image.");
        }

        // Defence in depth over the declared type. Promoted from FileValidationService rather than
        // duplicated, so the avatar path and the general upload path cannot drift on what a JPEG is.
        if (!FileValidationService.HeaderMatchesContentType(contentType, bytes[..Math.Min(16, bytes.Length)]))
        {
            throw new DomainValidationException(
                $"The image content does not match the declared type '{Describe(declaredContentType)}'.");
        }

        var walk = ImageContainerWalk.Walk(bytes, contentType);
        switch (walk.Outcome)
        {
            case ImageWalkOutcome.Animated:
                throw new DomainValidationException(
                    "Animated images cannot be used as a contact image. Choose a still "
                    + $"{ContactAvatarLimits.TypeLabel} image.");
            case ImageWalkOutcome.Ok:
                break;
            default:
                // For this slot an unreadable container is a defect, not a degraded read.
                throw new DomainValidationException(
                    $"That image could not be read. Choose a {ContactAvatarLimits.TypeLabel} image.");
        }

        if (walk.Width > ContactAvatarLimits.MaxAvatarDimension || walk.Height > ContactAvatarLimits.MaxAvatarDimension)
        {
            throw new DomainValidationException(
                $"The image is {walk.Width} × {walk.Height} pixels. It must be at most "
                + $"{ContactAvatarLimits.MaxAvatarDimension} × {ContactAvatarLimits.MaxAvatarDimension}.");
        }

        // Stripping cannot grow a container, but the cap is re-applied to the stored bytes because the
        // stored bytes are what the cap is about.
        if (walk.Bytes.Length == 0 || walk.Bytes.Length > effectiveMaxBytes)
        {
            throw new DomainValidationException(
                $"The image must be {Megabytes(effectiveMaxBytes)} or smaller.");
        }

        Reverify(walk, contentType);

        return new ValidatedAvatar(walk.Bytes, contentType, walk.Width, walk.Height);
    }

    /// <summary>
    /// Re-parses the stripped output and requires <b>all three</b> of the walk's outputs to hold: the
    /// same dimensions, zero EXIF/IPTC/XMP/ICC payloads, and still non-animated. An earlier draft
    /// verified only the first two, leaving the animation determination — which gates a stated
    /// non-goal — unchecked.
    ///
    /// <para>
    /// The <c>ConsumedLength</c> equality is the trailer check: a ZIP appended after <c>EOI</c> or
    /// <c>IEND</c> means the output carries bytes the container itself does not account for.
    /// </para>
    ///
    /// <para>
    /// <b>This and the test oracle are deliberately different implementations.</b> This is a second pass
    /// of the project's own walk, so on its own it is a self-check — a bug in the walk could be
    /// invisible to it. The independence that makes strip-then-verify meaningful lives in the
    /// integration test that asserts the stored artifact with <c>MetadataExtractor</c>, a third-party
    /// parser sharing no code with this. Keep both; collapsing them removes the property, not just a test.
    /// </para>
    /// </summary>
    private static void Reverify(ImageWalkResult walk, string contentType)
    {
        var again = ImageContainerWalk.Walk(walk.Bytes, contentType);

        var verified = again.IsOk
            && !again.HasMetadata
            && again.Width == walk.Width
            && again.Height == walk.Height
            && again.ConsumedLength == walk.Bytes.Length;

        if (!verified)
        {
            // An image whose metadata cannot be PROVEN gone is not stored.
            throw new DomainValidationException(
                "That image could not be prepared for storage — its embedded data could not be removed. "
                + $"Choose a different {ContactAvatarLimits.TypeLabel} image.");
        }
    }

    /// <summary>
    /// <c>image/jpg</c> is on the global allow-list as a synonym and browsers do send it; the avatar
    /// list names the registered type only, so it is folded in here rather than widened there.
    /// </summary>
    private static string? NormalizeContentType(string? contentType)
    {
        var trimmed = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return trimmed == "image/jpg" ? "image/jpeg" : trimmed;
    }

    /// <summary>
    /// Echoes the caller's declared type back at them for an actionable message. It is the only piece
    /// of caller-supplied text any avatar error carries, it never reaches a log line (§10.11 keeps the
    /// attacker-controlled string out of logs as an injection vector), and it is length-bounded here.
    /// </summary>
    private static string Describe(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? "unknown"
            : contentType.Length > 64 ? contentType[..64] : contentType;

    private static string Megabytes(long bytes) =>
        $"{Math.Round(bytes / (1024d * 1024d), 1)} MB";
}
