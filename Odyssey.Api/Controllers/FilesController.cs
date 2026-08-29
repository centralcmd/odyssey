using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly FileService fileService;

    public FilesController(FileService fileService)
    {
        this.fileService = fileService;
    }

    [HttpPost(Name = "PostFile")]
    [Authorize(Policy = FilePermissionClaims.FilesCreate)]
    [Consumes("multipart/form-data")]
    // The transport-level cap comes from the admin-editable upload setting (issue #421 Wave 4), applied
    // per-request by the size-limit middleware; over-cap bodies are rejected with 413 before the action
    // runs. FileValidationService re-checks the same number on the parsed file.
    [UploadSizeLimit]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(FileUploadResponse))]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Upload a file",
        Description = "Upload a file with optional description. Returns file metadata.")]
    public async Task<IActionResult> UploadFile(
        //[FromForm] // Swagger doesn't handle [FromForm] on IFormFile correctly, so we rely on the default binding behavior for multipart/form-data
        IFormFile file,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return this.BadRequestProblem("File is required");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return this.UnauthorizedProblem("User identity is missing from the request.");
        }

        var result = await fileService.UploadFileAsync(file, userId, description, cancellationToken);
        return CreatedAtAction(nameof(GetFileMetadata), new { id = result.Id }, result);
    }

    [HttpGet(Name = "GetFiles")]
    [Authorize(Policy = FilePermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<FileListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List files",
        Description = "List files with search, filtering, sorting, and pagination.")]
    public async Task<IActionResult> GetFiles(
        [FromQuery] FilesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await fileService.ListAsync(query, cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id}", Name = "GetFileMetadata")]
    [Authorize(Policy = FilePermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileMetadataResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get file metadata",
        Description = "Get metadata for a specific file by ID.")]
    public async Task<IActionResult> GetFileMetadata(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var metadata = await fileService.GetFileMetadataAsync(id, cancellationToken);

        if (metadata is null)
        {
            return this.NotFoundProblem($"File ID {id} not found.");
        }

        return Ok(metadata);
    }

    [HttpGet("{id}/content", Name = "DownloadFile")]
    [Authorize(Policy = FilePermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Download file content",
        Description = "Download the binary content of a file.")]
    public async Task<IActionResult> DownloadFile(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var (metadata, content) = await fileService.GetFileContentAsync(id, cancellationToken);

        if (metadata is null || content is null)
        {
            return this.NotFoundProblem($"File ID {id} not found.");
        }

        // Set appropriate headers for file download. nosniff stops the browser from
        // re-interpreting a mislabeled upload (e.g. an SVG/HTML body) and rendering it
        // inline in the app origin — alongside the forced attachment disposition.
        Response.Headers.ContentDisposition = $"attachment; filename=\"{metadata.FileName}\"";
        Response.Headers.ContentType = metadata.ContentType;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";

        return File(content, metadata.ContentType, metadata.FileName);
    }

    [HttpPut("{id}/metadata", Name = "UpdateFileMetadata")]
    [Authorize(Policy = FilePermissionClaims.FilesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileMetadataResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update file metadata",
        Description = "Update metadata for a file (currently only description).")]
    public async Task<IActionResult> UpdateFileMetadata(
        [FromRoute] Guid id,
        [FromBody] UpdateFileMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var updatedMetadata = await fileService.UpdateFileMetadataAsync(id, request, cancellationToken);

        if (updatedMetadata is null)
        {
            return this.NotFoundProblem($"File ID {id} not found.");
        }

        return Ok(updatedMetadata);
    }

    [HttpDelete("{id}", Name = "DeleteFile")]
    [Authorize(Policy = FilePermissionClaims.FilesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Delete file",
        Description = "Delete a file and its associated data.")]
    public async Task<IActionResult> DeleteFile(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await fileService.DeleteFileAsync(id, cancellationToken);

        if (!deleted)
        {
            return this.NotFoundProblem($"File ID {id} not found.");
        }

        return NoContent();
    }
}