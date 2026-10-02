using System.ComponentModel.DataAnnotations;
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

/// <summary>
/// A contract's attached files. Split out of <c>ContractsController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contracts")]
public sealed class ContractFilesController : ControllerBase
{
    private readonly ContractService contracts;
    private readonly ContractFileService service;
    private readonly FileService fileService;
    private readonly IUserDisplayNameResolver displayNames;

    public ContractFilesController(
        ContractService contracts,
        ContractFileService service,
        FileService fileService,
        IUserDisplayNameResolver displayNames)
    {
        this.contracts = contracts;
        this.service = service;
        this.fileService = fileService;
        this.displayNames = displayNames;
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    [HttpPost("{id}/files", Name = "AttachContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContractFile))]
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
        if (!await contracts.Exists(id, cancellationToken))
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        if (await this.ValidateAttachableDocumentAsync(fileService, request.FileMetadataId, "contract", cancellationToken) is { } problem)
        {
            return problem;
        }

        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.AttachFile(id, request, userId, cancellationToken);
        if (created is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        // The created link carries AttachedByUserId, so it is enriched like every list (issue #106).
        await displayNames.EnrichFileAttributionAsync(User, [created], cancellationToken);
        return CreatedAtRoute("DownloadContractFile", new { id, fileId = request.FileMetadataId }, created);
    }

    [HttpGet("{id}/files", Name = "GetContractFiles")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingContractFile>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the documents attached to a contract.",
        Description = "Unpaged and bounded by the per-contract file cap. A contract with no documents " +
                      "returns an empty array, never a 404.")]
    public async Task<IActionResult> GetFiles(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var files = await service.GetFiles(id, cancellationToken);
        if (files is null)
        {
            return this.NotFoundProblem($"Contract ID {id} not found.");
        }

        await displayNames.EnrichFileAttributionAsync(User, files, cancellationToken);
        return Ok(files);
    }

    [HttpPut("{id}/files/{fileId}", Name = "UpdateContractFile")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update an attached document's type and validity metadata.",
        Description = "A full replacement: an omitted date or issuer clears the stored value. " +
                      "fileType may not be omitted. files.read is deliberately not required — this " +
                      "verb reads no file metadata and takes no file id the caller had not already " +
                      "attached.")]
    public async Task<IActionResult> UpdateFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "fileId")] Guid fileId,
        [FromBody] UpdateContractFileRequest request, CancellationToken cancellationToken = default)
    {
        return await service.UpdateFile(id, fileId, request, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to contract ID {id}.");
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

        return await this.StreamDocumentAsync(fileService, fileId, cancellationToken);
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
}
