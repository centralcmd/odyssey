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
public class AccountSmartTagsController : ControllerBase
{
    private readonly AccountSmartTagService accountSmartTagService;

    public AccountSmartTagsController(AccountSmartTagService accountSmartTagService)
    {
        this.accountSmartTagService = accountSmartTagService;
    }

    [HttpGet("{accountId}/smart-tags", Name = "GetAccountSmartTags")]
    [Authorize(Policy = PermissionClaims.AccountsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTransactionTag>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the smart tags configured for an account.",
        Description = @"Lists the transaction tags currently associated with the account as smart tags,
                        oldest association first.")]
    public async Task<IActionResult> GetAccountSmartTags(
        [FromRoute(Name = "accountId")] Guid accountId, CancellationToken cancellationToken = default)
    {
        var tags = await accountSmartTagService.GetSmartTags(accountId, cancellationToken);
        if (tags is null)
            return this.NotFoundProblem($"Account ID {accountId} not found.");

        return Ok(tags);
    }

    [HttpPost("{accountId}/smart-tags/{tagId}", Name = "AddAccountSmartTag")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTransactionTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Associate an existing tag with an account as a smart tag.")]
    public async Task<IActionResult> AddAccountSmartTag(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var tag = await accountSmartTagService.AddSmartTag(accountId, tagId, cancellationToken);
        return CreatedAtRoute("GetAccountSmartTags", new { accountId }, tag);
    }

    [HttpDelete("{accountId}/smart-tags/{tagId}", Name = "RemoveAccountSmartTag")]
    [Authorize(Policy = PermissionClaims.AccountsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a smart-tag association from an account.")]
    public async Task<IActionResult> RemoveAccountSmartTag(
        [FromRoute(Name = "accountId")] Guid accountId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var removed = await accountSmartTagService.RemoveSmartTag(accountId, tagId, cancellationToken);
        if (!removed)
            return this.NotFoundProblem($"Tag ID {tagId} is not a smart tag for account ID {accountId}.");

        return NoContent();
    }
}
