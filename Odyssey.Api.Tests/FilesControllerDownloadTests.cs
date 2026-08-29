using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Guards the safe-download headers on the files content route: a browser-renderable upload (e.g. an
/// SVG) must be forced to download (Content-Disposition: attachment) and never sniffed/rendered
/// inline (X-Content-Type-Options: nosniff), closing the stored-XSS-via-mislabeled-upload vector.
/// </summary>
public class FilesControllerDownloadTests
{
    [Fact]
    public async Task DownloadFile_SetsNosniffAndAttachmentHeaders()
    {
        var options = new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var financeContext = new OdysseyContext(options);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = "test-user",
            FileName = "logo.svg",
            ContentType = "image/svg+xml",
            SizeBytes = 3,
            Sha256Hash = "hash",
            UploadedAtUtc = DateTime.UtcNow,
            Description = null,
            FileBlobId = blob.Id,
            FileBlob = blob,
        };
        financeContext.Add(metadata);
        await financeContext.SaveChangesAsync();

        var controller = new FilesController(new FileService(financeContext, new FileValidationService()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.DownloadFile(metadata.Id);

        Assert.IsAssignableFrom<FileResult>(result);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.StartsWith("attachment", controller.Response.Headers["Content-Disposition"].ToString());
    }
}
