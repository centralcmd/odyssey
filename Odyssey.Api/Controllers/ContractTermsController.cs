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
/// A contract's terms (rates and fees). Split out of <c>ContractsController</c> along the Property precedent, one controller per
/// sub-resource under the same route prefix; the routes and route names are unchanged (issue #287 M2).
/// </summary>
[ApiController]
[Route("api/contracts")]
public sealed class ContractTermsController : ControllerBase
{
    private readonly TermService termService;

    public ContractTermsController(
        TermService termService)
    {
        this.termService = termService;
    }

    // ── Terms (rates & fees) ─────────────────────────────────────────────────────
    //
    // Gated on the parent contracts.read / contracts.update, NOT on a dedicated claim (issue #135
    // §7.2). That is this controller's own convention rather than a departure from it: parties and
    // file attach/download are already gated the same way. Since issue #190 these five are the only
    // term API — the account-owned routes and their dedicated claim pair were removed.

    [HttpGet("{id}/terms", Name = "GetContractTerms")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTerm>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the term (rate/fee) history for a contract.",
        Description = @"Lists the full term history for the contract, newest effective date first.
                        Optionally filtered by an as-of date. An archived contract's history stays
                        readable.")]
    public async Task<IActionResult> GetTerms(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetContractHistory(id, asOf, cancellationToken);
        return terms is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(terms);
    }

    [HttpGet("{id}/terms/current", Name = "GetCurrentContractTerms")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<CurrentTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective term of each series on a contract.",
        Description = @"Returns the in-force entry of each labelled series that has at least one
                        entry, as of now or the supplied as-of date. An empty array is a healthy
                        response — a contract with no recorded price is not a defect.")]
    public async Task<IActionResult> GetCurrentTerms(
        [FromRoute(Name = "id")] Guid id,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetContractCurrent(id, asOf, cancellationToken);
        return terms is null ? this.NotFoundProblem($"Contract ID {id} not found.") : Ok(terms);
    }

    [HttpPost("{id}/terms", Name = "PostContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTerm))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a term (rate/fee) entry on a contract.",
        Description = @"Every term is a labelled series; 'label' names what it prices. A money-valued
                        term must name its currency — a contract has none of its own to default from.
                        'direction' says which way the money moves from the household's perspective;
                        it is optional, omitting it means Outgoing, and every unit accepts either.")]
    public async Task<IActionResult> PostTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromBody] NewTerm newTerm, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        // The acting user, threaded through exactly as every sibling contract write already does. Without
        // it the TermChanged system event this write records would read "Unknown user" — which is
        // indistinguishable from a deleted author, so the defect would look like correct behaviour
        // (issue #154 §5.6).
        var term = await termService.CreateForContract(
            id, newTerm, userId, cancellationToken);
        // A term has no standalone GET (it is only ever read through its owner), so the 201 Location
        // points at the contract's term list — the addressable collection that now contains it.
        return CreatedAtRoute("GetContractTerms", new { id }, term);
    }

    [HttpPut("{id}/terms/{termId}", Name = "PutContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Replace a term entry on a contract.",
        Description = @"The owner is not changeable through this endpoint — the route is the only
                        thing that names it. A term id belonging to a different contract is a 404, never a 403 and never a silent success.

                        This is a FULL replace and 'direction' is not exempt: omitting it resets the
                        term to Outgoing, exactly as omitting 'label', 'currencyCode', 'interval' or
                        'anchorDate' already clears those. That matters more here than elsewhere, since
                        it moves the amount from one side of the household's net to the other — read
                        the term back and send its direction with the replacement.")]
    public async Task<IActionResult> PutTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "termId")] Guid termId,
        [FromBody] NewTerm putTerm, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        var updated = await termService.UpdateForContract(
            id, termId, putTerm, userId, cancellationToken);
        return updated
            ? NoContent()
            // Deliberately does not reveal which owner DOES hold the id (issue #135 §9).
            : this.NotFoundProblem($"Term ID {termId} is not attached to contract ID {id}.");
    }

    [HttpDelete("{id}/terms/{termId}", Name = "DeleteContractTerm")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a term entry from a contract (the contract itself is untouched).")]
    public async Task<IActionResult> DeleteTerm(
        [FromRoute(Name = "id")] Guid id,
        [FromRoute(Name = "termId")] Guid termId, CancellationToken cancellationToken = default)
    {
        if (User.ActingUserId() is not { } userId)
        {
            return this.MissingUserProblem();
        }

        return await termService.DeleteForContract(
            id, termId, userId, cancellationToken)
            ? NoContent()
            : this.NotFoundProblem($"Term ID {termId} is not attached to contract ID {id}.");
    }
}
