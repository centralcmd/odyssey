using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Swashbuckle.AspNetCore.Annotations;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The property document endpoints (issue #210) — a separate controller on the <c>api/properties</c>
/// route prefix beside <see cref="PropertyEstimatesController"/> and
/// <see cref="PropertySmartTagsController"/>. Behaviourally the contract-document surface on
/// <see cref="ContractController"/>, sharing its content-type allow-list and download helper through
/// <see cref="DocumentAttachmentExtensions"/>, minus the per-owner file cap.
///
/// <para>
/// A link is addressed by the pair <c>(propertyId, fileId)</c>, where <c>fileId</c> is the
/// <c>FileMetadata.Id</c>. The property always comes from the route, never the body.
/// </para>
/// </summary>
[ApiController]
[Route("api/properties")]
public class PropertyFilesController : ControllerBase
{
    private readonly PropertyFileService service;
    private readonly FileService fileService;
    private readonly IUserDisplayNameResolver displayNames;

    public PropertyFilesController(
        PropertyFileService service, FileService fileService, IUserDisplayNameResolver displayNames)
    {
        this.service = service;
        this.fileService = fileService;
        this.displayNames = displayNames;
    }

    [HttpPost("{propertyId}/files", Name = "AttachPropertyFile")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Attach an already-uploaded document to a property (requires properties.update + files.read).",
        Description = @"files.read is required because attaching makes the file's bytes downloadable
                        through properties.read. The file's server-recorded content type must be PDF,
                        PNG, JPEG or WebP. There is no per-property document cap.")]
    public async Task<IActionResult> AttachFile(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromBody] AttachPropertyFileRequest request, CancellationToken cancellationToken = default)
    {
        // The cheap property check first, so attaching to a missing property 404s without a file read.
        if (!await service.PropertyExists(propertyId, cancellationToken))
        {
            return this.NotFoundProblem($"Property ID {propertyId} not found.");
        }

        if (await this.ValidateAttachableDocumentAsync(
                fileService, request.FileMetadataId, "property", cancellationToken) is { } problem)
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.AttachFile(propertyId, request, userId, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Property ID {propertyId} not found.")
            : CreatedAtRoute("DownloadPropertyFile", new { propertyId, fileId = request.FileMetadataId }, null);
    }

    [HttpGet("{propertyId}/files", Name = "GetPropertyFiles")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingPropertyFile>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the documents attached to a property.",
        Description = @"Unpaged and uncapped, oldest attachment first; metadata only. A property with no
                        documents returns an empty array, never a 404. issuedBy is the contact id only.")]
    public async Task<IActionResult> GetFiles(
        [FromRoute(Name = "propertyId")] Guid propertyId, CancellationToken cancellationToken = default)
    {
        var files = await service.GetFiles(propertyId, cancellationToken);
        if (files is null)
        {
            return this.NotFoundProblem($"Property ID {propertyId} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, files, cancellationToken);
        return Ok(files);
    }

    [HttpPut("{propertyId}/files/{fileId}", Name = "UpdatePropertyFile")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update an attached property document's type and validity metadata.",
        Description = @"A full replacement: an omitted date or issuer clears the stored value; fileType
                        may not be omitted. files.read is deliberately not required — this verb reads no
                        file metadata and takes no file id the caller had not already attached.")]
    public async Task<IActionResult> UpdateFile(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "fileId")] Guid fileId,
        [FromBody] UpdatePropertyFileRequest request, CancellationToken cancellationToken = default)
    {
        return await service.UpdateFile(propertyId, fileId, request, cancellationToken)
            ? NoContent()
            : NotAttached(propertyId, fileId);
    }

    [HttpGet("{propertyId}/files/{fileId}", Name = "DownloadPropertyFile")]
    [Authorize(Policy = PermissionClaims.PropertiesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Download a document attached to a property.")]
    public async Task<IActionResult> DownloadFile(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!await service.IsFileAttached(propertyId, fileId, cancellationToken))
        {
            return NotAttached(propertyId, fileId);
        }

        return await this.StreamDocumentAsync(fileService, fileId, cancellationToken);
    }

    [HttpDelete("{propertyId}/files/{fileId}", Name = "DetachPropertyFile")]
    [Authorize(Policy = PermissionClaims.PropertiesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a document from a property (the underlying file is left intact).")]
    public async Task<IActionResult> DetachFile(
        [FromRoute(Name = "propertyId")] Guid propertyId,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        return await service.DetachFile(propertyId, fileId, cancellationToken)
            ? NoContent()
            : NotAttached(propertyId, fileId);
    }

    // One message for "no such property" and "not attached to it" (issue #210 §5.4, §9).
    private ObjectResult NotAttached(Guid propertyId, Guid fileId) =>
        this.NotFoundProblem($"File ID {fileId} is not attached to property ID {propertyId}.");
}
