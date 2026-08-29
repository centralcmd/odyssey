using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/tax-statements")]
public class TaxStatementController : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    private readonly ILogger<TaxStatementController> logger;
    private readonly TaxStatementService service;
    private readonly FileService fileService;

    public TaxStatementController(
        ILogger<TaxStatementController> logger,
        TaxStatementService service,
        FileService fileService)
    {
        this.logger = logger;
        this.service = service;
        this.fileService = fileService;
    }

    [HttpGet(Name = "GetTaxStatements")]
    [Authorize(Policy = PermissionClaims.TaxesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ExistingTaxStatement>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List tax statements.")]
    public async Task<IActionResult> Get(
        [FromQuery] TaxStatementsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("summary", Name = "GetTaxStatementSummary")]
    [Authorize(Policy = PermissionClaims.TaxesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TaxStatementSummary))]
    [SwaggerOperation(Summary = "Summary rollup: years on file, the fiscal-year bounds and the per-year declared figures.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await service.GetSummary(cancellationToken));
    }

    [HttpGet("{id}", Name = "GetTaxStatement")]
    [Authorize(Policy = PermissionClaims.TaxesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get a tax statement with its selected tag IDs and file metadata.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var statement = await service.Get(id, cancellationToken);
        return statement is null ? this.NotFoundProblem($"Tax statement ID {id} not found.") : Ok(statement);
    }

    [HttpGet("{id}/report", Name = "GetTaxStatementReport")]
    [Authorize(Policy = PermissionClaims.TaxesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TaxStatementReport))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get the reconciliation report (declared vs. derived figures and diffs).")]
    public async Task<IActionResult> GetReport(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var report = await service.GetReport(id, cancellationToken);
        return report is null ? this.NotFoundProblem($"Tax statement ID {id} not found.") : Ok(report);
    }

    [HttpPost(Name = "PostTaxStatement")]
    [Authorize(Policy = PermissionClaims.TaxesCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a tax statement.")]
    public async Task<IActionResult> Post(
        [FromBody] NewTaxStatement request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetTaxStatement", new { id = created.TaxStatementId }, created);
    }

    [HttpPut("{id}", Name = "PutTaxStatement")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a tax statement's declared figures, period, currency, dates and settlement.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateTaxStatement request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Tax statement ID {id} not found.") : Ok(updated);
    }

    [HttpPatch("{id}/status", Name = "PatchTaxStatementStatus")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Set the review status and optional status comment; stamps StatusChangedAt.")]
    public async Task<IActionResult> PatchStatus(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateTaxStatementStatus request, CancellationToken cancellationToken = default)
    {
        var updated = await service.UpdateStatus(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Tax statement ID {id} not found.") : Ok(updated);
    }

    [HttpPut("{id}/tags", Name = "PutTaxStatementTags")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace the selected tax-payment and income tag sets in one call.")]
    public async Task<IActionResult> PutTags(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateTaxStatementTags request, CancellationToken cancellationToken = default)
    {
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        var updated = await service.UpdateTags(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Tax statement ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteTaxStatement")]
    [Authorize(Policy = PermissionClaims.TaxesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Archive (soft-delete) a tax statement.")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Tax statement ID {id} not found.");
    }

    [HttpPost("{id}/files", Name = "AttachTaxStatementFile")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an already-uploaded document to a tax statement.")]
    public async Task<IActionResult> AttachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] AttachTaxStatementFileRequest request, CancellationToken cancellationToken = default)
    {
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        var metadata = await fileService.GetFileMetadataAsync(request.FileId, cancellationToken);
        if (metadata is null)
        {
            return this.NotFoundProblem($"File ID {request.FileId} not found.");
        }

        if (!AllowedContentTypes.Contains(metadata.ContentType))
        {
            return this.BadRequestProblem($"Content type '{metadata.ContentType}' is not allowed for tax statements.");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        await service.AttachFile(id, request.FileId, userId, request.FileType, cancellationToken);
        return CreatedAtRoute("DownloadTaxStatementFile", new { id, fileId = request.FileId }, null);
    }

    [HttpGet("{id}/files/{fileId}", Name = "DownloadTaxStatementFile")]
    [Authorize(Policy = PermissionClaims.TaxesRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Download an attached tax statement document.")]
    public async Task<IActionResult> DownloadFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        var attached = await service.GetFiles(id, cancellationToken);
        if (attached.All(f => f.FileMetadata.Id != fileId))
        {
            return this.NotFoundProblem($"File ID {fileId} is not attached to tax statement ID {id}.");
        }

        var (metadata, content) = await fileService.GetFileContentAsync(fileId, cancellationToken);
        if (metadata is null || content is null)
        {
            return NotFound();
        }

        // Force a download and forbid content-type sniffing so a mislabeled upload
        // cannot be rendered/executed inline in the app origin (matches the insurance
        // and contract download handlers).
        Response.Headers.ContentDisposition = $"attachment; filename=\"{metadata.FileName}\"";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";
        return File(content, metadata.ContentType, metadata.FileName);
    }

    [HttpDelete("{id}/files/{fileId}", Name = "DetachTaxStatementFile")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a document from a tax statement.")]
    public async Task<IActionResult> DetachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        return await service.DetachFile(id, fileId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to tax statement ID {id}.");
    }
}
