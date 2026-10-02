using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests;

public class FileServiceTests
{
    // A fresh stream per call so content validation (header sniff) and the upload (hash + read) get
    // independent streams — mirroring real FormFile.OpenReadStream().
    private static FileUpload CreateMockFile(string fileName, string contentType, byte[] content) =>
        new(fileName, contentType, content.Length, () => new MemoryStream(content));

    // A minimal buffer beginning with the %PDF magic number so it passes content-type sniffing.
    // The trailing bytes distinguish otherwise-identical fixtures.
    private static byte[] Pdf(params byte[] tail) => [(byte)'%', (byte)'P', (byte)'D', (byte)'F', .. tail];

    [Fact]
    public async Task UploadFileAsync_ValidFile_ReturnsResponse()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("test.pdf", "application/pdf", Pdf(5));

        var result = await fileService.UploadFileAsync(file, "user-1", "Test description");

        Assert.NotNull(result);
        Assert.Equal("test.pdf", result.FileName);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(5, result.SizeBytes);
        Assert.Equal("Test description", result.Description);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task UploadFileAsync_DescriptionTruncatedAt256Characters()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("doc.pdf", "application/pdf", Pdf(1));
        var longDescription = new string('x', 300);

        var result = await fileService.UploadFileAsync(file, "user-1", longDescription);

        Assert.Equal(256, result.Description!.Length);
    }

    [Fact]
    public async Task GetFileMetadataAsync_ReturnsMetadataWhenFileExists()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("report.pdf", "application/pdf", Pdf(10, 20));

        var uploaded = await fileService.UploadFileAsync(file, "user-2", "My report");

        var metadata = await fileService.GetFileMetadataAsync(uploaded.Id);

        Assert.NotNull(metadata);
        Assert.Equal("report.pdf", metadata!.FileName);
        Assert.Equal(uploaded.Id, metadata.Id);
        Assert.Equal("My report", metadata.Description);
    }

    [Fact]
    public async Task GetFileMetadataAsync_ReturnsNullWhenNotFound()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        var result = await fileService.GetFileMetadataAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsStreamAndMetadata()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var content = Pdf(1, 2, 3, 4);
        var file = CreateMockFile("data.pdf", "application/pdf", content);

        var uploaded = await fileService.UploadFileAsync(file, "user-3", null);

        var (metadata, stream) = await fileService.GetFileContentAsync(uploaded.Id);

        Assert.NotNull(metadata);
        Assert.NotNull(stream);
        Assert.Equal("data.pdf", metadata!.FileName);

        var buffer = new byte[stream!.Length];
        await stream.ReadExactlyAsync(buffer);
        Assert.Equal(content, buffer);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsNullsWhenNotFound()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        var (metadata, stream) = await fileService.GetFileContentAsync(Guid.NewGuid());

        Assert.Null(metadata);
        Assert.Null(stream);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllUploadedFiles()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        await fileService.UploadFileAsync(CreateMockFile("a.pdf", "application/pdf", Pdf(1)), "u", null);
        await fileService.UploadFileAsync(CreateMockFile("b.pdf", "application/pdf", Pdf(2)), "u", null);

        var result = await fileService.ListAsync(new FilesQueryParams { Limit = 100 });

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task ListAsync_FiltersWithSearch()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        await fileService.UploadFileAsync(CreateMockFile("invoice_jan.pdf", "application/pdf", Pdf(1)), "u", null);
        await fileService.UploadFileAsync(CreateMockFile("receipt_feb.pdf", "application/pdf", Pdf(2)), "u", null);

        var result = await fileService.ListAsync(new FilesQueryParams { Search = "invoice" });

        Assert.Single(result.Items);
        Assert.Equal("invoice_jan.pdf", result.Items[0].FileName);
    }

    [Fact]
    public async Task ListAsync_PaginationWorks()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        for (var i = 1; i <= 5; i++)
        {
            await fileService.UploadFileAsync(CreateMockFile($"file{i}.pdf", "application/pdf", Pdf((byte)i)), "u", null);
        }

        var page1 = await fileService.ListAsync(new FilesQueryParams { Offset = 0, Limit = 2 });
        var page2 = await fileService.ListAsync(new FilesQueryParams { Offset = 2, Limit = 2 });
        var page3 = await fileService.ListAsync(new FilesQueryParams { Offset = 4, Limit = 2 });

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Single(page3.Items);
    }

    [Fact]
    public async Task ListAsync_SortsByFilenameAscending()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        await fileService.UploadFileAsync(CreateMockFile("zebra.pdf", "application/pdf", Pdf(1)), "u", null);
        await fileService.UploadFileAsync(CreateMockFile("alpha.pdf", "application/pdf", Pdf(2)), "u", null);

        var result = await fileService.ListAsync(
            new FilesQueryParams { SortBy = FileSortBy.Name, SortDir = SortDirection.Asc, Limit = 100 });

        Assert.Equal("alpha.pdf", result.Items[0].FileName);
        Assert.Equal("zebra.pdf", result.Items[1].FileName);
    }

    [Fact]
    public async Task UpdateFileMetadataAsync_UpdatesDescription()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("note.pdf", "application/pdf", Pdf(1));

        var uploaded = await fileService.UploadFileAsync(file, "u", "Original desc");

        var updated = await fileService.UpdateFileMetadataAsync(uploaded.Id, new UpdateFileMetadataRequest { Description = "New desc" });

        Assert.NotNull(updated);
        Assert.Equal("New desc", updated!.Description);
    }

    [Fact]
    public async Task UpdateFileMetadataAsync_RenamesWhenFileNameProvided()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("note.pdf", "application/pdf", Pdf(1));

        var uploaded = await fileService.UploadFileAsync(file, "u", "Keep desc");

        var updated = await fileService.UpdateFileMetadataAsync(
            uploaded.Id, new UpdateFileMetadataRequest { Description = "Keep desc", FileName = "statement-2026.pdf" });

        Assert.NotNull(updated);
        Assert.Equal("statement-2026.pdf", updated!.FileName);
        Assert.Equal("Keep desc", updated.Description);
    }

    [Fact]
    public async Task UpdateFileMetadataAsync_LeavesFileNameWhenNotProvided()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("note.pdf", "application/pdf", Pdf(1));

        var uploaded = await fileService.UploadFileAsync(file, "u", "desc");

        var updated = await fileService.UpdateFileMetadataAsync(uploaded.Id, new UpdateFileMetadataRequest { Description = "desc" });

        Assert.NotNull(updated);
        Assert.Equal("note.pdf", updated!.FileName);
    }

    // Issue #247: a rename runs the upload path's sanitizer, so no stored name can carry a header- or
    // path-breaking character into a download header, an analysis prompt or an export.
    [Theory]
    [InlineData("evil\r\nSet-Cookie: x=1.pdf", "evilSet-Cookie_ x=1.pdf")]
    [InlineData("quo\"te.pdf", "quo_te.pdf")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("  padded.pdf  ", "padded.pdf")]
    public async Task UpdateFileMetadataAsync_SanitizesRenamedFileName(string requested, string expected)
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var uploaded = await fileService.UploadFileAsync(CreateMockFile("note.pdf", "application/pdf", Pdf(1)), "u", null);

        var updated = await fileService.UpdateFileMetadataAsync(uploaded.Id, new UpdateFileMetadataRequest { FileName = requested });

        Assert.Equal(expected, updated!.FileName);
        Assert.Equal(expected, context.FileMetadata.Single(fm => fm.Id == uploaded.Id).FileName);
    }

    [Theory]
    [InlineData("///")]
    [InlineData("\u0001\u007F")]
    [InlineData("\"<>|")]
    public async Task UpdateFileMetadataAsync_NameWithNothingLeftAfterSanitizing_IsRefusedAndNothingChanges(string requested)
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var uploaded = await fileService.UploadFileAsync(CreateMockFile("note.pdf", "application/pdf", Pdf(1)), "u", "old");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fileService.UpdateFileMetadataAsync(uploaded.Id, new UpdateFileMetadataRequest { Description = "new", FileName = requested }));

        Assert.True(ex.Errors!.ContainsKey("FileName"));
        var row = context.FileMetadata.Single(fm => fm.Id == uploaded.Id);
        Assert.Equal("note.pdf", row.FileName);
        Assert.Equal("old", row.Description);
    }

    [Fact]
    public async Task UpdateFileMetadataAsync_ReturnsNullWhenNotFound()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        var result = await fileService.UpdateFileMetadataAsync(Guid.NewGuid(), new UpdateFileMetadataRequest { Description = "desc" });

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFileAndBlob()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());
        var file = CreateMockFile("todelete.pdf", "application/pdf", Pdf(1, 2));

        var uploaded = await fileService.UploadFileAsync(file, "u", null);

        var deleted = await fileService.DeleteFileAsync(uploaded.Id);

        Assert.True(deleted);
        Assert.Null(await fileService.GetFileMetadataAsync(uploaded.Id));
        Assert.Equal(0, context.FileBlob.Count());
    }

    [Fact]
    public async Task DeleteFileAsync_ReturnsFalseWhenNotFound()
    {
        await using var context = TestContextFactory.Create();
        var fileService = new FileService(context, new FileValidationService());

        var result = await fileService.DeleteFileAsync(Guid.NewGuid());

        Assert.False(result);
    }
}
