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
public class AccountTermsController : ControllerBase
{
    private readonly AccountTermService accountTermService;

    public AccountTermsController(AccountTermService accountTermService)
    {
        this.accountTermService = accountTermService;
    }

    [HttpGet("{accountId}/terms", Name = "GetAccountTerms")]
    [Authorize(Policy = PermissionClaims.AccountsTermsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingAccountTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the term (rate/fee) history for an account.",
        Description = @"Lists the full term history for the account, newest effective date first.
                        Optionally filtered by term kind and/or an as-of date.")]
    public async Task<IActionResult> GetAccountTerms(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "kind")] TermKind? kind = null,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await accountTermService.GetHistory(accountId, kind, asOf, cancellationToken);
        if (terms is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(terms);
    }

    [HttpGet("{accountId}/terms/current", Name = "GetCurrentAccountTerms")]
    [Authorize(Policy = PermissionClaims.AccountsTermsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<CurrentAccountTerm>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective term for each kind on an account.",
        Description = @"Returns the in-force value of each term kind that has at least one entry, as of
                        now or the supplied as-of date.")]
    public async Task<IActionResult> GetCurrentAccountTerms(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var terms = await accountTermService.GetCurrent(accountId, asOf, cancellationToken);
        if (terms is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(terms);
    }

    [HttpPost("{accountId}/terms", Name = "PostAccountTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingAccountTerm))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a new term (rate/fee) entry on an account.")]
    public async Task<IActionResult> PostAccountTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromBody] NewAccountTerm newTerm, CancellationToken cancellationToken = default)
    {
        var term = await accountTermService.Create(accountId, newTerm, cancellationToken);
        return CreatedAtRoute("GetAccountTerms", new { accountId }, term);
    }

    [HttpPut("{accountId}/terms/{termId}", Name = "PutAccountTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update a term entry on an account.")]
    public async Task<IActionResult> PutAccountTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "termId")] Guid termId,
        [FromBody] NewAccountTerm putTerm, CancellationToken cancellationToken = default)
    {
        var updated = await accountTermService.Update(accountId, termId, putTerm, cancellationToken);
        if (!updated)
            return this.NotFoundProblem($"Term ID {termId} is not attached to account ID {accountId}.");

        return NoContent();
    }

    [HttpDelete("{accountId}/terms/{termId}", Name = "DeleteAccountTerm")]
    [Authorize(Policy = PermissionClaims.AccountsTermsWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete a term entry from an account.")]
    public async Task<IActionResult> DeleteAccountTerm(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "termId")] Guid termId, CancellationToken cancellationToken = default)
    {
        var deleted = await accountTermService.Delete(accountId, termId, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Term ID {termId} is not attached to account ID {accountId}.");

        return NoContent();
    }
}
