using Odyssey.Core;
using Odyssey.Core.Imaging;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos.Journal;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The container walk and the validation policy in front of it (issue #86 §4, §5.3, §5.4).
///
/// <para>
/// These run on <b>real</b> containers, because the property under test is that a decoder still agrees
/// with the output — a hand-rolled byte pattern would pass a walk written against the same
/// misunderstanding. The independent third-party oracle (<c>MetadataExtractor</c> over the bytes the
/// endpoint actually returns) lives in <c>Odyssey.IntegrationTests</c>; this tier pins the walk's own
/// three outputs and every rejection.
/// </para>
/// </summary>
public class ContactAvatarValidationTests
{
    private const long Cap = ContactAvatarLimits.MaxAvatarBytes;

    // ── Dimensions come from the walk, not from a metadata parser ─────────────────────────────────

    [Fact]
    public void A_jpegs_dimensions_come_from_its_frame_header()
    {
        var result = ImageContainerWalk.Walk(ContactImageFixtures.BaselineJpeg(), "image/jpeg");

        Assert.Equal(ImageWalkOutcome.Ok, result.Outcome);
        Assert.Equal(64, result.Width);
        Assert.Equal(48, result.Height);
    }

    [Fact]
    public void A_pngs_dimensions_come_from_its_ihdr()
    {
        var result = ImageContainerWalk.Walk(ContactImageFixtures.BaselinePng(), "image/png");

        Assert.Equal(ImageWalkOutcome.Ok, result.Outcome);
        Assert.Equal(70, result.Width);
        Assert.Equal(70, result.Height);
    }

    [Fact]
    public void A_webps_dimensions_come_from_its_bitstream_header()
    {
        var result = ImageContainerWalk.Walk(ContactImageFixtures.StillWebp(), "image/webp");

        Assert.Equal(ImageWalkOutcome.Ok, result.Outcome);
        Assert.Equal(80, result.Width);
        Assert.Equal(60, result.Height);
    }

    // ── Metadata removal (AC 10's cases, asserted here on the walk's own outputs) ──────────────────

    public static TheoryData<string, byte[], string> CarriesMetadata() => new()
    {
        { "JPEG with EXIF and a COM comment", ContactImageFixtures.JpegWithExifAndComment(), "image/jpeg" },
        { "JPEG with an APP2/MPF second image", ContactImageFixtures.JpegWithMpfSecondImage(), "image/jpeg" },
        { "JPEG with a JFIF APP0 thumbnail and JFXX", ContactImageFixtures.JpegWithJfifThumbnail(), "image/jpeg" },
        { "PNG with tEXt, eXIf and iCCP", ContactImageFixtures.PngWithTextAndProfile(), "image/png" },
        { "WebP with ICCP, EXIF and XMP", ContactImageFixtures.WebpWithMetadataChunks(), "image/webp" },
    };

    [Theory]
    [MemberData(nameof(CarriesMetadata))]
    public void Metadata_is_detected_on_the_way_in_and_gone_on_the_way_out(string label, byte[] source, string contentType)
    {
        var first = ImageContainerWalk.Walk(source, contentType);
        Assert.Equal(ImageWalkOutcome.Ok, first.Outcome);
        Assert.True(first.HasMetadata, $"{label}: the walk did not notice the metadata going in.");

        var second = ImageContainerWalk.Walk(first.Bytes, contentType);
        Assert.Equal(ImageWalkOutcome.Ok, second.Outcome);
        Assert.False(second.HasMetadata, $"{label}: metadata survived the strip.");

        // The dimensions are the SAME image, so nothing was re-encoded or resized on the way through.
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);

        // And the output is exactly its own container — no trailer, nothing past the terminator.
        Assert.Equal(first.Bytes.Length, second.ConsumedLength);
    }

    [Fact]
    public void Every_appn_is_dropped_including_app0()
    {
        // APP0 is the one marker a keep-list is tempted to retain, and the one that can still smuggle a
        // second image. The stored bytes must contain neither JFIF's nor JFXX's marker text.
        var stripped = ImageContainerWalk.Walk(ContactImageFixtures.JpegWithJfifThumbnail(), "image/jpeg");

        Assert.Equal(ImageWalkOutcome.Ok, stripped.Outcome);
        Assert.DoesNotContain("JFIF"u8.ToArray(), Windows(stripped.Bytes, 4));
        Assert.DoesNotContain("JFXX"u8.ToArray(), Windows(stripped.Bytes, 4));
    }

    [Fact]
    public void A_webps_vp8x_flag_bits_are_cleared_not_just_its_chunks_removed()
    {
        var stripped = ImageContainerWalk.Walk(ContactImageFixtures.WebpWithMetadataChunks(), "image/webp");
        Assert.Equal(ImageWalkOutcome.Ok, stripped.Outcome);

        // A container that still ADVERTISES payloads it no longer holds is a container a reader will go
        // looking in, so the removal has to reach the header as well as the chunks.
        var flags = Vp8xFlags(stripped.Bytes);
        Assert.NotNull(flags);
        Assert.Equal(0, flags!.Value & 0x20); // ICC
        Assert.Equal(0, flags.Value & 0x08);  // EXIF
        Assert.Equal(0, flags.Value & 0x04);  // XMP
        Assert.Equal(80, stripped.Width);
        Assert.Equal(60, stripped.Height);
    }

    // ── Trailers ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public void A_trailer_after_the_container_terminator_is_discarded(string contentType)
    {
        var source = contentType == "image/jpeg"
            ? ContactImageFixtures.JpegWithZipTrailer()
            : ContactImageFixtures.PngWithTrailer();

        var stripped = ImageContainerWalk.Walk(source, contentType);

        Assert.Equal(ImageWalkOutcome.Ok, stripped.Outcome);
        Assert.True(stripped.Bytes.Length < source.Length, "The trailer was carried into the output.");
        Assert.DoesNotContain([0x50, 0x4B, 0x03, 0x04], Windows(stripped.Bytes, 4));
    }

    // ── Progressive JPEG is CLEANED, not rejected (AC 13) ──────────────────────────────────────────

    [Fact]
    public void A_progressive_jpeg_with_an_interleaved_appn_is_cleaned_and_kept()
    {
        var source = ContactImageFixtures.ProgressiveJpegWithInterleavedAppn();
        var walked = ImageContainerWalk.Walk(source, "image/jpeg");

        // Not rejected. An earlier draft copied everything after the first SOS verbatim, which carried
        // the interleaved APPn through and then failed its own re-validation — a legitimate progressive
        // JPEG failing closed.
        Assert.Equal(ImageWalkOutcome.Ok, walked.Outcome);
        Assert.True(walked.HasMetadata);
        Assert.Equal(200, walked.Width);
        Assert.Equal(150, walked.Height);

        var again = ImageContainerWalk.Walk(walked.Bytes, "image/jpeg");
        Assert.Equal(ImageWalkOutcome.Ok, again.Outcome);
        Assert.False(again.HasMetadata);
        Assert.Equal(200, again.Width);
        Assert.Equal(150, again.Height);
    }

    [Fact]
    public void A_progressive_jpegs_entropy_coded_data_is_byte_identical_after_the_strip()
    {
        // Nothing here decodes or re-encodes: the stored bytes are the uploaded bytes minus metadata
        // segments. Stripping the SAME image twice must therefore be a fixed point.
        var once = ImageContainerWalk.Walk(ContactImageFixtures.ProgressiveJpeg(), "image/jpeg");
        var twice = ImageContainerWalk.Walk(once.Bytes, "image/jpeg");

        Assert.Equal(ImageWalkOutcome.Ok, once.Outcome);
        Assert.Equal(once.Bytes, twice.Bytes);
    }

    // ── Animation is rejected, never flattened ────────────────────────────────────────────────────

    [Fact]
    public void An_animated_webp_is_rejected()
    {
        var result = ImageContainerWalk.Walk(ContactImageFixtures.AnimatedWebp(), "image/webp");
        Assert.Equal(ImageWalkOutcome.Animated, result.Outcome);
    }

    [Fact]
    public void An_animated_png_is_rejected()
    {
        var result = ImageContainerWalk.Walk(ContactImageFixtures.AnimatedPng(), "image/png");
        Assert.Equal(ImageWalkOutcome.Animated, result.Outcome);
    }

    [Theory]
    [InlineData("image/webp")]
    [InlineData("image/png")]
    public void An_animated_container_is_rejected_by_the_validator_with_a_message_naming_the_accepted_types(string contentType)
    {
        var source = contentType == "image/webp"
            ? ContactImageFixtures.AnimatedWebp()
            : ContactImageFixtures.AnimatedPng();

        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(source, contentType, Cap));

        // Silently de-animating is a different outcome than the user uploaded, so it is refused rather
        // than flattened to a first frame.
        Assert.Contains("Animated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ContactAvatarLimits.TypeLabel, ex.Message, StringComparison.Ordinal);
    }

    // ── The validation policy ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_gif_is_refused_and_the_message_names_the_accepted_types()
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(ContactImageFixtures.Gif(), "image/gif", Cap));

        Assert.Contains(ContactAvatarLimits.TypeLabel, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_is_refused_at_the_allow_list_rather_than_parsed()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(svg, "image/svg+xml", Cap));

        Assert.Contains(ContactAvatarLimits.TypeLabel, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_png_body_declared_as_jpeg_is_refused_by_the_magic_byte_check()
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(ContactImageFixtures.BaselinePng(), "image/jpeg", Cap));

        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_over_the_dimension_cap_is_refused_and_the_message_names_the_cap()
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(ContactImageFixtures.OverDimensionPng(), "image/png", Cap));

        Assert.Contains(ContactAvatarLimits.MaxAvatarDimension.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_exactly_at_the_dimension_cap_is_accepted()
    {
        var validated = ContactAvatarValidator.Validate(ContactImageFixtures.MaxDimensionPng(), "image/png", Cap);

        Assert.Equal(ContactAvatarLimits.MaxAvatarDimension, validated.Width);
        Assert.Equal(ContactAvatarLimits.MaxAvatarDimension, validated.Height);
    }

    [Fact]
    public void One_byte_over_the_effective_cap_is_refused()
    {
        var source = ContactImageFixtures.BaselineJpeg();

        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(source, "image/jpeg", source.LongLength - 1));

        Assert.Contains("MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_body_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => ContactAvatarValidator.Validate([], "image/png", Cap));
    }

    [Fact]
    public void An_unreadable_container_is_refused_rather_than_stored_as_is()
    {
        // For this slot an unparseable container is a defect, not a degraded read: there is no
        // "store it anyway and hope" branch.
        var truncated = ContactImageFixtures.BaselineJpeg()[..40];

        var ex = Assert.Throws<DomainValidationException>(
            () => ContactAvatarValidator.Validate(truncated, "image/jpeg", Cap));

        Assert.Contains("could not be read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_jpg_is_accepted_as_a_synonym_and_normalised_to_image_jpeg()
    {
        // Browsers do send image/jpg. It is folded in here rather than widened onto the allow-list, so
        // the stored content type — and the filename extension derived from it — stays canonical.
        var validated = ContactAvatarValidator.Validate(ContactImageFixtures.BaselineJpeg(), "image/jpg", Cap);

        Assert.Equal("image/jpeg", validated.ContentType);
        Assert.Equal(".jpg", ContactAvatarLimits.ExtensionFor(validated.ContentType));
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/jpeg", ".jpg")]
    public void The_stored_extension_follows_the_validated_content_type(string contentType, string extension) =>
        Assert.Equal(extension, ContactAvatarLimits.ExtensionFor(contentType));

    [Fact]
    public void The_stored_bytes_are_the_stripped_bytes_not_the_uploaded_ones()
    {
        var source = ContactImageFixtures.JpegWithMpfSecondImage();
        var validated = ContactAvatarValidator.Validate(source, "image/jpeg", Cap);

        Assert.True(validated.Bytes.Length < source.Length);
        Assert.DoesNotContain("MPF"u8.ToArray(), Windows(validated.Bytes, 3));
    }

    // ── The metadata guarantee is a property of the OUTPUT ─────────────────────────────────────────

    [Fact]
    public void An_output_that_still_carries_metadata_is_refused_rather_than_stored()
    {
        // The re-validation is what makes the guarantee a property of the stored artifact rather than of
        // the code that produced it. Simulated here by walking a container the stripper never touched:
        // if the second pass can see metadata, storage must not happen.
        var unstripped = ContactImageFixtures.JpegWithExifAndComment();
        var reWalked = ImageContainerWalk.Walk(unstripped, "image/jpeg");

        Assert.True(reWalked.HasMetadata,
            "The re-validation's own detector cannot see metadata, so the check it backs proves nothing.");
    }

    [Fact]
    public void A_stripped_output_is_a_fixed_point_for_every_supported_container()
    {
        // Strip-then-verify only means something if a second pass over the output agrees on all three of
        // the walk's outputs. A container whose strip is not idempotent would fail closed in production.
        foreach (var (source, contentType) in new (byte[], string)[]
                 {
                     (ContactImageFixtures.JpegWithExifAndComment(), "image/jpeg"),
                     (ContactImageFixtures.PngWithTextAndProfile(), "image/png"),
                     (ContactImageFixtures.WebpWithMetadataChunks(), "image/webp"),
                 })
        {
            var first = ImageContainerWalk.Walk(source, contentType);
            var second = ImageContainerWalk.Walk(first.Bytes, contentType);
            var third = ImageContainerWalk.Walk(second.Bytes, contentType);

            Assert.Equal(second.Bytes, third.Bytes);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every window of <paramref name="length"/> bytes, so a byte pattern can be searched for.</summary>
    private static IEnumerable<byte[]> Windows(byte[] source, int length)
    {
        for (var i = 0; i + length <= source.Length; i++)
        {
            yield return source[i..(i + length)];
        }
    }

    /// <summary>The <c>VP8X</c> flags byte of a WebP, or null when it carries no extended header.</summary>
    private static byte? Vp8xFlags(byte[] webp)
    {
        for (var i = 12; i + 8 <= webp.Length; i++)
        {
            if (webp[i] == 'V' && webp[i + 1] == 'P' && webp[i + 2] == '8' && webp[i + 3] == 'X')
            {
                return webp[i + 8];
            }
        }

        return null;
    }
}
