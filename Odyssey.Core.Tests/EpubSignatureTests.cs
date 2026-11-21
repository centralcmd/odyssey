using System.Text;
using Odyssey.Core.Imaging;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>The EPUB container signature and the widened header (issue #527 §6.1).</summary>
public class EpubSignatureTests
{
    /// <summary>A local-file header whose first entry is the stored file <c>mimetype</c> holding <paramref name="content"/>.</summary>
    private static byte[] Zip(string firstName, string content)
    {
        var header = new byte[30];
        header[0] = 0x50; header[1] = 0x4B; header[2] = 0x03; header[3] = 0x04;
        header[26] = (byte)firstName.Length;
        return [.. header, .. Encoding.ASCII.GetBytes(firstName), .. Encoding.ASCII.GetBytes(content), .. new byte[16]];
    }

    [Fact]
    public void The_header_is_long_enough_for_epub() => Assert.True(FileHeaderSignatures.HeaderLength >= 58);

    [Fact]
    public void A_real_epub_container_matches() =>
        Assert.True(FileHeaderSignatures.Matches("application/epub+zip", Zip("mimetype", "application/epub+zip")));

    [Fact]
    public void A_plain_zip_declared_epub_does_not_match()
    {
        Assert.False(FileHeaderSignatures.Matches("application/epub+zip", Zip("chapter1.html", "<html></html>.....")));
        Assert.False(FileHeaderSignatures.Matches("application/epub+zip", Zip("mimetype", "application/zip")));
        Assert.True(FileHeaderSignatures.Matches("application/zip", Zip("chapter1.html", "<html></html>.....")));
    }

    /// <summary>
    /// The EPUB OCF rule is a stored, uncompressed <c>mimetype</c> first: a deflated one carries no readable media type at
    /// offset 38, and an extra field shifts it, so both are refused rather than read loosely.
    /// </summary>
    [Fact]
    public void A_compressed_or_shifted_mimetype_does_not_match()
    {
        var compressed = Zip("mimetype", "\u004B\u004C\u00C9\u00CC\u004D\u00D5\u004D\u002C\u0028");
        Assert.False(FileHeaderSignatures.Matches("application/epub+zip", compressed));

        var withExtra = Zip("mimetype", "\0\0\0\0application/epub+zip");
        withExtra[28] = 4;
        Assert.False(FileHeaderSignatures.Matches("application/epub+zip", withExtra));
    }

    /// <summary>Widening the header from 16 to 64 bytes changed no existing signature: each still matches its own type.</summary>
    [Theory]
    [InlineData("application/pdf", "%PDF-1.7")]
    [InlineData("image/png", "\u0089PNG\r\n\u001a\n")]
    [InlineData("image/jpeg", "\u00FF\u00D8\u00FF\u00E0")]
    public void The_wider_header_keeps_the_existing_signatures(string contentType, string prefix)
    {
        var bytes = prefix.Select(c => (byte)c).Concat(new byte[FileHeaderSignatures.HeaderLength]).ToArray();
        Assert.True(FileHeaderSignatures.Matches(contentType, bytes));
        Assert.True(FileHeaderSignatures.Matches(contentType, bytes[..16]));
    }

    [Fact]
    public void A_short_header_never_matches_epub() =>
        Assert.False(FileHeaderSignatures.Matches("application/epub+zip", Zip("mimetype", "application/epub+zip")[..40]));

    [Fact]
    public void Epub_is_allowed_and_has_an_extension()
    {
        Assert.Contains("application/epub+zip", global::Odyssey.Core.Finance.FileValidationService.GetDefaultAllowedMimeTypes());
        Assert.Equal(".epub", global::Odyssey.Core.FileStorage.ContentTypeExtensions.For("application/epub+zip"));
    }
}
