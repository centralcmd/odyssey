using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

using Odyssey.Api.Identity;
using Odyssey.Core.Finance;
using Odyssey.Core.Identity;

namespace Odyssey.Api.Controllers;

/// <summary>
/// The contract smart-tag endpoints (issue #166) — a separate controller on the <c>api/contracts</c>
/// route prefix, mirroring how <see cref="AccountSmartTagsController"/> sits beside
/// <see cref="ContractsController"/>.
///
/// <para>
/// <strong>Neither write takes a request body.</strong> Both the contract and the tag are named by
/// route <see cref="Guid"/>s, so mass assignment is closed structurally rather than by a check: there
/// is no DTO through which a nested <c>TransactionTag</c> or <c>Contract</c> object could be
/// over-posted.
/// </para>
/// </summary>
[ApiController]
[Route("api/contracts")]
public sealed class ContractSmartTagsController : ControllerBase
{
    private readonly ContractSmartTagService contractSmartTagService;
    private readonly ContractSmartTagTransactionService smartTagTransactions;
    private readonly IUserDisplayNameResolver displayNames;

    public ContractSmartTagsController(
        ContractSmartTagService contractSmartTagService,
        ContractSmartTagTransactionService smartTagTransactions,
        IUserDisplayNameResolver displayNames)
    {
        this.contractSmartTagService = contractSmartTagService;
        this.smartTagTransactions = smartTagTransactions;
        this.displayNames = displayNames;
    }

    [HttpGet("{contractId}/smart-tags", Name = "GetContractSmartTags")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(List<ExistingTransactionTag>))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "Get the smart tags configured for a contract.",
        Description = @"Lists the transaction tags currently associated with the contract as smart
                        tags, oldest association first. An empty list is a healthy 200, never a 404.")]
    public async Task<IActionResult> GetContractSmartTags(
        [FromRoute(Name = "contractId")] Guid contractId, CancellationToken cancellationToken = default)
    {
        var tags = await contractSmartTagService.GetSmartTags(contractId, cancellationToken);
        if (tags is null)
            return this.NotFoundProblem($"Contract ID {contractId} not found.");

        return Ok(tags);
    }

    /// <summary>
    /// The contract-scoped smart-tag match (issue #226). Both claims are required: the scope is contract
    /// data and the payload is transactions, so transaction data is never reachable under
    /// <c>contracts.read</c> alone — two stacked policies AND-combine.
    /// </summary>
    [HttpGet("{contractId}/smart-tag-transactions", Name = "GetContractSmartTagTransactions")]
    [Authorize(Policy = PermissionClaims.ContractsRead)]
    [Authorize(Policy = PermissionClaims.TransactionsRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ContractSmartTagTransactionsResult))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(
        Summary = "List the transactions a contract's smart tags match.",
        Description = @"A transaction matches when it carries one of the contract's smart tags, is dated
                        inside the contract's term, and its merchant is a contact party on the contract.
                        Paged, searchable and sortable with the transaction list's keys. The summary
                        totals the whole matched set per currency, independent of search and paging.
                        A structurally empty result is a 200 with scope.emptyReason set.")]
    public async Task<IActionResult> GetContractSmartTagTransactions(
        [FromRoute(Name = "contractId")] Guid contractId,
        [FromQuery] ContractSmartTagTransactionsQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var result = await smartTagTransactions.ListAsync(contractId, query, cancellationToken);
        if (result is null)
            return this.NotFoundProblem($"Contract ID {contractId} not found.");

        await displayNames.EnrichFileAttributionAsync(User, result.Page.Items, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{contractId}/smart-tags/{tagId}", Name = "AddContractSmartTag")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ExistingTransactionTag))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Associate an existing tag with a contract as a smart tag.")]
    public async Task<IActionResult> AddContractSmartTag(
        [FromRoute(Name = "contractId")] Guid contractId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var tag = await contractSmartTagService.AddSmartTag(contractId, tagId, cancellationToken);
        return CreatedAtRoute("GetContractSmartTags", new { contractId }, tag);
    }

    [HttpDelete("{contractId}/smart-tags/{tagId}", Name = "RemoveContractSmartTag")]
    [Authorize(Policy = PermissionClaims.ContractsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    [SwaggerOperation(Summary = "Remove a smart-tag association from a contract.")]
    public async Task<IActionResult> RemoveContractSmartTag(
        [FromRoute(Name = "contractId")] Guid contractId,
        [FromRoute(Name = "tagId")] Guid tagId, CancellationToken cancellationToken = default)
    {
        var removed = await contractSmartTagService.RemoveSmartTag(contractId, tagId, cancellationToken);
        if (!removed)
            return this.NotFoundProblem(
                $"Tag ID {tagId} is not a smart tag for contract ID {contractId}.");

        return NoContent();
    }
}
