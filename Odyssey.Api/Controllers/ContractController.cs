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
[Route("api/contracts")]
public class ContractController : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    private readonly ILogger<ContractController> logger;
    private readonly ContractService service;
    private readonly FileService fileService;

    public ContractController(
        ILogger<ContractController> logger,
        ContractService service,
        FileService fileService)
    {
        this.logger = logger;
        this.service = service;
        this.fileService = fileService;
    }

    // ── Contracts ────────────────────────────────────────────────────────────────

    [HttpGet(Name = "GetContracts")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<ContractListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List contracts (lean projection with derived status), with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] ContractsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("summary", Name = "GetContractSummary")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ContractSummary))]
    [SwaggerOperation(Summary = "Summary rollup: counts by status and by type.")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await service.GetSummary(cancellationToken));
    }

    [HttpGet("{id}", Name = "GetContract")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get one contract with minimal party/file references and derived status.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var contract = await service.Get(id, cancellationToken);
        return contract is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(contract);
    }

    [HttpPost(Name = "PostContract")]
    [Authorize(Policy = PermissionClaims.ContractsCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a contract.")]
    public async Task<IActionResult> Post(
        [FromBody] NewContract request, CancellationToken cancellationToken = default)
    {
        var created = await service.Create(request, cancellationToken);
        return CreatedAtRoute("GetContract", new { id = created.ContractId }, created);
    }

    [HttpPut("{id}", Name = "PutContract")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContract))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a contract's fields, including its archive state (no dedicated archive endpoint).")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateContract request, CancellationToken cancellationToken = default)
    {
        var updated = await service.Update(id, request, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteContract")]
    [Authorize(Policy = PermissionClaims.ContractsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete a contract (cascades party + file links; leaves the underlying records).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Contract ID {id} not found.");
    }

    // ── Parties ──────────────────────────────────────────────────────────────────

    [HttpPost("{id}/parties", Name = "AddContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContractParty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a party (exactly one of accountId/contactId/insurancePolicyId).")]
    public async Task<IActionResult> AddParty(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] AddContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        var created = await service.AddParty(id, request, cancellationToken);
        // A party has no standalone GET (it is only ever read through its contract), so the 201
        // Location points at the contract — the addressable resource that now contains the new
        // party — while the body is the created party. Mirrors the insurance renewal/file endpoints.
        return created is null
            ? this.NotFoundProblem($"Contract ID {id} not found.")
            : CreatedAtRoute("GetContract", new { id }, created);
    }

    [HttpDelete("{id}/parties/{partyId}", Name = "DeleteContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a party from a contract.")]
    public async Task<IActionResult> DeleteParty(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "partyId")] Guid partyId, CancellationToken cancellationToken = default)
    {
        return await service.DeleteParty(id, partyId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Party ID {partyId} is not part of contract ID {id}.");
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    [HttpPost("{id}/files", Name = "AttachContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an already-uploaded document to a contract (requires contracts.update + files.read).")]
    public async Task<IActionResult> AttachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] AttachContractFileRequest request, CancellationToken cancellationToken = default)
    {
        // Check the (cheap) contract existence before the file-metadata lookup + allow-list, so
        // attaching to a missing contract 404s without a wasted file read.
        if (!await service.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        if (await ValidateAttachableFile(request.FileMetadataId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.AttachFile(id, request.FileMetadataId, userId, request.FileType, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Contract ID {id} not found.")
            : CreatedAtRoute("DownloadContractFile", new { id, fileId = request.FileMetadataId }, null);
    }

    [HttpGet("{id}/files/{fileId}", Name = "DownloadContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Download a document attached to a contract.")]
    public async Task<IActionResult> DownloadFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!await service.IsFileAttachedToContract(id, fileId, cancellationToken))
        {
            return this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
        }

        return await StreamFile(fileId, cancellationToken);
    }

    [HttpDelete("{id}/files/{fileId}", Name = "DetachContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a document from a contract (the underlying file is left intact).")]
    public async Task<IActionResult> DetachFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        return await service.DetachFile(id, fileId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates an attach target: the file must exist and its server-recorded content type must be on
    /// the allow-list. Returns a problem result to short-circuit, or null when the file is allowed.
    /// </summary>
    private async Task<IActionResult?> ValidateAttachableFile(Guid fileId, CancellationToken cancellationToken = default)
    {
        var metadata = await fileService.GetFileMetadataAsync(fileId, cancellationToken);
        if (metadata is null)
        {
            return this.NotFoundProblem($"File ID {fileId} not found.");
        }

        if (!AllowedContentTypes.Contains(metadata.ContentType))
        {
            return this.BadRequestProblem($"Content type '{metadata.ContentType}' is not allowed for contract documents.");
        }

        return null;
    }

    private async Task<IActionResult> StreamFile(Guid fileId, CancellationToken cancellationToken = default)
    {
        var (metadata, content) = await fileService.GetFileContentAsync(fileId, cancellationToken);
        if (metadata is null || content is null)
        {
            return NotFound();
        }

        // Safe-download headers (§10): force a download and forbid content-type sniffing so a
        // mislabeled upload cannot be rendered/executed inline in the browser.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";
        return File(content, metadata.ContentType, metadata.FileName);
    }

}
