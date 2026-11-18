using Odyssey.Core.Imaging;

namespace Odyssey.Core.Internet.News.Images;

/// <summary>The outcome of validating a downloaded news image (§6.2).</summary>
/// <param name="Image">The stripped, re-validated image to store, when accepted.</param>
/// <param name="RejectionReason">Why it was rejected (for the log line; never shown to a reader, never with a URL).</param>
public sealed record NewsImageValidationResult(ValidatedImage? Image, string? RejectionReason)
{
    public bool IsAccepted => Image is not null;
}

/// <summary>
/// The news image surface over the shared still-image pipeline (issue #470 §6.2). It adds a POLICY, not a parser: the
/// magic-byte table, the container walk, the animation check, the metadata strip and the re-validation are all
/// <see cref="StillImageValidator"/>'s, the solution's one copy.
///
/// <para>
/// <strong>The declared type is ignored.</strong> A publisher's <c>Content-Type</c> or enclosure type is not trusted:
/// the type is sniffed from the bytes against the three accepted signatures and that is what the pipeline validates
/// against, so a GIF or an SVG labelled <c>image/png</c> is rejected. What is stored is the validator's stripped output,
/// so bytes appended after an image's end marker never reach the disk (AC 43).
/// </para>
/// </summary>
public static class NewsImageValidation
{
    public const int MaxDimension = 4096;

    /// <summary>A news image smaller than this on either axis is a tracking pixel or a spacer (§6.2, AC 25).</summary>
    public const int MinDimension = 32;

    private static readonly string[] AcceptedTypes = ["image/jpeg", "image/png", "image/webp"];

    public static readonly StillImagePolicy Policy = new(AcceptedTypes, "JPEG, PNG or WebP", MaxDimension, "a news image", MinDimension);

    /// <param name="bytes">The downloaded body, already within <paramref name="maxBytes"/> by the streaming cap.</param>
    /// <param name="maxBytes">The resolved <c>NewsImageMaxSizeKb</c> in bytes, clamped by <c>NewsImageSizeCeiling</c>.</param>
    public static NewsImageValidationResult Validate(ReadOnlySpan<byte> bytes, long maxBytes)
    {
        var header = bytes[..Math.Min(FileHeaderSignatures.HeaderLength, bytes.Length)];
        string? sniffed = null;
        foreach (var type in AcceptedTypes)
        {
            if (FileHeaderSignatures.Matches(type, header))
            {
                sniffed = type;
                break;
            }
        }

        if (sniffed is null)
        {
            return new NewsImageValidationResult(null, "not a JPEG, PNG or WebP image");
        }

        try
        {
            return new NewsImageValidationResult(StillImageValidator.Validate(bytes, sniffed, maxBytes, Policy), null);
        }
        catch (DomainValidationException ex)
        {
            return new NewsImageValidationResult(null, ex.Message);
        }
    }
}
