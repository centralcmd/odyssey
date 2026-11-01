using System.Text;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Xmp;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Worker.Thumbnails;
using Xunit;

namespace Odyssey.Worker.Tests.Thumbnails;

/// <summary>
/// The real decoder under the real policy (issue #362 §3.5, §6): what the fake renderer in <c>Odyssey.Core.Tests</c>
/// cannot show — sizes, orientation, frame 0, metadata removal, and refusal of input that is not what it claims.
/// </summary>
public sealed class MagickThumbnailRendererTests
{
    private static readonly MagickThumbnailRenderer Renderer = new();

    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Thumbnails", "Fixtures", name));

    private static ThumbnailRenderResult Render(byte[] bytes, ThumbnailSourceFormat format, int orientation = 1)
    {
        var prepared = PhotoThumbnailPreStrip.Prepare(bytes, format);
        Assert.Null(prepared.FailureCode);
        return Renderer.Render(new ThumbnailRenderRequest(prepared.Bytes, format, orientation));
    }

    private static void AssertWebP(ThumbnailRendition rendition, int width, int height)
    {
        Assert.Equal((width, height), (rendition.Width, rendition.Height));
        using var image = new MagickImage(rendition.WebP);
        Assert.Equal(MagickFormat.WebP, image.Format);
        Assert.Equal(((uint)width, (uint)height), (image.Width, image.Height));
    }

    [Fact]
    public void A_landscape_jpeg_renders_400_and_200_on_the_long_side()
    {
        var result = Render(Fixture("photo-4000x3000.jpg"), ThumbnailSourceFormat.Jpeg);

        Assert.Null(result.FailureCode);
        AssertWebP(result.Medium!, 400, 300);
        AssertWebP(result.Small!, 200, 150);
        Assert.True(result.Medium!.WebP.Length <= PhotoThumbnailRecipe.MaxOutputBytes);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(7)]
    public void A_rotating_orientation_turns_landscape_into_portrait(int orientation)
    {
        var result = Render(Fixture("photo-4000x3000.jpg"), ThumbnailSourceFormat.Jpeg, orientation);

        AssertWebP(result.Medium!, 300, 400);
        AssertWebP(result.Small!, 150, 200);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_non_rotating_orientation_keeps_landscape(int orientation)
    {
        var result = Render(Fixture("photo-4000x3000.jpg"), ThumbnailSourceFormat.Jpeg, orientation);

        AssertWebP(result.Medium!, 400, 300);
    }

    [Fact]
    public void Orientation_3_turns_the_marked_corner_to_the_opposite_one()
    {
        // The fixture's orange block sits in the top-left quarter; 180 degrees moves it bottom-right.
        var upright = Render(Fixture("photo-4000x3000.jpg"), ThumbnailSourceFormat.Jpeg, 1);
        var turned = Render(Fixture("photo-4000x3000.jpg"), ThumbnailSourceFormat.Jpeg, 3);

        Assert.True(IsOrange(upright.Medium!.WebP, 20, 20));
        Assert.False(IsOrange(turned.Medium!.WebP, 20, 20));
        Assert.True(IsOrange(turned.Medium!.WebP, 380, 280));
    }

    [Fact]
    public void A_small_image_is_never_upscaled()
    {
        var result = Render(Fixture("small-150x100.png"), ThumbnailSourceFormat.Png);

        AssertWebP(result.Medium!, 150, 100);
        AssertWebP(result.Small!, 150, 100);
    }

    [Theory]
    [InlineData("progressive-640x480.jpg", ThumbnailSourceFormat.Jpeg)]
    [InlineData("still-320x240.webp", ThumbnailSourceFormat.WebP)]
    [InlineData("still-320x240.gif", ThumbnailSourceFormat.Gif)]
    public void Every_accepted_format_renders(string fixture, ThumbnailSourceFormat format)
    {
        var result = Render(Fixture(fixture), format);

        Assert.Null(result.FailureCode);
        Assert.NotNull(result.Medium);
        Assert.NotNull(result.Small);
    }

    [Theory]
    [InlineData("anim.gif", ThumbnailSourceFormat.Gif)]
    [InlineData("anim.webp", ThumbnailSourceFormat.WebP)]
    public void An_animation_renders_its_first_frame_as_a_still(string fixture, ThumbnailSourceFormat format)
    {
        var result = Render(Fixture(fixture), format);

        AssertWebP(result.Medium!, 64, 48);
        using var image = new MagickImage(result.Medium!.WebP);
        var pixel = image.GetPixels().GetPixel(32, 24).ToColor()!;
        // Frame 0 is red; frames 1 and 2 are green and blue.
        Assert.True(pixel.R > 150 && pixel.G < 100 && pixel.B < 100, $"Expected red, got {pixel}.");
        using var frames = new MagickImageCollection(result.Medium.WebP);
        Assert.Single(frames);
    }

    [Fact]
    public void A_60_megapixel_png_renders_within_the_memory_limit()
    {
        var result = Render(Fixture("large-9000x6660.png"), ThumbnailSourceFormat.Png);

        AssertWebP(result.Medium!, 400, 296);
    }

    [Fact]
    public void The_output_carries_no_metadata_by_an_independent_reader()
    {
        // EXIF (an IFD0 Make and a GPS IFD) and XMP spliced in by hand: the policy refuses JPEG writes, so ImageMagick
        // cannot build the input. It goes to the renderer directly, past the pre-strip, so the renderer's own strip is
        // what the oracle checks.
        var tagged = WithMetadata(Fixture("photo-4000x3000.jpg"));

        var input = ImageMetadataReader.ReadMetadata(new MemoryStream(tagged));
        Assert.NotEmpty(input.OfType<ExifIfd0Directory>());
        Assert.NotEmpty(input.OfType<GpsDirectory>());
        Assert.NotEmpty(input.OfType<XmpDirectory>());

        var result = Renderer.Render(new ThumbnailRenderRequest(tagged, ThumbnailSourceFormat.Jpeg, 1));

        foreach (var rendition in new[] { result.Medium!, result.Small! })
        {
            var output = ImageMetadataReader.ReadMetadata(new MemoryStream(rendition.WebP));
            Assert.Empty(output.OfType<ExifDirectoryBase>());
            Assert.Empty(output.OfType<GpsDirectory>());
            Assert.Empty(output.OfType<XmpDirectory>());
            Assert.Empty(output.OfType<IccDirectory>());
        }
    }

    [Fact]
    public void A_jpeg_declared_as_png_never_reaches_the_decoder_and_the_decoder_refuses_it_too()
    {
        var jpeg = Fixture("photo-4000x3000.jpg");

        Assert.Equal(PhotoThumbnailFailure.Decode, PhotoThumbnailPreStrip.Prepare(jpeg, ThumbnailSourceFormat.Png).FailureCode);
        Assert.ThrowsAny<MagickException>(() => Renderer.Render(new ThumbnailRenderRequest(jpeg, ThumbnailSourceFormat.Png, 1)));
    }

    [Fact]
    public void A_truncated_jpeg_is_refused_before_the_decoder()
    {
        var jpeg = Fixture("photo-4000x3000.jpg");

        var prepared = PhotoThumbnailPreStrip.Prepare(jpeg[..(jpeg.Length / 2)], ThumbnailSourceFormat.Jpeg);

        Assert.Equal(PhotoThumbnailFailure.Decode, prepared.FailureCode);
    }

    [Theory]
    [InlineData(ThumbnailSourceFormat.Gif)]
    [InlineData(ThumbnailSourceFormat.Png)]
    [InlineData(ThumbnailSourceFormat.Jpeg)]
    [InlineData(ThumbnailSourceFormat.WebP)]
    public void A_vector_payload_under_any_raster_pin_is_parsed_as_that_raster(ThumbnailSourceFormat format)
    {
        // The pin decides the coder, so an SVG or MVG script never meets its interpreter: the raster coder rejects
        // the bytes as malformed. A policy error would mean a vector coder had been reached and refused.
        byte[][] payloads =
        [
            [.. "viewbox 0 0 4 4\nimage over 0,0 0,0 'text:@/etc/hostname'\n"u8],
            [.. "<svg xmlns='http://www.w3.org/2000/svg'><image href='text:@/etc/hostname'/></svg>"u8],
        ];

        foreach (var payload in payloads)
        {
            var thrown = Assert.ThrowsAny<MagickException>(() => Renderer.Render(new ThumbnailRenderRequest(payload, format, 1)));
            Assert.IsNotType<MagickPolicyErrorException>(thrown);
        }
    }

    [Fact]
    public void A_gif_signature_on_a_vector_script_never_runs_the_script()
    {
        var polyglot = Encoding.ASCII.GetBytes("GIF89a\nviewbox 0 0 4 4\nimage over 0,0 0,0 'text:@/etc/hostname'\n");

        try
        {
            var result = Renderer.Render(new ThumbnailRenderRequest(polyglot, ThumbnailSourceFormat.Gif, 1));
            // Read as a (garbage) GIF header, if at all: whatever renders is a raster, never the host name as text.
            Assert.True(result.FailureCode is not null || result.Medium!.Width <= 16);
        }
        catch (MagickException thrown)
        {
            Assert.IsNotType<MagickPolicyErrorException>(thrown);
        }
    }

    /// <summary>AC 4: frame 0 only, so a 5 000-frame GIF neither decodes every frame nor trips the list-length limit.</summary>
    [Fact]
    public void A_five_thousand_frame_gif_renders_its_first_frame_in_under_a_second()
    {
        var gif = AnimatedGif(frames: 5_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = Renderer.Render(new ThumbnailRenderRequest(gif, ThumbnailSourceFormat.Gif, 1));

        watch.Stop();
        Assert.Null(result.FailureCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Took {watch.Elapsed}.");
    }

    public static TheoryData<string, string> Pairs()
    {
        string[] fixtures = ["progressive-640x480.jpg", "small-150x100.png", "still-320x240.gif", "still-320x240.webp"];
        var data = new TheoryData<string, string>();
        foreach (var declared in fixtures)
        {
            foreach (var other in fixtures.Where(f => f != declared))
            {
                data.Add(declared, other);
            }
        }

        return data;
    }

    /// <summary>
    /// AC 12: a file that is one format followed by another, pinned as the first, decodes as the first or is refused —
    /// and pinned as the second (whose signature is not at the start), it is refused. Never the other coder's output.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void A_pairwise_polyglot_is_decoded_as_its_declared_type_or_refused(string declared, string other)
    {
        byte[] polyglot = [.. Fixture(declared), .. Fixture(other)];
        var declaredFormat = FormatOf(declared);
        var expected = SizeOf(declared);

        try
        {
            var result = Renderer.Render(new ThumbnailRenderRequest(polyglot, declaredFormat, 1));
            if (result.FailureCode is null)
            {
                Assert.Equal(Fit(expected, PhotoThumbnailRecipe.MediumLongSide), (result.Medium!.Width, result.Medium.Height));
            }
        }
        catch (MagickException refused)
        {
            Assert.IsNotType<MagickPolicyErrorException>(refused);
        }

        var thrown = Record.Exception(() => Renderer.Render(new ThumbnailRenderRequest(polyglot, FormatOf(other), 1)));
        Assert.True(thrown is MagickException and not MagickPolicyErrorException, $"{other} trailing {declared} was not refused: {thrown?.GetType().Name ?? "rendered"}.");
    }

    private static ThumbnailSourceFormat FormatOf(string fixture) => Path.GetExtension(fixture) switch
    {
        ".jpg" => ThumbnailSourceFormat.Jpeg,
        ".png" => ThumbnailSourceFormat.Png,
        ".gif" => ThumbnailSourceFormat.Gif,
        _ => ThumbnailSourceFormat.WebP,
    };

    private static (int Width, int Height) SizeOf(string fixture)
    {
        var size = System.Text.RegularExpressions.Regex.Match(fixture, @"(\d+)x(\d+)");
        return (int.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(size.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static (int, int) Fit((int Width, int Height) size, int longSide)
    {
        if (Math.Max(size.Width, size.Height) <= longSide)
        {
            return size;
        }

        var scale = (double)longSide / Math.Max(size.Width, size.Height);
        return ((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
    }

    /// <summary>A GIF of 1×1 frames, built by hand: the policy lets ImageMagick write WebP only.</summary>
    private static byte[] AnimatedGif(int frames)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write([0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);
        ms.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        for (var i = 0; i < frames; i++)
        {
            ms.Write([0x21, 0xF9, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);
            ms.Write([0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
            ms.Write([0x02, 0x02, 0x44, 0x01, 0x00]);
        }

        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    [Fact]
    public void A_gif_beyond_the_pixel_limit_is_too_large_at_the_ping()
    {
        var gif = GifHeader(16_385, 4);

        var result = Renderer.Render(new ThumbnailRenderRequest(gif, ThumbnailSourceFormat.Gif, 1));

        Assert.Equal(PhotoThumbnailFailure.TooLarge, result.FailureCode);
    }

    /// <summary>A GIF whose header and image descriptor claim the given size, with a one-pixel LZW stream.</summary>
    private static byte[] GifHeader(ushort width, ushort height)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write(BitConverter.GetBytes(width));
        ms.Write(BitConverter.GetBytes(height));
        ms.Write([0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);
        ms.WriteByte(0x2C);
        ms.Write([0, 0, 0, 0]);
        ms.Write(BitConverter.GetBytes(width));
        ms.Write(BitConverter.GetBytes(height));
        ms.Write([0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B]);
        return ms.ToArray();
    }

    /// <summary>The JPEG with an EXIF APP1 (IFD0 Make, a GPS IFD with GPSLatitudeRef) and an XMP APP1 after SOI.</summary>
    private static byte[] WithMetadata(byte[] jpeg)
    {
        // Little-endian TIFF: header, IFD0 at 8 with Make and the GPS pointer, GPS IFD at 38, Make's text at 56.
        using var tiff = new MemoryStream();
        tiff.Write("II"u8);
        tiff.Write([0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        tiff.Write([0x02, 0x00]);
        tiff.Write([0x0F, 0x01, 0x02, 0x00, 0x08, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x00]);
        tiff.Write([0x25, 0x88, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x26, 0x00, 0x00, 0x00]);
        tiff.Write([0x00, 0x00, 0x00, 0x00]);
        tiff.Write([0x01, 0x00]);
        tiff.Write([0x01, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, (byte)'N', 0x00, 0x00, 0x00]);
        tiff.Write([0x00, 0x00, 0x00, 0x00]);
        tiff.Write("Odyssey\0"u8);
        byte[] exif = [.. "Exif\0\0"u8, .. tiff.ToArray()];
        byte[] xmp = [.. "http://ns.adobe.com/xap/1.0/\0"u8,
            .. "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:creator='Odyssey'/></rdf:RDF></x:xmpmeta>"u8];

        using var output = new MemoryStream();
        output.Write(jpeg.AsSpan(0, 2));
        foreach (var payload in new[] { exif, xmp })
        {
            output.Write([0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            output.Write(payload);
        }

        output.Write(jpeg.AsSpan(2));
        return output.ToArray();
    }

    private static bool IsOrange(byte[] webp, int x, int y)
    {
        using var image = new MagickImage(webp);
        var pixel = image.GetPixels().GetPixel(x, y).ToColor()!;
        return pixel.R > 180 && pixel.G > 120 && pixel.B < 100;
    }
}
