using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Core.Finance;

namespace Odyssey.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public class TermsController : ControllerBase
{
    private readonly TermService termService;

    public TermsController(TermService termService)
    {
        this.termService = termService;
    }

    [HttpGet("{accountId}/terms", Name = "GetTerms")]
    [Authorize(Policy = PermissionClaims.AccountsTermsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the term (rate/fee) history for an account.",
        Description = @"Lists the full term history for the account, newest effective date first.
                        Optionally filtered by term kind and/or an as-of date.")]
    public async Task<IActionResult> GetTerms(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "kind")] TermKind? kind = null,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetHistory(accountId, kind, asOf, cancellationToken);
        if (terms is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(terms);
    }

    [HttpGet("{accountId}/terms/current", Name = "GetCurrentTerms")]
    [Authorize(Policy = PermissionClaims.AccountsTermsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<CurrentTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective term for each kind on an account.",
        Description = @"Returns the in-force value of each term kind that has at least one entry, as of
                        now or the supplied as-of date.")]
    public async Task<IActionResult> GetCurrentTerms(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await termService.GetCurrent(accountId, asOf, cancellationToken);
        if (terms is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(terms);
    }

    [HttpPost("{accountId}/terms", Name = "PostTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTerm))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Create a new term (rate/fee) entry on an account.",
        Description = @"'direction' is accepted only as Outgoing, or omitted: an account term may not
                        carry a non-default direction, because no account surface reads one (issue #159).")]
    public async Task<IActionResult> PostTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromBody] NewTerm newTerm, CancellationToken cancellationToken = default)
    {
        var term = await termService.Create(accountId, newTerm, cancellationToken);
        return CreatedAtRoute("GetTerms", new { accountId }, term);
    }

    [HttpPut("{accountId}/terms/{termId}", Name = "PutTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Update a term entry on an account.",
        Description = @"A full replace. 'direction' is accepted only as Outgoing, or omitted — an
                        account term may not carry a non-default direction (issue #159).")]
    public async Task<IActionResult> PutTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "termId")] Guid termId,
        [FromBody] NewTerm putTerm, CancellationToken cancellationToken = default)
    {
        var updated = await termService.Update(accountId, termId, putTerm, cancellationToken);
        if (!updated)
            return this.NotFoundProblem($"Term ID {termId} is not attached to account ID {accountId}.");

        return NoContent();
    }

    [HttpDelete("{accountId}/terms/{termId}", Name = "DeleteTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a term entry from an account.")]
    public async Task<IActionResult> DeleteTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "termId")] Guid termId, CancellationToken cancellationToken = default)
    {
        var deleted = await termService.Delete(accountId, termId, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Term ID {termId} is not attached to account ID {accountId}.");

        return NoContent();
    }
}
