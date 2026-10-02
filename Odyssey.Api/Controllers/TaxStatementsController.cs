using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/tax-statements")]
public sealed class TaxStatementsController : ControllerBase
{
    private readonly ILogger<TaxStatementsController> logger;
    private readonly TaxStatementService service;
    private readonly FileService fileService;
    private readonly IUserDisplayNameResolver displayNames;

    public TaxStatementsController(
        ILogger<TaxStatementsController> logger,
        TaxStatementService service,
        FileService fileService,
        IUserDisplayNameResolver displayNames)
    {
        this.logger = logger;
        this.service = service;
        this.fileService = fileService;
        this.displayNames = displayNames;
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
        var result = await service.ListAsync(query, cancellationToken);
        await displayNames.EnrichFileAttributionAsync(User, result.Items, cancellationToken);
        return Ok(result);
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
        if (statement is null)
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [statement], cancellationToken);
        return Ok(statement);
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
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
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
        if (updated is null)
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [updated], cancellationToken);
        return Ok(updated);
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
        if (updated is null)
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [updated], cancellationToken);
        return Ok(updated);
    }

    [HttpPut("{id}/tags", Name = "PutTaxStatementTags")]
    [Authorize(Policy = PermissionClaims.TaxesUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingTaxStatement))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Replace the selected tax-payment, income and settlement tag sets in one call.")]
    public async Task<IActionResult> PutTags(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateTaxStatementTags request, CancellationToken cancellationToken = default)
    {
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        var updated = await service.UpdateTags(id, request, cancellationToken);
        if (updated is null)
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, [updated], cancellationToken);
        return Ok(updated);
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
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTaxStatementFile))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an already-uploaded document to a tax statement (requires taxes.update + files.read).")]
    public async Task<IActionResult> AttachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] AttachTaxStatementFileRequest request, CancellationToken cancellationToken = default)
    {
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        // The shared allow-list and the shared check (issue #287 H4): a private copy of the list here was
        // a security control that a change to DocumentContentTypes.Allowed would silently skip.
        if (await this.ValidateAttachableDocumentAsync(
                fileService, request.FileId, "tax statement", cancellationToken) is { } problem)
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.AttachFile(id, request.FileId, userId, request.FileType, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Tax statement ID {id} not found.");
        }

        // The created link carries AttachedByUserId, so it is enriched like every list (issue #106).
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
        return CreatedAtRoute("DownloadTaxStatementFile", new { id, fileId = request.FileId }, created);
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
        if (!await service.IsFileAttached(id, fileId, cancellationToken))
        {
            return this.NotFoundProblem($"File ID {fileId} is not attached to tax statement ID {id}.");
        }

        return await this.StreamDocumentAsync(fileService, fileId, cancellationToken);
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
