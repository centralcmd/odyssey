using System.Text;
using Microsoft.AspNetCore.Http;
using Odyssey.Api;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// <see cref="FormFileExtensions.ToFileUpload"/> is the one seam between a multipart part and the
/// domain's <c>FileUpload</c> (issue #287 L9), so every field it carries across is pinned here.
/// </summary>
public sealed class FormFileExtensionsTests
{
    [Fact]
    public void ToFileUpload_CarriesNameTypeLengthAndContent()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 body");
        using var body = new MemoryStream(bytes);
        var formFile = new FormFile(body, 0, bytes.Length, "file", "report.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

        var upload = formFile.ToFileUpload();

        Assert.Equal("report.pdf", upload.FileName);
        Assert.Equal("application/pdf", upload.ContentType);
        Assert.Equal(bytes.Length, upload.Length);
        Assert.Equal(bytes, ReadAll(upload.OpenReadStream()));
    }

    [Fact]
    public void ToFileUpload_OpensAFreshStreamPerCall()
    {
        // Validation sniffs the header and the upload then hashes and reads the body, each from its
        // own stream; a shared stream would leave the second reader at the first one's position.
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 body");
        using var body = new MemoryStream(bytes);
        var formFile = new FormFile(body, 0, bytes.Length, "file", "report.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
        var upload = formFile.ToFileUpload();

        using (var first = upload.OpenReadStream())
        {
            first.ReadByte();
        }

        Assert.Equal(bytes, ReadAll(upload.OpenReadStream()));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
