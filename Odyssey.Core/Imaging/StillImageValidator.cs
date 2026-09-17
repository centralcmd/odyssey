namespace Odyssey.Core.Imaging;

/// <summary>What an accepted still-image upload produced — the bytes that actually get stored.</summary>
/// <param name="Bytes">The stripped container. These are what get hashed, sized and stored.</param>
/// <param name="ContentType">The <b>validated</b> content type, from which a filename extension is derived.</param>
/// <param name="Width">Pixel width, from the container's own header.</param>
/// <param name="Height">Pixel height, from the container's own header.</param>
public sealed record ValidatedImage(byte[] Bytes, string ContentType, int Width, int Height);

/// <summary>
/// The caps and vocabulary one surface applies on top of the shared pipeline. Passed in as a
/// <b>parameter</b> rather than read from a constants class, which is what keeps
/// <see cref="StillImageValidator"/> storage-agnostic: a contact image and a user's profile picture
/// run the identical parser with different numbers (issue #94 §5).
/// </summary>
/// <param name="AllowedContentTypes">
/// The surface's MIME allow-list, narrower than the general upload list and applied in addition to
/// it. No animated container type and no <c>image/svg+xml</c> — this path serves untrusted bytes
/// <i>inline</i>.
/// </param>
/// <param name="TypeLabel">How the accepted types are named in user-visible text ("PNG, JPEG or WebP").</param>
/// <param name="MaxDimension">The stored pixel-dimension cap, on both axes.</param>
/// <param name="SubjectLabel">
/// What the image is, for the two messages that have to name it ("a contact image", "a profile
/// picture"). User-visible, so it reads inside a sentence.
/// </param>
public sealed record StillImagePolicy(
    IReadOnlyList<string> AllowedContentTypes,
    string TypeLabel,
    int MaxDimension,
    string SubjectLabel)
{
    /// <summary>Whether a declared or stored <c>ContentType</c> is legal for this surface.</summary>
    public bool IsAllowedContentType(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType);
}

/// <summary>
/// The product's <b>one</b> still-image validation pipeline: allow-list → magic bytes → container
/// walk → animation → dimensions → metadata strip → re-validation.
///
/// <para>
/// It is deliberately transport-agnostic <i>and</i> storage-agnostic: it takes bytes, a declared
/// content type and a <see cref="StillImagePolicy"/> rather than an <c>IFormFile</c> or a
/// constants class. The vCard import path has no <c>IFormFile</c> at all and must run the
/// <i>identical</i> pipeline; the user profile picture stores into entirely different tables and
/// must too.
/// </para>
///
/// <para>
/// <b>There is exactly one implementation of the walk and the strip in the solution</b> (issue #94
/// §5), and a source-lint asserts it. Two copies of a parser on an untrusted-input path will
/// diverge, and the copy with fewer eyes on it is the one that will.
/// </para>
/// </summary>
public static class StillImageValidator
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
    /// <c>min(instance upload cap, the surface's own byte cap)</c>. <c>min</c> is the only correct
    /// direction: lowering the instance-wide cap must still bind here.
    /// </param>
    /// <param name="policy">The surface's allow-list, dimension cap and user-visible vocabulary.</param>
    public static ValidatedImage Validate(
        ReadOnlySpan<byte> bytes,
        string? declaredContentType,
        long effectiveMaxBytes,
        StillImagePolicy policy)
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
        if (contentType is null || !policy.IsAllowedContentType(contentType))
        {
            throw new DomainValidationException(
                $"'{Describe(declaredContentType)}' is not an accepted image type. "
                + $"Choose a {policy.TypeLabel} image.");
        }

        // Defence in depth over the declared type, from the solution's single signature table, so this
        // path and the general upload path cannot drift on what a JPEG is.
        if (!FileHeaderSignatures.Matches(contentType, bytes[..Math.Min(FileHeaderSignatures.HeaderLength, bytes.Length)]))
        {
            throw new DomainValidationException(
                $"The image content does not match the declared type '{Describe(declaredContentType)}'.");
        }

        var walk = ImageContainerWalk.Walk(bytes, contentType);
        switch (walk.Outcome)
        {
            case ImageWalkOutcome.Animated:
                throw new DomainValidationException(
                    $"Animated images cannot be used as {policy.SubjectLabel}. Choose a still "
                    + $"{policy.TypeLabel} image.");
            case ImageWalkOutcome.Ok:
                break;
            default:
                // For this slot an unreadable container is a defect, not a degraded read.
                throw new DomainValidationException(
                    $"That image could not be read. Choose a {policy.TypeLabel} image.");
        }

        if (walk.Width > policy.MaxDimension || walk.Height > policy.MaxDimension)
        {
            throw new DomainValidationException(
                $"The image is {walk.Width} × {walk.Height} pixels. It must be at most "
                + $"{policy.MaxDimension} × {policy.MaxDimension}.");
        }

        // Stripping cannot grow a container, but the cap is re-applied to the stored bytes because the
        // stored bytes are what the cap is about.
        if (walk.Bytes.Length == 0 || walk.Bytes.Length > effectiveMaxBytes)
        {
            throw new DomainValidationException(
                $"The image must be {Megabytes(effectiveMaxBytes)} or smaller.");
        }

        Reverify(walk, contentType, policy);

        return new ValidatedImage(walk.Bytes, contentType, walk.Width, walk.Height);
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
    /// invisible to it. The independence that makes strip-then-verify meaningful lives in the tests
    /// that assert the stored artifact with <c>MetadataExtractor</c>, a third-party parser sharing no
    /// code with this. Keep both; collapsing them removes the property, not just a test.
    /// </para>
    /// </summary>
    private static void Reverify(ImageWalkResult walk, string contentType, StillImagePolicy policy)
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
                + $"Choose a different {policy.TypeLabel} image.");
        }
    }

    /// <summary>
    /// <c>image/jpg</c> is on the global allow-list as a synonym and browsers do send it; a surface
    /// list names the registered type only, so it is folded in here rather than widened there.
    /// </summary>
    public static string? NormalizeContentType(string? contentType)
    {
        var trimmed = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return trimmed == "image/jpg" ? "image/jpeg" : trimmed;
    }

    /// <summary>
    /// Echoes the caller's declared type back at them for an actionable message. It is the only piece
    /// of caller-supplied text any image error carries, it never reaches a log line (the
    /// attacker-controlled string is a log-injection vector), and it is length-bounded here.
    /// </summary>
    private static string Describe(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? "unknown"
            : contentType.Length > 64 ? contentType[..64] : contentType;

    private static string Megabytes(long bytes) =>
        $"{Math.Round(bytes / (1024d * 1024d), 1)} MB";
}
