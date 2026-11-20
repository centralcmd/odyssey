using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Testing.FileStorage;
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

        var store = TestFileStores.For(financeContext);
        // The store names a file from an allow-listed type, and SVG is not one; the row is what the download serves, so an
        // SVG row over stored bytes is the risky case this test needs (an older upload, say).
        var metadata = await StoredFileFixtures.StageFileAsync(
            financeContext, store.Writer, [1, 2, 3], "logo.pdf", "application/pdf", uploadedByUserId: "test-user");
        metadata.FileName = "logo.svg";
        metadata.ContentType = "image/svg+xml";
        await financeContext.SaveChangesAsync();

        var controller = new FilesController(new FileService(
            financeContext, new FileValidationService(), writer: store.Writer, store: store.Store, source: store.Source))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.DownloadFile(metadata.Id);

        // The attachment disposition is written by the FileResult when it executes, from its download
        // name — never hand-built on the response (issue #247) — so the download name is what forces it.
        var file = Assert.IsAssignableFrom<FileResult>(result);
        Assert.Equal("logo.svg", file.FileDownloadName);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }
}
