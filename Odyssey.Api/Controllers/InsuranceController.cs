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
[Route("api/insurance-policies")]
public class InsuranceController : ControllerBase
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    private readonly ILogger<InsuranceController> logger;
    private readonly InsuranceService service;
    private readonly FileService fileService;

    public InsuranceController(
        ILogger<InsuranceController> logger,
        InsuranceService service,
        FileService fileService)
    {
        this.logger = logger;
        this.service = service;
        this.fileService = fileService;
    }

    // ── Policies ──────────────────────────────────────────────────────────────

    [HttpGet(Name = "GetInsurancePolicies")]
    [Authorize(Policy = PermissionClaims.InsuranceRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PagedResult<InsurancePolicyListItem>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "List insurance policies (lean projection with derived status), with search, filtering, sorting and pagination.")]
    public async Task<IActionResult> Get(
        [FromQuery] InsurancePoliciesQueryParams query,
        CancellationToken cancellationToken = default)
    {
        return Ok(await service.ListAsync(query, cancellationToken));
    }

    [HttpGet("summary", Name = "GetInsurancePortfolioSummary")]
    [Authorize(Policy = PermissionClaims.InsuranceRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(InsurancePortfolioSummary))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Portfolio rollup: counts by status, per-currency premium/coverage, optional converted totals.")]
    public async Task<IActionResult> GetSummary(
        [FromQuery(Name = "baseCurrency")] string? baseCurrency = null, CancellationToken cancellationToken = default)
    {
        return Ok(await service.GetSummary(baseCurrency, cancellationToken));
    }

    [HttpGet("{id}", Name = "GetInsurancePolicy")]
    [Authorize(Policy = PermissionClaims.InsuranceRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInsurancePolicy))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Get one policy with renewals, file metadata and derived coverage status.")]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        var policy = await service.Get(id, cancellationToken);
        return policy is null ? this.NotFoundProblem($"Insurance policy ID {id} not found.") : Ok(policy);
    }

    [HttpPost(Name = "PostInsurancePolicy")]
    [Authorize(Policy = PermissionClaims.InsuranceCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingInsurancePolicy))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create an insurance policy.",
        Description = @"The four link collections — insurerIds, insuredAccountIds, insuredContactIds and
beneficiaryIds — are arrays of scalar ids, all optional. 400 when an id names no existing, non-archived
record or an array is longer than the compile-time ceiling; 422 when a collection exceeds the effective
InsuranceMaxLinksPerPolicy cap.")]
    public async Task<IActionResult> Post(
        [FromBody] NewInsurancePolicy request, CancellationToken cancellationToken = default)
    {
        // The calling user is stamped on any beneficiary link this write creates — the same
        // User.FindFirstValue pattern AttachPolicyRenewalFile already uses, and the only source of
        // InsurancePolicyBeneficiary.CreatedByUserId.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var created = await service.Create(request, userId, cancellationToken);
        return CreatedAtRoute("GetInsurancePolicy", new { id = created.InsurancePolicyId }, created);
    }

    [HttpPut("{id}", Name = "PutInsurancePolicy")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingInsurancePolicy))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an insurance policy's fields.",
        Description = @"For each of the four link collections, null leaves it unchanged and [] clears it.
422 when a collection would exceed the effective cap, or when the submitted array omits a stored link
whose target is archived or no longer resolves — such a link cannot be removed by an ordinary write.")]
    public async Task<IActionResult> Put(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] UpdateInsurancePolicy request, CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var updated = await service.Update(id, request, userId, cancellationToken);
        return updated is null ? this.NotFoundProblem($"Insurance policy ID {id} not found.") : Ok(updated);
    }

    [HttpDelete("{id}", Name = "DeleteInsurancePolicy")]
    [Authorize(Policy = PermissionClaims.InsuranceDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Permanently delete an insurance policy (cascades renewals + file links; leaves blobs).")]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "id")] Guid id, CancellationToken cancellationToken = default)
    {
        return await service.Delete(id, cancellationToken) ? NoContent() : this.NotFoundProblem($"Insurance policy ID {id} not found.");
    }

    // ── Renewals ──────────────────────────────────────────────────────────────

    [HttpPost("{id}/renewals", Name = "PostPolicyRenewal")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingPolicyRenewal))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a renewal period to a policy.")]
    public async Task<IActionResult> PostRenewal(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewPolicyRenewal request, CancellationToken cancellationToken = default)
    {
        var created = await service.AddRenewal(id, request, cancellationToken);
        return created is null
            ? this.NotFoundProblem($"Insurance policy ID {id} not found.")
            : CreatedAtRoute("GetInsurancePolicy", new { id }, created);
    }

    [HttpPut("{id}/renewals/{renewalId}", Name = "PutPolicyRenewal")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingPolicyRenewal))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a renewal period.")]
    public async Task<IActionResult> PutRenewal(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "renewalId")] Guid renewalId,
        [FromBody] UpdatePolicyRenewal request, CancellationToken cancellationToken = default)
    {
        var updated = await service.UpdateRenewal(id, renewalId, request, cancellationToken);
        return updated is null
            ? this.NotFoundProblem($"Renewal ID {renewalId} is not part of insurance policy ID {id}.")
            : Ok(updated);
    }

    [HttpDelete("{id}/renewals/{renewalId}", Name = "DeletePolicyRenewal")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a renewal period.")]
    public async Task<IActionResult> DeleteRenewal(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "renewalId")] Guid renewalId, CancellationToken cancellationToken = default)
    {
        return await service.DeleteRenewal(id, renewalId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Renewal ID {renewalId} is not part of insurance policy ID {id}.");
    }

    // ── Renewal-level files ─────────────────────────────────────────────────────
    // The only document surface there is. The policy-level trio was removed in issue #26 — a period is
    // the only home for an insurance document — so every route here is scoped by BOTH parent ids, and
    // RenewalExists(id, renewalId) runs on all three verbs, not just the attach.

    [HttpPost("{id}/renewals/{renewalId}/files", Name = "AttachPolicyRenewalFile")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [Authorize(Policy = PermissionClaims.FilesRead)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Attach an already-uploaded document to a renewal (requires insurance.update + files.read).")]
    public async Task<IActionResult> AttachRenewalFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "renewalId")] Guid renewalId,
        [FromBody] AttachInsurancePolicyFileRequest request, CancellationToken cancellationToken = default)
    {
        if (!await service.RenewalExists(id, renewalId, cancellationToken))
        {
            return this.NotFoundProblem($"Renewal ID {renewalId} is not part of insurance policy ID {id}.");
        }

        if (await ValidateAttachableFile(request.FileId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        await service.AttachRenewalFile(renewalId, request.FileId, userId, request.FileType, request.EffectiveDate, cancellationToken);
        return CreatedAtRoute("DownloadPolicyRenewalFile", new { id, renewalId, fileId = request.FileId }, null);
    }

    [HttpGet("{id}/renewals/{renewalId}/files/{fileId}", Name = "DownloadPolicyRenewalFile")]
    [Authorize(Policy = PermissionClaims.InsuranceRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Download a document attached to a renewal.")]
    public async Task<IActionResult> DownloadRenewalFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "renewalId")] Guid renewalId,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!await service.RenewalExists(id, renewalId, cancellationToken) || !await service.IsFileAttachedToRenewal(renewalId, fileId, cancellationToken))
        {
            return this.NotFoundProblem($"File ID {fileId} is not attached to renewal ID {renewalId} of insurance policy ID {id}.");
        }

        return await StreamFile(fileId, cancellationToken);
    }

    [HttpDelete("{id}/renewals/{renewalId}/files/{fileId}", Name = "DetachPolicyRenewalFile")]
    [Authorize(Policy = PermissionClaims.InsuranceUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Detach a document from a renewal (the underlying file is left intact).")]
    public async Task<IActionResult> DetachRenewalFile(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "renewalId")] Guid renewalId,
        [FromRoute(Name = "fileId")] Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!await service.RenewalExists(id, renewalId, cancellationToken))
        {
            return this.NotFoundProblem($"Renewal ID {renewalId} is not part of insurance policy ID {id}.");
        }

        return await service.DetachRenewalFile(renewalId, fileId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"File ID {fileId} is not attached to renewal ID {renewalId}.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates an attach target: the file must exist and its server-recorded content type must be on
    /// the allow-list (§4). Returns a problem result to short-circuit, or null when the file is allowed.
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
            return this.BadRequestProblem($"Content type '{metadata.ContentType}' is not allowed for insurance documents.");
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

        // Safe-download headers (§10 #2): force a download and forbid content-type sniffing so a
        // mislabeled upload cannot be rendered/executed inline in the browser.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{metadata.Sha256Hash}\"";
        return File(content, metadata.ContentType, metadata.FileName);
    }

}
