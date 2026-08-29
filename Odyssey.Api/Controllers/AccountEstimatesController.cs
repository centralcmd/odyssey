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
public class AccountEstimatesController : ControllerBase
{
    private readonly AccountService accountService;
    private readonly AccountEstimateService accountEstimateService;

    public AccountEstimatesController(
        AccountService accountService,
        AccountEstimateService accountEstimateService)
    {
        this.accountService = accountService;
        this.accountEstimateService = accountEstimateService;
    }

    [HttpGet("{accountId}/estimates", Name = "GetAccountEstimates")]
    [Authorize(Policy = PermissionClaims.AccountsEstimatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingAccountEstimate>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the estimated-value history for an account.",
        Description = @"Lists the full estimate history for the account, newest effective date first.
                        Optionally filtered by an as-of date.")]
    public async Task<IActionResult> GetAccountEstimates(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var estimates = await accountEstimateService.GetHistory(accountId, asOf, cancellationToken);
        if (estimates is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(estimates);
    }

    [HttpGet("{accountId}/estimates/current", Name = "GetCurrentAccountEstimate")]
    [Authorize(Policy = PermissionClaims.AccountsEstimatesRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CurrentAccountEstimate))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the currently-effective estimated value for an account.",
        Description = @"Returns the in-force estimate as of now or the supplied as-of date, or an empty
                        body when the account has no estimate in force.")]
    public async Task<IActionResult> GetCurrentAccountEstimate(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromQuery(Name = "asOf")] DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var accountExists = await accountService.Get(accountId, cancellationToken) is not null;
        if (!accountExists)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        var estimate = await accountEstimateService.GetCurrent(accountId, asOf, cancellationToken);

        // Always 200: a null body means "no estimate in force" (Ok(null) would 204, but the
        // contract is a 200 with an empty/null body). JsonResult keeps the status and serializes null.
        return new JsonResult(estimate) { StatusCode = StatusCodes.Status200OK };
    }

    [HttpPost("{accountId}/estimates", Name = "PostAccountEstimate")]
    [Authorize(Policy = PermissionClaims.AccountsEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingAccountEstimate))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Create a new estimated-value entry on an account.")]
    public async Task<IActionResult> PostAccountEstimate(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromBody] NewAccountEstimate newEstimate, CancellationToken cancellationToken = default)
    {
        var estimate = await accountEstimateService.Create(accountId, newEstimate, cancellationToken);
        return CreatedAtRoute("GetAccountEstimates", new { accountId }, estimate);
    }

    [HttpPut("{accountId}/estimates/{estimateId}", Name = "PutAccountEstimate")]
    [Authorize(Policy = PermissionClaims.AccountsEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Update an estimated-value entry on an account.")]
    public async Task<IActionResult> PutAccountEstimate(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "estimateId")] Guid estimateId,
        [FromBody] NewAccountEstimate putEstimate, CancellationToken cancellationToken = default)
    {
        var updated = await accountEstimateService.Update(accountId, estimateId, putEstimate, cancellationToken);
        if (!updated)
            return this.NotFoundProblem($"Estimate ID {estimateId} is not attached to account ID {accountId}.");

        return NoContent();
    }

    [HttpDelete("{accountId}/estimates/{estimateId}", Name = "DeleteAccountEstimate")]
    [Authorize(Policy = PermissionClaims.AccountsEstimatesWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Delete an estimated-value entry from an account.")]
    public async Task<IActionResult> DeleteAccountEstimate(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "estimateId")] Guid estimateId, CancellationToken cancellationToken = default)
    {
        var deleted = await accountEstimateService.Delete(accountId, estimateId, cancellationToken);
        if (!deleted)
            return this.NotFoundProblem($"Estimate ID {estimateId} is not attached to account ID {accountId}.");

        return NoContent();
    }
}
