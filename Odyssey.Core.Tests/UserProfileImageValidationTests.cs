using Odyssey.Core.Imaging;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Core.Profiles;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Odyssey.TestData.Fixtures;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// AC 13. The profile-picture validator, driven with no HTTP plumbing at all — which is the whole
/// reason the service lives in <c>Odyssey.Core</c> rather than <c>Odyssey.Api</c> (issue #94 §5).
///
/// <para>
/// The behavioural assertions here duplicate very little of <c>ContactAvatarValidationTests</c> on
/// purpose. The pipeline is <b>shared</b> and that suite already pins the walk's three outputs and
/// every rejection; what this suite pins is the part that is <i>not</i> shared — that the profile
/// surface supplies its own caps and vocabulary, and that the two surfaces have not quietly become one
/// policy or drifted into two pipelines.
/// </para>
/// </summary>
public class UserProfileImageValidationTests
{
    private const long Cap = UserProfileImageLimits.MaxImageBytes;

    // ── The surface's own policy ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The extraction's load-bearing property, asserted as a property rather than trusted: the two
    /// surfaces are two <see cref="StillImagePolicy"/> values over ONE validator. If a follow-up ever
    /// gives the profile surface its own parser, this is what says so.
    /// </summary>
    [Fact]
    public void Both_surfaces_are_policies_over_the_one_shared_validator()
    {
        Assert.NotSame(UserProfileImageValidator.Policy, ContactAvatarValidator.Policy);

        // Same numbers today, different policy objects — so a change to one cannot silently move the
        // other, and a divergence is a deliberate edit rather than an accident.
        Assert.Equal(UserProfileImageLimits.MaxImageDimension, UserProfileImageValidator.Policy.MaxDimension);
        Assert.Equal(ContactAvatarLimits.MaxAvatarDimension, ContactAvatarValidator.Policy.MaxDimension);

        // The subject label is what makes the animation rejection read correctly on each surface.
        Assert.Equal("a profile picture", UserProfileImageValidator.Policy.SubjectLabel);
        Assert.Equal("a contact image", ContactAvatarValidator.Policy.SubjectLabel);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public void The_allow_list_is_the_three_still_types(string contentType) =>
        Assert.True(UserProfileImageValidator.Policy.IsAllowedContentType(contentType));

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    [InlineData(null)]
    public void Animated_and_active_content_types_are_off_the_allow_list(string? contentType) =>
        Assert.False(UserProfileImageValidator.Policy.IsAllowedContentType(contentType));

    // ── Accepted inputs ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void A_still_container_of_each_allowed_type_is_accepted(string contentType)
    {
        var source = contentType switch
        {
            "image/png" => ContactImageFixtures.BaselinePng(),
            "image/webp" => ContactImageFixtures.StillWebp(),
            _ => ContactImageFixtures.BaselineJpeg(),
        };

        var validated = UserProfileImageValidator.Validate(source, contentType, Cap);

        Assert.Equal(contentType, validated.ContentType);
        Assert.True(validated.Width > 0 && validated.Height > 0);
        Assert.NotEmpty(validated.Bytes);
    }

    /// <summary>
    /// <c>image/jpg</c> is a synonym browsers really do send. It is normalised on input and
    /// <b>never stored</b> — the stored type has to be the registered one, because the read path
    /// re-checks it against the allow-list and an unnormalised row would read as absent.
    /// </summary>
    [Fact]
    public void image_jpg_is_normalised_and_never_reaches_storage()
    {
        var validated = UserProfileImageValidator.Validate(
            ContactImageFixtures.BaselineJpeg(), "image/jpg", Cap);

        Assert.Equal("image/jpeg", validated.ContentType);
        Assert.True(UserProfileImageLimits.IsAllowedContentType(validated.ContentType));
    }

    // ── Rejections ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_body_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate([], "image/jpeg", Cap));

    [Fact]
    public void A_body_over_the_effective_cap_is_refused_and_the_message_names_the_cap_in_force()
    {
        // The EFFECTIVE cap, which is min(instance, surface) — so an administrator's lowered value is
        // what the message names, not the compiled 2 MB.
        const long lowered = 1024;

        var ex = Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(ContactImageFixtures.MaxDimensionPng(), "image/png", lowered));

        Assert.Contains("MB", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    public void A_type_off_the_allow_list_is_refused_and_the_message_names_the_accepted_types(string contentType)
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(ContactImageFixtures.Gif(), contentType, Cap));

        Assert.Contains(UserProfileImageLimits.TypeLabel, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Magic_bytes_disagreeing_with_the_declared_type_are_refused()
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(ContactImageFixtures.BaselinePng(), "image/jpeg", Cap));

        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("image/webp")]
    [InlineData("image/png")]
    public void An_animated_container_is_refused_rather_than_flattened(string contentType)
    {
        var source = contentType == "image/png"
            ? ContactImageFixtures.AnimatedPng()
            : ContactImageFixtures.AnimatedWebp();

        var ex = Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(source, contentType, Cap));

        // Named as a refusal, not as a degraded read: flattening would store something other than what
        // the user uploaded.
        Assert.Contains("Animated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile picture", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_container_is_refused_rather_than_stored()
    {
        // Truncated mid-container. For this slot an unparseable container is a defect, not a degraded
        // read — there is no partial picture worth keeping.
        var truncated = ContactImageFixtures.BaselineJpeg()[..12];

        Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(truncated, "image/jpeg", Cap));
    }

    [Fact]
    public void An_image_over_the_dimension_cap_is_refused_and_the_message_names_both_numbers()
    {
        var ex = Assert.Throws<DomainValidationException>(
            () => UserProfileImageValidator.Validate(ContactImageFixtures.OverDimensionPng(), "image/png", Cap));

        Assert.Contains("1100", ex.Message, StringComparison.Ordinal);
        Assert.Contains("900", ex.Message, StringComparison.Ordinal);
        Assert.Contains(UserProfileImageLimits.MaxImageDimension.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_exactly_at_the_dimension_cap_is_accepted()
    {
        var validated = UserProfileImageValidator.Validate(ContactImageFixtures.MaxDimensionPng(), "image/png", Cap);

        Assert.Equal(UserProfileImageLimits.MaxImageDimension, validated.Width);
        Assert.Equal(UserProfileImageLimits.MaxImageDimension, validated.Height);
    }

    [Fact]
    public void A_trailer_appended_after_the_terminator_does_not_survive_into_the_stored_bytes()
    {
        // A ZIP after EOI / IEND — the polyglot shape. The walk's ConsumedLength equality is what
        // catches it, and the re-validation is where that equality is enforced.
        foreach (var (source, contentType) in new[]
        {
            (ContactImageFixtures.JpegWithZipTrailer(), "image/jpeg"),
            (ContactImageFixtures.PngWithTrailer(), "image/png"),
        })
        {
            var validated = UserProfileImageValidator.Validate(source, contentType, Cap);

            Assert.True(validated.Bytes.Length < source.Length, "The trailer survived into the stored bytes.");

            var again = ImageContainerWalk.Walk(validated.Bytes, contentType);
            Assert.True(again.IsOk);
            Assert.Equal(validated.Bytes.Length, again.ConsumedLength);
        }
    }

    // ── The metadata strip, verified by an INDEPENDENT parser (AC 14) ──────────────────────────────

    /// <summary>
    /// AC 14. The stored bytes are re-parsed with <c>MetadataExtractor</c> — a third-party parser
    /// sharing no code with the production walk.
    ///
    /// <para>
    /// <b>That independence is the whole point.</b> The validator's own re-validation is a second pass
    /// of the project's own walk, so on its own it is a self-check: a bug in the walk could be
    /// invisible to it. Keep both; collapsing them removes the property, not just a test.
    /// </para>
    ///
    /// <para>
    /// The fixture carries EXIF <b>GPS</b> and an embedded thumbnail specifically — the two payloads
    /// whose survival would be an actual privacy failure rather than a tidiness one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_jpeg_carrying_exif_gps_and_a_thumbnail_is_stored_with_its_metadata_gone()
    {
        var source = ContactImageFixtures.JpegWithExifAndComment();

        // The precondition: the ORACLE must see metadata on the input, or the assertion below proves
        // nothing at all.
        Assert.True(HasMetadataAccordingToTheOracle(source), "The fixture carries no metadata to strip.");

        var validated = UserProfileImageValidator.Validate(source, "image/jpeg", Cap);

        Assert.False(
            HasMetadataAccordingToTheOracle(validated.Bytes),
            "An independent parser still finds EXIF/IPTC/XMP/ICC in the stored bytes.");
    }

    [Fact]
    public void A_jfif_thumbnail_does_not_survive_the_strip()
    {
        // APP0 is the one marker a keep-list is tempted to retain, and it is exactly the one that can
        // smuggle a second image: JFIF carries a thumbnail and JFXX embeds its own. Every APPn is
        // dropped, APP0 included.
        var validated = UserProfileImageValidator.Validate(
            ContactImageFixtures.JpegWithJfifThumbnail(), "image/jpeg", Cap);

        Assert.False(HasMetadataAccordingToTheOracle(validated.Bytes));
        Assert.True(validated.Bytes.Length < ContactImageFixtures.JpegWithJfifThumbnail().Length);
    }

    [Fact]
    public void A_second_image_in_an_mpf_segment_does_not_survive_the_strip()
    {
        var source = ContactImageFixtures.JpegWithMpfSecondImage();
        var validated = UserProfileImageValidator.Validate(source, "image/jpeg", Cap);

        Assert.True(validated.Bytes.Length < source.Length, "The MPF payload survived.");
        Assert.False(HasMetadataAccordingToTheOracle(validated.Bytes));
    }

    [Fact]
    public void A_png_carrying_text_chunks_and_a_colour_profile_is_stored_with_them_gone()
    {
        var source = ContactImageFixtures.PngWithTextAndProfile();
        var validated = UserProfileImageValidator.Validate(source, "image/png", Cap);

        Assert.True(validated.Bytes.Length < source.Length);
        Assert.False(HasMetadataAccordingToTheOracle(validated.Bytes));
    }

    /// <summary>
    /// The oracle. <c>MetadataExtractor</c> appears in <b>tests only</b> — a source-lint keeps it off
    /// the request path, where a full EXIF/IPTC/ICC/XMP parse over untrusted input would be a large
    /// parser used to learn two integers the walk already produces.
    /// </summary>
    private static bool HasMetadataAccordingToTheOracle(byte[] bytes)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            using var stream = new MemoryStream(bytes);
            directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(stream);
        }
        catch (MetadataExtractor.ImageProcessingException)
        {
            // Unparseable to the oracle is not "clean"; the validator refuses those before storage, so
            // reaching here would itself be the defect.
            return true;
        }

        // The file-type and per-format frame directories are structural (dimensions, bit depth) rather
        // than metadata payloads, so they are not what is being asserted away.
        return directories.Any(directory =>
            directory is MetadataExtractor.Formats.Exif.ExifDirectoryBase
                or MetadataExtractor.Formats.Exif.ExifThumbnailDirectory
                or MetadataExtractor.Formats.Iptc.IptcDirectory
                or MetadataExtractor.Formats.Xmp.XmpDirectory
                or MetadataExtractor.Formats.Icc.IccDirectory
                or MetadataExtractor.Formats.Jpeg.JpegCommentDirectory
                or MetadataExtractor.Formats.Jfif.JfifDirectory);
    }
}
