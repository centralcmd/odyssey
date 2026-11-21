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
