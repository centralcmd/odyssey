using Odyssey.Core;
using Odyssey.Core.Finance;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Odyssey.Core.Tests;

public class FileValidationServiceTests
{
    private static IFormFile CreateMockFile(string fileName, string contentType, long size, byte[]? content = null)
    {
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.FileName).Returns(fileName);
        mock.Setup(f => f.ContentType).Returns(contentType);
        mock.Setup(f => f.Length).Returns(size);
        if (content is not null)
        {
            mock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        }

        return mock.Object;
    }

    [Fact]
    public async Task ValidateFile_Succeeds_WithValidPdf()
    {
        var service = new FileValidationService();
        var file = CreateMockFile("doc.pdf", "application/pdf", 100, "%PDF-1.7"u8.ToArray());

        var ex = await Record.ExceptionAsync(() => service.ValidateFileAsync(file));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ValidateFile_Throws_WhenContentDoesNotMatchDeclaredType()
    {
        var service = new FileValidationService();
        // Declared as a PDF, but the bytes are a PNG signature — a mislabeled upload.
        var file = CreateMockFile("fake.pdf", "application/pdf", 100,
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Fact]
    public async Task ValidateFile_Succeeds_ForSignatureLessTextType()
    {
        var service = new FileValidationService();
        // text/plain has no magic number, so the content check is intentionally skipped.
        var file = CreateMockFile("notes.txt", "text/plain", 100, [0x00, 0x01, 0x02]);

        var ex = await Record.ExceptionAsync(() => service.ValidateFileAsync(file));

        Assert.Null(ex);
    }

    public static IEnumerable<object[]> MagicByteCases() => new List<object[]>
    {
        // JPEG
        new object[] { "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0x00 }, true },
        new object[] { "image/jpeg", new byte[] { 0x00, 0x01, 0x02, 0x03 }, false },
        // GIF (both '87a' and '89a' variants)
        new object[] { "image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true },
        new object[] { "image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, true },
        // WebP: RIFF container with a "WEBP" form type at offset 8
        new object[] { "image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, true },
        new object[] { "image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x41, 0x56, 0x49, 0x20 }, false }, // RIFF but AVI
        new object[] { "image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46 }, false }, // RIFF only, too short for the offset-8 read
        // ZIP — also the OOXML container
        new object[] { "application/zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true },
        new object[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true },
        new object[] { "application/zip", new byte[] { 0x00, 0x00, 0x00, 0x00 }, false },
        // Legacy Office (OLE2 compound file)
        new object[] { "application/msword", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, true },
        new object[] { "application/msword", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, false }, // a ZIP mislabeled as .doc
        // Other archives
        new object[] { "application/x-7z-compressed", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, true },
        new object[] { "application/x-rar-compressed", new byte[] { 0x52, 0x61, 0x72, 0x21 }, true },
        // Truncated header shorter than the signature must be rejected, not slice out of range
        new object[] { "application/pdf", new byte[] { 0x25, 0x50 }, false }, // "%P" — shorter than "%PDF"
    };

    [Theory]
    [MemberData(nameof(MagicByteCases))]
    public async Task ValidateFile_EnforcesMagicBytesPerContentType(string contentType, byte[] content, bool shouldAccept)
    {
        var service = new FileValidationService();
        var file = CreateMockFile("file.bin", contentType, content.Length, content);

        var ex = await Record.ExceptionAsync(() => service.ValidateFileAsync(file));

        if (shouldAccept)
        {
            Assert.Null(ex);
        }
        else
        {
            Assert.IsType<DomainValidationException>(ex);
        }
    }

    [Fact]
    public async Task ValidateFile_EnforcesConfiguredCap_FromFileStorageOptions()
    {
        // Proves the validator honours the size limit it is constructed with — the value Program.cs
        // feeds from FileStorageOptions:MaxFileSizeBytes (the size check runs before the content read,
        // so no body is needed). Guards the config-driven cap wiring.
        var options = new FileStorageOptions { MaxFileSizeBytes = 2048 };
        var service = new FileValidationService(options.MaxFileSizeBytes);
        var tooBig = CreateMockFile("big.pdf", "application/pdf", options.MaxFileSizeBytes + 1);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(tooBig));
    }

    [Fact]
    public async Task ValidateFile_Throws_WhenFileIsEmpty()
    {
        var service = new FileValidationService();
        var file = CreateMockFile("empty.pdf", "application/pdf", 0);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Fact]
    public async Task ValidateFile_Throws_WhenFileTooLarge()
    {
        var maxBytes = 1024L;
        var service = new FileValidationService(maxFileSizeBytes: maxBytes);
        var file = CreateMockFile("big.pdf", "application/pdf", maxBytes + 1);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Fact]
    public async Task ValidateFile_Throws_WhenContentTypeNotAllowed()
    {
        var service = new FileValidationService();
        var file = CreateMockFile("script.exe", "application/x-msdownload", 100);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Fact]
    public async Task ValidateFile_Throws_ForSvg_ActiveContentNotAllowed()
    {
        // SVG is an active-content format (can embed <script>); it must not be on the upload allow-list.
        var service = new FileValidationService();
        var file = CreateMockFile("logo.svg", "image/svg+xml", 100,
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Fact]
    public async Task ValidateFile_Throws_WhenContentTypeIsEmpty()
    {
        var service = new FileValidationService();
        var file = CreateMockFile("file.pdf", "", 100);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.ValidateFileAsync(file));
    }

    [Theory]
    [InlineData("doc.pdf", "doc.pdf")]
    [InlineData("path/to/file.pdf", "path_to_file.pdf")]
    [InlineData("C:\\Windows\\file.txt", "C_Windows_file.txt")]
    [InlineData("file<name>.pdf", "file_name_.pdf")]
    [InlineData("file|pipe.pdf", "file_pipe.pdf")]
    public void SanitizeFileName_RemovesDangerousCharacters(string input, string expected)
    {
        var service = new FileValidationService();

        var result = service.SanitizeFileName(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileName_RemovesControlCharacters()
    {
        var service = new FileValidationService();

        var result = service.SanitizeFileName("file\x01\x1Fname.pdf");

        Assert.True(result.All(c => c >= 0x20 || c == '\t'), $"Result contained control chars: {result}");
    }

    [Fact]
    public void SanitizeFileName_ReturnsInputUnchangedForWhitespaceOnly()
    {
        var service = new FileValidationService();

        var result = service.SanitizeFileName("   ");

        Assert.Equal("   ", result);
    }

    [Fact]
    public async Task ComputeSha256Hash_ReturnsConsistentHash()
    {
        var service = new FileValidationService();
        var content = new byte[] { 1, 2, 3, 4, 5 };

        var hash1 = await service.ComputeSha256HashAsync(new MemoryStream(content));
        var hash2 = await service.ComputeSha256HashAsync(new MemoryStream(content));

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash1);
    }

    [Fact]
    public async Task ComputeSha256Hash_DiffersForDifferentContent()
    {
        var service = new FileValidationService();

        var hash1 = await service.ComputeSha256HashAsync(new MemoryStream(new byte[] { 1 }));
        var hash2 = await service.ComputeSha256HashAsync(new MemoryStream(new byte[] { 2 }));

        Assert.NotEqual(hash1, hash2);
    }
}
