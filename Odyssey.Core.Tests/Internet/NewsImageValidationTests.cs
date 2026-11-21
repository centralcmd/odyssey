using MetadataExtractor;
using Odyssey.Core.Imaging;
using Odyssey.Core.Internet.News.Images;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Core.Profiles;
using Odyssey.Testing.Fixtures;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The news image policy over the shared still-image pipeline (issue #470 §6.2; AC 25, AC 43): a policy, not a second
/// parser; the declared type ignored; the minimum dimension; and the stored bytes being the stripped output.
/// </summary>
public class NewsImageValidationTests
{
    private const long Cap = 5L * 1024 * 1024;

    [Fact]
    public void The_news_surface_is_a_policy_over_the_one_shared_validator()
    {
        Assert.NotSame(ContactAvatarValidator.Policy, NewsImageValidation.Policy);
        Assert.NotSame(UserProfileImageValidator.Policy, NewsImageValidation.Policy);
        Assert.Equal(NewsImageValidation.MaxDimension, NewsImageValidation.Policy.MaxDimension);
        Assert.Equal(32, NewsImageValidation.Policy.MinDimension);
        Assert.Null(ContactAvatarValidator.Policy.MinDimension); // the new member defaults to no minimum
        Assert.Equal(["image/jpeg", "image/png", "image/webp"], NewsImageValidation.Policy.AllowedContentTypes);
    }

    public static TheoryData<string, Func<byte[]>, string> StillImages => new()
    {
        { "jpeg", ContactImageFixtures.BaselineJpeg, "image/jpeg" },
        { "png", ContactImageFixtures.BaselinePng, "image/png" },
        { "webp", ContactImageFixtures.StillWebp, "image/webp" },
    };

    [Theory]
    [MemberData(nameof(StillImages))]
    public void AC25_jpeg_png_and_webp_are_stored_stripped(string label, Func<byte[]> fixture, string expectedType)
    {
        var result = NewsImageValidation.Validate(fixture(), Cap);

        Assert.True(result.IsAccepted, $"{label}: {result.RejectionReason}");
        Assert.Equal(expectedType, result.Image!.ContentType);
        AssertNoMetadata(result.Image.Bytes);
    }

    [Fact]
    public void AC25_metadata_is_stripped_as_seen_by_an_independent_parser()
    {
        var result = NewsImageValidation.Validate(ContactImageFixtures.JpegWithExifAndComment(), Cap);
        Assert.True(result.IsAccepted, result.RejectionReason);
        AssertNoMetadata(result.Image!.Bytes);
    }

    [Fact]
    public void The_declared_type_is_ignored_the_bytes_decide()
    {
        // No content type is passed at all; a PNG is recognised as a PNG from its signature.
        Assert.Equal("image/png", NewsImageValidation.Validate(ContactImageFixtures.BaselinePng(), Cap).Image!.ContentType);
    }

    public static TheoryData<string, Func<byte[]>> Rejected => new()
    {
        { "gif", ContactImageFixtures.Gif },
        { "animated webp", ContactImageFixtures.AnimatedWebp },
        { "animated png", ContactImageFixtures.AnimatedPng },
        { "16x16", ContactImageFixtures.SmallJpeg },
        { "svg", () => "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\"><script>alert(1)</script></svg>"u8.ToArray() },
        { "html", () => "<html><body>not an image</body></html>"u8.ToArray() },
        { "empty", () => [] },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void AC25_gif_svg_animated_and_tiny_images_are_rejected(string label, Func<byte[]> fixture)
    {
        var result = NewsImageValidation.Validate(fixture(), Cap);
        Assert.False(result.IsAccepted, label);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void AC25_an_image_over_the_cap_is_rejected()
    {
        var png = ContactImageFixtures.BaselinePng();
        Assert.False(NewsImageValidation.Validate(png, png.Length - 1).IsAccepted);
        Assert.True(NewsImageValidation.Validate(png, png.Length).IsAccepted);
    }

    [Fact]
    public void Images_larger_than_the_avatar_cap_but_within_4096_are_kept()
    {
        // 1024 x 1024 and 1100 x 900: over the avatar surface's cap, well inside the news surface's.
        Assert.True(NewsImageValidation.Validate(ContactImageFixtures.MaxDimensionPng(), Cap).IsAccepted);
        Assert.True(NewsImageValidation.Validate(ContactImageFixtures.OverDimensionPng(), Cap).IsAccepted);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    public void AC43_bytes_appended_after_the_end_marker_are_not_stored_and_a_second_pass_accepts_the_output(string kind)
    {
        var polyglot = kind == "jpeg" ? ContactImageFixtures.JpegWithZipTrailer() : ContactImageFixtures.PngWithTrailer();
        var first = NewsImageValidation.Validate(polyglot, Cap);

        Assert.True(first.IsAccepted, first.RejectionReason);
        Assert.True(first.Image!.Bytes.Length < polyglot.Length);
        if (kind == "jpeg")
        {
            Assert.True(first.Image.Bytes.AsSpan().IndexOf("PK"u8) < 0, "The appended ZIP reached the stored bytes.");
        }

        var second = NewsImageValidation.Validate(first.Image.Bytes, Cap);
        Assert.True(second.IsAccepted, second.RejectionReason);
        Assert.Equal(first.Image.Bytes, second.Image!.Bytes);
    }

    [Fact]
    public void AC25_the_same_image_validates_to_the_same_bytes_each_time()
    {
        var a = NewsImageValidation.Validate(ContactImageFixtures.BaselineJpeg(), Cap).Image!.Bytes;
        var b = NewsImageValidation.Validate(ContactImageFixtures.BaselineJpeg(), Cap).Image!.Bytes;
        Assert.Equal(a, b);
    }

    private static void AssertNoMetadata(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var directories = ImageMetadataReader.ReadMetadata(stream);
        Assert.DoesNotContain(directories, d => d.Name is "Exif IFD0" or "Exif SubIFD" or "IPTC" or "XMP" or "ICC Profile" or "GPS");
    }
}
