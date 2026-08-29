using System.Security.Claims;
using Odyssey.Api.Controllers;
using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Guards the RFC 7807 contract on FilesController: every handled error path must emit an
/// application/problem+json ProblemDetails body rather than a bare NotFound()/Unauthorized().
/// A regression to a bare result (empty body) would otherwise pass unnoticed.
/// </summary>
public class FilesControllerProblemDetailsTests
{
    private static OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static FilesController NewController(OdysseyContext context, ClaimsPrincipal? user = null) =>
        new(new FileService(context, new FileValidationService()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };

    private static void AssertProblem(IActionResult result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("application/problem+json", objectResult.ContentTypes);
    }

    [Fact]
    public async Task GetFileMetadata_WhenMissing_ReturnsNotFoundProblem()
    {
        await using var context = NewContext();
        var controller = NewController(context);

        var result = await controller.GetFileMetadata(Guid.NewGuid());

        AssertProblem(result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DownloadFile_WhenMissing_ReturnsNotFoundProblem()
    {
        await using var context = NewContext();
        var controller = NewController(context);

        var result = await controller.DownloadFile(Guid.NewGuid());

        AssertProblem(result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task UpdateFileMetadata_WhenMissing_ReturnsNotFoundProblem()
    {
        await using var context = NewContext();
        var controller = NewController(context);

        var result = await controller.UpdateFileMetadata(Guid.NewGuid(), new UpdateFileMetadataRequest("desc"));

        AssertProblem(result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DeleteFile_WhenMissing_ReturnsNotFoundProblem()
    {
        await using var context = NewContext();
        var controller = NewController(context);

        var result = await controller.DeleteFile(Guid.NewGuid());

        AssertProblem(result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task UploadFile_WhenUserIdentityMissing_ReturnsUnauthorizedProblem()
    {
        await using var context = NewContext();
        // Authenticated route, but the principal carries no NameIdentifier claim.
        var controller = NewController(context);
        var file = new FormFile(Stream.Null, 0, 10, "file", "f.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

        var result = await controller.UploadFile(file, description: null, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task UploadFile_WhenFileMissing_ReturnsBadRequestProblem()
    {
        await using var context = NewContext();
        var controller = NewController(context);

        var result = await controller.UploadFile(file: null!, description: null, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }
}
