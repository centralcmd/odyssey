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
/// A contract's parties. Split out of <c>ContractController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contracts")]
public sealed class ContractPartiesController : ControllerBase
{
    private readonly ContractPartyService service;

    public ContractPartiesController(
        ContractPartyService service)
    {
        this.service = service;
    }

    // ── Parties ──────────────────────────────────────────────────────────────────

    [HttpPost("{id}/parties", Name = "AddContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingContractParty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Add a party in a role, optionally for a term (exactly one of accountId/contactId).")]
    public async Task<IActionResult> AddParty(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var created = await service.AddParty(id, request, userId, cancellationToken);
        // A party has no standalone GET (it is only ever read through its contract), so the 201
        // Location points at the contract — the addressable resource that now contains the new
        // party — while the body is the created party. Mirrors the contract file endpoints.
        return created is null
            ? this.NotFoundProblem($"Contract ID {id} not found.")
            : CreatedAtRoute("GetContract", new { id }, created);
    }

    [HttpPut("{id}/parties/{partyId}", Name = "UpdateContractParty")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExistingContractParty))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    // The edit enforces the same type x role matrix the add does, so it can answer 422 too — declared
    // here so the generated client and Swagger match reality (issue #169 §5 item 2). The party CAP is
    // deliberately not among the reasons: an in-place update is row-count-neutral and never re-checks
    // it, which is what keeps every party on an over-cap contract editable.
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Re-write one party: its role, its target, its dates, or any combination (full replacement).")]
    public async Task<IActionResult> UpdateParty(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "partyId")] Guid partyId,
        [FromBody] ContractPartyRequest request, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // A missing contract throws its own 404 from the service; a null here is the narrower
        // PartyNotOnContract class. Neither carries a field key, which is what distinguishes both from
        // the inline PartyTargetNotFound the record picker renders (issue #121 §9).
        var updated = await service.UpdateParty(
            id, partyId, request, userId, cancellationToken);
        return updated is null
            ? this.NotFoundProblem($"Party ID {partyId} is not part of contract ID {id}.")
            : Ok(updated);
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
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await service.DeleteParty(id, partyId, userId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Party ID {partyId} is not part of contract ID {id}.");
    }
}
